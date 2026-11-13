using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Groups;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Groups;
using Xunit;
using static GridGate.Launcher.Tests.SplashPictureApiTests;

namespace GridGate.Launcher.Tests;

/// <summary>The admin's Groups pages at /groups and /groups/group (spec 2026-11-02-groups-admin-design).</summary>
public sealed class GroupsPagesTests
{
    private const string Visitor = "6a0f3c2e-1d55-4c1b-9b9e-0d8a4f3b2c11;https://grid.example:8002/;Hope Visitor";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> fields, string tokenFrom)
    {
        var page = Parser.ParseDocument(await client.GetStringAsync(tokenFrom, Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token }), Ct);
    }

    private static string GroupUrl(Guid id) => "/groups/group?id=" + id;

    private static async Task<IHtmlDocument> PostOnGroupAsync(HttpClient client, Guid id, string handler, Dictionary<string, string> fields)
    {
        using var r = await PostAsync(client, $"{GroupUrl(id)}&handler={handler}", fields, GroupUrl(id));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string? Error(IHtmlDocument page) => page.QuerySelector(".alert-danger")?.TextContent.Trim();
    private static string? Notice(IHtmlDocument page) => page.QuerySelector(".alert-success")?.TextContent.Trim();

    [Theory]
    [InlineData("supervisor")]
    [InlineData(null)]
    public async Task Supervisor_and_anonymous_cannot_open_or_post(string? role)
    {
        await using var f = Factory();
        var g = f.Groups.AddGroup("Builders", TestAuth.OwnerId);
        var client = role is null ? Client(f) : await StaffAsync(f, role);

        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/groups", Ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync(GroupUrl(g), Ct)).StatusCode);
        if (role is not null)
        {
            using var r = await PostAsync(client, $"{GroupUrl(g)}&handler=Delete", new() { ["confirm"] = "true" }, "/people");
            Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        }
        Assert.True(f.Groups.Exists(g));
    }

    [Fact]
    public async Task Not_there_without_a_guest_side()
    {
        await using var f = Factory(settings: s => s with
        {
            Plan = OwnerPlan.RegionServer,
            Join = new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006),
            Hypergrid = false,
        });
        var client = await StaffAsync(f, "owner");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/groups", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("owner", true)]
    [InlineData("admin", true)]
    [InlineData("supervisor", false)]
    public async Task Menu_has_groups_for_admins_only(string role, bool shown)
    {
        await using var f = Factory();
        var client = await StaffAsync(f, role);
        var page = await PageAsync(client, "/profile/two-factor");
        Assert.Equal(shown, page.QuerySelectorAll("header nav a").Any(a => a.TextContent.Trim() == "Groups"));
    }

    [Fact]
    public async Task List_shows_groups_founder_count_and_default_badge()
    {
        await using var f = Factory();
        var builders = f.Groups.AddGroup("Builders", TestAuth.OwnerId, open: true);
        f.Groups.Join(builders, Visitor);
        f.Groups.AddGroup("Gardeners", TestAuth.AdminId);
        var client = await StaffAsync(f, "admin");
        await PostOnGroupAsync(client, builders, "Default", []);

        var page = await PageAsync(client, "/groups");

        var rows = page.QuerySelectorAll("#groups-list tbody tr").ToList();
        Assert.Equal(["Builders", "Gardeners"], rows.Select(r => r.QuerySelector("a")!.TextContent.Trim()));
        Assert.Equal(GroupUrl(builders), rows[0].QuerySelector("a")!.GetAttribute("href"));
        Assert.Contains("Test Owner", rows[0].TextContent);
        Assert.Equal("2", rows[0].QuerySelector(".members")!.TextContent.Trim());
        Assert.Equal("Yes", rows[0].QuerySelector(".open")!.TextContent.Trim());
        Assert.NotNull(rows[0].QuerySelector(".default-badge"));
        Assert.Null(rows[1].QuerySelector(".default-badge"));
    }

    [Fact]
    public async Task No_groups_says_they_are_made_in_the_viewer()
    {
        await using var f = Factory();
        var page = await PageAsync(await StaffAsync(f, "admin"), "/groups");
        Assert.Contains("No groups yet. Groups are made in the viewer.", page.Body!.TextContent);
    }

    [Fact]
    public async Task Filter_matches_part_of_the_name()
    {
        await using var f = Factory();
        f.Groups.AddGroup("Builders", TestAuth.OwnerId);
        f.Groups.AddGroup("Gardeners", TestAuth.OwnerId);
        var page = await PageAsync(await StaffAsync(f, "admin"), "/groups?q=GARD");
        Assert.Equal(["Gardeners"], page.QuerySelectorAll("#groups-list tbody tr a").Select(a => a.TextContent.Trim()));
        Assert.Equal("GARD", page.QuerySelector("input[name=q]")!.GetAttribute("value"));
    }

    [Fact]
    public async Task Database_down_says_so()
    {
        await using var f = Factory();
        f.Groups.FailAfter(0);
        var page = await PageAsync(await StaffAsync(f, "admin"), "/groups");
        Assert.Contains(GroupsAdmin.DatabaseDown, page.Body!.TextContent);
    }

    [Fact]
    public async Task Save_renames()
    {
        await using var f = Factory();
        var g = f.Groups.AddGroup("Builders", TestAuth.OwnerId);
        var page = await PostOnGroupAsync(await StaffAsync(f, "admin"), g, "Save", new() { ["name"] = "Master Builders", ["charter"] = "We build." });
        Assert.StartsWith("Saved.", Notice(page));
        var detail = (await f.Groups.GetAsync(g, Ct))!;
        Assert.Equal("Master Builders", detail.Name);
        Assert.Equal("We build.", detail.Charter);
    }

    [Fact]
    public async Task Save_with_a_taken_name_says_taken_and_keeps_typed_text()
    {
        await using var f = Factory();
        f.Groups.AddGroup("Builders", TestAuth.OwnerId);
        var g = f.Groups.AddGroup("Gardeners", TestAuth.OwnerId);
        var page = await PostOnGroupAsync(await StaffAsync(f, "admin"), g, "Save", new() { ["name"] = "builders", ["charter"] = "Typed charter" });
        Assert.Equal("Another group is already called builders.", Error(page));
        Assert.Equal("builders", page.QuerySelector("input[name=name]")!.GetAttribute("value"));
        Assert.Equal("Typed charter", page.QuerySelector("textarea[name=charter]")!.TextContent);
    }

    [Fact]
    public async Task Add_by_name_adds()
    {
        await using var f = Factory();
        var g = f.Groups.AddGroup("Builders", TestAuth.OwnerId);
        var page = await PostOnGroupAsync(await StaffAsync(f, "admin"), g, "Add", new() { ["avatar"] = "Test Guest" });
        Assert.StartsWith("Added Test Guest to Builders.", Notice(page));
        Assert.True(f.Groups.IsMember(g, TestAuth.GuestId));
        Assert.Contains(page.QuerySelectorAll("#group-members tbody tr"), r => r.TextContent.Contains("Test Guest"));
    }

    [Fact]
    public async Task Add_an_unknown_name_says_no_avatar()
    {
        await using var f = Factory();
        var g = f.Groups.AddGroup("Builders", TestAuth.OwnerId);
        var page = await PostOnGroupAsync(await StaffAsync(f, "admin"), g, "Add", new() { ["avatar"] = "Nobody Here" });
        Assert.Equal("No avatar called Nobody Here.", Error(page));
        Assert.Equal("Nobody Here", page.QuerySelector("input[name=avatar]")!.GetAttribute("value"));
    }

    [Fact]
    public async Task Remove_a_visitor_by_full_id()
    {
        await using var f = Factory();
        var g = f.Groups.AddGroup("Builders", TestAuth.OwnerId);
        f.Groups.Join(g, Visitor);
        var client = await StaffAsync(f, "admin");
        var before = await PageAsync(client, GroupUrl(g));
        var row = before.QuerySelectorAll("#group-members tbody tr").Single(r => r.TextContent.Contains("Hope Visitor (visitor)"));
        Assert.Equal(Visitor, row.QuerySelector("input[name=member]")!.GetAttribute("value"));

        var page = await PostOnGroupAsync(client, g, "Remove", new() { ["member"] = Visitor });

        Assert.StartsWith("Removed Hope Visitor (visitor) from Builders.", Notice(page));
        Assert.DoesNotContain(await f.Groups.MemberIdsAsync(g, Ct), id => id == Visitor);
    }

    [Fact]
    public async Task Remove_last_owner_warns_then_confirm_removes()
    {
        await using var f = Factory();
        var g = f.Groups.AddGroup("Builders", TestAuth.OwnerId);
        var client = await StaffAsync(f, "admin");

        var page = await PostOnGroupAsync(client, g, "Remove", new() { ["member"] = TestAuth.OwnerId.ToString() });

        Assert.StartsWith("Test Owner is the group's last owner.", Error(page));
        Assert.True(f.Groups.IsMember(g, TestAuth.OwnerId));
        var confirm = page.QuerySelector("#confirm-remove")!;
        Assert.Equal("true", confirm.QuerySelector("input[name=confirm]")!.GetAttribute("value"));
        Assert.Equal(TestAuth.OwnerId.ToString(), confirm.QuerySelector("input[name=member]")!.GetAttribute("value"));

        page = await PostOnGroupAsync(client, g, "Remove", new() { ["member"] = TestAuth.OwnerId.ToString(), ["confirm"] = "true" });
        Assert.StartsWith("Removed Test Owner", Notice(page));
        Assert.False(f.Groups.IsMember(g, TestAuth.OwnerId));
    }

    [Fact]
    public async Task Delete_without_the_box_ticked_does_nothing()
    {
        await using var f = Factory();
        var g = f.Groups.AddGroup("Builders", TestAuth.OwnerId);
        var page = await PostOnGroupAsync(await StaffAsync(f, "admin"), g, "Delete", []);
        Assert.Equal("Tick the box to say you want to delete this group.", Error(page));
        Assert.True(f.Groups.Exists(g));
    }

    [Fact]
    public async Task Delete_with_the_box_redirects_to_the_list_with_a_notice()
    {
        await using var f = Factory();
        var g = f.Groups.AddGroup("Builders", TestAuth.OwnerId);
        var client = await StaffAsync(f, "admin");
        var form = (await PageAsync(client, GroupUrl(g))).QuerySelector("#group-delete")!;
        Assert.Contains("Delete Builders and remove its 1 members", form.TextContent);

        using var r = await PostAsync(client, $"{GroupUrl(g)}&handler=Delete", new() { ["confirm"] = "true" }, GroupUrl(g));

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/groups", r.Headers.Location!.OriginalString);
        Assert.False(f.Groups.Exists(g));
        var list = await PageAsync(client, "/groups");
        Assert.StartsWith("Deleted Builders.", Notice(list));
    }

    [Fact]
    public async Task Make_default_then_add_everyone_reports_counts()
    {
        await using var f = Factory();
        var g = f.Groups.AddGroup("Builders", TestAuth.OwnerId);
        var client = await StaffAsync(f, "admin");

        var page = await PostOnGroupAsync(client, g, "Default", []);
        Assert.Equal("Builders is now the default group. New residents join it when their sign-up is done.", Notice(page));
        Assert.NotNull(page.QuerySelector("#add-everyone"));

        page = await PostOnGroupAsync(client, g, "Everyone", []);
        // The test staff and guest: the owner is in it already.
        Assert.StartsWith("Added 3 residents to Builders; skipped 1", Notice(page));
        Assert.True(f.Groups.IsMember(g, TestAuth.GuestId));

        page = await PostOnGroupAsync(client, g, "NoDefault", []);
        Assert.StartsWith("There is no default group now.", Notice(page));
        Assert.Null(page.QuerySelector("#add-everyone"));
    }

    [Fact]
    public async Task Unknown_group_is_404()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "admin");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(GroupUrl(Guid.NewGuid()), Ct)).StatusCode);
    }

    [Fact]
    public async Task Posts_without_a_token_are_refused()
    {
        await using var f = Factory();
        var g = f.Groups.AddGroup("Builders", TestAuth.OwnerId);
        var client = await StaffAsync(f, "admin");
        using var r = await client.PostAsync($"{GroupUrl(g)}&handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["confirm"] = "true" }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.True(f.Groups.Exists(g));
    }
}
