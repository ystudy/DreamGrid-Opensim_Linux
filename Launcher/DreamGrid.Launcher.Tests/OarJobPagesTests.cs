using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class OarJobPagesTests : IAsyncLifetime
{
    private static readonly Guid HomeId = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid BeachId = Guid.Parse("50000000-0000-0000-0000-000000000002");
    private static readonly Guid SunsetId = Guid.Parse("50000000-0000-0000-0000-000000000003");

    private readonly LauncherFactory _f = new(s => s with
    {
        SetupToken = null,
        Setup = s.Setup with { OwnerCreated = true, Finished = true },
        // Home and Beach share one program; Sunset Shore has its own.
        Programs =
        [
            new ProgramSettings("Home", 8004, 8011, "ubODE"),
            new ProgramSettings("Sunset", 8005, 8012, "ubODE"),
        ],
        Regions =
        [
            new RegionSettings("Home", "Home", HomeId, UdpPort: 8004),
            new RegionSettings("Home", "Beach", BeachId, X: 1001, UdpPort: 8004),
            new RegionSettings("Sunset", "Sunset Shore", SunsetId, X: 1002, UdpPort: 8005),
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

    private void AddOar(string name)
    {
        Directory.CreateDirectory(_f.Paths.Oars);
        File.WriteAllBytes(Path.Combine(_f.Paths.Oars, name), [1, 2, 3]);
    }

    private static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync(url, Ct));
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return token;
    }

    /// <summary>Posts a form to <paramref name="url"/>, with the antiforgery token read from <paramref name="tokenPage"/>.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string tokenPage, string url, params (string, string)[] fields)
    {
        var form = fields.ToDictionary(f => f.Item1, f => f.Item2);
        form["__RequestVerificationToken"] = await TokenAsync(client, tokenPage);
        return await client.PostAsync(url, new FormUrlEncodedContent(form), Ct);
    }

    private static Task<HttpResponseMessage> PostSaveAsync(HttpClient client, Guid id) =>
        PostAsync(client, $"/regions/region/{id}", $"/regions/region/{id}?handler=oar", ("action", "Save"));

    /// <summary>The job page, fetched again (up to 5 s) until <paramref name="until"/> likes its HTML.</summary>
    private static async Task<IHtmlDocument> JobPageAsync(HttpClient client, Guid id, Func<string, bool>? until = null)
    {
        var html = "";
        for (var i = 0; i < 100; i++)
        {
            html = await client.GetStringAsync($"/regions/region/{id}/job", Ct);
            if (until is null || until(html)) break;
            await Task.Delay(50, Ct);
        }
        return new HtmlParser().ParseDocument(html);
    }

    private async Task<bool> WaitForJobAsync(Func<OarJobRequest, bool> match)
    {
        for (var i = 0; i < 100; i++)
        {
            lock (_f.Grid.OarJobs) if (_f.Grid.OarJobs.Any(match)) return true;
            await Task.Delay(50, Ct);
        }
        return false;
    }

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) await Task.Delay(50, Ct);
        Assert.True(done());
    }

    [Fact]
    public async Task Import_page_asks_before_replacing()
    {
        AddOar("castle.oar");
        AddOar("farm.oar");
        using var client = await SignedInAsync();
        var url = $"/regions/region/{SunsetId}/import";

        var list = new HtmlParser().ParseDocument(await client.GetStringAsync(url, Ct));
        Assert.Equal(["castle.oar", "farm.oar"], list.QuerySelectorAll("input[type=radio][name=file]")
            .Select(r => r.GetAttribute("value")!).Order().ToArray());

        using var asked = await PostAsync(client, url, url, ("file", "castle.oar"));
        var question = new HtmlParser().ParseDocument(await asked.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Import castle.oar into Sunset Shore? Its land and objects are replaced by the OAR's. This can't be undone; Save as OAR first if you may want them back.",
            question.Body!.TextContent);
        Assert.Contains("Yes, import", question.Body.TextContent);
        Assert.Contains("Save as OAR first", question.Body.TextContent);
        await Task.Delay(100, Ct);
        Assert.Empty(_f.Grid.OarJobs);

        using var yes = await PostAsync(client, url, url, ("file", "castle.oar"), ("confirmed", "true"));
        Assert.Equal(HttpStatusCode.Redirect, yes.StatusCode);
        Assert.Equal($"/regions/region/{SunsetId}/job", yes.Headers.Location!.OriginalString);
        Assert.True(await WaitForJobAsync(r => r.Kind == OarJobKind.Import && r.File == "castle.oar" && r.RegionId == SunsetId));
    }

    [Fact]
    public async Task Import_page_save_first_starts_a_save()
    {
        AddOar("castle.oar");
        using var client = await SignedInAsync();
        var url = $"/regions/region/{SunsetId}/import";

        using var response = await PostAsync(client, url, url + "?handler=save");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await WaitForJobAsync(r => r.Kind == OarJobKind.Save && r.RegionId == SunsetId));
    }

    [Fact]
    public async Task Import_page_refuses_a_missing_file()
    {
        AddOar("castle.oar");
        using var client = await SignedInAsync();
        var url = $"/regions/region/{SunsetId}/import";

        using var none = await PostAsync(client, url, url);
        using var gone = await PostAsync(client, url, url, ("file", "gone.oar"), ("confirmed", "true"));

        Assert.Contains("Choose a file", await none.Content.ReadAsStringAsync(Ct));
        Assert.Contains("That file is not in the OAR library.", await gone.Content.ReadAsStringAsync(Ct));
        Assert.Empty(_f.Grid.OarJobs);
    }

    [Fact]
    public async Task Import_page_with_empty_library_links_to_it()
    {
        using var client = await SignedInAsync();

        var doc = new HtmlParser().ParseDocument(await client.GetStringAsync($"/regions/region/{SunsetId}/import", Ct));

        Assert.Empty(doc.QuerySelectorAll("input[type=radio]"));
        Assert.Equal("/regions/library", doc.QuerySelector("a[href='/regions/library']")!.GetAttribute("href"));
    }

    [Fact]
    public async Task Import_page_for_a_stopped_region_says_to_start_it()
    {
        AddOar("castle.oar");
        _f.Grid.RegionState = ProcessState.Stopped;
        using var client = await SignedInAsync();
        var url = $"/regions/region/{SunsetId}/import";

        using var response = await PostAsync(client, url, url, ("file", "castle.oar"), ("confirmed", "true"));

        Assert.Contains("Start Sunset Shore first; an OAR needs it running.", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(_f.Grid.OarJobs);
    }

    [Fact]
    public async Task Save_starts_and_job_page_shows_progress()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.Grid.OnOarJob = async (_, progress, _) =>
        {
            progress!.Report(new OarJobProgress("Saving the OAR", 1048576));
            await release.Task;
        };
        using var client = await SignedInAsync();

        using var response = await PostSaveAsync(client, SunsetId);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/regions/region/{SunsetId}/job", response.Headers.Location!.OriginalString);
        var doc = await JobPageAsync(client, SunsetId, h => h.Contains("1.0 MB written so far"));
        var text = doc.Body!.TextContent;
        Assert.Contains("Saving Sunset Shore as an OAR", text);
        Assert.Contains("Saving the OAR", text);
        Assert.Contains("1.0 MB written so far", text);
        Assert.Contains("Elapsed", text);
        Assert.Equal("3", doc.QuerySelector("meta[http-equiv=refresh]")!.GetAttribute("content"));
        release.SetResult();
    }

    [Fact]
    public async Task Finished_save_links_its_file()
    {
        _f.Grid.OarJobResult = "Sunset Shore-2026-10-28.oar";
        using var client = await SignedInAsync();

        (await PostSaveAsync(client, SunsetId)).Dispose();
        var doc = await JobPageAsync(client, SunsetId, h => h.Contains("Done."));

        Assert.Contains("Done.", doc.Body!.TextContent);
        Assert.Null(doc.QuerySelector("meta[http-equiv=refresh]"));
        var link = doc.QuerySelector("a[href^='/regions/library/file/']")!;
        Assert.Equal("/regions/library/file/Sunset%20Shore-2026-10-28.oar", link.GetAttribute("href"));
    }

    [Fact]
    public async Task Failed_job_shows_its_reason()
    {
        _f.Grid.OnOarJob = (_, _, _) => throw new InvalidOperationException("The save did not finish; see the region log.");
        using var client = await SignedInAsync();

        (await PostSaveAsync(client, SunsetId)).Dispose();
        var doc = await JobPageAsync(client, SunsetId, h => h.Contains("did not finish"));

        Assert.Contains("The save did not finish; see the region log.", doc.Body!.TextContent);
        Assert.DoesNotContain("Done.", doc.Body.TextContent);
    }

    [Fact]
    public async Task A_refused_request_shows_its_words_without_the_parameter()
    {
        _f.Grid.OnOarJob = (_, _, _) => throw new ArgumentException("That file is not in the OAR library.", "request");
        using var client = await SignedInAsync();

        (await PostSaveAsync(client, SunsetId)).Dispose();
        var doc = await JobPageAsync(client, SunsetId, h => h.Contains("not in the OAR library"));

        Assert.Contains("That file is not in the OAR library.", doc.Body!.TextContent);
        Assert.DoesNotContain("Parameter", doc.Body.TextContent);
    }

    [Fact]
    public async Task Second_job_in_busy_program_shows_the_running_one()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.Grid.OnOarJob = (_, _, _) => release.Task;
        using var client = await SignedInAsync();

        (await PostSaveAsync(client, HomeId)).Dispose();
        using var second = await PostSaveAsync(client, BeachId);
        var doc = await JobPageAsync(client, BeachId);

        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal($"/regions/region/{BeachId}/job", second.Headers.Location!.OriginalString);
        Assert.Contains("Saving Home as an OAR is still running. Try again when it finishes.", doc.Body!.TextContent);
        Assert.True(await WaitForJobAsync(r => r.RegionId == HomeId));
        Assert.Single(_f.Grid.OarJobs);
        release.SetResult();
    }

    [Fact]
    public async Task Job_page_without_a_job_is_not_found()
    {
        using var client = await SignedInAsync();

        using var response = await client.GetAsync($"/regions/region/{SunsetId}/job", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Job_api_needs_admin()
    {
        using var client = _f.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync($"/api/regions/{SunsetId}/job", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Job_api_gives_the_snapshot_or_404()
    {
        _f.Grid.OarJobResult = "x.oar";
        using var client = await SignedInAsync();
        using var none = await client.GetAsync($"/api/regions/{SunsetId}/job", Ct);
        (await PostSaveAsync(client, SunsetId)).Dispose();
        await JobPageAsync(client, SunsetId, h => h.Contains("Done."));

        using var response = await client.GetAsync($"/api/regions/{SunsetId}/job", Ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Equal("Saving Sunset Shore as an OAR", json.RootElement.GetProperty("text").GetString());
        Assert.False(json.RootElement.GetProperty("running").GetBoolean());
        Assert.Equal("x.oar", json.RootElement.GetProperty("savedAs").GetString());
    }

    [Fact]
    public async Task Then_runs_after_a_successful_job_only()
    {
        var jobs = _f.Services.GetRequiredService<OarJobs>();
        var ran = 0;
        _f.Grid.OarJobResult = "x.oar";
        Assert.True(jobs.TryStart(new OarJobRequest(SunsetId, OarJobKind.Save), "Saving"));
        jobs.Then(SunsetId, _ => { Interlocked.Increment(ref ran); return Task.CompletedTask; });
        await WaitUntilAsync(() => jobs.For(SunsetId) is { Running: false });
        Assert.Equal(1, ran);

        _f.Grid.OnOarJob = (_, _, _) => throw new InvalidOperationException("nope");
        Assert.True(jobs.TryStart(new OarJobRequest(SunsetId, OarJobKind.Save), "Saving"));
        jobs.Then(SunsetId, _ => { Interlocked.Increment(ref ran); return Task.CompletedTask; });
        await WaitUntilAsync(() => jobs.For(SunsetId) is { Running: false, Error: not null });
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task A_failing_then_is_shown_as_the_error()
    {
        var jobs = _f.Services.GetRequiredService<OarJobs>();
        Assert.True(jobs.TryStart(new OarJobRequest(SunsetId, OarJobKind.Save), "Saving"));
        jobs.Then(SunsetId, _ => throw new InvalidOperationException("The file could not be deleted."));

        await WaitUntilAsync(() => jobs.For(SunsetId) is { Running: false });

        Assert.Equal("The file could not be deleted.", jobs.For(SunsetId)!.Error);
    }
}
