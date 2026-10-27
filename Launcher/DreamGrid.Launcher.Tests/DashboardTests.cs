using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Endpoints;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed partial class DashboardTests : IAsyncLifetime
{
    private const string PortMessage = "Port 8002 (Robust public) is already in use by another program.";
    private const string PlainPort =
        "Port 8002 is already in use by another program, so your world can't start. Close that program, or pick another port in Advanced Settings → Network.";
    private const string CouldNotStart = "Your world could not start.";

    private readonly LauncherFactory _f = new(s => s with
    {
        SetupToken = null,
        Setup = s.Setup with { OwnerCreated = true, Finished = true },
        Programs = TestSettings.ProgramFor(new RegionSettings("Welcome", "Welcome", Guid.NewGuid())), Regions = TestSettings.RegionsFor(new RegionSettings("Welcome", "Welcome", Guid.NewGuid())),
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

    private static ComponentStatus Component(string name, ProcessState state, string? lastError = null) =>
        new(name, state, null, null, null, lastError);

    private static readonly Guid WelcomeId = Guid.Parse("20000000-0000-0000-0000-000000000001");

    /// <summary>The database, Robust and (when <paramref name="region"/> is set) one region, Welcome, in a program of its own.</summary>
    private static GridStatus Status(ProcessState db, ProcessState robust, ProcessState? region,
        StartFailure? startFailure = null, GridActivity activity = GridActivity.None)
    {
        var program = region is { } p ? Component("region", p) : null;
        var welcome = new RegionSettings("Welcome", "Welcome", WelcomeId);
        IReadOnlyList<RegionStatus> regions = program is null
            ? []
            : [new RegionStatus(WelcomeId, "Welcome", "Welcome", RegionStates.Derive(welcome, program, false, null, true), true, 1000, 1000, 1, [])];
        return new(Component("database", db), Component("robust", robust),
            program is null ? [] : [new ProgramStatus("Welcome", program, [WelcomeId])], regions,
            false, "http://127.0.0.1:8002", startFailure, activity);
    }

    private static string Token(string html) =>
        WebUtility.HtmlDecode(TokenPattern().Match(html).Groups[1].Value);

    /// <summary>The text a person sees: no scripts, styles or tags.</summary>
    private static string VisibleText(string html) =>
        WebUtility.HtmlDecode(TagPattern().Replace(ScriptPattern().Replace(html, " "), " "));

    private async Task WaitForCallAsync(string call)
    {
        for (var i = 0; i < 100 && !_f.Grid.Snapshot().Contains(call); i++) await Task.Delay(50, Ct);
        Assert.Contains(call, _f.Grid.Snapshot());
    }

    [Fact]
    public async Task Dashboard_shows_running_state_and_login_uri()
    {
        using var client = await SignedInAsync();
        using var resp = await client.GetAsync("/", Ct);
        var html = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Your world is running", html);
        Assert.Contains("http://127.0.0.1:8002", html);
        foreach (var name in new[] { "Database", "Grid service", "Welcome", "Running" })
            Assert.Contains(name, html);
    }

    [Fact]
    public async Task Dashboard_renders_when_grid_is_down_and_shows_problem_and_start()
    {
        _f.Grid.Status = Status(ProcessState.Failed, ProcessState.Stopped, null) with
        {
            Database = Component("database", ProcessState.Failed, "MariaDB exited with code 1"),
        };
        Directory.CreateDirectory(_f.Paths.Logs);
        File.WriteAllText(Path.Combine(_f.Paths.Logs, "mariadb.err"), "2026-10-25 [ERROR] Can't open the mysql.plugin table\n");

        using var client = await SignedInAsync();
        using var resp = await client.GetAsync("/", Ct);
        var html = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("The database isn't running.", VisibleText(html));
        Assert.Matches("<button[^>]*value=\"start\"[^>]*>Start</button>", html);
        // The failed component's last log lines, folded away.
        Assert.Contains("Show details", html);
        Assert.Contains("Can't open the mysql.plugin table", VisibleText(html));
    }

    [Fact]
    public async Task A_port_conflict_is_shown_in_plain_words_from_the_port_number()
    {
        _f.Grid.Status = Status(ProcessState.Stopped, ProcessState.Stopped, ProcessState.Stopped, new StartFailure(PortMessage, 8002));

        using var client = await SignedInAsync();
        var text = VisibleText(await client.GetStringAsync("/", Ct));
        var admin = await client.GetStringAsync("/api/status", Ct);
        var anonymous = await _f.CreateClient().GetStringAsync("/api/status", Ct);

        Assert.Contains(PlainPort, text);
        Assert.DoesNotMatch(@"(?i)\brobust\b", text);
        foreach (var body in new[] { admin, anonymous })
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(PlainPort, json.RootElement.GetProperty("problem").GetString());
            Assert.DoesNotContain("Robust", body);
        }
    }

    [Fact]
    public async Task Any_other_start_failure_is_plain_with_the_raw_message_only_in_the_admin_details()
    {
        var raw = "MariaDB server not found; searched: " + Path.Combine(_f.Paths.Data, "mariadb", "bin");
        _f.Grid.Status = Status(ProcessState.Stopped, ProcessState.Stopped, ProcessState.Stopped, new StartFailure(raw, null));

        using var client = await SignedInAsync();
        var html = await client.GetStringAsync("/", Ct);
        var anonymous = await _f.CreateClient().GetStringAsync("/api/status", Ct);
        var admin = await client.GetStringAsync("/api/status", Ct);

        Assert.Contains(CouldNotStart, VisibleText(html));
        Assert.Contains("Show details", html);
        Assert.Contains(raw, VisibleText(html));
        foreach (var body in new[] { anonymous, admin })
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(CouldNotStart, json.RootElement.GetProperty("problem").GetString());
            Assert.DoesNotContain(_f.Paths.Data, body);
            Assert.DoesNotContain(JsonEncodedText.Encode(_f.Paths.Data).ToString(), body);
        }
    }

    [Fact]
    public async Task A_start_failure_is_ignored_once_everything_runs()
    {
        _f.Grid.Status = Status(ProcessState.Running, ProcessState.Running, ProcessState.Running, new StartFailure(PortMessage, 8002));

        using var client = await SignedInAsync();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/status", Ct));

        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("problem").ValueKind);
        Assert.Equal("Your world is running.", json.RootElement.GetProperty("headline").GetString());
    }

    [Theory]
    // Settled states.
    [InlineData(ProcessState.Running, ProcessState.Running, ProcessState.Running, GridActivity.None, null)]
    [InlineData(ProcessState.Stopped, ProcessState.Stopped, ProcessState.Stopped, GridActivity.None, null)]
    [InlineData(ProcessState.Running, ProcessState.Stopped, ProcessState.Stopped, GridActivity.None, "The grid service isn't running.")]
    // A region stopped on purpose is not a problem (spec 6.8); a failed one is, by name.
    [InlineData(ProcessState.Running, ProcessState.Running, ProcessState.Stopped, GridActivity.None, null)]
    // Failed and Crashed are problems at any time.
    [InlineData(ProcessState.Failed, ProcessState.Stopped, ProcessState.Stopped, GridActivity.None, "The database isn't running.")]
    [InlineData(ProcessState.Running, ProcessState.Failed, ProcessState.Stopped, GridActivity.None, "The grid service isn't running.")]
    [InlineData(ProcessState.Running, ProcessState.Running, ProcessState.Failed, GridActivity.None, "Welcome isn't running.")]
    [InlineData(ProcessState.Running, ProcessState.Running, ProcessState.Crashed, GridActivity.Starting, "Welcome isn't running.")]
    [InlineData(ProcessState.Running, ProcessState.Failed, ProcessState.Stopped, GridActivity.Starting, "The grid service isn't running.")]
    // A component in transition: no partial-stop problem.
    [InlineData(ProcessState.Running, ProcessState.Starting, ProcessState.Stopped, GridActivity.None, null)]
    [InlineData(ProcessState.Running, ProcessState.Running, ProcessState.Starting, GridActivity.None, null)]
    [InlineData(ProcessState.Running, ProcessState.Stopping, ProcessState.Stopped, GridActivity.None, null)]
    // StartAsync step by step, including the moments between components.
    [InlineData(ProcessState.Starting, ProcessState.Stopped, ProcessState.Stopped, GridActivity.Starting, null)]
    [InlineData(ProcessState.Running, ProcessState.Stopped, ProcessState.Stopped, GridActivity.Starting, null)]
    [InlineData(ProcessState.Running, ProcessState.Starting, ProcessState.Stopped, GridActivity.Starting, null)]
    [InlineData(ProcessState.Running, ProcessState.Running, ProcessState.Stopped, GridActivity.Starting, null)]
    [InlineData(ProcessState.Running, ProcessState.Running, ProcessState.Starting, GridActivity.Starting, null)]
    // StopAllAsync step by step: the region goes first.
    [InlineData(ProcessState.Running, ProcessState.Running, ProcessState.Stopping, GridActivity.Stopping, null)]
    [InlineData(ProcessState.Running, ProcessState.Running, ProcessState.Stopped, GridActivity.Stopping, null)]
    [InlineData(ProcessState.Running, ProcessState.Stopping, ProcessState.Stopped, GridActivity.Stopping, null)]
    [InlineData(ProcessState.Running, ProcessState.Stopped, ProcessState.Stopped, GridActivity.Stopping, null)]
    [InlineData(ProcessState.Stopping, ProcessState.Stopped, ProcessState.Stopped, GridActivity.Stopping, null)]
    // A one-component restart.
    [InlineData(ProcessState.Running, ProcessState.Running, ProcessState.Stopped, GridActivity.Restarting, null)]
    public void Problem_names_the_first_component_that_is_down(ProcessState db, ProcessState robust, ProcessState region, GridActivity activity, string? problem)
    {
        Assert.Equal(problem, StatusApi.Problem(Status(db, robust, region, activity: activity)));
    }

    [Theory]
    [InlineData(ProcessState.Running, ProcessState.Running, ProcessState.Running, GridActivity.None, "Your world is running.")]
    [InlineData(ProcessState.Stopped, ProcessState.Stopped, ProcessState.Stopped, GridActivity.None, "Your world is stopped.")]
    [InlineData(ProcessState.Running, ProcessState.Stopped, ProcessState.Stopped, GridActivity.Starting, "Your world is starting.")]
    [InlineData(ProcessState.Running, ProcessState.Running, ProcessState.Stopped, GridActivity.Stopping, "Your world is stopping.")]
    [InlineData(ProcessState.Running, ProcessState.Running, ProcessState.Stopped, GridActivity.Restarting, "Your world is restarting.")]
    [InlineData(ProcessState.Running, ProcessState.Starting, ProcessState.Stopped, GridActivity.None, "Your world is starting.")]
    public void Headline_follows_what_the_grid_is_doing(ProcessState db, ProcessState robust, ProcessState region, GridActivity activity, string headline)
    {
        Assert.Equal(headline, StatusApi.Headline(Status(db, robust, region, activity: activity)));
    }

    [Fact]
    public async Task Status_api_exposes_no_paths_or_secrets()
    {
        var s = _f.Store.LoadOrCreate();
        _f.Grid.Status = Status(ProcessState.Failed, ProcessState.Stopped, null) with
        {
            Database = Component("database", ProcessState.Failed, "see " + Path.Combine(_f.Paths.Data, "logs", "mariadb.err")),
        };

        // Anonymous: the endpoint needs no sign-in.
        using var resp = await _f.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/api/status", Ct);
        var body = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.DoesNotContain("assword", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_f.Paths.Data, body);
        Assert.DoesNotContain(JsonEncodedText.Encode(_f.Paths.Data).ToString(), body);
        foreach (var secret in new[] { s.MachineId, s.Db.RootPassword, s.Db.RobustPassword, s.Db.OpensimPassword, s.Console.Password })
            Assert.DoesNotContain(secret, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("The database isn't running.", json.RootElement.GetProperty("problem").GetString());
        Assert.Equal("Failed", json.RootElement.GetProperty("grid").GetProperty("database").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Status_api_lists_who_is_online_only_for_a_signed_in_admin()
    {
        _f.OnlineUsers.Result = new OnlineList([new OnlineAvatar("Test Owner", Guid.NewGuid())], true);

        var anonymous = await _f.CreateClient().GetStringAsync("/api/status", Ct);
        using var admin = await SignedInAsync();
        var signedIn = await admin.GetStringAsync("/api/status", Ct);

        using (var json = JsonDocument.Parse(anonymous))
        {
            Assert.Empty(json.RootElement.GetProperty("online").EnumerateArray());
            Assert.False(json.RootElement.GetProperty("onlineAvailable").GetBoolean());
        }
        using (var json = JsonDocument.Parse(signedIn))
        {
            Assert.Equal(["Test Owner"], json.RootElement.GetProperty("online").EnumerateArray().Select(e => e.GetString()));
            Assert.True(json.RootElement.GetProperty("onlineAvailable").GetBoolean());
        }
        Assert.DoesNotContain("Test Owner", anonymous);
    }

    [Fact]
    public async Task Grid_actions_require_admin_and_antiforgery()
    {
        using var anonymous = await _f.CreateClient(new() { AllowAutoRedirect = false }).PostAsync("/api/grid/restart-robust", null, Ct);
        Assert.Contains(anonymous.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Redirect });

        using var client = await SignedInAsync();
        using var noHeader = await client.PostAsync("/api/grid/restart-robust", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noHeader.StatusCode);
        Assert.DoesNotContain("Restart robust", _f.Grid.Snapshot());

        var token = Token(await client.GetStringAsync("/", Ct));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/grid/restart-robust");
        request.Headers.Add("RequestVerificationToken", token);
        using var ok = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        await WaitForCallAsync("Restart robust");

        using var unknown = new HttpRequestMessage(HttpMethod.Post, "/api/grid/explode");
        unknown.Headers.Add("RequestVerificationToken", token);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(unknown, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("stop", "StopAllAsync")]
    [InlineData("restart-database", "Restart database")]
    [InlineData("restart-robust", "Restart robust")]
    public async Task Controls_work_as_a_plain_form_without_javascript(string action, string call)
    {
        using var client = await SignedInAsync();
        var token = Token(await client.GetStringAsync("/", Ct));
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["gridAction"] = action,
            ["__RequestVerificationToken"] = token,
        });

        using var resp = await client.PostAsync("/dashboard?handler=run", form, Ct);

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/", resp.Headers.Location?.OriginalString);
        await WaitForCallAsync(call);
    }

    [Fact]
    public async Task Restart_stops_everything_then_starts_it()
    {
        using var client = await SignedInAsync();
        var token = Token(await client.GetStringAsync("/", Ct));
        await _f.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct); // the host's own start at launch
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/grid/restart");
        request.Headers.Add("RequestVerificationToken", token);

        (await client.SendAsync(request, Ct)).Dispose();

        for (var i = 0; i < 100 && _f.Grid.Snapshot().Count(c => c == "StartAsync") < 2; i++) await Task.Delay(50, Ct);
        var calls = _f.Grid.Snapshot();
        Assert.Equal(2, calls.Count(c => c == "StartAsync"));
        Assert.True(Array.IndexOf(calls, "StopAllAsync") < Array.LastIndexOf(calls, "StartAsync"));
    }

    [Fact]
    public async Task Dashboard_lists_who_is_online()
    {
        _f.OnlineUsers.Result = new OnlineList([new OnlineAvatar("Test Owner", Guid.NewGuid())], true);
        using var client = await SignedInAsync();

        Assert.Contains("Test Owner", await client.GetStringAsync("/", Ct));
    }

    [Fact]
    public async Task Dashboard_says_nobody_is_in_world()
    {
        _f.OnlineUsers.Result = new OnlineList([], true);
        using var client = await SignedInAsync();

        Assert.Contains("Nobody is in-world right now", await client.GetStringAsync("/", Ct));
    }

    [Fact]
    public async Task Dashboard_lists_what_is_coming()
    {
        using var client = await SignedInAsync();
        var html = await client.GetStringAsync("/", Ct);

        Assert.Contains("Coming soon", html);
        foreach (var item in new[] { "Backups", "Visitors from the internet", "Hypergrid", "More regions", "SmartStart", "Voice", "Birds", "Tides" })
            Assert.Contains(item, html);
    }

    [Fact]
    public async Task Dashboard_wording_is_plain()
    {
        _f.Grid.Status = Status(ProcessState.Running, ProcessState.Failed, ProcessState.Stopped);
        using var client = await SignedInAsync();
        var text = VisibleText(await client.GetStringAsync("/", Ct));

        // The details box holds raw log text; this grid has no log files, so the box is empty.
        Assert.DoesNotMatch(@"(?i)\b(robust|uuid|console)\b", text);
        Assert.DoesNotMatch(@"\bINI\b", text);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"(?is)<(script|style)\b.*?</\1>")]
    private static partial Regex ScriptPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    [Fact]
    public async Task Online_users_turns_an_unexpected_failure_into_unavailable_and_warns_once()
    {
        var logs = new System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Category, string Message)>();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(logs)).SetMinimumLevel(LogLevel.Trace));
        // Stands in for an odd row (a NULL name gives an invalid cast): anything but cancellation.
        var online = new OnlineUsers(() => throw new InvalidCastException("NULL name"), factory.CreateLogger<OnlineUsers>());

        Assert.Equal(OnlineList.Unavailable, await online.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(OnlineList.Unavailable, await online.ListAsync(TestContext.Current.CancellationToken));

        Assert.Single(logs, l => l.Level == LogLevel.Warning);
        Assert.Single(logs, l => l.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task Online_users_lets_the_callers_cancellation_through()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var online = new OnlineUsers(() => throw new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => online.ListAsync(cts.Token));
    }

    [Fact]
    public async Task Status_reports_admin_false_anonymously_and_true_signed_in()
    {
        using var anonymous = JsonDocument.Parse(await _f.CreateClient().GetStringAsync("/api/status", Ct));
        using var client = await SignedInAsync();
        using var signedIn = JsonDocument.Parse(await client.GetStringAsync("/api/status", Ct));

        Assert.False(anonymous.RootElement.GetProperty("admin").GetBoolean());
        Assert.True(signedIn.RootElement.GetProperty("admin").GetBoolean());
    }

    // ---- many regions (plan 4a) ----

    private static GridStatus TwoRegions()
    {
        var running = Component("x", ProcessState.Running);
        var beach = Guid.NewGuid();
        return new GridStatus(running with { Name = "database" }, running with { Name = "robust" },
            [new ProgramStatus("Welcome", running with { Name = "region" }, [WelcomeId, beach])],
            [
                new RegionStatus(WelcomeId, "Welcome", "Welcome", RegionState.Running, true, 1000, 1000, 1, ["Sunset Beach"]),
                new RegionStatus(beach, "Sunset Beach", "Welcome", RegionState.Parked, false, 1001, 1000, 1, ["Welcome"]),
            ],
            true, "http://127.0.0.1:8002");
    }

    [Fact]
    public async Task Dashboard_lists_each_region_with_its_state()
    {
        _f.Grid.Status = TwoRegions();
        using var client = await SignedInAsync();
        var text = VisibleText(await client.GetStringAsync("/", Ct));

        Assert.Contains("Sunset Beach", text);
        Assert.Contains("Parked", text);
        Assert.Contains("runs with Welcome", text);
        Assert.Contains("Your world is running.", text);
    }

    [Fact]
    public async Task Power_menu_offers_start_everything()
    {
        using var client = await SignedInAsync();
        var html = await client.GetStringAsync("/", Ct);

        foreach (var (value, label) in new[] { ("start", "Start"), ("start-everything", "Start everything"), ("restart", "Restart"), ("stop", "Stop all") })
            Assert.Matches($"<button[^>]*value=\"{value}\"[^>]*>{label}</button>", html);
        Assert.Contains("<details", html);
    }

    [Theory]
    [InlineData("start", "StartWorld")]
    [InlineData("start-everything", "StartWorld everything")]
    public async Task Start_actions_call_StartWorldAsync(string action, string call)
    {
        using var client = await SignedInAsync();
        var token = Token(await client.GetStringAsync("/", Ct));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/grid/" + action);
        request.Headers.Add("RequestVerificationToken", token);

        using var ok = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        await WaitForCallAsync(call);
    }

    [Fact]
    public async Task Status_api_lists_regions_without_details_for_anonymous()
    {
        _f.Grid.Status = TwoRegions();
        using var anonymous = _f.CreateClient();
        using var json = JsonDocument.Parse(await anonymous.GetStringAsync("/api/status", Ct));

        var regions = json.RootElement.GetProperty("grid").GetProperty("regions").EnumerateArray().ToList();
        Assert.Equal(["Welcome", "Sunset Beach"], regions.Select(r => r.GetProperty("name").GetString()));
        Assert.Equal("Parked", regions[1].GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, regions[0].GetProperty("runsWith").ValueKind);
        Assert.True(json.RootElement.GetProperty("grid").GetProperty("loginsEnabled").GetBoolean());

        using var admin = await SignedInAsync();
        using var full = JsonDocument.Parse(await admin.GetStringAsync("/api/status", Ct));
        var first = full.RootElement.GetProperty("grid").GetProperty("regions")[0];
        Assert.Equal(["Sunset Beach"], first.GetProperty("runsWith").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Failed_region_problem_names_it()
    {
        var status = TwoRegions() with
        {
            Regions = [new RegionStatus(WelcomeId, "Welcome", "Welcome", RegionState.Failed, false, 1000, 1000, 1, [])],
        };

        Assert.Equal("Welcome isn't running.", StatusApi.Problem(status));
    }

    [Fact]
    public async Task Online_list_names_each_persons_region()
    {
        var welcome = RegionModel.First(_f.Grid.CurrentSettings())!;
        _f.OnlineUsers.Result = new OnlineList([new OnlineAvatar("Test Owner", welcome.Uuid), new OnlineAvatar("Ada Guest", Guid.NewGuid())], true);
        using var client = await SignedInAsync();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/status", Ct));

        Assert.Equal(["Test Owner in Welcome", "Ada Guest"], json.RootElement.GetProperty("online").EnumerateArray().Select(e => e.GetString()));
    }
}
