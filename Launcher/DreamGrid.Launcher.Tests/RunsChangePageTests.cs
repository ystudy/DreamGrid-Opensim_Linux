using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RunsChangePageTests : IAsyncLifetime
{
    private static readonly Guid HomeId = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid BeachId = Guid.Parse("70000000-0000-0000-0000-000000000002");
    private static readonly Guid SeaId = Guid.Parse("70000000-0000-0000-0000-000000000003");
    private static readonly Guid IsleId = Guid.Parse("70000000-0000-0000-0000-000000000004");
    private static readonly Guid SunsetId = Guid.Parse("70000000-0000-0000-0000-000000000005");

    private readonly LauncherFactory _f = new(s => s with
    {
        SetupToken = null,
        Setup = s.Setup with { OwnerCreated = true, Finished = true },
        // Home and Beach share one program (BulletSim), Sea and Isle another; Sunset Shore has its own.
        Programs =
        [
            new ProgramSettings("Home", 8004, 8011, "BulletSim"),
            new ProgramSettings("Sea", 8005, 8012, "ubODE"),
            new ProgramSettings("Sunset", 8006, 8013, "ubODE"),
        ],
        Regions =
        [
            new RegionSettings("Home", "Home", HomeId, UdpPort: 8004),
            new RegionSettings("Home", "Beach", BeachId, X: 1001, UdpPort: 8007),
            new RegionSettings("Sea", "Sea", SeaId, X: 1020, UdpPort: 8005),
            new RegionSettings("Sea", "Isle", IsleId, X: 1021, UdpPort: 8008),
            new RegionSettings("Sunset", "Sunset Shore", SunsetId, X: 1040, UdpPort: 8006),
        ],
    })
    {
        Before = TestAuth.Apply,
    };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _f.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/__test/signin", Ct)).Dispose();
        return client;
    }

    private static async Task<AngleSharp.Html.Dom.IHtmlDocument> GetAsync(HttpClient client, Guid id) =>
        new HtmlParser().ParseDocument(await client.GetStringAsync($"/regions/region/{id}/runs", Ct));

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, params (string Key, string Value)[] fields)
    {
        var token = (await GetAsync(client, id)).QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
            .Append(new("__RequestVerificationToken", token)).ToList();
        return await client.PostAsync($"/regions/region/{id}/runs", new FormUrlEncodedContent(form), Ct);
    }

    private async Task<bool> WaitForCallAsync(string call)
    {
        for (var i = 0; i < 100; i++)
        {
            if (_f.Grid.Snapshot().Contains(call)) return true;
            await Task.Delay(50);
        }
        return false;
    }

    [Fact]
    public async Task Runs_change_names_both_groups()
    {
        using var client = await SignedInAsync();

        var page = await GetAsync(client, SeaId);
        var home = page.QuerySelector($"[data-group='{HomeId}']")!;
        Assert.Equal("Runs with Home, Beach (2 regions)", home.QuerySelector(".group-label")!.TextContent.Trim());
        Assert.Contains("I understand Home, Beach and Isle will restart", home.QuerySelector("label.confirm")!.TextContent);
        // On its own restarts only the group it leaves.
        Assert.Contains("I understand Isle will restart", page.QuerySelector("[data-group=own] label.confirm")!.TextContent);
        Assert.Null(page.QuerySelector($"[data-group='{SeaId}']")); // its own group is not a choice

        using var unconfirmed = await PostAsync(client, SeaId, ("Group", HomeId.ToString()));
        Assert.Equal(HttpStatusCode.OK, unconfirmed.StatusCode);
        Assert.Contains("Tick the box to confirm that Home, Beach and Isle will restart.", await unconfirmed.Content.ReadAsStringAsync(Ct));
        await Task.Delay(200, Ct);
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("ChangeRunsWith", StringComparison.Ordinal));

        using var confirmed = await PostAsync(client, SeaId, ("Group", HomeId.ToString()), ("Confirm", HomeId.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
        Assert.Equal($"/regions/region/{SeaId}", confirmed.Headers.Location!.OriginalString);
        Assert.True(await WaitForCallAsync($"ChangeRunsWith {SeaId} Home"));
        var panel = new HtmlParser().ParseDocument(await client.GetStringAsync($"/regions/region/{SeaId}", Ct));
        Assert.Equal("Changing how Sea runs. This can take a few minutes.", panel.QuerySelector(".notice")!.TextContent.Trim());
    }

    [Fact]
    public async Task Runs_change_of_running_region_names_both_groups()
    {
        using var client = await SignedInAsync();

        var page = await GetAsync(client, SeaId);
        Assert.Contains("I understand Home, Beach and Isle will restart", page.QuerySelector($"[data-group='{HomeId}'] label.confirm")!.TextContent);

        _f.Grid.ProgramStates["Home"] = Core.Processes.ProcessState.Stopped; // a stopped group doesn't restart; Isle still does
        page = await GetAsync(client, SeaId);
        Assert.Contains("I understand Isle will restart", page.QuerySelector($"[data-group='{HomeId}'] label.confirm")!.TextContent);
        Assert.DoesNotContain("Home", page.QuerySelector($"[data-group='{HomeId}'] label.confirm")!.TextContent);
    }

    [Fact]
    public async Task Runs_change_of_stopped_region_asks_nothing()
    {
        using var client = await SignedInAsync();
        _f.Grid.ProgramStates["Sunset"] = Core.Processes.ProcessState.Stopped;
        _f.Grid.ProgramStates["Sea"] = Core.Processes.ProcessState.Failed; // its regions show as failed, not running

        // Sharing with running Home: the region stays down and loads at Home's next start, so nothing restarts.
        Assert.Null((await GetAsync(client, SunsetId)).QuerySelector("input[name=Confirm]"));
        Assert.Null((await GetAsync(client, SeaId)).QuerySelector("input[name=Confirm]"));

        using var response = await PostAsync(client, SunsetId, ("Group", HomeId.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await WaitForCallAsync($"ChangeRunsWith {SunsetId} Home"));
    }

    [Fact]
    public async Task Runs_change_names_physics_difference()
    {
        using var client = await SignedInAsync();

        var page = await GetAsync(client, SeaId);

        Assert.Contains("Physics will change from ubODE to BulletSim.",
            page.QuerySelector($"[data-group='{HomeId}']")!.TextContent);
        Assert.DoesNotContain("Physics", page.QuerySelector($"[data-group='{SunsetId}']")!.TextContent);
        Assert.DoesNotContain("Physics", page.QuerySelector("[data-group=own]")!.TextContent);
    }

    [Fact]
    public async Task Runs_page_lists_every_program_setting_that_changes()
    {
        // Sea runs ubODE, Map Good, AboveNormal; Home's group runs BulletSim, Simple, Normal: three lines, one each.
        _f.Grid.UpdateSettings(l => l with
        {
            Programs = [.. l.Programs.Select(p => p.Instance == "Sea" ? p with { Options = new ProgramOptions(Map: MapQuality.Good, Priority: CpuPriority.AboveNormal) } : p)],
        });
        using var client = await SignedInAsync();

        var page = await GetAsync(client, SeaId);

        var lines = page.QuerySelectorAll($"[data-group='{HomeId}'] .group-note").Select(n => n.TextContent.Trim()).ToList();
        Assert.Equal(
        [
            "Physics will change from ubODE to BulletSim.",
            "The map will be drawn Simple instead of Good.",
            "Its priority will change from AboveNormal to Normal.",
        ], lines);
        // Sunset runs ubODE like Sea: its note has no physics line.
        var sunset = page.QuerySelectorAll($"[data-group='{SunsetId}'] .group-note").Select(n => n.TextContent.Trim()).ToList();
        Assert.Equal(2, sunset.Count);
        Assert.DoesNotContain(sunset, l => l.StartsWith("Physics", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runs_change_names_the_grid_default_when_the_regions_program_entry_is_missing()
    {
        var lostId = Guid.Parse("70000000-0000-0000-0000-000000000006");
        _f.Grid.UpdateSettings(l => l with { Regions = [.. l.Regions, new RegionSettings("Lost", "Lost", lostId, X: 1060, UdpPort: 8009)] });
        using var client = await SignedInAsync();

        var page = await GetAsync(client, lostId);

        var text = page.QuerySelector($"[data-group='{HomeId}']")!.TextContent;
        Assert.Contains($"Physics will change from {_f.Grid.CurrentSettings().DefaultPhysics} to BulletSim.", text);
    }

    [Fact]
    public async Task Runs_page_never_says_program()
    {
        using var client = await SignedInAsync();

        foreach (var id in new[] { SeaId, SunsetId })
        {
            var text = (await GetAsync(client, id)).Body!.TextContent;
            Assert.DoesNotContain("program", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("instance", text, StringComparison.OrdinalIgnoreCase);
        }
        using var unpicked = await PostAsync(client, SeaId);
        var error = await unpicked.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Pick how the region should run.", error);
        Assert.DoesNotContain("program", new HtmlParser().ParseDocument(error).Body!.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runs_page_of_a_region_on_its_own_offers_only_groups()
    {
        using var client = await SignedInAsync();

        var page = await GetAsync(client, SunsetId);

        Assert.Contains("Sunset Shore runs on its own.", page.QuerySelector(".runs-now")!.TextContent);
        Assert.Null(page.QuerySelector("[data-group=own]"));
        Assert.NotNull(page.QuerySelector($"[data-group='{HomeId}']"));
        Assert.NotNull(page.QuerySelector($"[data-group='{SeaId}']"));
    }

    [Fact]
    public async Task Runs_on_its_own_posts_and_runs()
    {
        using var client = await SignedInAsync();

        var page = await GetAsync(client, SeaId);
        Assert.Contains("Sea runs with Isle.", page.QuerySelector(".runs-now")!.TextContent);
        using var response = await PostAsync(client, SeaId, ("Group", "own"), ("Confirm", "own"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await WaitForCallAsync($"ChangeRunsWith {SeaId} own"));
    }

    [Theory]
    [InlineData("Sea")] // its own group
    [InlineData("Home")] // the group it would join
    public async Task Runs_change_is_refused_while_a_job_runs_in_either_group(string busy)
    {
        _f.Grid.Busy[busy] = $"Saving {busy} as an OAR";
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, SeaId, ("Group", HomeId.ToString()), ("Confirm", HomeId.ToString()));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/regions/region/{SeaId}", response.Headers.Location!.OriginalString);
        var panel = new HtmlParser().ParseDocument(await client.GetStringAsync($"/regions/region/{SeaId}", Ct));
        Assert.Equal($"Saving {busy} as an OAR is still running. Try again when it finishes.", panel.QuerySelector(".notice")!.TextContent.Trim());
        await Task.Delay(200, Ct);
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("ChangeRunsWith", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runs_page_for_an_unknown_region_is_not_found()
    {
        using var client = await SignedInAsync();

        using var response = await client.GetAsync($"/regions/region/{Guid.NewGuid()}/runs", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
