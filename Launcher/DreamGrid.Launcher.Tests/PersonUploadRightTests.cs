using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Guests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The "May upload models" row on a person's page (model import, task 10).</summary>
public sealed class PersonUploadRightTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid GailId = Guid.Parse("0b000000-0000-0000-0000-00000000000b");

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

    private static async Task<IHtmlDocument> PostTaskAsync(HttpClient client, string task)
    {
        var url = $"/people/{GailId}";
        var token = (await PageAsync(client, url)).QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        using var r = await client.PostAsync(url + "?handler=Task", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = GailId.ToString(), ["task"] = task, ["__RequestVerificationToken"] = token,
        }), Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static List<AuditEntry> Audit(LauncherFactory f) =>
        [.. f.Services.GetRequiredService<AuditLog>().Recent(e => e.Entry.Action == "upload-right-changed", 50).Select(e => e.Entry)];

    [Fact]
    public async Task Person_page_shows_upload_state()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");

        var page = await PageAsync(admin, $"/people/{GailId}");
        Assert.Contains("May upload models: No", page.QuerySelector("#person-upload")!.TextContent);
        Assert.NotNull(page.QuerySelector("[data-task=AllowUpload]"));
        Assert.Null(page.QuerySelector("[data-task=StopUpload]"));

        f.Services.GetRequiredService<UploadRights>().Grant(GailId, "Gail Guest");

        page = await PageAsync(admin, $"/people/{GailId}");
        Assert.Contains("May upload models: Yes", page.QuerySelector("#person-upload")!.TextContent);
        Assert.NotNull(page.QuerySelector("[data-task=StopUpload]"));
        Assert.Null(page.QuerySelector("[data-task=AllowUpload]"));
    }

    [Fact]
    public async Task Admin_switches_upload_on_and_it_is_audited()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");

        var page = await PostTaskAsync(admin, "AllowUpload");

        Assert.True(f.Services.GetRequiredService<UploadRights>().Has(GailId));
        Assert.Contains("May upload models: Yes", page.QuerySelector("#person-upload")!.TextContent);
        var e = Assert.Single(Audit(f));
        Assert.Equal(("Gail Guest: off -> on", "ok"), (e.Target, e.Result));
        Assert.Contains("upload-right-changed", page.QuerySelector("#person-history")!.TextContent);

        await PostTaskAsync(admin, "AllowUpload"); // already on: nothing changes, no new line
        Assert.Single(Audit(f));

        await PostTaskAsync(admin, "StopUpload");

        Assert.False(f.Services.GetRequiredService<UploadRights>().Has(GailId));
        Assert.Equal(["Gail Guest: on -> off", "Gail Guest: off -> on"], Audit(f).Select(x => x.Target));

        await PostTaskAsync(admin, "StopUpload"); // already off
        Assert.Equal(2, Audit(f).Count);
    }

    [Fact]
    public async Task Owner_may_switch_upload()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");

        await PostTaskAsync(owner, "AllowUpload");

        Assert.True(f.Services.GetRequiredService<UploadRights>().Has(GailId));
    }

    [Fact]
    public async Task Supervisor_cannot_switch_upload()
    {
        await using var f = Factory();
        var supervisor = await SignedInAsync(f, "supervisor");

        var page = await PostTaskAsync(supervisor, "AllowUpload");

        Assert.False(f.Services.GetRequiredService<UploadRights>().Has(GailId));
        Assert.Contains("Supervisors can only suspend or unsuspend.", page.QuerySelector(".error-box")!.TextContent);
        Assert.Contains("May upload models: No", page.QuerySelector("#person-upload")!.TextContent);
        Assert.Null(page.QuerySelector("[data-task=AllowUpload]"));
        Assert.Null(page.QuerySelector("[data-task=StopUpload]"));
        Assert.Equal("refused", Assert.Single(Audit(f)).Result);
    }
}
