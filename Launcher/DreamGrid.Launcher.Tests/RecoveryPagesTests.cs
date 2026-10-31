using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Setup;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Choosing the recovery rules: Setup's "What kind of grid is this?" step and the owner's Advanced → Password reset page.</summary>
public sealed class RecoveryPagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LauncherSettings Local(Func<LauncherSettings, LauncherSettings>? more = null)
    {
        var s = TestSettings.Create() with { Plan = OwnerPlan.Local };
        return more is null ? s : more(s);
    }

    private static LauncherFactory Factory(OwnerPlan plan = OwnerPlan.Local, bool finished = true, RecoverySettings? rules = null) =>
        new(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = plan,
            Setup = new SetupState(WorldNamed: true, OwnerCreated: true, Finished: finished,
                Owner: new OwnerInfo("Test", "Owner", TestAuth.OwnerId), NetworkSet: true),
            Recovery = rules,
            Security = s.Security with { AdminLevel = 250 },
        })
        {
            Before = TestAuth.Apply,
        };

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role = "owner")
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/__test/signin?role=" + role, Ct)).Dispose();
        return client;
    }

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> fields)
    {
        var page = await PageAsync(client, url.Split('?')[0]);
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token }), Ct);
    }

    [Fact]
    public void Guest_side_plans_ask_the_question_after_the_avatar()
    {
        foreach (var plan in new[] { OwnerPlan.Local, OwnerPlan.SimplePublic, OwnerPlan.GridAsset })
        {
            var paths = SetupSteps.For(Local(s => s with { Plan = plan })).Select(st => st.Path).ToList();
            Assert.Equal(paths.IndexOf("/setup/avatar") + 1, paths.IndexOf("/setup/security"));
        }
        foreach (var plan in new[] { OwnerPlan.RegionServer, OwnerPlan.Connected })
            Assert.DoesNotContain("/setup/security", SetupSteps.For(Local(s => s with { Plan = plan })).Select(st => st.Path));
    }

    [Fact]
    public void Finished_install_skips_the_step()
    {
        var s = Local(s => s with { Setup = s.Setup with { Finished = true }, Recovery = null });
        Assert.True(SetupSteps.For(s).Single(st => st.Path == "/setup/security").Done(s));
        var fresh = Local(s => s with { Setup = s.Setup with { OwnerCreated = true } });
        Assert.False(SetupSteps.For(fresh).Single(st => st.Path == "/setup/security").Done(fresh));
    }

    [Fact]
    public async Task Setup_step_offers_three_kinds_with_home_lab_first()
    {
        await using var f = Factory(finished: false);
        var owner = await SignedInAsync(f);

        using var setup = await owner.GetAsync("/setup", Ct);
        Assert.Equal("/setup/security", setup.Headers.Location?.OriginalString);
        var page = await PageAsync(owner, "/setup/security");
        Assert.Equal(["HomeLab", "SchoolOrClub", "PublicGrid"], page.QuerySelectorAll("input[name=Kind]").Select(i => i.GetAttribute("value")!));
        Assert.True(page.QuerySelector("input[name=Kind][value=HomeLab]")!.HasAttribute("checked"));
        Assert.Contains("What kind of grid is this?", page.QuerySelector("h1")!.TextContent);
    }

    [Fact]
    public async Task Saving_public_grid_sets_rules_and_switch()
    {
        await using var f = Factory(finished: false);
        var owner = await SignedInAsync(f);

        using var r = await PostAsync(owner, "/setup/security", new() { ["Kind"] = "PublicGrid" });

        Assert.Equal("/setup", r.Headers.Location?.OriginalString);
        var s = f.Store.LoadOrCreate();
        Assert.Equal(Recovery.Level(GridKind.PublicGrid).Rules, s.Recovery);
        Assert.True(s.Security.RequireTwoFactorForStaff);
        Assert.Equal(250, s.Security.AdminLevel);
    }

    [Fact]
    public async Task Email_not_ready_line_shown()
    {
        await using var f = Factory(finished: false);
        var owner = await SignedInAsync(f);
        Assert.NotNull((await PageAsync(owner, "/setup/security")).QuerySelector("#email-first"));

        await using var g = Factory(rules: Recovery.Level(GridKind.HomeLab).Rules);
        Assert.NotNull((await PageAsync(await SignedInAsync(g), "/advanced/password-reset")).QuerySelector("#email-first a[href='/security/email']"));
    }

    [Fact]
    public async Task Advanced_page_owner_only()
    {
        await using var f = Factory(rules: Recovery.Level(GridKind.HomeLab).Rules);
        using var admin = await (await SignedInAsync(f, "admin")).GetAsync("/advanced/password-reset", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        Assert.DoesNotContain("/advanced/password-reset", await (await SignedInAsync(f, "admin")).GetStringAsync("/advanced", Ct));

        var owner = await SignedInAsync(f);
        await PageAsync(owner, "/advanced/password-reset");
        Assert.Contains("/advanced/password-reset", await owner.GetStringAsync("/advanced", Ct));
    }

    [Fact]
    public async Task Advanced_page_404_on_region_server()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        using var r = await (await SignedInAsync(f)).GetAsync("/advanced/password-reset", Ct);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Not_chosen_says_it_is_available()
    {
        await using var f = Factory();
        var page = await PageAsync(await SignedInAsync(f), "/advanced/password-reset");
        Assert.Equal("Password reset is available. Choose a level to turn it on.", page.QuerySelector("#recovery-unset")?.TextContent.Trim());
        Assert.Equal("Off", page.QuerySelector("select[name=Residents] option[selected]")?.GetAttribute("value"));
    }

    [Fact]
    public async Task Saving_rules_keeps_other_security_settings()
    {
        await using var f = Factory(rules: Recovery.Level(GridKind.HomeLab).Rules);
        var owner = await SignedInAsync(f);

        using var r = await PostAsync(owner, "/advanced/password-reset?handler=Save", new()
        {
            ["Residents"] = "Off", ["Staff"] = "EmailLinkAndCode", ["Owner"] = "EmailLink", ["RequireTwoFactor"] = "true",
        });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var s = f.Store.LoadOrCreate();
        Assert.Equal(new RecoverySettings(RecoveryRule.Off, RecoveryRule.EmailLinkAndCode, RecoveryRule.EmailLink), s.Recovery);
        Assert.True(s.Security.RequireTwoFactorForStaff);
        Assert.Equal(250, s.Security.AdminLevel);
    }

    [Fact]
    public async Task Use_a_level_applies_the_table()
    {
        await using var f = Factory(rules: Recovery.Level(GridKind.PublicGrid).Rules);
        var owner = await SignedInAsync(f);

        using var r = await PostAsync(owner, "/advanced/password-reset?handler=Level", new() { ["Kind"] = "SchoolOrClub" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var s = f.Store.LoadOrCreate();
        Assert.Equal(Recovery.Level(GridKind.SchoolOrClub).Rules, s.Recovery);
        Assert.False(s.Security.RequireTwoFactorForStaff);
    }

    [Fact]
    public async Task A_bad_value_changes_nothing()
    {
        await using var f = Factory(rules: Recovery.Level(GridKind.HomeLab).Rules);
        var owner = await SignedInAsync(f);

        using var r = await PostAsync(owner, "/advanced/password-reset?handler=Save", new()
        {
            ["Residents"] = "Sometimes", ["Staff"] = "EmailLink", ["Owner"] = "EmailLink",
        });

        Assert.Equal(Recovery.Level(GridKind.HomeLab).Rules, f.Store.LoadOrCreate().Recovery);
    }
}
