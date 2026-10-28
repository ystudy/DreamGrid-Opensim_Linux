using System.Net;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class MoveRegionPageTests : IAsyncLifetime
{
    private static readonly Guid HomeId = Guid.Parse("60000000-0000-0000-0000-000000000001");
    private static readonly Guid BeachId = Guid.Parse("60000000-0000-0000-0000-000000000002");
    private static readonly Guid SunsetId = Guid.Parse("60000000-0000-0000-0000-000000000003");

    private readonly LauncherFactory _f = new(s => s with
    {
        SetupToken = null,
        Setup = s.Setup with { OwnerCreated = true, Finished = true },
        // Home and Beach share one program; Sunset Shore has its own.
        Programs = [new ProgramSettings("Home", 8004, 8011, "ubODE"), new ProgramSettings("Sunset", 8005, 8012, "ubODE")],
        Regions =
        [
            new RegionSettings("Home", "Home", HomeId, UdpPort: 8004),
            new RegionSettings("Home", "Beach", BeachId, X: 1001, UdpPort: 8004),
            new RegionSettings("Sunset", "Sunset Shore", SunsetId, X: 1010, Size: 2, UdpPort: 8005),
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

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, Dictionary<string, string> fields)
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync($"/regions/region/{id}/move", Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        return await client.PostAsync($"/regions/region/{id}/move", new FormUrlEncodedContent(form), Ct);
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
    public async Task Move_page_prefills_the_spot_and_says_what_it_would_join()
    {
        using var client = await SignedInAsync();

        var doc = new HtmlParser().ParseDocument(await client.GetStringAsync($"/regions/region/{SunsetId}/move?x=1002&y=1000", Ct));

        Assert.Equal("1002", doc.QuerySelector("#X")!.GetAttribute("value"));
        Assert.Equal("1000", doc.QuerySelector("#Y")!.GetAttribute("value"));
        Assert.Contains("This region will join Beach on its east side.", doc.QuerySelector("#placement-text")!.TextContent);
        Assert.Equal($"/regions?move={SunsetId}", doc.QuerySelector("a.choose-on-map")!.GetAttribute("href"));
        Assert.Null(doc.QuerySelector("input[name=Confirm]")); // it runs on its own
    }

    [Fact]
    public async Task Move_in_shared_program_asks_first()
    {
        using var client = await SignedInAsync();

        var page = new HtmlParser().ParseDocument(await client.GetStringAsync($"/regions/region/{BeachId}/move", Ct));
        Assert.Contains("I understand Home will restart", page.QuerySelector("label.confirm")!.TextContent);

        using var response = await PostAsync(client, BeachId, new() { ["X"] = "1500", ["Y"] = "1500" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Tick the box to confirm that Home will restart.", doc.Body!.TextContent);
        await Task.Delay(200, Ct);
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("MoveRegion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Move_posts_and_returns_to_panel()
    {
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, SunsetId, new() { ["X"] = "1500", ["Y"] = "1600" });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/regions/region/{SunsetId}", response.Headers.Location!.OriginalString);
        Assert.True(await WaitForCallAsync($"MoveRegion {SunsetId} 1500 1600"));
        var panel = new HtmlParser().ParseDocument(await client.GetStringAsync($"/regions/region/{SunsetId}", Ct));
        Assert.Equal("Moving Sunset Shore…", panel.QuerySelector(".notice")!.TextContent.Trim());
    }

    [Fact]
    public async Task Move_is_refused_while_a_job_runs_in_its_program()
    {
        _f.Grid.Busy["Home"] = "Saving Home as an OAR";
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, BeachId, new() { ["X"] = "1500", ["Y"] = "1500", ["Confirm"] = "true" });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/regions/region/{BeachId}", response.Headers.Location!.OriginalString);
        var panel = new HtmlParser().ParseDocument(await client.GetStringAsync($"/regions/region/{BeachId}", Ct));
        Assert.Equal("Saving Home as an OAR is still running. Try again when it finishes.", panel.QuerySelector(".notice")!.TextContent.Trim());
        await Task.Delay(200, Ct);
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("MoveRegion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Move_confirmed_in_shared_program_runs()
    {
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, BeachId, new() { ["X"] = "1500", ["Y"] = "1500", ["Confirm"] = "true" });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await WaitForCallAsync($"MoveRegion {BeachId} 1500 1500"));
    }

    [Fact]
    public async Task Move_onto_another_region_or_off_the_map_shows_the_reason()
    {
        using var client = await SignedInAsync();

        using var taken = await PostAsync(client, SunsetId, new() { ["X"] = "1001", ["Y"] = "1000" });
        using var off = await PostAsync(client, SunsetId, new() { ["X"] = "65535", ["Y"] = "0" });

        Assert.Contains("That spot is taken by Beach.", await taken.Content.ReadAsStringAsync(Ct));
        Assert.Contains("That spot is off the map.", await off.Content.ReadAsStringAsync(Ct));
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("MoveRegion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Move_page_for_an_unknown_region_is_not_found()
    {
        using var client = await SignedInAsync();

        using var response = await client.GetAsync($"/regions/region/{Guid.NewGuid()}/move", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Regions_page_in_move_mode_tells_the_map_which_region()
    {
        using var client = await SignedInAsync();

        var doc = new HtmlParser().ParseDocument(await client.GetStringAsync($"/regions?move={SunsetId}", Ct));

        var map = doc.QuerySelector("#map")!;
        Assert.Equal(SunsetId.ToString(), map.GetAttribute("data-move"));
        Assert.Equal("Sunset Shore", map.GetAttribute("data-move-name"));
        Assert.Equal("2", map.GetAttribute("data-move-size"));
    }
}
