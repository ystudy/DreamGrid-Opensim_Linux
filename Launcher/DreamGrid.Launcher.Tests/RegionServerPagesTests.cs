using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Task 6 of plan 6b: who may sign in to a Region Server, how the dashboard shows the Grid Asset Server, and Advanced settings.</summary>
public sealed class RegionServerPagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid OwnerId = Guid.Parse("30000000-0000-0000-0000-000000000001");

    private static JoinedGrid Avalon(string name = "Avalon") =>
        new("192.168.1.5", 8002, 8003, name, "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006);

    private static LauncherSettings Settings(OwnerPlan plan, Guid? owner = null) => TestSettings.Create() with
    {
        Plan = plan,
        Join = plan == OwnerPlan.RegionServer ? Avalon() : null,
        Setup = new SetupState(WorldNamed: true, OwnerCreated: true, RegionCreated: true, Finished: true,
            Owner: new OwnerInfo("Estate", "Owner", owner ?? OwnerId)),
    };

    private static RobustAccount Account(int level, Guid? id = null) => new(id ?? Guid.NewGuid(), "A", "B", level, "");

    // ---- AdminRule ----

    [Theory]
    [InlineData(OwnerPlan.Local)]
    [InlineData(OwnerPlan.GridAsset)]
    [InlineData(OwnerPlan.RegionServer)]
    public void Level_200_may_administer_on_any_plan(OwnerPlan plan) =>
        Assert.True(AdminRule.MayAdminister(Settings(plan), Account(200)));

    [Fact]
    public void Level_0_estate_owner_may_administer_on_a_region_server() =>
        Assert.True(AdminRule.MayAdminister(Settings(OwnerPlan.RegionServer), Account(0, OwnerId)));

    [Fact]
    public void Level_0_owner_may_not_administer_on_local() =>
        Assert.False(AdminRule.MayAdminister(Settings(OwnerPlan.Local), Account(0, OwnerId)));

    [Fact]
    public void Level_0_non_owner_may_not_administer_on_a_region_server() =>
        Assert.False(AdminRule.MayAdminister(Settings(OwnerPlan.RegionServer), Account(0)));

    [Fact]
    public void Nobody_below_200_may_administer_when_no_owner_is_named() =>
        Assert.False(AdminRule.MayAdminister(Settings(OwnerPlan.RegionServer) with { Setup = new SetupState() }, Account(0, OwnerId)));

    // ---- helpers ----

    private static LauncherFactory Factory(OwnerPlan plan) => new(s => s with
    {
        SetupToken = null,
        GridName = "Avalon",
        Plan = plan,
        Join = plan == OwnerPlan.RegionServer ? Avalon() : null,
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

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> fields, string? tokenFrom = null)
    {
        var page = await client.GetStringAsync(tokenFrom ?? url.Split('?')[0], Ct);
        var token = new HtmlParser().ParseDocument(page).QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token }), Ct);
    }

    private static string MainText(string html) => new HtmlParser().ParseDocument(html).QuerySelector("main")!.TextContent;

    // ---- sign-in ----

    [Fact]
    public async Task Level_0_estate_owner_signs_in_to_a_region_server()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        f.Robust.Add("Estate", "Owner", 0, "pass-word1", OwnerId).Add("Plain", "Person", 0, "pass-word1");
        var client = f.CreateClient(new() { AllowAutoRedirect = false });

        using var ok = await PostAsync(client, "/login", new() { ["Name"] = "Estate Owner", ["Password"] = "pass-word1" });
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);

        var other = f.CreateClient(new() { AllowAutoRedirect = false });
        using var refused = await PostAsync(other, "/login", new() { ["Name"] = "Plain Person", ["Password"] = "pass-word1" });
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
    }

    [Fact]
    public async Task Level_0_estate_owner_is_refused_on_a_local_grid()
    {
        await using var f = Factory(OwnerPlan.Local);
        f.Robust.Add("Estate", "Owner", 0, "pass-word1", OwnerId);
        var client = f.CreateClient(new() { AllowAutoRedirect = false });

        using var refused = await PostAsync(client, "/login", new() { ["Name"] = "Estate Owner", ["Password"] = "pass-word1" });
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
    }

    // ---- status and dashboard ----

    private static GridStatus RegionServerStatus(ProcessState gridAsset)
    {
        ComponentStatus C(string n, ProcessState s) => new(n, s, null, null, null, null);
        return new GridStatus(C("database", ProcessState.Running), C("robust", gridAsset), [], [], false, "http://192.168.1.5:8002/",
            RunsRobust: false, GridAssetHost: "192.168.1.5");
    }

    [Fact]
    public async Task Status_json_labels_the_grid_asset_server_with_its_address()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        f.Grid.Status = RegionServerStatus(ProcessState.Running);
        using var client = f.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/status", Ct));
        var robust = doc.RootElement.GetProperty("grid").GetProperty("robust");
        Assert.Equal("Grid Asset Server", robust.GetProperty("label").GetString());
        Assert.Equal("192.168.1.5:8002", robust.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Status_problem_names_the_grid_asset_server_when_it_does_not_answer()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        f.Grid.Status = RegionServerStatus(ProcessState.Failed);
        using var client = f.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/status", Ct));
        Assert.Equal("The Grid Asset Server isn't answering.", doc.RootElement.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task Region_server_dashboard_has_no_restart_grid_service_button()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        f.Grid.Status = RegionServerStatus(ProcessState.Running);
        using var client = await SignedInAsync(f);

        var html = await client.GetStringAsync("/", Ct);
        Assert.DoesNotContain("restart-robust", html);
        Assert.DoesNotContain("Restart the grid service", html);
        Assert.Contains("Grid Asset Server", html);
        Assert.Contains("restart-database", html);
    }

    [Fact]
    public async Task Local_dashboard_keeps_the_restart_grid_service_button_and_label()
    {
        await using var f = Factory(OwnerPlan.Local);
        using var client = await SignedInAsync(f);

        var html = await client.GetStringAsync("/", Ct);
        Assert.Contains("restart-robust", html);
        Assert.Contains("Grid service", html);
    }

    [Fact]
    public async Task Coming_soon_does_not_name_the_region_server()
    {
        await using var f = Factory(OwnerPlan.Local);
        using var client = await SignedInAsync(f);

        var doc = new HtmlParser().ParseDocument(await client.GetStringAsync("/", Ct));
        Assert.DoesNotContain(doc.QuerySelectorAll(".coming-soon li"), li => li.TextContent.Contains("Region Server"));
    }

    // ---- Advanced: plan ----

    [Fact]
    public async Task Leaving_a_grid_asset_server_is_refused_while_other_computers_have_regions_joined()
    {
        await using var f = Factory(OwnerPlan.GridAsset);
        f.Grid.JoinedRegions = 2;
        using var client = await SignedInAsync(f);

        using var resp = await PostAsync(client, "/advanced/plan?handler=Change", new() { ["To"] = "local" });
        var text = MainText(await resp.Content.ReadAsStringAsync(Ct));

        Assert.Contains("2 region(s) from other computers are joined to this grid. Move them to another grid or shut those computers down first.", text);
        Assert.Equal(OwnerPlan.GridAsset, f.Store.LoadOrCreate().Plan);
    }

    [Fact]
    public async Task Leaving_a_grid_asset_server_is_refused_when_the_grid_service_does_not_answer()
    {
        await using var f = Factory(OwnerPlan.GridAsset);
        f.Grid.JoinedRegionError = new InvalidOperationException("down");
        using var client = await SignedInAsync(f);

        using var resp = await PostAsync(client, "/advanced/plan?handler=Change", new() { ["To"] = "simple-public" });

        Assert.Contains("Start your world first, so DreamGrid can check whether other computers have joined.", MainText(await resp.Content.ReadAsStringAsync(Ct)));
        Assert.Equal(OwnerPlan.GridAsset, f.Store.LoadOrCreate().Plan);
    }

    [Fact]
    public async Task Leaving_a_grid_asset_server_is_saved_when_nobody_has_joined()
    {
        await using var f = Factory(OwnerPlan.GridAsset);
        f.Grid.JoinedRegions = 0;
        using var client = await SignedInAsync(f);

        using var resp = await PostAsync(client, "/advanced/plan?handler=Change", new() { ["To"] = "local" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(OwnerPlan.Local, f.Store.LoadOrCreate().Plan);
    }

    [Fact]
    public async Task Changes_that_do_not_leave_a_grid_asset_server_do_not_ask_for_the_count()
    {
        await using var f = Factory(OwnerPlan.Local);
        using var client = await SignedInAsync(f);

        (await PostAsync(client, "/advanced/plan?handler=Change", new() { ["To"] = "simple-public" })).Dispose();

        Assert.DoesNotContain("JoinedRegionCount", f.Grid.Snapshot());
    }

    [Fact]
    public async Task Region_server_plan_page_has_no_hypergrid_switch_and_links_to_the_join_page()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        using var client = await SignedInAsync(f);

        var doc = new HtmlParser().ParseDocument(await client.GetStringAsync("/advanced/plan", Ct));
        Assert.Null(doc.QuerySelector("form#hypergrid"));
        Assert.Contains("Hypergrid is off for this grid; the Grid Asset Server decides.", doc.QuerySelector("main")!.TextContent);
        Assert.Contains(doc.QuerySelectorAll("a"), a => a.GetAttribute("href") == "/advanced/join");
    }

    [Fact]
    public async Task Region_server_plan_page_refuses_to_turn_hypergrid_on()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        using var client = await SignedInAsync(f);

        (await PostAsync(client, "/advanced/plan?handler=Hypergrid", new() { ["Hypergrid"] = "true" }, tokenFrom: "/advanced/plan")).Dispose();

        Assert.False(f.Store.LoadOrCreate().Hypergrid);
    }

    // ---- Advanced: network ----

    [Fact]
    public async Task Region_server_network_page_says_the_grid_asset_server_decides_hypergrid()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        using var client = await SignedInAsync(f);

        var doc = new HtmlParser().ParseDocument(await client.GetStringAsync("/advanced/network", Ct));
        Assert.Contains("Hypergrid is off for this grid; the Grid Asset Server decides.", doc.QuerySelector("main")!.TextContent);
        Assert.Null(doc.QuerySelector("[name=Hypergrid]"));
    }

    [Fact]
    public async Task Local_network_page_has_no_hypergrid_line()
    {
        await using var f = Factory(OwnerPlan.Local);
        using var client = await SignedInAsync(f);

        Assert.DoesNotContain("the Grid Asset Server decides", MainText(await client.GetStringAsync("/advanced/network", Ct)));
    }

    [Fact]
    public async Task A_network_post_on_a_region_server_cannot_turn_hypergrid_on()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        using var client = await SignedInAsync(f);
        var s = f.Store.LoadOrCreate();
        var fields = new Dictionary<string, string>
        {
            ["Hostname"] = "", ["WebPort"] = s.WebPort.ToString(), ["RobustPublicPort"] = s.RobustPublicPort.ToString(),
            ["RobustPrivatePort"] = s.RobustPrivatePort.ToString(), ["RegionPort"] = s.RegionPort.ToString(), ["MariaDbPort"] = s.MariaDbPort.ToString(),
            ["RobustConsolePort"] = s.RobustConsolePort.ToString(), ["RegionConsolePort"] = s.RegionConsolePort.ToString(),
            ["Hypergrid"] = "true",
        };

        (await PostAsync(client, "/advanced/network", fields)).Dispose();

        var saved = f.Store.LoadOrCreate();
        Assert.False(saved.Hypergrid);
        Assert.False(PlanCatalog.HypergridOn(saved));
    }

    // ---- Advanced: join ----

    [Fact]
    public async Task Join_page_confirm_saves_the_new_join_and_rewrites_the_config()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        f.Join.Result = Avalon("Elsewhere") with { Host = "192.168.1.9", LoginUri = "http://192.168.1.9:8002/", Hypergrid = true };
        using var client = await SignedInAsync(f);

        using var first = await PostAsync(client, "/advanced/join", new() { ["Address"] = "192.168.1.9" });
        var doc = new HtmlParser().ParseDocument(await first.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Elsewhere", doc.QuerySelector("main")!.TextContent);
        Assert.Equal("192.168.1.5", f.Store.LoadOrCreate().Join!.Host); // nothing saved by the first read

        using var confirm = await PostAsync(client, "/advanced/join?handler=Confirm", new() { ["Address"] = "192.168.1.9" }, tokenFrom: "/advanced/join");
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var s = f.Store.LoadOrCreate();
        Assert.Equal("192.168.1.9", s.Join!.Host);
        Assert.Equal("Elsewhere", s.GridName);
        Assert.Equal(OwnerPlan.RegionServer, s.Plan);
        Assert.True(f.Grid.UpdateWriteConfig.Last());
    }

    [Fact]
    public async Task Join_page_keeps_the_old_join_when_the_new_address_does_not_answer()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        f.Join.Result = null;
        using var client = await SignedInAsync(f);

        using var resp = await PostAsync(client, "/advanced/join?handler=Confirm", new() { ["Address"] = "192.168.1.9" }, tokenFrom: "/advanced/join");

        Assert.Contains("Nothing answered", MainText(await resp.Content.ReadAsStringAsync(Ct)));
        Assert.Equal("192.168.1.5", f.Store.LoadOrCreate().Join!.Host);
    }

    [Theory]
    [InlineData(OwnerPlan.Local)]
    [InlineData(OwnerPlan.GridAsset)]
    public async Task Join_page_redirects_to_advanced_on_another_plan(OwnerPlan plan)
    {
        await using var f = Factory(plan);
        using var client = await SignedInAsync(f);

        using var get = await client.GetAsync("/advanced/join", Ct);
        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        Assert.Equal("/advanced", get.Headers.Location?.OriginalString);
    }
}
