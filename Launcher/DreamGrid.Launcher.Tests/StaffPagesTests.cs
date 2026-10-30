using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The owner's Staff page (/staff) and the People page (/people) with suspend, on the real host.</summary>
public sealed class StaffPagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid GuestId = Guid.Parse("0b000000-0000-0000-0000-00000000000b");

    private static LauncherFactory Factory(Func<LauncherSettings, LauncherSettings>? more = null)
    {
        var f = new LauncherFactory(s => (more ?? (x => x))(s with
        {
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        }))
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Gail", "Guest", 50, "pass-word1", GuestId);
        return f;
    }

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        using var r = await client.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    /// <summary>Posts a form with the antiforgery token the layout's sign-out form carries (from the page itself, or <paramref name="tokenFrom"/>).</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> fields, string? tokenFrom = null)
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync(tokenFrom ?? url.Split('?')[0], Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token }), Ct);
    }

    private static List<JsonElement> Audit(LauncherFactory f, string action)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).Where(e => e.GetProperty("action").GetString() == action).ToList();
    }

    private static string MainText(string html) => new HtmlParser().ParseDocument(html).QuerySelector("main")!.TextContent;

    private static int LevelOf(LauncherFactory f, string first, string last) => f.Robust.All.Single(a => a.FirstName == first && a.LastName == last).UserLevel;

    private static string[] SetLevelCalls(LauncherFactory f) => f.Grid.Snapshot().Where(c => c.StartsWith("SetUserLevel ", StringComparison.Ordinal)).ToArray();

    // ---- /staff ----

    [Fact]
    public async Task Owner_promotes_guest_to_supervisor()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");

        using var r = await PostAsync(owner, "/staff?handler=Change", new() { ["name"] = "Gail Guest", ["change"] = "MakeSupervisor" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(["SetUserLevel Gail Guest 100"], SetLevelCalls(f));
        Assert.Equal(100, LevelOf(f, "Gail", "Guest"));
        var entry = Assert.Single(Audit(f, "level-changed"));
        Assert.Equal("ok", entry.GetProperty("result").GetString());
        Assert.Equal("owner", entry.GetProperty("role").GetString());
        Assert.Contains("Gail Guest", entry.GetProperty("target").GetString());
        var html = await r.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Gail Guest is now a supervisor.", MainText(html));
        Assert.Contains("Gail Guest", new HtmlParser().ParseDocument(html).QuerySelector("#staff-people")!.TextContent); // listed now
    }

    [Fact]
    public async Task Owner_makes_an_admin_and_uses_the_admin_level_setting()
    {
        await using var f = Factory(s => s with { Security = s.Security with { AdminLevel = 250, SupervisorLevel = 120 } });
        var owner = await SignedInAsync(f, "owner");

        using var r = await PostAsync(owner, "/staff?handler=Change", new() { ["name"] = "Gail Guest", ["change"] = "MakeAdmin" });

        Assert.Equal(["SetUserLevel Gail Guest 250"], SetLevelCalls(f));
        Assert.Contains("Gail Guest is now an admin.", MainText(await r.Content.ReadAsStringAsync(Ct)));
    }

    [Fact]
    public async Task Owner_makes_a_supervisor_a_guest_and_that_ends_their_sessions()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");
        var supervisor = await SignedInAsync(f, "supervisor");
        using (var ok = await supervisor.GetAsync("/people", Ct)) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using var r = await PostAsync(owner, "/staff?handler=Change", new() { ["name"] = "Test Supervisor", ["change"] = "MakeGuest" });

        Assert.Equal(["SetUserLevel Test Supervisor 0"], SetLevelCalls(f));
        using var after = await supervisor.GetAsync("/people", Ct);
        Assert.NotEqual(HttpStatusCode.OK, after.StatusCode); // signed out at once
    }

    [Fact]
    public async Task Demoting_an_admin_to_supervisor_ends_their_sessions_but_a_promotion_does_not()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");
        var admin = await SignedInAsync(f, "admin");
        var supervisor = await SignedInAsync(f, "supervisor");

        using (await PostAsync(owner, "/staff?handler=Change", new() { ["name"] = "Test Supervisor", ["change"] = "MakeAdmin" }))
            Assert.Equal(200, LevelOf(f, "Test", "Supervisor"));
        using (var still = await supervisor.GetAsync("/people", Ct)) Assert.Equal(HttpStatusCode.OK, still.StatusCode);

        using (await PostAsync(owner, "/staff?handler=Change", new() { ["name"] = "Test Admin", ["change"] = "MakeSupervisor" }))
            Assert.Equal(100, LevelOf(f, "Test", "Admin"));
        using var gone = await admin.GetAsync("/people", Ct);
        Assert.NotEqual(HttpStatusCode.OK, gone.StatusCode);
    }

    [Fact]
    public async Task Owner_cannot_be_changed()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");

        using var r = await PostAsync(owner, "/staff?handler=Change", new() { ["name"] = "Test Owner", ["change"] = "MakeGuest" });

        Assert.Contains("The grid owner can't be changed here.", MainText(await r.Content.ReadAsStringAsync(Ct)));
        Assert.Empty(SetLevelCalls(f));
        Assert.Equal("refused", Assert.Single(Audit(f, "level-changed")).GetProperty("result").GetString());
    }

    [Fact]
    public async Task Add_by_name_says_when_there_is_no_such_avatar()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");

        using var r = await PostAsync(owner, "/staff?handler=Change", new() { ["name"] = "Nobody Here", ["change"] = "MakeSupervisor" });

        Assert.Contains("No avatar called Nobody Here.", MainText(await r.Content.ReadAsStringAsync(Ct)));
        Assert.Empty(SetLevelCalls(f));
        Assert.Empty(Audit(f, "level-changed"));
    }

    [Fact]
    public async Task Add_by_name_needs_a_first_and_last_name()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");

        using var r = await PostAsync(owner, "/staff?handler=Change", new() { ["name"] = "Gail", ["change"] = "MakeSupervisor" });

        Assert.Contains("Type the avatar's first and last name.", MainText(await r.Content.ReadAsStringAsync(Ct)));
        Assert.Empty(SetLevelCalls(f));
    }

    [Fact]
    public async Task A_change_that_is_not_a_staff_change_does_nothing()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");

        using var r = await PostAsync(owner, "/staff?handler=Change", new() { ["name"] = "Gail Guest", ["change"] = "Suspend" });

        Assert.Empty(SetLevelCalls(f));
        Assert.Equal(50, LevelOf(f, "Gail", "Guest"));
        Assert.Empty(Audit(f, "level-changed"));
    }

    [Fact]
    public async Task Staff_page_lists_everyone_at_or_above_the_supervisor_level_with_their_role()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");

        var html = await owner.GetStringAsync("/staff", Ct);

        var doc = new HtmlParser().ParseDocument(html);
        var rows = doc.QuerySelectorAll("#staff-people tbody tr").Select(r => r.TextContent).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Contains("Test Owner") && r.Contains("Owner") && r.Contains("200"));
        Assert.Contains(rows, r => r.Contains("Test Admin") && r.Contains("Admin"));
        Assert.Contains(rows, r => r.Contains("Test Supervisor") && r.Contains("Supervisor") && r.Contains("100"));
        Assert.DoesNotContain("Gail Guest", MainText(html));
        // The owner's row has no buttons; the others have the two changes that are not what they already are, and Make guest.
        Assert.Empty(doc.QuerySelectorAll("tr[data-person='Test Owner'] button"));
        Assert.Equal(["Make supervisor", "Make guest"], doc.QuerySelectorAll("tr[data-person='Test Admin'] button").Select(b => b.TextContent.Trim()));
        Assert.Equal(["Make admin", "Make guest"], doc.QuerySelectorAll("tr[data-person='Test Supervisor'] button").Select(b => b.TextContent.Trim()));
    }

    [Fact]
    public async Task Staff_page_says_to_start_the_world_when_the_database_is_down()
    {
        await using var f = Factory();
        f.Staff.Down = true;
        var owner = await SignedInAsync(f, "owner");

        var html = await owner.GetStringAsync("/staff", Ct);

        Assert.Contains("Start your world to see and change staff.", MainText(html));
        Assert.Null(new HtmlParser().ParseDocument(html).QuerySelector("#staff-people"));
    }

    [Fact]
    public async Task A_change_while_robust_is_not_running_says_so_and_audits_nothing_as_done()
    {
        await using var f = Factory();
        f.Grid.SetUserLevelError = new HttpRequestException("Connection refused");
        var owner = await SignedInAsync(f, "owner");

        using var r = await PostAsync(owner, "/staff?handler=Change", new() { ["name"] = "Gail Guest", ["change"] = "MakeAdmin" });

        Assert.Contains("Your world's sign-in service isn't running yet. Start your world, then try again.", MainText(await r.Content.ReadAsStringAsync(Ct)));
        Assert.Empty(Audit(f, "level-changed"));
        Assert.Equal(50, LevelOf(f, "Gail", "Guest"));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("supervisor")]
    public async Task Admin_gets_403_on_staff_page(string role)
    {
        await using var f = Factory();
        var client = await SignedInAsync(f, role);

        using var get = await client.GetAsync("/staff", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        using var post = await client.PostAsync("/staff?handler=Change",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "Gail Guest", ["change"] = "MakeSupervisor" }), Ct);
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        Assert.Empty(SetLevelCalls(f));
    }

    [Fact]
    public async Task Anonymous_is_sent_to_sign_in()
    {
        await using var f = Factory();
        var anon = f.CreateClient(new() { AllowAutoRedirect = false });

        foreach (var url in new[] { "/staff", "/people" })
        {
            using var r = await anon.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        }
    }

    // ---- /people ----

    [Fact]
    public async Task Supervisor_suspends_guest_and_unsuspend_restores_level()
    {
        await using var f = Factory();
        var supervisor = await SignedInAsync(f, "supervisor");

        using (var suspend = await PostAsync(supervisor, "/people?handler=Suspend", new() { ["name"] = "Gail Guest" }))
        {
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
            Assert.Contains("Gail Guest is suspended.", MainText(await suspend.Content.ReadAsStringAsync(Ct)));
        }
        Assert.Equal(["SetUserLevel Gail Guest -1"], SetLevelCalls(f));
        Assert.Equal(-1, LevelOf(f, "Gail", "Guest"));
        var suspended = Assert.Single(Audit(f, "suspended"));
        Assert.Equal("ok", suspended.GetProperty("result").GetString());
        Assert.Equal("supervisor", suspended.GetProperty("role").GetString());

        using (var unsuspend = await PostAsync(supervisor, "/people?handler=Unsuspend", new() { ["name"] = "Gail Guest" }))
            Assert.Contains("Gail Guest is not suspended any more.", MainText(await unsuspend.Content.ReadAsStringAsync(Ct)));
        Assert.Equal(50, LevelOf(f, "Gail", "Guest"));
        Assert.Equal("SetUserLevel Gail Guest 50", SetLevelCalls(f)[^1]);
        Assert.Equal("ok", Assert.Single(Audit(f, "unsuspended")).GetProperty("result").GetString());

        // The remembered level is gone: suspended some other way and unsuspended again, the person gets 0, not a stale 50.
        f.Robust.SetLevel("Gail", "Guest", -1);
        using (await PostAsync(supervisor, "/people?handler=Unsuspend", new() { ["name"] = "Gail Guest" })) { }
        Assert.Equal(0, LevelOf(f, "Gail", "Guest"));
    }

    [Fact]
    public async Task Supervisor_cannot_suspend_admin_by_post()
    {
        await using var f = Factory();
        var supervisor = await SignedInAsync(f, "supervisor");

        using var r = await PostAsync(supervisor, "/people?handler=Suspend", new() { ["name"] = "Test Admin" });

        Assert.Contains("Supervisors can only suspend or unsuspend guests.", MainText(await r.Content.ReadAsStringAsync(Ct)));
        Assert.Empty(SetLevelCalls(f));
        Assert.Equal(200, LevelOf(f, "Test", "Admin"));
        var entry = Assert.Single(Audit(f, "suspended"));
        Assert.Equal("refused", entry.GetProperty("result").GetString());
        Assert.Contains("Test Admin", entry.GetProperty("target").GetString());
    }

    [Fact]
    public async Task Supervisor_cannot_suspend_another_supervisor_or_the_owner()
    {
        await using var f = Factory();
        f.Robust.Add("Sue", "Super", 100, "pass-word1");
        var supervisor = await SignedInAsync(f, "supervisor");

        using (await PostAsync(supervisor, "/people?handler=Suspend", new() { ["name"] = "Sue Super" })) { }
        using (var owner = await PostAsync(supervisor, "/people?handler=Suspend", new() { ["name"] = "Test Owner" }))
            Assert.Contains("The grid owner can't be changed here.", MainText(await owner.Content.ReadAsStringAsync(Ct)));

        Assert.Empty(SetLevelCalls(f));
        Assert.Equal(2, Audit(f, "suspended").Count(e => e.GetProperty("result").GetString() == "refused"));
    }

    [Fact]
    public async Task Admin_suspends_a_supervisor_but_not_an_admin()
    {
        await using var f = Factory();
        f.Robust.Add("Other", "Admin", 200, "pass-word1");
        var admin = await SignedInAsync(f, "admin");

        using (await PostAsync(admin, "/people?handler=Suspend", new() { ["name"] = "Test Supervisor" })) { }
        using (var refused = await PostAsync(admin, "/people?handler=Suspend", new() { ["name"] = "Other Admin" }))
            Assert.Contains("Admins can only suspend or unsuspend supervisors and guests.", MainText(await refused.Content.ReadAsStringAsync(Ct)));

        Assert.Equal(-1, LevelOf(f, "Test", "Supervisor"));
        Assert.Equal(200, LevelOf(f, "Other", "Admin"));
    }

    [Fact]
    public async Task Nobody_can_suspend_themselves()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");

        using var r = await PostAsync(admin, "/people?handler=Suspend", new() { ["name"] = "Test Admin" });

        Assert.Contains("You can't suspend yourself.", MainText(await r.Content.ReadAsStringAsync(Ct)));
        Assert.Empty(SetLevelCalls(f));
        Assert.Equal("refused", Assert.Single(Audit(f, "suspended")).GetProperty("result").GetString());
    }

    [Fact]
    public async Task A_suspended_admin_is_still_an_admin_when_unsuspending()
    {
        // Suspended, the admin's level is -1 and reads as a guest; a supervisor must not be able to give them 200 back.
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");
        var supervisor = await SignedInAsync(f, "supervisor");
        using (await PostAsync(owner, "/people?handler=Suspend", new() { ["name"] = "Test Admin" })) { }
        Assert.Equal(-1, LevelOf(f, "Test", "Admin"));

        using var r = await PostAsync(supervisor, "/people?handler=Unsuspend", new() { ["name"] = "Test Admin" });

        Assert.Contains("Supervisors can only suspend or unsuspend guests.", MainText(await r.Content.ReadAsStringAsync(Ct)));
        Assert.Equal(-1, LevelOf(f, "Test", "Admin"));
        using (await PostAsync(owner, "/people?handler=Unsuspend", new() { ["name"] = "Test Admin" })) { }
        Assert.Equal(200, LevelOf(f, "Test", "Admin")); // the owner can
    }

    [Fact]
    public async Task Suspend_ends_target_sessions()
    {
        await using var f = Factory();
        var admin = await SignedInAsync(f, "admin");
        var supervisor = await SignedInAsync(f, "supervisor");
        var sessions = f.Services.GetRequiredService<StaffSessions>();
        Assert.NotEmpty(sessions.List(TestAuth.SupervisorId));

        using (await PostAsync(admin, "/people?handler=Suspend", new() { ["name"] = "Test Supervisor" })) { }

        Assert.Empty(sessions.List(TestAuth.SupervisorId));
        using var after = await supervisor.GetAsync("/people", Ct);
        Assert.NotEqual(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEmpty(sessions.List(TestAuth.AdminId)); // the person who suspended stays signed in
    }

    [Fact]
    public async Task Find_shows_name_level_role_email_and_created()
    {
        await using var f = Factory();
        var supervisor = await SignedInAsync(f, "supervisor");

        var html = await supervisor.GetStringAsync("/people?name=Test+Admin", Ct);

        var doc = new HtmlParser().ParseDocument(html);
        var card = doc.QuerySelector("#person")!.TextContent;
        Assert.Contains("Test Admin", card);
        Assert.Contains("200", card);
        Assert.Contains("Admin", card);
        Assert.Contains("Email", card);
        Assert.Contains("Created", card);
        // A supervisor sees no Suspend button on an admin.
        Assert.Null(doc.QuerySelector("#person button"));
    }

    [Fact]
    public async Task Find_offers_suspend_for_a_guest_and_unsuspend_once_suspended()
    {
        await using var f = Factory();
        var supervisor = await SignedInAsync(f, "supervisor");

        var guest = new HtmlParser().ParseDocument(await supervisor.GetStringAsync("/people?name=Gail+Guest", Ct));
        Assert.Equal(["Suspend"], guest.QuerySelectorAll("#person button").Select(b => b.TextContent.Trim()));

        f.Robust.SetLevel("Gail", "Guest", -1);
        var suspended = new HtmlParser().ParseDocument(await supervisor.GetStringAsync("/people?name=Gail+Guest", Ct));
        Assert.Contains("Suspended", suspended.QuerySelector("#person")!.TextContent);
        Assert.Equal(["Unsuspend"], suspended.QuerySelectorAll("#person button").Select(b => b.TextContent.Trim()));
    }

    [Fact]
    public async Task Find_says_when_there_is_no_such_avatar_or_robust_is_down()
    {
        await using var f = Factory();
        var supervisor = await SignedInAsync(f, "supervisor");

        Assert.Contains("No avatar called Nobody Here.", MainText(await supervisor.GetStringAsync("/people?name=Nobody+Here", Ct)));

        f.Robust.FailureReply = true;
        Assert.Contains("Your world's sign-in service isn't running yet. Start your world, then try again.",
            MainText(await supervisor.GetStringAsync("/people?name=Gail+Guest", Ct)));
    }

    // ---- menus and plans ----

    [Fact]
    public async Task Menus_show_staff_to_the_owner_and_people_to_all_staff()
    {
        await using var f = Factory();
        var owner = await SignedInAsync(f, "owner");
        var admin = await SignedInAsync(f, "admin");
        var supervisor = await SignedInAsync(f, "supervisor");

        var ownerMenu = await owner.GetStringAsync("/profile/sessions", Ct);
        Assert.Contains("href=\"/staff\"", ownerMenu);
        Assert.Contains("href=\"/people\"", ownerMenu);
        var adminMenu = await admin.GetStringAsync("/profile/sessions", Ct);
        Assert.DoesNotContain("href=\"/staff\"", adminMenu);
        Assert.Contains("href=\"/people\"", adminMenu);
        var supervisorMenu = await supervisor.GetStringAsync("/profile/sessions", Ct);
        Assert.DoesNotContain("href=\"/staff\"", supervisorMenu);
        Assert.Contains("href=\"/people\"", supervisorMenu);
        Assert.DoesNotContain("href=\"/regions\"", supervisorMenu);
        Assert.Contains("href=\"/profile/sessions\"", supervisorMenu);
    }

    [Theory]
    [InlineData(OwnerPlan.RegionServer)]
    [InlineData(OwnerPlan.Connected)]
    public async Task Pages_404_on_region_server_and_connected(OwnerPlan plan)
    {
        await using var f = Factory(s => s with
        {
            Plan = plan,
            Join = plan == OwnerPlan.RegionServer
                ? new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006) : null,
            Connected = plan == OwnerPlan.Connected
                ? new ConnectedGrid("osgrid", "OSGrid", "http://login.osgrid.org/", "http://login.osgrid.org/", "http://grid.osgrid.org/", null) : null,
            Hypergrid = false,
        });
        var owner = await SignedInAsync(f, "owner");

        foreach (var url in new[] { "/staff", "/people", "/people?name=Gail+Guest" })
        {
            using var r = await owner.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }
        foreach (var url in new[] { "/staff?handler=Change", "/people?handler=Suspend" })
        {
            using var r = await PostAsync(owner, url, new() { ["name"] = "Gail Guest", ["change"] = "MakeAdmin" }, tokenFrom: "/profile/sessions");
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }
        Assert.Empty(SetLevelCalls(f));
        var menu = await owner.GetStringAsync("/profile/sessions", Ct);
        Assert.DoesNotContain("href=\"/staff\"", menu);
        Assert.DoesNotContain("href=\"/people\"", menu);
    }
}
