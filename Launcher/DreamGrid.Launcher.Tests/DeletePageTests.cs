using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class DeletePageTests : IAsyncLifetime
{
    private static readonly Guid HomeId = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid BeachId = Guid.Parse("70000000-0000-0000-0000-000000000002");
    private static readonly Guid SunsetId = Guid.Parse("70000000-0000-0000-0000-000000000003");
    private static readonly Guid OldId = Guid.Parse("70000000-0000-0000-0000-000000000004");

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
            new RegionSettings("Sunset", "Sunset Shore", SunsetId, X: 1002, UdpPort: 8005),
            new RegionSettings("Old", "Old Town", OldId, X: 1500, Size: 2, UdpPort: 8006, DeletedAt: new DateTimeOffset(2026, 10, 20, 14, 30, 0, TimeSpan.Zero)),
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

    private static async Task<IHtmlDocument> GetAsync(HttpClient client, string url) =>
        new HtmlParser().ParseDocument(await client.GetStringAsync(url, Ct));

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string tokenPage, string url, params (string, string)[] fields)
    {
        var token = (await GetAsync(client, tokenPage)).QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = fields.ToDictionary(f => f.Item1, f => f.Item2);
        form["__RequestVerificationToken"] = token;
        return await client.PostAsync(url, new FormUrlEncodedContent(form), Ct);
    }

    private static Task<HttpResponseMessage> PostDeleteAsync(HttpClient client, Guid id, params (string, string)[] fields) =>
        PostAsync(client, $"/regions/region/{id}", $"/regions/region/{id}?handler=delete", fields);

    private static async Task<IHtmlDocument> DocAsync(HttpResponseMessage response) =>
        new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync(Ct));

    private async Task<bool> WaitForCallAsync(string call)
    {
        for (var i = 0; i < 100; i++)
        {
            if (_f.Grid.Snapshot().Contains(call)) return true;
            await Task.Delay(50, Ct);
        }
        return false;
    }

    [Fact]
    public async Task Delete_asks_and_names_regions_that_restart()
    {
        using var client = await SignedInAsync();

        using var shared = await PostDeleteAsync(client, BeachId);
        using var alone = await PostDeleteAsync(client, SunsetId);

        var sharedText = (await DocAsync(shared)).Body!.TextContent;
        Assert.Contains("Delete Beach? It goes to Recently deleted, where you can restore it. Its land and objects are kept until you delete it forever.", sharedText);
        Assert.Contains("Home restart without it.", sharedText);
        var aloneText = (await DocAsync(alone)).Body!.TextContent;
        Assert.Contains("Delete Sunset Shore? It goes to Recently deleted", aloneText);
        Assert.DoesNotContain("restart without it", aloneText);
        await Task.Delay(100, Ct);
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("DeleteRegion"));
    }

    [Fact]
    public async Task Delete_confirmed_runs_and_goes_to_regions_page()
    {
        using var client = await SignedInAsync();

        using var response = await PostDeleteAsync(client, BeachId, ("confirmed", "true"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/regions", response.Headers.Location!.OriginalString);
        Assert.True(await WaitForCallAsync($"DeleteRegion {BeachId}"));
        Assert.Contains("Beach is in Recently deleted.", await client.GetStringAsync("/regions", Ct));
    }

    [Fact]
    public async Task Delete_with_save_first_waits_for_the_save()
    {
        _f.Grid.OnOarJob = (_, _, _) => throw new InvalidOperationException("The save did not finish; see the region log.");
        using var client = await SignedInAsync();

        using var response = await PostDeleteAsync(client, SunsetId, ("confirmed", "true"), ("saveFirst", "true"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/regions/region/{SunsetId}/job", response.Headers.Location!.OriginalString);
        var html = "";
        for (var i = 0; i < 100 && !html.Contains("did not finish"); i++)
        {
            html = await client.GetStringAsync($"/regions/region/{SunsetId}/job", Ct);
            await Task.Delay(50, Ct);
        }
        Assert.Contains("The save did not finish; see the region log.", html);
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("DeleteRegion"));
    }

    [Fact]
    public async Task Delete_with_save_first_deletes_once_the_save_succeeds()
    {
        using var client = await SignedInAsync();

        using var response = await PostDeleteAsync(client, SunsetId, ("confirmed", "true"), ("saveFirst", "true"));

        Assert.Equal($"/regions/region/{SunsetId}/job", response.Headers.Location!.OriginalString);
        Assert.True(await WaitForCallAsync($"DeleteRegion {SunsetId}"));
        Assert.Single(_f.Grid.OarJobs);
    }

    [Fact]
    public async Task Save_first_box_disabled_when_not_running()
    {
        using var client = await SignedInAsync();
        using var running = await PostDeleteAsync(client, SunsetId);
        var on = await DocAsync(running);

        _f.Grid.RegionState = ProcessState.Stopped;
        using var stopped = await PostDeleteAsync(client, SunsetId);
        var off = await DocAsync(stopped);

        var box = on.QuerySelector("input[name=saveFirst]")!;
        Assert.False(box.HasAttribute("disabled"));
        Assert.False(box.HasAttribute("checked"));
        Assert.Contains("Also save it to the OAR library first (from seconds to many minutes; it needs the region running)", on.Body!.TextContent);
        Assert.True(off.QuerySelector("input[name=saveFirst]")!.HasAttribute("disabled"));
    }

    [Fact]
    public async Task Delete_is_refused_while_a_job_runs_in_its_program()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.Grid.OnOarJob = (_, _, _) => release.Task;
        using var client = await SignedInAsync();
        (await PostAsync(client, $"/regions/region/{HomeId}", $"/regions/region/{HomeId}?handler=oar", ("action", "Save"))).Dispose();

        using var response = await PostDeleteAsync(client, BeachId, ("confirmed", "true"));
        var panel = await client.GetStringAsync($"/regions/region/{BeachId}", Ct);

        Assert.Equal($"/regions/region/{BeachId}", response.Headers.Location!.OriginalString);
        Assert.Contains("is still running. Try again when it finishes.", panel);
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("DeleteRegion"));
        release.SetResult();
    }

    [Fact]
    public async Task Deleted_page_lists_with_space_and_date()
    {
        _f.Grid.DeletedSpace = new Dictionary<Guid, long> { [OldId] = 12L * 1024 * 1024 };
        using var client = await SignedInAsync();

        var doc = await GetAsync(client, "/regions/deleted");

        var row = doc.QuerySelector($"tr[data-region='{OldId}']")!;
        Assert.Contains("Old Town", row.TextContent);
        Assert.Contains("1500, 1000", row.TextContent);
        Assert.Contains("2×2", row.TextContent);
        Assert.Contains("2026-10-20", row.TextContent);
        Assert.Contains("about 12.0 MB", row.TextContent);
        Assert.DoesNotContain("Sunset Shore", doc.Body!.TextContent);
        Assert.Contains("Restore", row.TextContent);
        Assert.Contains("Delete forever…", row.TextContent);
    }

    [Fact]
    public async Task Deleted_page_shows_unknown_space_when_db_stopped()
    {
        _f.Grid.DeletedSpace = null;
        using var client = await SignedInAsync();

        var doc = await GetAsync(client, "/regions/deleted");

        Assert.Contains("Unknown while the database is stopped", doc.QuerySelector($"tr[data-region='{OldId}']")!.TextContent);
    }

    [Fact]
    public async Task Restore_brings_it_back_stopped()
    {
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, "/regions/deleted", "/regions/deleted?handler=restore", ("id", OldId.ToString()));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await WaitForCallAsync($"RestoreRegion {OldId}"));
        Assert.Contains("Old Town is back, stopped. Start it when you're ready.", System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/regions/deleted", Ct)));
    }

    [Fact]
    public async Task Restore_of_a_live_region_is_not_found()
    {
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, "/regions/deleted", "/regions/deleted?handler=restore", ("id", SunsetId.ToString()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("RestoreRegion"));
    }

    [Fact]
    public async Task Erase_asks_first()
    {
        using var client = await SignedInAsync();
        var url = "/regions/deleted?handler=erase";

        using var asked = await PostAsync(client, "/regions/deleted", url, ("id", OldId.ToString()));
        Assert.Contains("Delete Old Town forever? Its land and objects are erased from the database. This can't be undone.", (await DocAsync(asked)).Body!.TextContent);
        await Task.Delay(100, Ct);
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("EraseRegion"));

        using var yes = await PostAsync(client, "/regions/deleted", url, ("id", OldId.ToString()), ("confirmed", "true"));
        Assert.Equal(HttpStatusCode.Redirect, yes.StatusCode);
        Assert.True(await WaitForCallAsync($"EraseRegion {OldId}"));
    }

    [Fact]
    public async Task Erase_is_refused_in_plain_words_while_db_stopped()
    {
        _f.Grid.DeletedSpace = null;
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, "/regions/deleted", "/regions/deleted?handler=erase", ("id", OldId.ToString()), ("confirmed", "true"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("Start the database first.", await client.GetStringAsync("/regions/deleted", Ct));
        Assert.DoesNotContain(_f.Grid.Snapshot(), c => c.StartsWith("EraseRegion"));
    }

    [Fact]
    public async Task Regions_page_links_recently_deleted_only_when_some()
    {
        using var client = await SignedInAsync();
        var with = await GetAsync(client, "/regions");
        Assert.Equal("Recently deleted (1)", with.QuerySelector("a[href='/regions/deleted']")!.TextContent.Trim());

        await using var none = new LauncherFactory(s => s with
        {
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true },
            Regions = [new RegionSettings("Home", "Home", HomeId, UdpPort: 8004)],
        }) { Before = TestAuth.Apply };
        using var other = none.CreateClient(new() { AllowAutoRedirect = false });
        (await other.GetAsync("/__test/signin", Ct)).Dispose();

        Assert.Null((await GetAsync(other, "/regions")).QuerySelector("a[href='/regions/deleted']"));
    }

    [Fact]
    public async Task Deleted_region_panel_is_404()
    {
        using var client = await SignedInAsync();

        using var panel = await client.GetAsync($"/regions/region/{OldId}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, panel.StatusCode);
    }

    [Fact]
    public async Task Deleted_page_needs_admin()
    {
        using var client = _f.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/regions/deleted", Ct);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
