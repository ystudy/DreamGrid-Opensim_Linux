using System.Net;
using System.Net.Sockets;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// GridController behaviour that needs no real child process: a fake program dir (real prototypes, dummy
/// OpenSim.dll), fake Robust and a fake console server reached through the IHttpClientFactory.
/// </summary>
public sealed class GridControllerWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-wiring-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;
    private readonly SettingsStore _store;
    private readonly FakeRobust _robust = new();
    private readonly ConsoleHttp _http = new();
    private readonly GridController _grid;

    public GridControllerWiringTests()
    {
        var program = Path.Combine(_root, "program");
        _paths = new LauncherPaths(Path.Combine(_root, "data"), program, null);
        CopyPrototypes(program);
        File.WriteAllBytes(Path.Combine(program, "OpenSim.dll"), [1]);
        new Core.Runtime.RuntimeStager(_paths).StageIfNeeded();

        _store = new SettingsStore(_paths.SettingsFile);
        var p = FreePorts(7);
        _store.Save(_store.LoadOrCreate() with
        {
            Hostname = "127.0.0.1",
            WebPort = p[0], RobustPublicPort = p[1], RobustPrivatePort = p[2], RegionPort = p[3],
            MariaDbPort = p[4], RobustConsolePort = p[5], RegionConsolePort = p[6],
        });
        // A MariaDB that cannot start: anything past the port check fails with a non-port error.
        var missing = Path.Combine(_root, "no-such-dir", "mariadbd");
        _grid = new GridController(_paths, _store, new MariaDbBinaries(missing, missing, null),
            [new LogLevelModule()], _robust, _http, TimeProvider.System);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static void CopyPrototypes(string program)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DreamGrid.sln"))) dir = dir.Parent;
        var bin = Path.Combine(dir!.FullName, "bin");
        Directory.CreateDirectory(Path.Combine(program, "config-include", "Proto", "Robust"));
        File.Copy(Path.Combine(bin, "Robust.HG.ini.proto"), Path.Combine(program, "Robust.HG.ini.proto"));
        File.Copy(Path.Combine(bin, "config-include", "FlotsamCache.ini.example"),
            Path.Combine(program, "config-include", "FlotsamCache.ini.example"));
        foreach (var f in new[] { "GridCommon.ini", "OpenSim.ini" })
            File.Copy(Path.Combine(bin, "config-include", "Proto", "Robust", f),
                Path.Combine(program, "config-include", "Proto", "Robust", f));
    }

    private static int[] FreePorts(int count)
    {
        var ls = new List<TcpListener>();
        while (ls.Count < count)
        {
            var l = new TcpListener(IPAddress.Any, 0);
            l.Start();
            ls.Add(l);
        }
        var ports = ls.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
        foreach (var l in ls) l.Stop();
        return ports;
    }

    [Fact]
    public async Task StartAsync_port_check_ignores_the_web_port_the_host_already_holds()
    {
        var s = _store.LoadOrCreate();
        var web = new TcpListener(IPAddress.Any, s.WebPort);
        web.Start();
        try
        {
            var e = await Record.ExceptionAsync(() => _grid.StartAsync(TestContext.Current.CancellationToken));
            Assert.NotNull(e); // MariaDB cannot start in this rig
            Assert.IsNotType<PortConflictException>(e);
        }
        finally { web.Stop(); }
    }

    [Fact]
    public async Task StartAsync_port_check_still_reports_a_held_Robust_port()
    {
        var s = _store.LoadOrCreate();
        var robust = new TcpListener(IPAddress.Any, s.RobustPublicPort);
        robust.Start();
        try
        {
            var e = await Assert.ThrowsAsync<PortConflictException>(() => _grid.StartAsync(TestContext.Current.CancellationToken));
            Assert.Contains(s.RobustPublicPort.ToString(), e.Message);
        }
        finally { robust.Stop(); }
    }

    [Fact]
    public async Task A_changed_console_port_is_used_once_the_component_is_not_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = _store.LoadOrCreate().RobustConsolePort;
        await _grid.CreateOwnerAsync("Test", "Owner", "pw1", null, ct);
        Assert.Contains(first, _http.CommandPorts);

        var changed = first + 1;
        _store.Save(_store.LoadOrCreate() with { RobustConsolePort = changed });
        _http.CommandPorts.Clear();
        await _grid.CreateOwnerAsync("Test", "Owner", "pw1", null, ct);

        Assert.Equal(new[] { changed }, _http.CommandPorts.Distinct());
    }

    [Fact]
    public async Task RegenerateConfig_and_GetStatus_do_not_wait_behind_a_long_operation()
    {
        var gate = new TaskCompletionSource();
        _robust.Block = gate.Task;
        var owner = _grid.CreateOwnerAsync("Test", "Owner", "pw1", null, TestContext.Current.CancellationToken);
        await _robust.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var quick = Task.Run(() => { _grid.RegenerateConfig(); return _grid.GetStatus(); }, TestContext.Current.CancellationToken);
        var status = await quick.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(_paths.RuntimeOpenSim, "Robust.HG.ini")));
        Assert.Equal($"http://127.0.0.1:{_store.LoadOrCreate().RobustPublicPort}", status.LoginUri);

        gate.SetResult();
        await owner;
    }

    private void AddRegion() =>
        _store.Save(_store.LoadOrCreate() with { Region = new RegionSettings("Welcome", "Welcome", Guid.NewGuid()) });

    [Fact]
    public async Task StartComponent_region_without_a_region_throws()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _grid.StartComponentAsync("region", TestContext.Current.CancellationToken));
        Assert.Contains("region", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartComponent_region_needs_Robust_running()
    {
        AddRegion();

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _grid.StartComponentAsync("region", TestContext.Current.CancellationToken));

        Assert.Contains("Robust is not running", e.Message);
        Assert.Empty(_http.CommandPorts);
        Assert.False(File.Exists(Path.Combine(_paths.Pids, "Welcome.pid")));
    }

    [Fact]
    public async Task StartComponent_robust_needs_the_database_running()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _grid.StartComponentAsync("robust", TestContext.Current.CancellationToken));

        Assert.Contains("database is not running", e.Message);
        Assert.False(File.Exists(Path.Combine(_paths.Pids, "robust.pid")));
    }

    [Fact]
    public async Task Component_operations_reject_unknown_names()
    {
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentException>(() => _grid.StartComponentAsync("apache", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _grid.StopComponentAsync("apache", ct));
    }

    [Fact]
    public async Task StopComponent_database_stops_the_region_then_Robust_first()
    {
        AddRegion();
        var s = _store.LoadOrCreate();

        await _grid.StopComponentAsync("database", TestContext.Current.CancellationToken);

        // Each graceful stop is a console "quit"; MariaDB's SHUTDOWN goes over SQL, not the console.
        Assert.Equal(new[] { s.RegionConsolePort, s.RobustConsolePort }, _http.CommandPorts);
    }

    [Fact]
    public async Task StopComponent_robust_leaves_the_region_alone()
    {
        AddRegion();
        var s = _store.LoadOrCreate();

        await _grid.StopComponentAsync("robust", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { s.RobustConsolePort }, _http.CommandPorts);
    }

    [Fact]
    public async Task StopComponent_region_stops_only_the_region()
    {
        AddRegion();
        var s = _store.LoadOrCreate();

        await _grid.StopComponentAsync("region", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { s.RegionConsolePort }, _http.CommandPorts);
        Assert.False(_grid.LoginsEnabled);
    }

    private sealed class FakeRobust : IRobustClient
    {
        public Task Block { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(false);

        public async Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Block;
            return new RobustAccount(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), first, last, 200, "");
        }

        public Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>A console server that accepts every command, prints nothing, and records which port each command went to.</summary>
    private sealed class ConsoleHttp : IHttpClientFactory
    {
        public List<int> CommandPorts { get; } = new();

        public HttpClient CreateClient(string name) => new(new StubHandler(r =>
        {
            switch (r.Uri.AbsolutePath)
            {
                case "/StartSession/":
                    return (HttpStatusCode.OK, "<ConsoleSession><SessionID>sid</SessionID></ConsoleSession>");
                case "/SessionCommand/":
                    lock (CommandPorts) CommandPorts.Add(r.Uri.Port);
                    return (HttpStatusCode.OK, "<ConsoleSession><Result>OK</Result></ConsoleSession>");
                default:
                    return (HttpStatusCode.OK, "<ConsoleSession></ConsoleSession>");
            }
        }));
    }
}
