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
        Region = new RegionSettings("Welcome", "Welcome", Guid.Parse(Rid)),
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
    public async Task Bare_root_returns_Test_Completed()
    {
        Assert.Equal("Test Completed", await GetAsync("/"));
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

    [Fact]
    public async Task Welcome_page_names_the_grid()
    {
        using var resp = await _factory.CreateClient().GetAsync("/welcome", Ct);
        var html = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Welcome to Avalon &lt;Isle&gt;", html);
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

        Assert.Equal("Test Completed", await GetAsync("/"));
        await _factory.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(await _factory.WaitForLogAsync(l =>
            l.Level == LogLevel.Critical && l.Message.Contains("Port 8002 (Robust public) is already in use by another program.")));
        Assert.Equal("Running", await GetAsync("/?command=RegionStatus"));
    }

    [Fact]
    public async Task Any_other_start_failure_is_logged_Error_and_the_host_keeps_answering()
    {
        _factory.Grid.OnStart = _ => throw new InvalidOperationException("MariaDB did not start");

        Assert.Equal("Test Completed", await GetAsync("/"));
        await _factory.Grid.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(await _factory.WaitForLogAsync(l => l.Level == LogLevel.Error && l.Message.Contains("MariaDB did not start")));
        Assert.Equal("Running", await GetAsync("/?command=RegionStatus"));
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
