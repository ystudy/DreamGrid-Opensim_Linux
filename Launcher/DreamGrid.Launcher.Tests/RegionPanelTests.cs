using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RegionPanelTests : IAsyncLifetime
{
    private static readonly Guid HomeId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid BeachId = Guid.Parse("40000000-0000-0000-0000-000000000002");
    private static readonly Guid SunsetId = Guid.Parse("40000000-0000-0000-0000-000000000003");
    private static readonly Guid IsleId = Guid.Parse("40000000-0000-0000-0000-000000000004");

    private readonly LauncherFactory _f = new(s => s with
    {
        SetupToken = null,
        Setup = s.Setup with { OwnerCreated = true, Finished = true },
        // Home and Beach share one program (side by side); Sunset Shore has its own, next to Beach; Isle is parked.
        Programs =
        [
            new ProgramSettings("Home", 8004, 8011, "ubODE"),
            new ProgramSettings("Sunset", 8005, 8012, "ubODE"),
            new ProgramSettings("Isle", 8006, 8013, "ubODE"),
        ],
        Regions =
        [
            new RegionSettings("Home", "Home", HomeId, UdpPort: 8004),
            new RegionSettings("Home", "Beach", BeachId, X: 1001, UdpPort: 8004),
            new RegionSettings("Sunset", "Sunset Shore", SunsetId, X: 1002, Size: 2, UdpPort: 8005),
            new RegionSettings("Isle", "Isle", IsleId, X: 1010, Y: 1010, UdpPort: 8006, Parked: true),
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

    private static async Task<IHtmlDocument> ParseAsync(HttpResponseMessage response) =>
        new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync(Ct));

    private async Task<IHtmlDocument> PageAsync(Guid id, string query = "")
    {
        using var client = await SignedInAsync();
        return new HtmlParser().ParseDocument(await client.GetStringAsync($"/regions/region/{id}{query}", Ct));
    }

    private static string Text(IHtmlDocument doc) => doc.Body!.TextContent;

    private static IEnumerable<string> MenuLabels(IHtmlDocument doc) =>
        doc.QuerySelectorAll("details.power-menu button").Select(b => b.TextContent.Trim());

    private static async Task<HttpResponseMessage> PostPowerAsync(HttpClient client, Guid id, string action, bool confirmed = false)
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync($"/regions/region/{id}", Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["action"] = action };
        if (confirmed) form["confirmed"] = "true";
        return await client.PostAsync($"/regions/region/{id}?handler=power", new FormUrlEncodedContent(form), Ct);
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
    public async Task Panel_shows_state_size_position_and_neighbours()
    {
        var doc = await PageAsync(SunsetId);

        var text = Text(doc);
        Assert.Contains("Sunset Shore", text);
        Assert.Contains("Running", doc.QuerySelector(".badge")!.TextContent);
        Assert.Contains("1002, 1000", text);
        Assert.Contains("2×2", text);
        // Beach is at 1001 and Sunset Shore touches its east edge, so Beach lies to the west.
        Assert.Contains("Next to Beach (west)", text);
        Assert.Contains("Runs on its own", text);
        Assert.Contains("Region settings are coming next.", text);
        Assert.Equal($"/regions/region/{SunsetId}/move", doc.QuerySelector("a[href$='/move']")!.GetAttribute("href"));
        Assert.Equal($"/regions/region/{SunsetId}/runs", doc.QuerySelector("a[href$='/runs']")!.GetAttribute("href"));
    }

    [Fact]
    public async Task Panel_shows_oar_items_for_a_running_region()
    {
        var doc = await PageAsync(SunsetId);

        var save = doc.QuerySelector("details.oar-menu button[value=Save]")!;
        Assert.Contains("Save as OAR", save.TextContent);
        Assert.False(save.HasAttribute("disabled"));
        var import = doc.QuerySelector($"a[href='/regions/region/{SunsetId}/import']")!;
        Assert.Contains("Import OAR", import.TextContent);
        Assert.Null(doc.QuerySelector("button[value=MakeLand]"));
        Assert.DoesNotContain("Start it first", Text(doc));
    }

    [Fact]
    public async Task Panel_disables_oar_items_when_stopped()
    {
        _f.Grid.RegionState = ProcessState.Stopped;

        var doc = await PageAsync(SunsetId);

        Assert.Contains("Start it first; an OAR needs it running.", Text(doc));
        Assert.True(doc.QuerySelector("details.oar-menu button[value=Save]")!.HasAttribute("disabled"));
        Assert.Null(doc.QuerySelector("a[href$='/import']"));
    }

    [Fact]
    public async Task Panel_shows_make_land_again_only_when_needed()
    {
        var plain = await PageAsync(SunsetId);
        var s = _f.Store.LoadOrCreate();
        _f.Store.Save(s with { Regions = s.Regions.Select(r => r.Uuid == SunsetId ? r with { NeedsContent = true } : r).ToList() });

        var needed = await PageAsync(SunsetId);

        Assert.Null(plain.QuerySelector("button[value=MakeLand]"));
        Assert.Contains("Make the land again", needed.QuerySelector("details.oar-menu button[value=MakeLand]")!.TextContent);
        Assert.Contains("Its land didn't finish loading when it was added.", Text(needed));
        Assert.Null((await PageAsync(HomeId)).QuerySelector("button[value=MakeLand]"));
    }

    [Fact]
    public async Task Panel_says_what_job_is_running_and_links_to_it()
    {
        _f.Grid.Busy["Home"] = "Saving Home as an OAR";

        var doc = await PageAsync(BeachId);

        Assert.Contains("Saving Home as an OAR is running.", Text(doc));
        Assert.NotNull(doc.QuerySelector($"a[href='/regions/region/{BeachId}/job']"));
        Assert.True(doc.QuerySelector("details.oar-menu button[value=Save]")!.HasAttribute("disabled"));
    }

    [Fact]
    public async Task Panel_lists_who_is_here()
    {
        _f.OnlineUsers.Result = new OnlineList([new OnlineAvatar("Ada Lovelace", BeachId), new OnlineAvatar("Bob Smith", SunsetId)], true);

        var beach = Text(await PageAsync(BeachId));
        var home = Text(await PageAsync(HomeId));
        _f.OnlineUsers.Result = OnlineList.Unavailable;
        var unavailable = Text(await PageAsync(BeachId));

        Assert.Contains("Ada Lovelace", beach);
        Assert.DoesNotContain("Bob Smith", beach);
        Assert.Contains("Nobody is here right now.", home);
        Assert.Contains("Who's here isn't available right now.", unavailable);
    }

    [Fact]
    public async Task Panel_says_runs_with_others()
    {
        var text = Text(await PageAsync(HomeId));

        Assert.Contains("Runs with Beach", text);

        var three = new LauncherFactory(s => s with
        {
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true },
            Programs = [new ProgramSettings("Home", 8004, 8011, "ubODE")],
            Regions =
            [
                new RegionSettings("Home", "Home", HomeId, UdpPort: 8004),
                new RegionSettings("Home", "Beach", BeachId, X: 1001, UdpPort: 8004),
                new RegionSettings("Home", "Sunset Shore", SunsetId, X: 1002, UdpPort: 8004),
            ],
        })
        { Before = TestAuth.Apply };
        await using (three)
        {
            using var client = three.CreateClient(new() { AllowAutoRedirect = false });
            (await client.GetAsync("/__test/signin", Ct)).Dispose();
            var shared = await client.GetStringAsync($"/regions/region/{SunsetId}", Ct);
            Assert.Contains("Runs with Home, Beach", shared);
        }
    }

    [Fact]
    public async Task Running_region_shows_time_and_memory_shared_by_its_group()
    {
        var running = new ComponentStatus("region", ProcessState.Running, 1, TimeSpan.FromMinutes(12), 612L << 20, null);
        var status = _f.Grid.GetStatus();
        _f.Grid.Status = status with { Programs = status.Programs.Select(p => p with { Process = running }).ToList() };

        var doc = await PageAsync(HomeId);

        var facts = doc.QuerySelector(".region-facts")!.TextContent;
        Assert.Contains("12 min, shared by Home and Beach", facts);
        Assert.Contains("610 MB, shared by Home and Beach", facts);
    }

    [Fact]
    public async Task Running_region_menu_has_restart_stop_park()
    {
        Assert.Equal(["Restart", "Stop", "Park"], MenuLabels(await PageAsync(SunsetId)));
    }

    [Fact]
    public async Task Stopped_region_menu_has_start_and_park()
    {
        _f.Grid.RegionState = ProcessState.Stopped;

        Assert.Equal(["Start", "Park"], MenuLabels(await PageAsync(SunsetId)));
    }

    [Fact]
    public async Task Parked_region_menu_has_unpark_items()
    {
        var doc = await PageAsync(IsleId);

        Assert.Contains("Parked", doc.QuerySelector(".badge")!.TextContent);
        Assert.Equal(["Unpark and start", "Unpark"], MenuLabels(doc));
    }

    [Fact]
    public async Task Failed_region_shows_message_and_log()
    {
        var status = _f.Grid.GetStatus();
        var failed = new ComponentStatus("region", ProcessState.Failed, null, null, null, "The region stopped right after it started.");
        _f.Grid.Status = status with
        {
            Programs = status.Programs.Select(p => p with { Process = failed }).ToList(),
            Regions = status.Regions.Select(r => r with { State = RegionState.Failed }).ToList(),
        };
        Directory.CreateDirectory(_f.Paths.Logs);
        await File.WriteAllLinesAsync(Path.Combine(_f.Paths.Logs, "region-Sunset.log"),
            ["2026-10-27 10:00:00,000 INFO  Starting up", "2026-10-27 10:00:01,000 ERROR Could not bind the UDP port"], Ct);

        var doc = await PageAsync(SunsetId);

        var text = Text(doc);
        Assert.Contains("Failed", doc.QuerySelector(".badge")!.TextContent);
        Assert.Contains("The region stopped right after it started.", text);
        Assert.Contains("Could not bind the UDP port", doc.QuerySelector(".region-failure pre")!.TextContent);
        Assert.Equal(["Start", "Park"], MenuLabels(doc));
    }

    [Fact]
    public async Task Power_on_region_of_its_own_runs_at_once()
    {
        using var client = await SignedInAsync();

        using var response = await PostPowerAsync(client, SunsetId, "Restart");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/regions/region/{SunsetId}", response.Headers.Location!.OriginalString);
        Assert.True(await WaitForCallAsync($"RegionPower Restart {SunsetId}"));
        var page = await client.GetStringAsync($"/regions/region/{SunsetId}", Ct);
        Assert.Contains("Restarting Sunset Shore.", page);
    }

    [Fact]
    public async Task Power_in_shared_group_asks_first()
    {
        using var client = await SignedInAsync();

        using var response = await PostPowerAsync(client, BeachId, "Stop");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await ParseAsync(response);
        Assert.Contains("Stopping Beach also restarts Home, and drops everyone in it. Go ahead?", Text(doc));
        Assert.Equal("Yes, stop", doc.QuerySelector("form.alert button")!.TextContent.Trim());
        Assert.Contains("Cancel", Text(doc));
        await Task.Delay(200, Ct);
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("RegionPower", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Power_confirmed_runs()
    {
        using var client = await SignedInAsync();

        using var response = await PostPowerAsync(client, BeachId, "Stop", confirmed: true);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await WaitForCallAsync($"RegionPower Stop {BeachId}"));
    }

    [Fact]
    public async Task Unpark_never_asks()
    {
        using var client = await SignedInAsync();

        using var response = await PostPowerAsync(client, IsleId, "Unpark");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await WaitForCallAsync($"RegionPower Unpark {IsleId}"));
    }

    [Fact]
    public async Task Power_in_a_stopped_shared_program_runs_at_once()
    {
        _f.Grid.RegionState = ProcessState.Stopped;
        using var client = await SignedInAsync();

        using var response = await PostPowerAsync(client, BeachId, "Start");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await WaitForCallAsync($"RegionPower Start {BeachId}"));
    }

    [Fact]
    public async Task Partial_has_no_layout()
    {
        var full = await PageAsync(SunsetId);
        var partial = await PageAsync(SunsetId, "?partial=1");

        Assert.NotNull(full.QuerySelector("nav"));
        Assert.NotNull(full.QuerySelector("footer"));
        Assert.Null(partial.QuerySelector("nav"));
        Assert.Null(partial.QuerySelector("footer"));
        Assert.Null(partial.QuerySelector("html > head > title"));
        Assert.NotNull(partial.QuerySelector("details.power-menu"));
    }

    [Fact]
    public async Task Unknown_region_is_404()
    {
        using var client = await SignedInAsync();

        using var response = await client.GetAsync($"/regions/region/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deleted_region_is_404()
    {
        var deleted = Guid.NewGuid();
        _f.Store.Save(_f.Store.LoadOrCreate() with
        {
            Regions = [.. _f.Store.LoadOrCreate().Regions,
                new RegionSettings("Gone", "Gone", deleted, X: 1500, DeletedAt: DateTimeOffset.UtcNow)],
        });
        using var client = await SignedInAsync();

        using var response = await client.GetAsync($"/regions/region/{deleted}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Panel_never_says_program()
    {
        foreach (var id in new[] { HomeId, SunsetId, IsleId })
        {
            var text = Text(await PageAsync(id)).ToLowerInvariant();
            Assert.DoesNotContain("program", text);
            Assert.DoesNotContain("instance", text);
        }
    }
}
