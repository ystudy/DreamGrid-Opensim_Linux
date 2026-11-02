using System.Net;
using System.Text;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class LegacyEndpointsTests : IAsyncLifetime
{
    private const string Rid = "6f1f0000-0000-0000-0000-00000000aaaa";

    private readonly LauncherFactory _factory = new(s => s with
    {
        GridName = "Avalon <Isle>",
        Programs = TestSettings.ProgramFor(new RegionSettings("Welcome", "Welcome", Guid.Parse(Rid))), Regions = TestSettings.RegionsFor(new RegionSettings("Welcome", "Welcome", Guid.Parse(Rid))),
    });

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> GetAsync(string url)
    {
        using var resp = await _factory.CreateClient().GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadAsStringAsync(Ct);
    }

    [Fact]
    public async Task Alt_query_echoes_requested_region()
    {
        using var resp = await _factory.CreateClient().GetAsync(
            $"/?alt={Rid}&agentid=11111111-0000-0000-0000-000000000001&password=x", Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/plain", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Rid, await resp.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Type_query_is_acknowledged()
    {
        Assert.Equal("ACK", await GetAsync($"/?type=Booted&UUID={Rid}"));
    }

    [Fact]
    public async Task Parameter_names_are_case_insensitive()
    {
        var capital = await GetAsync("/?Command=RegionStatus&RegionName=Welcome");
        var lower = await GetAsync("/?command=regionstatus&regionname=Welcome");

        Assert.Equal("Running", capital);
        Assert.Equal(capital, lower);
    }

    [Fact]
    public async Task Command_by_POST_form_with_the_MachineId()
    {
        var machineId = _factory.Store.LoadOrCreate().MachineId;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["COMMAND"] = "StopMysql",
            ["Password"] = machineId,
        });

        using var resp = await _factory.CreateClient().PostAsync("/", form, Ct);

        Assert.Equal("ACK", await resp.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Stop database", _factory.Grid.Snapshot());
    }

    [Fact]
    public async Task With_the_dashboard_on_root_legacy_calls_still_reach_the_legacy_handler()
    {
        await using var f = new LauncherFactory(s => s with
        {
            Setup = s.Setup with { OwnerCreated = true, Finished = true },
            Programs = TestSettings.ProgramFor(new RegionSettings("Welcome", "Welcome", Guid.Parse(Rid))), Regions = TestSettings.RegionsFor(new RegionSettings("Welcome", "Welcome", Guid.Parse(Rid))),
        })
        {
            Before = TestAuth.Apply,
        };
        using var client = f.CreateClient(new() { AllowAutoRedirect = false });
        // Signed in, so the dashboard page is a valid candidate for "/" as well.
        (await client.GetAsync("/__test/signin", Ct)).Dispose();
        var machineId = f.Store.LoadOrCreate().MachineId;

        using var type = await client.GetAsync("/?type=Booted", Ct);
        using var command = await client.GetAsync("/?command=RegionStatus&RegionName=x", Ct);
        using var post = await client.PostAsync("/", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["command"] = "StopMysql",
            ["password"] = machineId,
        }), Ct);
        using var postNoKey = await client.PostAsync("/", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);
        using var page = await client.GetAsync("/", Ct);

        Assert.Equal("ACK", await type.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, command.StatusCode);
        Assert.Equal("text/plain", command.Content.Headers.ContentType?.MediaType);
        Assert.Equal("ACK", await post.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Stop database", f.Grid.Snapshot());
        // A POST with no known key: the legacy handler's 404, as before the dashboard existed.
        Assert.Equal(HttpStatusCode.NotFound, postNoKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Your world", await page.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Command_log_names_the_command_but_never_the_password()
    {
        var machineId = _factory.Store.LoadOrCreate().MachineId;
        await GetAsync($"/?command=StopMysql&password={machineId}");
        await GetAsync("/?command=StopRobust&avatar=God%20Owner&password=sekrit-avatar-pw");

        Assert.True(await _factory.WaitForLogAsync(l => l.Message.Contains("StopRobust")));
        Assert.DoesNotContain(_factory.Logs, l => l.Message.Contains(machineId) || l.Message.Contains("sekrit-avatar-pw"));
    }

    [Fact]
    public async Task RegionReady_post_sets_logins_enabled()
    {
        var json = "{\"alert\":\"region_ready\",\"login\":\"enabled\",\"region_name\":\"Welcome\",\"region_id\":\"" + Rid + "\"}";
        using var body = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _factory.CreateClient().PostAsync("/dreamgrid/regionready", body, Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(_factory.Grid.GetStatus().LoginsEnabled);
    }

    [Fact]
    public async Task RegionReady_post_with_garbage_is_400()
    {
        using var body = new StringContent("not json", Encoding.UTF8, "application/json");

        using var resp = await _factory.CreateClient().PostAsync("/dreamgrid/regionready", body, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.DoesNotContain(_factory.Grid.Snapshot(), c => c.StartsWith("Alert", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RegionReady_post_from_another_machine_is_refused()
    {
        await using var factory = new LauncherFactory
        {
            Before = app => app.Use((ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
                return next(ctx);
            }),
        };
        var json = "{\"alert\":\"region_ready\",\"login\":\"enabled\",\"region_name\":\"Welcome\",\"region_id\":\"" + Rid + "\"}";
        using var body = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await factory.CreateClient().PostAsync("/dreamgrid/regionready", body, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(factory.Grid.GetStatus().LoginsEnabled);
    }

    [Theory]
    [InlineData("203.0.113.5", HttpStatusCode.NotFound, false)]
    [InlineData("10.2.3.4", HttpStatusCode.OK, true)]
    public async Task Command_api_obeys_allow_list(string caller, HttpStatusCode expected, bool ran)
    {
        await using var factory = new LauncherFactory(s => s with
        {
            Security = s.Security with { AdminNetworks = ["10.0.0.0/8"] },
        })
        {
            Before = app => app.Use((ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Parse(caller);
                return next(ctx);
            }),
        };
        var machineId = factory.Store.LoadOrCreate().MachineId;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["COMMAND"] = "StopMysql",
            ["Password"] = machineId,
        });

        using var resp = await factory.CreateClient().PostAsync("/", form, Ct);

        // Outside the list even the right MachineId runs nothing, and the reply says nothing.
        Assert.Equal(expected, resp.StatusCode);
        Assert.Equal(ran, factory.Grid.Snapshot().Contains("Stop database"));
        if (!ran) Assert.Equal("", await resp.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Welcome_page_names_the_grid()
    {
        using var resp = await _factory.CreateClient().GetAsync("/welcome", Ct);
        var html = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Avalon &lt;Isle&gt;", html);
        Assert.DoesNotContain("<Isle>", html);
    }

    [Fact]
    public async Task Setup_link_is_logged_until_the_owner_exists()
    {
        var s = _factory.Store.LoadOrCreate();
        _factory.CreateClient().Dispose(); // starts the host

        var expected = $":{s.WebPort}/setup?token={s.SetupToken}";
        Assert.True(await _factory.WaitForLogAsync(l =>
            l.Message.StartsWith("Setup link: http://", StringComparison.Ordinal) && l.Message.EndsWith(expected, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Setup_link_is_not_logged_once_the_owner_exists()
    {
        await using var factory = new LauncherFactory(s => s with { Setup = s.Setup with { OwnerCreated = true } });
        factory.CreateClient().Dispose();

        Assert.True(await factory.WaitForLogAsync(l => l.Message.Contains("Now listening", StringComparison.Ordinal)
            || l.Category.EndsWith("GridHostedService", StringComparison.Ordinal)));
        Assert.DoesNotContain(factory.Logs, l => l.Message.StartsWith("Setup link", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_port_conflict_is_logged_Critical_and_the_host_keeps_answering()
    {
        _factory.Grid.OnStart = _ => throw new PortConflictException("Port 8002 (Robust public) is already in use by another program.");

        Assert.Equal("ACK", await GetAsync("/?type=Booted"));
        await _factory.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(await _factory.WaitForLogAsync(l =>
            l.Level == LogLevel.Critical && l.Message.Contains("Port 8002 (Robust public) is already in use by another program.")));
        Assert.Equal("Running", await GetAsync("/?command=RegionStatus"));
    }

    [Fact]
    public async Task Any_other_start_failure_is_logged_Error_and_the_host_keeps_answering()
    {
        _factory.Grid.OnStart = _ => throw new InvalidOperationException("MariaDB did not start");

        Assert.Equal("ACK", await GetAsync("/?type=Booted"));
        await _factory.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(await _factory.WaitForLogAsync(l => l.Level == LogLevel.Error && l.Message.Contains("MariaDB did not start")));
        Assert.Equal("Running", await GetAsync("/?command=RegionStatus"));
    }

    [Fact]
    public async Task Settings_come_from_the_grid_controller_not_the_file()
    {
        var file = _factory.Store.LoadOrCreate();
        var cached = new RegionSettings("Cached", "Cached", Guid.NewGuid());
        _factory.Grid.SettingsSource = () => file with
        {
            GridName = "Cached Name",
            Programs = TestSettings.ProgramFor(cached), Regions = TestSettings.RegionsFor(cached),
        };

        Assert.Equal("Running", await GetAsync("/?command=RegionStatus&RegionName=Cached"));
        Assert.StartsWith("NAK", await GetAsync("/?command=RegionStatus&RegionName=Welcome"));
        Assert.Contains("Cached Name", await GetAsync("/welcome"));
    }

    [Fact]
    public async Task Aborting_the_request_does_not_cancel_the_grid_operation()
    {
        var request = new TaskCompletionSource<HttpContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = new LauncherFactory
        {
            Before = app => app.Use((ctx, next) => { request.TrySetResult(ctx); return next(ctx); }),
        };
        var machineId = factory.Store.LoadOrCreate().MachineId;
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Grid.OnComponent = async ct => { entered.TrySetResult(ct); await release.Task; };
        var client = factory.CreateClient();

        try
        {
            var call = client.GetAsync($"/?command=StopRobust&password={machineId}", Ct);
            var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            var ctx = await request.Task;
            ctx.Abort(); // the caller went away
            Assert.True(ctx.RequestAborted.IsCancellationRequested);

            Assert.False(token.IsCancellationRequested);
            release.TrySetResult();
            await Record.ExceptionAsync(() => call); // the aborted request may fail on the client side
            Assert.Contains("Stop robust", factory.Grid.Snapshot());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Untrusted_values_are_logged_on_one_line_and_truncated()
    {
        var evil = "Booted%0D%0Afake: Robust stopped%07" + new string('x', 500);
        await GetAsync("/?type=" + evil + "&UUID=" + Rid);
        await GetAsync("/?command=Nope%0Ainjected");

        Assert.True(await _factory.WaitForLogAsync(l => l.Message.StartsWith("Command ", StringComparison.Ordinal)));
        var mine = _factory.Logs.Where(l => l.Category == "DreamGrid.Launcher.LegacyEndpoints").ToList();
        Assert.Equal(2, mine.Count);
        Assert.All(mine, l =>
        {
            Assert.DoesNotContain(l.Message, char.IsControl);
            Assert.True(l.Message.Length < 300, l.Message);
        });
    }

    [Theory]
    [InlineData("/?alt=x")]
    [InlineData("/?type=Booted")]
    [InlineData("/?command=RegionStatus")]
    public async Task Text_replies_are_not_sniffed(string url)
    {
        using var resp = await _factory.CreateClient().GetAsync(url, Ct);

        Assert.Equal("nosniff", string.Join(",", resp.Headers.GetValues("X-Content-Type-Options")));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.1.50", false)]
    [InlineData("::ffff:192.168.1.50", false)]
    public void Only_loopback_callers_count_as_local(string address, bool local)
    {
        Assert.Equal(local, LegacyEndpoints.IsLoopbackCaller(IPAddress.Parse(address)));
    }

    [Fact]
    public void Parser_reads_names_case_insensitively_and_the_form_wins()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["COMMAND"] = "StartRegion",
            ["Avatar"] = "God Owner",
            ["regionuuid"] = Rid,
            ["password"] = "from-query",
        });
        var form = new FormCollection(new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["PASSWORD"] = "from-form",
            ["regionname"] = "Welcome",
        });

        var r = LegacyRequestParser.From(query, form);

        Assert.Equal("StartRegion", r.Command);
        Assert.Equal("God Owner", r.Avatar);
        Assert.Equal("from-form", r.Password);
        Assert.Equal("Welcome", r.RegionName);
        Assert.Equal(Guid.Parse(Rid), r.RegionUuid);
        Assert.Equal("Welcome", r.Raw["RegionName"]);
    }

    [Fact]
    public void Parser_without_a_form_or_a_valid_uuid()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues> { ["command"] = "RegionStatus", ["RegionUUID"] = "nope" });

        var r = LegacyRequestParser.From(query, null);

        Assert.Equal("RegionStatus", r.Command);
        Assert.Null(r.Password);
        Assert.Null(r.RegionUuid);
    }
}
