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

public sealed class AddRegionPageTests : IAsyncLifetime
{
    private const long GB = 1L << 30;
    private const long MB = 1L << 20;

    private static readonly Guid HomeId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid BeachId = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid IsleId = Guid.Parse("30000000-0000-0000-0000-000000000003");

    private long? _free = 64 * GB;
    private readonly LauncherFactory _f;

    public AddRegionPageTests()
    {
        _f = new LauncherFactory(s => s with
        {
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true },
            // Home and Beach share one program; Isle (parked, so stopped) has its own.
            Programs = [new ProgramSettings("Home", 8004, 8011, "ubODE"), new ProgramSettings("Isle", 8005, 8012, "ubODE")],
            Regions =
            [
                new RegionSettings("Home", "Home", HomeId, UdpPort: 8004),
                new RegionSettings("Home", "Beach", BeachId, X: 1001, UdpPort: 8004),
                new RegionSettings("Isle", "Isle", IsleId, X: 1010, Y: 1010, UdpPort: 8005, Parked: true),
            ],
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddSingleton<Func<long?>>(_ => () => _free),
        };
    }

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

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url) =>
        new HtmlParser().ParseDocument(await client.GetStringAsync(url, Ct));

    private static Dictionary<string, string> Form(string name = "Meadow One", string type = "meadow", string x = "1020", string y = "1000",
        string size = "2", string group = "own") => new() { ["Name"] = name, ["Type"] = type, ["X"] = x, ["Y"] = y, ["Size"] = size, ["Group"] = group };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Dictionary<string, string> fields)
    {
        var doc = await PageAsync(client, "/regions/add");
        var token = doc.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        return await client.PostAsync("/regions/add", new FormUrlEncodedContent(form), Ct);
    }

    private static async Task<JsonElement> WaitForProgressAsync(HttpClient client, Func<JsonElement, bool> done)
    {
        for (var i = 0; i < 100; i++)
        {
            var p = JsonDocument.Parse(await client.GetStringAsync("/api/regions/progress", Ct)).RootElement;
            if (done(p)) return p;
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException("The add never reached the expected state.");
    }

    private static string Text(IHtmlDocument doc) => doc.QuerySelector("main")!.TextContent;

    private void StopRobust()
    {
        var s = _f.Grid.GetStatus();
        _f.Grid.Status = s with { Robust = s.Robust with { State = ProcessState.Stopped } };
    }

    [Fact]
    public async Task Get_prefills_from_the_map()
    {
        using var client = await SignedInAsync();

        var doc = await PageAsync(client, "/regions/add?type=beach&x=1020&y=1000");

        Assert.Equal("beach", doc.QuerySelector("input[name=Type]:checked")!.GetAttribute("value"));
        Assert.Contains("Sandy Beach", doc.QuerySelector("input[name=Type]:checked")!.ParentElement!.TextContent);
        Assert.Equal("1020", doc.QuerySelector("input[name=X]")!.GetAttribute("value"));
        Assert.Equal("1000", doc.QuerySelector("input[name=Y]")!.GetAttribute("value"));
        Assert.Equal("1", doc.QuerySelector("select[name=Size] option[selected]")!.GetAttribute("value"));
        Assert.Equal(16, doc.QuerySelectorAll("select[name=Size] option").Length);
        Assert.Contains("This will be a separate island", Text(doc));
    }

    [Fact]
    public async Task Get_says_which_regions_a_spot_would_join()
    {
        using var client = await SignedInAsync();

        var doc = await PageAsync(client, "/regions/add?type=beach&x=1002&y=1000");

        Assert.Contains("This region will join Beach on its east side.", Text(doc));
    }

    [Fact]
    public async Task Own_preselected_with_plenty_of_memory()
    {
        using var client = await SignedInAsync();

        var doc = await PageAsync(client, "/regions/add");

        Assert.Equal("own", doc.QuerySelector("input[name=Group]:checked")!.GetAttribute("value"));
        Assert.Contains("Uses about 400 MB more memory.", Text(doc));
    }

    [Fact]
    public async Task Share_preselected_when_memory_is_short()
    {
        _free = 300 * MB;
        using var client = await SignedInAsync();

        var doc = await PageAsync(client, "/regions/add");

        var chosen = doc.QuerySelector("input[name=Group]:checked")!;
        Assert.NotEqual("own", chosen.GetAttribute("value"));
        Assert.Contains("Runs with Home, Beach (2 regions)", chosen.ParentElement!.TextContent);
        Assert.Contains("may not have enough free memory", Text(doc));
    }

    [Fact]
    public async Task Groups_are_named_by_their_regions()
    {
        using var client = await SignedInAsync();

        var doc = await PageAsync(client, "/regions/add");

        var labels = doc.QuerySelectorAll("input[name=Group]").Select(i => i.ParentElement!.QuerySelector(".group-label")!.TextContent.Trim()).ToList();
        Assert.Equal(["Runs on its own", "Runs with Home, Beach (2 regions)", "Runs with Isle"], labels);
        Assert.Contains("Restarting this region also restarts Home and Beach", Text(doc));
    }

    [Fact]
    public async Task Add_refuses_spot_taken_by_stopped_region()
    {
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, Form(x: "1010", y: "1010", size: "1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("That spot is taken by Isle.", Text(await ParseAsync(response)));
        Assert.Empty(_f.Grid.AddedRegions);
    }

    [Fact]
    public async Task Add_refuses_duplicate_name_ignoring_case()
    {
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, Form(name: "BEACH"));

        Assert.Contains("There is already a region called BEACH.", Text(await ParseAsync(response)));
        Assert.Empty(_f.Grid.AddedRegions);
    }

    [Fact]
    public async Task Add_refuses_a_bad_name_an_unknown_type_a_bad_size_a_spot_off_the_map_and_an_unknown_group()
    {
        using var client = await SignedInAsync();

        Assert.Contains("Use 2 to 32 letters", Text(await ParseAsync(await PostAsync(client, Form(name: "x")))));
        Assert.Contains("Pick what your region should look like.", Text(await ParseAsync(await PostAsync(client, Form(type: "lava")))));
        Assert.Contains("A region is 1 to 16 squares on each side.", Text(await ParseAsync(await PostAsync(client, Form(size: "17")))));
        Assert.Contains("That spot is off the map.", Text(await ParseAsync(await PostAsync(client, Form(x: "65535", size: "2")))));
        Assert.Contains("Pick how the region should run.", Text(await ParseAsync(await PostAsync(client, Form(group: "nowhere")))));
        Assert.Empty(_f.Grid.AddedRegions);
    }

    [Fact]
    public async Task Share_with_running_needs_confirmation()
    {
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, Form(group: HomeId.ToString()));

        var doc = await ParseAsync(response);
        Assert.Contains("Tick the box to confirm that Home and Beach will restart.", Text(doc));
        Assert.Contains("I understand Home and Beach will restart", Text(doc));
        Assert.Empty(_f.Grid.AddedRegions);
    }

    [Fact]
    public async Task Share_with_a_stopped_group_needs_no_confirmation()
    {
        using var client = await SignedInAsync();
        _f.Grid.RegionState = ProcessState.Stopped;

        using var response = await PostAsync(client, Form(group: HomeId.ToString()));
        await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean() && p.GetProperty("stage").GetString() == "Ready");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("Home", Assert.Single(_f.Grid.AddedRegions).ShareWith);
    }

    [Fact]
    public async Task Add_when_world_is_stopped_says_start_first()
    {
        StopRobust();
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, Form());

        var doc = await ParseAsync(response);
        Assert.Contains("Start your world first: the grid service isn't running.", Text(doc));
        Assert.NotNull(doc.QuerySelector("main a[href='/']"));
        Assert.Empty(_f.Grid.AddedRegions);
    }

    [Fact]
    public async Task Add_starts_the_job_and_shows_progress()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.Grid.OnAddRegion = async (_, progress, _) =>
        {
            progress?.Report("Loading the region content");
            await release.Task;
        };
        using var client = await SignedInAsync();

        using var response = await PostAsync(client, Form());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/regions/add?working=1", response.Headers.Location!.OriginalString);
        var running = await WaitForProgressAsync(client, p => p.GetProperty("stage").GetString() == "Loading the region content");
        Assert.True(running.GetProperty("running").GetBoolean());

        var doc = await PageAsync(client, "/regions/add?working=1");
        Assert.Contains("Loading the region content", doc.QuerySelector("#add-stage")!.TextContent);
        Assert.NotNull(doc.QuerySelector("progress#add-bar"));
        Assert.Contains("http-equiv=\"refresh\"", doc.QuerySelector("noscript")!.InnerHtml);

        release.SetResult();
        var done = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());
        Assert.Equal("Ready", done.GetProperty("stage").GetString());
        var added = Assert.Single(_f.Grid.AddedRegions);
        Assert.Equal(new NewRegion("Meadow One", 1020, 1000, 2, "meadow", null), added);

        var id = _f.Store.LoadOrCreate().Regions.Single(r => r.Name == "Meadow One").Uuid;
        Assert.Equal(id, done.GetProperty("regionId").GetGuid());
        var ready = await PageAsync(client, "/regions/add?working=1");
        Assert.Contains("Meadow One is ready.", Text(ready));
        Assert.Equal($"/regions?region={id}", ready.QuerySelector("a.show-on-map")!.GetAttribute("href"));
    }

    [Fact]
    public async Task Second_add_while_one_runs_shows_its_progress()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.Grid.OnAddRegion = (_, _, _) => release.Task;
        using var client = await SignedInAsync();

        (await PostAsync(client, Form())).Dispose();
        await WaitForProgressAsync(client, p => p.GetProperty("running").GetBoolean());
        using var second = await PostAsync(client, Form(name: "Forest Two", type: "forest", x: "1030"));

        Assert.Equal("/regions/add?working=1", second.Headers.Location!.OriginalString);
        release.SetResult();
        await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());
        Assert.Equal("Meadow One", Assert.Single(_f.Grid.AddedRegions).Name);
    }

    [Fact]
    public async Task Failed_add_shows_message_and_details()
    {
        Directory.CreateDirectory(_f.Paths.Logs);
        await File.WriteAllTextAsync(Path.Combine(_f.Paths.Logs, "region-Meadow_One.log"), "12:00:00 - [ERROR] meadow exploded\n", Ct);
        // The region is saved before the add falls over, as the controller does.
        _f.Grid.OnAddRegion = (request, _, _) =>
        {
            _f.Grid.UpdateSettings(l => l with
            {
                Programs = [.. l.Programs, new ProgramSettings("Meadow_One", 8006, 8013)],
                Regions = [.. l.Regions, new RegionSettings("Meadow_One", request.Name, Guid.NewGuid(), request.X, request.Y, request.Content, request.Size)],
            });
            throw new InvalidOperationException("The region failed: boom");
        };
        using var client = await SignedInAsync();

        (await PostAsync(client, Form())).Dispose();
        var failed = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());

        Assert.Equal("Your region could not be created.", failed.GetProperty("error").GetString());
        var doc = await PageAsync(client, "/regions/add?working=1");
        Assert.Contains("Your region could not be created.", Text(doc));
        Assert.Contains("Show details", doc.QuerySelector("details summary")!.TextContent);
        Assert.Contains("meadow exploded", doc.QuerySelector("details")!.TextContent);
        Assert.DoesNotContain("boom", doc.QuerySelector("details")!.TextContent); // the exception goes to the launcher log
        Assert.True(await _f.WaitForLogAsync(l => l.Message.Contains("boom")));
        var id = _f.Store.LoadOrCreate().Regions.Single(r => r.Name == "Meadow One").Uuid;
        var open = doc.QuerySelector("a.open-region")!;
        Assert.Equal("Open Meadow One", open.TextContent.Trim());
        Assert.Equal($"/regions?region={id}", open.GetAttribute("href"));
        Assert.Null(doc.QuerySelector("main form")); // the region stays saved; its panel starts it again
        Assert.Null(doc.QuerySelector("meta[http-equiv=refresh]"));
    }

    [Fact]
    public async Task Refused_add_shows_the_controllers_words()
    {
        _f.Grid.OnAddRegion = (_, _, _) => throw new ArgumentException("That spot overlaps Somewhere.");
        using var client = await SignedInAsync();

        (await PostAsync(client, Form())).Dispose();
        await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());

        var doc = await PageAsync(client, "/regions/add?working=1");
        Assert.Contains("That spot overlaps Somewhere.", Text(doc));
        Assert.NotNull(doc.QuerySelector("main form")); // nothing was saved, so the form is offered again
    }

    [Fact]
    public async Task Refusal_text_has_no_parameter_suffix()
    {
        _f.Grid.OnAddRegion = (_, _, _) => throw new ArgumentException("That spot overlaps Somewhere.", "request");
        using var client = await SignedInAsync();

        (await PostAsync(client, Form())).Dispose();
        var failed = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());

        Assert.Equal("That spot overlaps Somewhere.", failed.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Other_argument_errors_get_the_plain_message()
    {
        _f.Grid.OnAddRegion = (_, _, _) => throw new ArgumentOutOfRangeException("index", "deep inside");
        using var client = await SignedInAsync();

        (await PostAsync(client, Form())).Dispose();
        var failed = await WaitForProgressAsync(client, p => !p.GetProperty("running").GetBoolean());

        Assert.Equal("Your region could not be created.", failed.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Add_page_never_says_program()
    {
        using var client = await SignedInAsync();

        foreach (var url in new[] { "/regions/add", "/regions/add?working=1" })
        {
            var html = (await client.GetStringAsync(url, Ct)).ToLowerInvariant();
            Assert.DoesNotContain("program", html);
            Assert.DoesNotContain("instance", html);
        }
        // Also with a mistake showing.
        using var response = await PostAsync(client, Form(name: "x", group: HomeId.ToString()));
        var posted = (await response.Content.ReadAsStringAsync(Ct)).ToLowerInvariant();
        Assert.DoesNotContain("program", posted);
        Assert.DoesNotContain("instance", posted);
    }

    [Fact]
    public async Task Progress_api_needs_sign_in()
    {
        using var client = _f.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/api/regions/progress", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Add_page_needs_sign_in()
    {
        using var client = _f.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/regions/add", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
