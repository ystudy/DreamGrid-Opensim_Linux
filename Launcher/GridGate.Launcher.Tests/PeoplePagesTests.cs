using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Email;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Email;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The People list, a person's page, edit and delete, on the real host (spec 2026-11-03-people-admin-design).</summary>
public sealed class PeoplePagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid GailId = Guid.Parse("0b000000-0000-0000-0000-00000000000b");
    private static readonly Guid SueId = Guid.Parse("0d000000-0000-0000-0000-00000000000d");

    private static LauncherFactory Factory()
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<IMailTransport>(new EmailPageTests.FakeTransport()),
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Gail", "Guest", 0, "pass-word1", GailId, email: "gail@example.com");
        f.Robust.Add("Sue", "Spended", -1, "pass-word1", SueId, email: "sue@example.com");
        for (var i = 0; i < 30; i++) f.Robust.Add("Res" + i.ToString("00"), "Zed", 0, "pass-word1");
        var a = new EmailAccount(Guid.NewGuid(), "gmail", "smtp.gmail.com", 587, MailSecurity.StartTls, "grid@gmail.com", null, "Avalon",
            "grid@gmail.com", null, 500, null, false, 0, null, null);
        f.Services.GetRequiredService<EmailAccounts>().Save(a, "pw");
        return f;
    }

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        using var r = await client.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> fields, string tokenFrom)
    {
        var page = await PageAsync(client, tokenFrom);
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token }), Ct);
    }

    private static async Task<IHtmlDocument> DocOf(HttpResponseMessage r) =>
        new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));

    private static IElement? Icon(IHtmlDocument page, Guid person, string task) =>
        page.QuerySelector($"tr[data-id='{person}'] [data-task={task}]");

    [Fact]
    public async Task List_searches_and_pages()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");

        var first = await PageAsync(admin, "/people?q=zed");
        Assert.Equal(25, first.QuerySelectorAll("#people-list tbody tr").Length);
        Assert.Equal("1–25 of 30", first.QuerySelector("#people-count")!.TextContent.Trim());
        var second = await PageAsync(admin, "/people?q=zed&page=2");
        Assert.Equal(5, second.QuerySelectorAll("#people-list tbody tr").Length);
        Assert.Contains("Res29 Zed", second.QuerySelector("#people-list")!.TextContent);

        var gail = await PageAsync(admin, "/people?q=gail");
        Assert.Equal($"/people/{GailId}", gail.QuerySelector($"tr[data-id='{GailId}'] a.person-name")!.GetAttribute("href"));
        var suspended = await PageAsync(admin, "/people?filter=Suspended");
        Assert.Single(suspended.QuerySelectorAll("#people-list tbody tr"));
    }

    [Fact]
    public async Task Row_icons_disabled_with_reason()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        var page = await PageAsync(admin, "/people?q=test");

        var ownerEdit = Icon(page, TestAuth.OwnerId, "Edit")!;
        Assert.True(ownerEdit.HasAttribute("disabled"));
        Assert.Equal("The grid owner can't be changed here.", ownerEdit.GetAttribute("title"));
        Assert.Equal("Use your own account page.", Icon(page, TestAuth.AdminId, "SignOut")!.GetAttribute("title"));

        var gail = await PageAsync(admin, "/people?q=gail");
        Assert.Equal("Suspend them first.", Icon(gail, GailId, "Delete")!.GetAttribute("title"));
        Assert.True(Icon(gail, GailId, "Delete")!.HasAttribute("disabled"));
        Assert.Equal($"/people/{GailId}/edit", Icon(gail, GailId, "Edit")!.GetAttribute("href"));
        var sue = await PageAsync(admin, "/people?q=sue");
        Assert.Equal($"/people/{SueId}/delete", Icon(sue, SueId, "Delete")!.GetAttribute("href"));
        Assert.NotNull(Icon(sue, SueId, "Unsuspend"));
    }

    [Fact]
    public async Task Supervisor_sees_only_suspend_enabled()
    {
        await using var f = Factory();
        var supervisor = await SignedInAsync(f, "supervisor");
        var page = await PageAsync(supervisor, "/people?q=gail");
        Assert.False(Icon(page, GailId, "Suspend")!.HasAttribute("disabled"));
        foreach (var task in new[] { "Edit", "ResetLink", "SignOut", "Delete" })
            Assert.True(Icon(page, GailId, task)!.HasAttribute("disabled"), task);
    }

    [Fact]
    public async Task Supervisor_gets_403_on_edit_and_delete()
    {
        await using var f = Factory();
        var supervisor = await SignedInAsync(f, "supervisor");
        foreach (var url in new[] { $"/people/{GailId}/edit", $"/people/{SueId}/delete" })
        {
            using var r = await supervisor.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }
        using var post = await PostAsync(supervisor, $"/people/{SueId}/delete", new() { ["confirm"] = "yes" }, "/people");
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Contains(f.Robust.All, a => a.PrincipalId == SueId);
    }

    [Fact]
    public async Task Reset_post_sends_and_returns_to_the_same_page()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        using var r = await PostAsync(admin, "/people?handler=Task",
            new() { ["id"] = GailId.ToString(), ["task"] = "ResetLink", ["q"] = "gail", ["page"] = "1" }, "/people?q=gail");
        var page = await DocOf(r);
        Assert.Equal("A reset link was sent to Gail Guest.", page.QuerySelector(".ok-box")!.TextContent.Trim());
        Assert.NotNull(page.QuerySelector($"tr[data-id='{GailId}']"));
    }

    [Fact]
    public async Task Edit_page_renames()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        var form = await PageAsync(admin, $"/people/{GailId}/edit");
        Assert.Equal("Gail", form.QuerySelector("input[name=First]")!.GetAttribute("value"));

        using var r = await PostAsync(admin, $"/people/{GailId}/edit",
            new() { ["First"] = "Gwen", ["Last"] = "Guest", ["Email"] = "gail@example.com" }, $"/people/{GailId}/edit");

        Assert.Contains("Gail Guest is now Gwen Guest.", (await DocOf(r)).QuerySelector(".ok-box")!.TextContent);
        Assert.Contains(f.Robust.All, a => a.PrincipalId == GailId && a.FirstName == "Gwen");
    }

    [Fact]
    public async Task Delete_without_confirm_is_refused()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        var preview = await PageAsync(admin, $"/people/{SueId}/delete");
        Assert.Equal("Are you sure?", preview.QuerySelector("h1")!.TextContent.Trim());
        Assert.Contains("inventory", preview.QuerySelector("#delete-what")!.TextContent);

        using var r = await PostAsync(admin, $"/people/{SueId}/delete", [], $"/people/{SueId}/delete");
        Assert.NotNull((await DocOf(r)).QuerySelector(".error-box"));
        Assert.Contains(f.Robust.All, a => a.PrincipalId == SueId);
    }

    [Fact]
    public async Task Delete_confirmed_removes()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        using var r = await PostAsync(admin, $"/people/{SueId}/delete", new() { ["confirm"] = "yes" }, $"/people/{SueId}/delete");
        Assert.Equal("Sue Spended is deleted.", (await DocOf(r)).QuerySelector(".ok-box")!.TextContent.Trim());
        Assert.DoesNotContain(f.Robust.All, a => a.PrincipalId == SueId);
    }

    [Fact]
    public async Task Person_page_shows_details_tasks_and_recent_changes()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        using (await PostAsync(admin, "/people?handler=Task", new() { ["id"] = GailId.ToString(), ["task"] = "Suspend" }, "/people")) { }

        var page = await PageAsync(admin, $"/people/{GailId}");

        Assert.Contains("Gail Guest", page.QuerySelector("h1")!.TextContent);
        Assert.Contains("gail@example.com", page.QuerySelector("#person-details")!.TextContent);
        Assert.NotNull(page.QuerySelector("[data-task=Unsuspend]"));
        Assert.Contains("suspended", page.QuerySelector("#person-history")!.TextContent);
    }

    [Fact]
    public async Task Person_history_is_only_theirs()
    {
        await using var f = Factory();
        var audit = f.Services.GetRequiredService<Core.Logs.AuditLog>();
        audit.Write(new Core.Logs.AuditEntry("Test Admin", null, "admin", null, "suspended", "Gail Guestly", "ok"));
        audit.Write(new Core.Logs.AuditEntry("Test Admin", null, "admin", null, "name-changed", "Gail Guest -> Gail Guests", "ok"));
        audit.Write(new Core.Logs.AuditEntry("Test Admin", null, "admin", null, "reset-sent", "Gail Guest", "ok"));
        var admin = await SignedInAsync(f, "admin");

        var history = (await PageAsync(admin, $"/people/{GailId}")).QuerySelector("#person-history")!.TextContent;

        Assert.Contains("reset-sent", history);
        Assert.Contains("name-changed", history);
        Assert.DoesNotContain("suspended", history);
    }

    [Fact]
    public async Task Old_card_suspend_kicks_in_world()
    {
        await using var f = Factory();
        var region = Guid.NewGuid();
        f.People.Online[GailId] = region;
        var admin = await SignedInAsync(f, "admin");
        using (await PostAsync(admin, "/people?handler=Suspend", new() { ["name"] = "Gail Guest" }, "/people?name=Gail+Guest")) { }
        Assert.Contains($"Kick {region} Gail Guest", f.Grid.Snapshot());
    }

    [Fact]
    public async Task Icons_have_accessible_names()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        var page = await PageAsync(admin, "/people?q=gail");
        var icons = page.QuerySelectorAll($"tr[data-id='{GailId}'] [data-task]");
        Assert.Equal(6, icons.Length);
        Assert.All(icons, i => Assert.False(string.IsNullOrWhiteSpace(i.GetAttribute("aria-label"))));
        Assert.All(icons, i => Assert.StartsWith("/img/people-icons.svg#", i.QuerySelector("use")!.GetAttribute("href")));
    }
}
