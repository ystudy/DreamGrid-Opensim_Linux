using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Task 6 of plan 6c: who may sign in to a Connected Server, how the dashboard shows the other grid, and Advanced settings.</summary>
public sealed class ConnectedServerPagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid OwnerId = Guid.Parse("30000000-0000-0000-0000-000000000001");

    private static ConnectedGrid Osgrid(string? gatekeeper = "http://gatekeeper.osgrid.org:8002/") =>
        new("osgrid", "OSGrid", "http://login.osgrid.org/", "http://login.osgrid.org/", "http://grid.osgrid.org/", gatekeeper);

    private static LauncherSettings Settings(OwnerPlan plan) => TestSettings.Create() with
    {
        Plan = plan,
        Connected = plan == OwnerPlan.Connected ? Osgrid() : null,
        Setup = new SetupState(WorldNamed: true, OwnerCreated: true, RegionCreated: true, Finished: true,
            Owner: new OwnerInfo("Estate", "Owner", OwnerId)),
    };

    private static RobustAccount Account(int level, Guid? id = null) => new(id ?? Guid.NewGuid(), "A", "B", level, "");

    // ---- AdminRule ----

    [Fact]
    public void Level_0_owner_may_administer_on_a_connected_server() =>
        Assert.True(AdminRule.MayAdminister(Settings(OwnerPlan.Connected), Account(0, OwnerId)));

    [Fact]
    public void Level_250_non_owner_may_not_administer_on_a_connected_server() =>
        Assert.False(AdminRule.MayAdminister(Settings(OwnerPlan.Connected), Account(250)));

    [Fact]
    public void Level_250_owner_may_administer_on_a_connected_server() =>
        Assert.True(AdminRule.MayAdminister(Settings(OwnerPlan.Connected), Account(250, OwnerId)));

    [Fact]
    public void Nobody_may_administer_a_connected_server_when_no_owner_is_named() =>
        Assert.False(AdminRule.MayAdminister(Settings(OwnerPlan.Connected) with { Setup = new SetupState() }, Account(250)));

    [Fact]
    public void Region_server_and_local_rules_are_unchanged()
    {
        var regionServer = Settings(OwnerPlan.RegionServer);
        Assert.True(AdminRule.MayAdminister(regionServer, Account(0, OwnerId)));
        Assert.True(AdminRule.MayAdminister(regionServer, Account(250)));
        Assert.False(AdminRule.MayAdminister(Settings(OwnerPlan.Local), Account(0, OwnerId)));
        Assert.True(AdminRule.MayAdminister(Settings(OwnerPlan.Local), Account(250)));
    }

    // ---- helpers ----

    private static LauncherFactory Factory() => new(s => s with
    {
        SetupToken = null,
        GridName = "OSGrid",
        Plan = OwnerPlan.Connected,
        Connected = Osgrid(),
        Hypergrid = false,
        Setup = new SetupState(WorldNamed: true, OwnerCreated: true, RegionCreated: true, Finished: true,
            Owner: new OwnerInfo("Estate", "Owner", OwnerId)),
    })
    {
        Before = TestAuth.Apply,
    };

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/__test/signin", Ct)).Dispose();
        return client;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> fields)
    {
        var page = await client.GetStringAsync(url.Split('?')[0], Ct);
        var token = new HtmlParser().ParseDocument(page).QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token }), Ct);
    }

    // ---- sign-in ----

    [Fact]
    public async Task Level_0_estate_owner_signs_in_and_a_level_250_stranger_is_refused()
    {
        await using var f = Factory();
        f.Robust.Add("Estate", "Owner", 0, "pass-word1", OwnerId).Add("Some", "Admin", 250, "pass-word1");
        var client = f.CreateClient(new() { AllowAutoRedirect = false });

        using var ok = await PostAsync(client, "/login", new() { ["Name"] = "Estate Owner", ["Password"] = "pass-word1" });
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Contains(ok.Headers.GetValues("Set-Cookie"), c => c.StartsWith("dg-auth=", StringComparison.Ordinal));

        var other = f.CreateClient(new() { AllowAutoRedirect = false });
        using var refused = await PostAsync(other, "/login", new() { ["Name"] = "Some Admin", ["Password"] = "pass-word1" });
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        var page = new HtmlParser().ParseDocument(await refused.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Name or password is wrong, or this avatar is not an administrator.", page.Body!.TextContent);
        Assert.DoesNotContain(refused.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            c => c.StartsWith("dg-auth=", StringComparison.Ordinal));
    }

    // ---- status and dashboard ----

    private static GridStatus ConnectedStatus(ProcessState otherGrid)
    {
        ComponentStatus C(string n, ProcessState s) => new(n, s, null, null, null, null);
        return new GridStatus(C("database", ProcessState.Running), C("robust", otherGrid), [], [], false, "http://login.osgrid.org/",
            RunsRobust: false, OtherGridAddress: "http://login.osgrid.org/");
    }

    [Fact]
    public async Task Status_json_labels_the_row_with_the_grids_name_and_address()
    {
        await using var f = Factory();
        f.Grid.Status = ConnectedStatus(ProcessState.Running);
        using var client = f.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/status", Ct));
        var robust = doc.RootElement.GetProperty("grid").GetProperty("robust");
        Assert.Equal("OSGrid", robust.GetProperty("label").GetString());
        Assert.Equal("http://login.osgrid.org/", robust.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Status_problem_names_the_other_grid_when_it_does_not_answer()
    {
        await using var f = Factory();
        f.Grid.Status = ConnectedStatus(ProcessState.Failed);
        using var client = f.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/status", Ct));
        Assert.Equal("OSGrid isn't answering.", doc.RootElement.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task Dashboard_has_no_restart_grid_service_button_and_no_connected_server_in_coming_soon()
    {
        await using var f = Factory();
        f.Grid.Status = ConnectedStatus(ProcessState.Running);
        using var client = await SignedInAsync(f);

        var html = await client.GetStringAsync("/", Ct);
        Assert.DoesNotContain("restart-robust", html);
        Assert.DoesNotContain("Restart the grid service", html);
        Assert.Contains("OSGrid", html);
        Assert.Contains("restart-database", html);
        var doc = new HtmlParser().ParseDocument(html);
        Assert.DoesNotContain(doc.QuerySelectorAll(".coming-soon li"), li => li.TextContent.Contains("Connected Server"));
    }

    // ---- Advanced ----

    [Fact]
    public async Task Plan_page_names_the_grid_and_offers_no_change()
    {
        await using var f = Factory();
        using var client = await SignedInAsync(f);

        var doc = new HtmlParser().ParseDocument(await client.GetStringAsync("/advanced/plan", Ct));
        var text = doc.QuerySelector("main")!.TextContent;
        Assert.Contains("OSGrid", text);
        Assert.Contains("http://login.osgrid.org/", text);
        Assert.Empty(doc.QuerySelectorAll("form button[name=To]"));
        Assert.Null(doc.QuerySelector("form#hypergrid"));
        Assert.DoesNotContain(doc.QuerySelectorAll("a"), a => a.GetAttribute("href") == "/advanced/join");
        var refused = doc.QuerySelectorAll(".plan-refused li").Select(li => li.TextContent).ToList();
        Assert.Equal(4, refused.Count);
        Assert.All(refused, t => Assert.Contains('✘', t));
        Assert.Contains("Hypergrid is on for OSGrid; that grid decides.", text);
    }

    [Fact]
    public async Task Network_page_has_the_hypergrid_line_and_no_hypergrid_input()
    {
        await using var f = Factory();
        using var client = await SignedInAsync(f);

        var doc = new HtmlParser().ParseDocument(await client.GetStringAsync("/advanced/network", Ct));
        Assert.Contains("Hypergrid is on for OSGrid; that grid decides.", doc.QuerySelector("main")!.TextContent);
        Assert.Null(doc.QuerySelector("[name=Hypergrid]"));
    }
}
