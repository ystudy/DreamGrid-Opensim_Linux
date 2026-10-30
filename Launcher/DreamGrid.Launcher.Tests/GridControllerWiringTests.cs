using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.Time.Testing;
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
        var p = _ports = FreePorts(9);
        (_secondPort, _secondConsole) = (p[7], p[8]);
        _store.Save(_store.LoadOrCreate() with
        {
            Hostname = "127.0.0.1",
            WebPort = p[0], RobustPublicPort = p[1], RobustPrivatePort = p[2], RegionPort = p[3],
            MariaDbPort = p[4], RobustConsolePort = p[5], RegionConsolePort = p[6],
        });
        _grid = NewGrid(TimeProvider.System);
    }

    /// <summary>The folder of the rig's MariaDB, which does not exist.</summary>
    private const string MissingMariaDb = "no-such-dir";

    /// <param name="launchStandIns">A program the controller starts is a stand-in serving like a leftover one, instead of OpenSim.</param>
    private GridController NewGrid(TimeProvider time, Action<int, IReadOnlyList<int>, CpuPriority>? cpu = null, bool launchStandIns = false)
    {
        // A MariaDB that cannot start: anything past the port check fails with a non-port error.
        var missing = Path.Combine(_root, MissingMariaDb, "mariadbd");
        return new GridController(_paths, _store, new MariaDbBinaries(missing, missing, null),
            [new LogLevelModule()], _robust, new FakeGridJoin(), new FakeOtherGrid(), _http, time, regionStores: _ => _regionStore, cpu: cpu ?? ((_, _, _) => { }))
        {
            DatabaseRunningForTests = () => _databaseUp,
            ProgramStartForTests = launchStandIns ? _ => StandIn() : null,
        };
    }

    /// <summary>What the controller's region store gets; the rig has no database.</summary>
    private readonly FakeRegionStore _regionStore = new();

    /// <summary>Whether the controller sees the database running (the rig's MariaDB never starts).</summary>
    private volatile bool _databaseUp;

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

    /// <summary>The second program's ports, free like every other port (a fixed offset from the first is often taken).</summary>
    private readonly int _secondPort, _secondConsole;

    /// <summary>Every port the settings hold; a stand-in's own port must not be one of them, or starting reports a port conflict.</summary>
    private readonly int[] _ports;

    private static int[] FreePorts(int count) => TestPorts.Free(count);

    /// <summary>A free port for a stand-in to serve on, never one the settings already hold (a released port can be handed out again).</summary>
    private int StandInPort()
    {
        while (true)
            if (FreePorts(1)[0] is var port && !_ports.Contains(port)) return port;
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
    public async Task A_failed_start_is_kept_in_the_status_with_the_conflicting_port()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = _store.LoadOrCreate();
        var robust = new TcpListener(IPAddress.Any, s.RobustPublicPort);
        robust.Start();
        PortConflictException conflict;
        try
        {
            conflict = await Assert.ThrowsAsync<PortConflictException>(() => _grid.StartAsync(ct));
        }
        finally { robust.Stop(); }
        Assert.Equal(s.RobustPublicPort, conflict.Port);
        Assert.Equal(new StartFailure(conflict.Message, s.RobustPublicPort), _grid.GetStatus().StartFailure);
        Assert.Equal(GridActivity.None, _grid.GetStatus().Activity);

        // Any other failure is kept too (MariaDB cannot start in this rig), without a port.
        var e = await Record.ExceptionAsync(() => _grid.StartAsync(ct));
        Assert.IsNotType<PortConflictException>(e);
        Assert.Equal(new StartFailure(e!.Message, null), _grid.GetStatus().StartFailure);
    }

    [Fact]
    public async Task Stop_all_clears_an_earlier_start_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = _store.LoadOrCreate();
        var robust = new TcpListener(IPAddress.Any, s.RobustPublicPort);
        robust.Start();
        try
        {
            await Assert.ThrowsAsync<PortConflictException>(() => _grid.StartAsync(ct));
        }
        finally { robust.Stop(); }
        Assert.NotNull(_grid.GetStatus().StartFailure);

        await _grid.StopAllAsync(ct);

        Assert.Null(_grid.GetStatus().StartFailure);
    }

    [Fact]
    public async Task A_port_conflict_on_a_component_start_is_kept_in_the_status()
    {
        var s = _store.LoadOrCreate();
        var robust = new TcpListener(IPAddress.Any, s.RobustPublicPort);
        robust.Start();
        try
        {
            await Assert.ThrowsAsync<PortConflictException>(() => _grid.StartComponentAsync("database", TestContext.Current.CancellationToken));
        }
        finally { robust.Stop(); }
        Assert.Equal(s.RobustPublicPort, _grid.GetStatus().StartFailure?.ConflictPort);
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
    public async Task CreateOwner_waits_for_Robust_to_apply_the_console_commands()
    {
        // Robust runs console commands on its own thread: under load the account (and then its level) shows up
        // only after the console output has settled.
        foreach (var level in new int?[] { null, null, null, 0, 0 }) _robust.Levels.Enqueue(level);

        var account = await _grid.CreateOwnerAsync("Test", "Owner", "pw1", null, TestContext.Current.CancellationToken);

        Assert.Equal(200, account.UserLevel);
        Assert.Empty(_robust.Levels);
    }

    [Fact]
    public async Task CreateOwner_gives_up_after_30_s_when_Robust_never_shows_the_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _robust.Missing = true;
        _robust.Clock = time.GetUtcNow;
        var owner = NewGrid(time).CreateOwnerAsync("Test", "Owner", "pw1", null, ct);

        // Fake time runs only while the controller polls; the console settle waits in real time.
        var guard = DateTime.UtcNow.AddSeconds(60);
        while (!owner.IsCompleted && DateTime.UtcNow < guard)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10, ct);
        }

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => owner);
        Assert.Contains("Robust did not create Test Owner within 30 s", e.Message);
        // The first lookup only decides create vs. reset; the polls after it span the full 30 s.
        var polls = _robust.Calls.Skip(1).ToList();
        Assert.True(polls.Count > 2, $"only {polls.Count} polls");
        Assert.True(polls[^1] - polls[0] >= TimeSpan.FromSeconds(30), $"gave up after {polls[^1] - polls[0]}");
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

    private void AddRegion(bool created = true)
    {
        var s = _store.LoadOrCreate();
        _store.Save(s with
        {
            Programs = [new ProgramSettings("Welcome", s.RegionPort, s.RegionConsolePort)],
            Regions = [new RegionSettings("Welcome", "Welcome", Guid.NewGuid(), UdpPort: s.RegionPort)],
            Setup = s.Setup with { RegionCreated = created },
        });
    }

    [Fact]
    public async Task StartComponent_on_an_idle_grid_stages_and_writes_the_config_first()
    {
        var ini = Path.Combine(_paths.RuntimeOpenSim, "Robust.HG.ini");
        Assert.False(File.Exists(ini));

        var e = await Record.ExceptionAsync(() => _grid.StartComponentAsync("database", TestContext.Current.CancellationToken));

        Assert.NotNull(e); // MariaDB cannot start in this rig
        Assert.True(File.Exists(ini));
    }

    [Fact]
    public async Task StartComponent_on_an_idle_grid_runs_the_port_check()
    {
        var s = _store.LoadOrCreate();
        var robust = new TcpListener(IPAddress.Any, s.RobustPublicPort);
        robust.Start();
        try
        {
            var e = await Assert.ThrowsAsync<PortConflictException>(() =>
                _grid.StartComponentAsync("database", TestContext.Current.CancellationToken));
            Assert.Contains(s.RobustPublicPort.ToString(), e.Message);
        }
        finally { robust.Stop(); }
    }

    [Fact]
    public async Task A_missing_MariaDB_surfaces_its_own_message_on_start()
    {
        var grid = new GridController(_paths, _store, MariaDbBinaries.NotFound("MariaDB server not found; searched: /nowhere/mariadbd"),
            [new LogLevelModule()], _robust, new FakeGridJoin(), new FakeOtherGrid(), _http, TimeProvider.System);

        var e = await Assert.ThrowsAsync<MariaDbNotFoundException>(() => grid.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("MariaDB server not found; searched: /nowhere/mariadbd", e.Message);
    }

    [Fact]
    public void CurrentSettings_is_the_controller_cache_and_does_not_reread_the_file()
    {
        var first = _grid.CurrentSettings();
        _store.Save(first with { GridName = "Changed on disk" });

        Assert.Equal(first.GridName, _grid.CurrentSettings().GridName);
        _grid.RegenerateConfig(); // reloads
        Assert.Equal("Changed on disk", _grid.CurrentSettings().GridName);
    }

    [Fact]
    public async Task Starting_an_unknown_region_throws()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _grid.RegionPowerAsync(Guid.NewGuid(), RegionPower.Start, TestContext.Current.CancellationToken));
        Assert.Contains("region", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Starting_a_region_needs_Robust_running()
    {
        AddRegion();
        var id = RegionModel.First(_store.LoadOrCreate())!.Uuid;

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _grid.RegionPowerAsync(id, RegionPower.Start, TestContext.Current.CancellationToken));

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
        // Regions go through RegionPowerAsync (plan 4a).
        await Assert.ThrowsAsync<ArgumentException>(() => _grid.StartComponentAsync("region", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _grid.StopComponentAsync("region", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _grid.RestartComponentAsync("region", ct));
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
    public async Task Stopping_a_region_holds_it_until_the_world_starts()
    {
        AddRegion();
        var id = RegionModel.First(_store.LoadOrCreate())!.Uuid;

        await _grid.RegionPowerAsync(id, RegionPower.Stop, TestContext.Current.CancellationToken);

        Assert.Empty(_http.CommandPorts); // its program wasn't running: nothing to stop
        Assert.Equal(RegionState.Stopped, _grid.GetStatus().Regions.Single().State);
        Assert.False(_grid.LoginsEnabled);
        Assert.True(File.Exists(Path.Combine(_paths.RegionDefinitionDir("Welcome"), "Welcome.ini.off")) ||
                    !File.Exists(Path.Combine(_paths.RegionDefinitionDir("Welcome"), "Welcome.ini")));
    }

    private static string FakeChildExe()
    {
        var dir = typeof(GridControllerWiringTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "FakeChildDir").Value!;
        return Path.Combine(dir, OperatingSystem.IsWindows() ? "DreamGrid.FakeChild.exe" : "DreamGrid.FakeChild");
    }

    [Fact]
    public async Task An_upgrade_stops_an_adoptable_Robust_before_restaging()
    {
        // I4: on Linux the runtime dir can be deleted under a running Robust, which then runs on in a deleted cwd.
        var ct = TestContext.Current.CancellationToken;
        var s = _store.LoadOrCreate();
        var stamp = Path.Combine(_paths.RuntimeOpenSim, ".dreamgrid-stamp");
        var oldStamp = File.ReadAllText(stamp);
        // A stand-in Robust whose command line names the staged Robust.dll, so the controller would adopt it.
        var psi = new ProcessStartInfo(FakeChildExe()) { UseShellExecute = false };
        psi.ArgumentList.Add("--serve");
        psi.ArgumentList.Add(StandInPort().ToString());
        psi.ArgumentList.Add(Path.Combine(_paths.RuntimeOpenSim, "Robust.dll"));
        using var fake = Process.Start(psi)!;
        try
        {
            _paths.EnsureCreated();
            PidFile.Write(Path.Combine(_paths.Pids, "robust.pid"), fake.Id);
            string? stampAtStop = null;
            _http.OnCommand = port =>
            {
                if (port != s.RobustConsolePort) return;
                stampAtStop = File.ReadAllText(stamp);
                fake.Kill(); // what Robust does on "quit"
            };
            File.SetLastWriteTimeUtc(Path.Combine(_paths.OpenSimProgram, "OpenSim.dll"), DateTime.UtcNow.AddHours(1));

            var e = await Record.ExceptionAsync(() => _grid.StartAsync(ct));

            Assert.NotNull(e); // MariaDB cannot start in this rig
            Assert.Equal(oldStamp, stampAtStop); // Robust was asked to quit before the old runtime went away
            Assert.True(fake.WaitForExit(5000), "the old Robust still runs");
            Assert.NotEqual(oldStamp, File.ReadAllText(stamp));
        }
        finally
        {
            if (!fake.HasExited) fake.Kill();
        }
    }

    [Fact]
    public async Task A_world_name_saved_while_a_start_stages_is_in_the_config_it_writes()
    {
        // Setup's world step can land while the launcher's first start stages the runtime: that start's config must not
        // be the settings it read before (Robust would come up with the old world name).
        var ct = TestContext.Current.CancellationToken;
        var s = _store.LoadOrCreate();
        var psi = new ProcessStartInfo(FakeChildExe()) { UseShellExecute = false };
        psi.ArgumentList.Add("--serve");
        psi.ArgumentList.Add(StandInPort().ToString());
        psi.ArgumentList.Add(Path.Combine(_paths.RuntimeOpenSim, "Robust.dll"));
        using var fake = Process.Start(psi)!;
        try
        {
            _paths.EnsureCreated();
            PidFile.Write(Path.Combine(_paths.Pids, "robust.pid"), fake.Id);
            _http.OnCommand = port =>
            {
                if (port != s.RobustConsolePort) return;
                // Mid-start, after the start read the settings: the person names the world.
                _grid.UpdateSettings(l => l with { GridName = "Named Meanwhile" });
                fake.Kill();
            };
            File.SetLastWriteTimeUtc(Path.Combine(_paths.OpenSimProgram, "OpenSim.dll"), DateTime.UtcNow.AddHours(1));

            var e = await Record.ExceptionAsync(() => _grid.StartAsync(ct));

            Assert.NotNull(e); // MariaDB cannot start in this rig
            var ini = File.ReadAllText(Path.Combine(_paths.RuntimeOpenSim, "Robust.HG.ini"));
            Assert.Contains("Named Meanwhile", ini);
        }
        finally
        {
            if (!fake.HasExited) fake.Kill();
        }
    }

    /// <summary>
    /// A stand-in for the region program (default "Welcome") an earlier launcher run left behind: its command line names the staged
    /// OpenSim.dll and its PID file is written after it started, so the controller would adopt it.
    /// </summary>
    private Process StartLeftoverRegion(string instance = "Welcome")
    {
        var fake = Process.Start(StandIn())!;
        _paths.EnsureCreated();
        PidFile.Write(Path.Combine(_paths.Pids, instance + ".pid"), fake.Id);
        return fake;
    }

    /// <summary>A region program stand-in: its command line names the staged OpenSim.dll, so the controller takes it for one.</summary>
    private ProcessStartInfo StandIn()
    {
        var psi = new ProcessStartInfo(FakeChildExe()) { UseShellExecute = false };
        psi.ArgumentList.Add("--serve");
        psi.ArgumentList.Add(StandInPort().ToString());
        psi.ArgumentList.Add(Path.Combine(_paths.RuntimeOpenSim, "OpenSim.dll"));
        return psi;
    }

    /// <summary>Stops watching (so no monitor restarts the stand-in), then kills it.</summary>
    private async Task EndLeftoverRegionAsync(Process fake)
    {
        await _grid.DetachAsync();
        if (!fake.HasExited) fake.Kill();
        fake.Dispose();
    }

    private static readonly Guid Beach = Guid.Parse("50000000-0000-0000-0000-000000000002");

    /// <summary>Welcome (2x2 at 1000, 1000) and Beach (1x1 at 1010, 1000), each in a program of its own.</summary>
    private Guid TwoRegions(bool created = true)
    {
        var s = _store.LoadOrCreate();
        var welcome = RegionModel.First(s)?.Uuid ?? Guid.NewGuid();
        _store.Save(s with
        {
            Programs = [new ProgramSettings("Welcome", s.RegionPort, s.RegionConsolePort), new ProgramSettings("Beach", _secondPort, _secondConsole)],
            Regions =
            [
                new RegionSettings("Welcome", "Welcome", welcome, Size: 2, UdpPort: s.RegionPort),
                new RegionSettings("Beach", "Beach", Beach, X: 1010, UdpPort: _secondPort),
            ],
            Setup = s.Setup with { RegionCreated = created },
        });
        return welcome;
    }

    [Fact]
    public async Task Move_saves_spot_and_restarts_running_program()
    {
        var id = TwoRegions(created: false);
        var regionPort = _store.LoadOrCreate().RegionConsolePort;
        var fake = StartLeftoverRegion();
        try
        {
            Assert.IsNotType<PortConflictException>(await Record.ExceptionAsync(() => _grid.StartAsync(TestContext.Current.CancellationToken))); // MariaDB cannot start; the region is adopted
            Assert.Equal(ProcessState.Running, _grid.GetStatus().Programs.Single(p => p.Instance == "Welcome").Process.State);
            _http.OnCommand = port =>
            {
                if (port == regionPort && _http.Commands(port).Contains("quit")) fake.Kill(); // what the region does on "quit"
            };

            // Overlapping its own old square is fine. The restart itself can't finish here (the stand-in OpenSim is one byte).
            Assert.IsNotType<PortConflictException>(await Record.ExceptionAsync(() => _grid.MoveRegionAsync(id, 1001, 1000, TestContext.Current.CancellationToken)));

            var saved = RegionModel.Find(_store.LoadOrCreate(), id)!;
            Assert.Equal((1001, 1000), (saved.X, saved.Y));
            Assert.Contains("quit", _http.Commands(regionPort)); // the running program was stopped to start again
        }
        finally { await EndLeftoverRegionAsync(fake); }
    }

    [Fact]
    public async Task Move_of_stopped_region_restarts_nothing()
    {
        var id = TwoRegions();

        await _grid.MoveRegionAsync(id, 2000, 3000, TestContext.Current.CancellationToken);

        var saved = RegionModel.Find(_store.LoadOrCreate(), id)!;
        Assert.Equal((2000, 3000), (saved.X, saved.Y));
        Assert.Equal((2000, 3000), (RegionModel.Find(_grid.CurrentSettings(), id)!.X, RegionModel.Find(_grid.CurrentSettings(), id)!.Y));
        Assert.Empty(_http.CommandPorts);
        Assert.All(_grid.GetStatus().Programs, p => Assert.Equal(ProcessState.Stopped, p.Process.State));
    }

    [Fact]
    public async Task Move_onto_another_region_is_refused()
    {
        var id = TwoRegions();

        var taken = await Assert.ThrowsAsync<ArgumentException>(() => _grid.MoveRegionAsync(id, 1009, 1000, TestContext.Current.CancellationToken));
        var off = await Assert.ThrowsAsync<ArgumentException>(() => _grid.MoveRegionAsync(id, 65535, 0, TestContext.Current.CancellationToken));

        Assert.Contains("That spot is taken by Beach.", taken.Message);
        Assert.Contains("That spot is off the map.", off.Message);
        var saved = RegionModel.Find(_store.LoadOrCreate(), id)!;
        Assert.Equal((1000, 1000), (saved.X, saved.Y));
    }

    [Fact]
    public async Task Move_next_to_itself_is_allowed()
    {
        var id = TwoRegions();

        await _grid.MoveRegionAsync(id, 1001, 1001, TestContext.Current.CancellationToken); // overlaps its own old square

        var saved = RegionModel.Find(_store.LoadOrCreate(), id)!;
        Assert.Equal((1001, 1001), (saved.X, saved.Y));
    }

    [Fact]
    public async Task Move_of_a_deleted_region_throws()
    {
        var id = TwoRegions();
        _grid.UpdateSettings(s => s with { Regions = s.Regions.Select(r => r.Uuid == id ? r with { DeletedAt = DateTimeOffset.UtcNow } : r).ToList() });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.MoveRegionAsync(id, 2000, 2000, TestContext.Current.CancellationToken));
    }

    private static readonly Guid Isle = Guid.Parse("50000000-0000-0000-0000-000000000003");

    /// <summary>
    /// Welcome (at 1000, 1000) and Isle (at 1010, 1000): in a program each, or both in Welcome's when
    /// <paramref name="shared"/>. Isle's UDP port is <c>_secondPort</c> either way.
    /// </summary>
    private void WelcomeAndIsle(bool shared, bool isleParked = false, string physics = "ubODE")
    {
        var s = _store.LoadOrCreate();
        var welcome = new ProgramSettings("Welcome", s.RegionPort, s.RegionConsolePort, physics);
        _store.Save(s with
        {
            Programs = shared ? [welcome] : [welcome, new ProgramSettings("Isle", _secondPort, _secondConsole)],
            Regions =
            [
                new RegionSettings("Welcome", "Welcome", Guid.NewGuid(), UdpPort: s.RegionPort),
                new RegionSettings(shared ? "Welcome" : "Isle", "Isle", Isle, X: 1010, UdpPort: _secondPort, Parked: isleParked),
            ],
            Setup = s.Setup with { RegionCreated = false }, // the leftover stand-ins are adopted
        });
    }

    [Fact]
    public async Task Share_moves_region_and_stops_emptied_program()
    {
        var ct = TestContext.Current.CancellationToken;
        WelcomeAndIsle(shared: false);
        var s = _store.LoadOrCreate();
        var (welcomeConsole, isleConsole) = (s.RegionConsolePort, _secondConsole);
        var welcome = StartLeftoverRegion("Welcome");
        var isle = StartLeftoverRegion("Isle");
        try
        {
            Assert.IsNotType<PortConflictException>(await Record.ExceptionAsync(() => _grid.StartAsync(ct))); // MariaDB cannot start; both programs are adopted
            Assert.All(_grid.GetStatus().Programs, p => Assert.Equal(ProcessState.Running, p.Process.State));
            _http.OnCommand = port =>
            {
                // What a region program does on "quit".
                if (port == welcomeConsole && _http.Commands(port).Contains("quit") && !welcome.HasExited) welcome.Kill();
                if (port == isleConsole && _http.Commands(port).Contains("quit") && !isle.HasExited) isle.Kill();
            };

            // Welcome's restart can't finish here (the stand-in OpenSim is one byte).
            Assert.IsNotType<PortConflictException>(await Record.ExceptionAsync(() => _grid.ChangeRunsWithAsync(Isle, "Welcome", ct)));

            var saved = _store.LoadOrCreate();
            Assert.Equal("Welcome", RegionModel.Find(saved, Isle)!.Instance);
            Assert.Equal(_secondPort, RegionModel.Find(saved, Isle)!.UdpPort);
            Assert.DoesNotContain(saved.Programs, p => p.Instance == "Isle");
            Assert.Contains("quit", _http.Commands(isleConsole)); // Isle's emptied program stopped
            Assert.Contains("quit", _http.Commands(welcomeConsole)); // Welcome's restarted to load Isle
            Assert.DoesNotContain(_grid.GetStatus().Programs, p => p.Instance == "Isle");
        }
        finally
        {
            await EndLeftoverRegionAsync(welcome);
            if (!isle.HasExited) isle.Kill();
            isle.Dispose();
        }
    }

    [Fact]
    public async Task Share_of_stopped_region_restarts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        WelcomeAndIsle(shared: false);
        var welcomeConsole = _store.LoadOrCreate().RegionConsolePort;
        var welcome = StartLeftoverRegion("Welcome"); // Welcome runs; Isle's program does not
        try
        {
            Assert.IsNotType<PortConflictException>(await Record.ExceptionAsync(() => _grid.StartAsync(ct))); // MariaDB cannot start; Welcome is adopted
            Assert.Equal(ProcessState.Running, _grid.GetStatus().Programs.Single(p => p.Instance == "Welcome").Process.State);

            await _grid.ChangeRunsWithAsync(Isle, "Welcome", ct);

            var saved = _store.LoadOrCreate();
            Assert.Equal("Welcome", RegionModel.Find(saved, Isle)!.Instance);
            Assert.DoesNotContain(saved.Programs, p => p.Instance == "Isle");
            Assert.DoesNotContain("quit", _http.Commands(welcomeConsole)); // Isle loads at Welcome's next start
            Assert.Equal(ProcessState.Running, _grid.GetStatus().Programs.Single(p => p.Instance == "Welcome").Process.State);
        }
        finally { await EndLeftoverRegionAsync(welcome); }
    }

    [Fact]
    public async Task Own_makes_a_new_program_keeping_udp_port()
    {
        WelcomeAndIsle(shared: true, physics: "BulletSim");
        var before = _store.LoadOrCreate();

        await _grid.ChangeRunsWithAsync(Isle, null, TestContext.Current.CancellationToken);

        var saved = _store.LoadOrCreate();
        var region = RegionModel.Find(saved, Isle)!;
        Assert.Equal("Isle", region.Instance);
        Assert.Equal(_secondPort, region.UdpPort);
        var program = Assert.Single(saved.Programs, p => p.Instance == "Isle");
        Assert.Equal("BulletSim", program.Physics); // the old group's, so nothing about the region changes
        var taken = PortAllocator.Taken(before);
        Assert.DoesNotContain(program.HttpPort, taken);
        Assert.DoesNotContain(program.ConsolePort, taken);
        Assert.NotEqual(program.HttpPort, program.ConsolePort);
        Assert.Contains(saved.Programs, p => p.Instance == "Welcome");
        Assert.True(File.Exists(Path.Combine(_paths.RegionDefinitionDir("Isle"), "Isle.ini")));
        Assert.True(File.Exists(Path.Combine(_paths.RuntimeOpenSim, "dreamgrid-region-Isle.log4net.xml")));
        Assert.Empty(_http.CommandPorts); // nothing was running: nothing restarts or starts
        Assert.Contains(_grid.GetStatus().Programs, p => p.Instance == "Isle");
    }

    [Fact]
    public async Task Own_of_parked_region_starts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        WelcomeAndIsle(shared: true, isleParked: true);
        var welcomeConsole = _store.LoadOrCreate().RegionConsolePort;
        var welcome = StartLeftoverRegion("Welcome");
        try
        {
            Assert.IsNotType<PortConflictException>(await Record.ExceptionAsync(() => _grid.StartAsync(ct))); // MariaDB cannot start; Welcome is adopted
            Assert.Equal(ProcessState.Running, _grid.GetStatus().Programs.Single().Process.State);

            await _grid.ChangeRunsWithAsync(Isle, null, ct);

            Assert.Equal("Isle", RegionModel.Find(_store.LoadOrCreate(), Isle)!.Instance);
            Assert.DoesNotContain("quit", _http.Commands(welcomeConsole)); // Welcome didn't load Isle: it keeps running
            var programs = _grid.GetStatus().Programs;
            Assert.Equal(ProcessState.Running, programs.Single(p => p.Instance == "Welcome").Process.State);
            Assert.Equal(ProcessState.Stopped, programs.Single(p => p.Instance == "Isle").Process.State);
        }
        finally { await EndLeftoverRegionAsync(welcome); }
    }

    [Fact]
    public async Task Share_with_own_group_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        WelcomeAndIsle(shared: false);
        var before = _store.LoadOrCreate();

        var own = await Assert.ThrowsAsync<ArgumentException>(() => _grid.ChangeRunsWithAsync(Isle, "Isle", ct));
        var unknown = await Assert.ThrowsAsync<ArgumentException>(() => _grid.ChangeRunsWithAsync(Isle, "Nowhere", ct));
        var alone = await Assert.ThrowsAsync<ArgumentException>(() => _grid.ChangeRunsWithAsync(Isle, null, ct));

        Assert.Contains("It already runs with them.", own.Message);
        Assert.Contains("There is no such group of regions.", unknown.Message);
        Assert.Contains("It already runs on its own.", alone.Message);
        Assert.Equal(before.Programs, _store.LoadOrCreate().Programs);
        Assert.Equal(before.Regions, _store.LoadOrCreate().Regions);
        Assert.Empty(_http.CommandPorts);
    }

    [Fact]
    public async Task Change_of_a_deleted_region_throws()
    {
        WelcomeAndIsle(shared: true);
        _grid.UpdateSettings(s => s with { Regions = s.Regions.Select(r => r.Uuid == Isle ? r with { DeletedAt = DateTimeOffset.UtcNow } : r).ToList() });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.ChangeRunsWithAsync(Isle, null, TestContext.Current.CancellationToken));
    }

    private const string IslandStats = "max/min/avg/sum: 26.3/4.8/12.5/819200";

    [Fact]
    public async Task Start_adopts_running_region_before_region_created()
    {
        // The launcher restarted while the region step was still running: the region is live, RegionCreated is not set.
        AddRegion(created: false);
        var fake = StartLeftoverRegion();
        try
        {
            var e = await Record.ExceptionAsync(() => _grid.StartAsync(TestContext.Current.CancellationToken));

            Assert.NotNull(e); // MariaDB cannot start in this rig
            var region = _grid.GetStatus().Programs.SingleOrDefault()?.Process;
            Assert.NotNull(region);
            Assert.Equal(ProcessState.Running, region.State);
            Assert.Equal(fake.Id, region.Pid);
            Assert.False(fake.HasExited);
        }
        finally { await EndLeftoverRegionAsync(fake); }
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("timeout")]
    public async Task A_failed_adoption_of_an_unfinished_region_does_not_keep_the_database_down(string failure)
    {
        // The region's health probe gets a session; the login query after it fails: the console refuses the login
        // (ConsoleAuthException), or the console HttpClient times out (TaskCanceledException).
        AddRegion(created: false);
        var regionPort = _store.LoadOrCreate().RegionConsolePort;
        var sessions = 0;
        _http.Override = r =>
        {
            if (r.Uri.Port != regionPort) return null;
            if (failure == "refused" && r.Uri.AbsolutePath == "/StartSession/" && Interlocked.Increment(ref sessions) > 1)
                return (HttpStatusCode.Unauthorized, "");
            if (failure == "timeout" && r.Uri.AbsolutePath == "/SessionCommand/")
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
                    new TimeoutException());
            return null;
        };
        var fake = StartLeftoverRegion();
        try
        {
            var e = await Record.ExceptionAsync(() => _grid.StartAsync(TestContext.Current.CancellationToken));

            // It went on to the database, which is what fails in this rig.
            Assert.NotNull(e);
            Assert.IsNotType<ConsoleAuthException>(e);
            Assert.IsNotAssignableFrom<OperationCanceledException>(e);
            Assert.Contains(MissingMariaDb, e.Message);
            Assert.Equal(ProcessState.Running, _grid.GetStatus().Programs.SingleOrDefault()?.Process.State); // adopted all the same
        }
        finally { await EndLeftoverRegionAsync(fake); }
    }

    [Fact]
    public async Task Region_retry_after_adoption_waits_for_logins_before_content()
    {
        // The adopted region is still booting: it says logins are disabled, then enables them while that answer is on
        // its way (its one-time alert arrives during the query). Content waits for logins, and the alert is not lost.
        var ct = TestContext.Current.CancellationToken;
        AddRegion(created: false);
        var regionPort = _store.LoadOrCreate().RegionConsolePort;
        var fake = StartLeftoverRegion();
        var id = Guid.NewGuid();
        var terrain = "max/min/avg/sum: 21/21/21/1376256";
        _http.Reply = (port, command) =>
        {
            if (port != regionPort) return [];
            if (command == "login status") return ["Login in Welcome are disabled"];
            if (command == "terrain stats") return [terrain];
            if (command.StartsWith("terrain load ", StringComparison.Ordinal)) terrain = IslandStats;
            return [];
        };
        _http.OnCommand = port =>
        {
            if (port != regionPort || _http.Commands(regionPort).Count(c => c == "login status") != 1) return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(300, CancellationToken.None);
                _http.Mark("alert: logins enabled");
                _grid.OnRegionAlert(new RegionAlert("region_ready", "enabled", "Welcome", id, null));
            }, CancellationToken.None);
        };
        try
        {
            await _grid.CreateRegionAsync("Welcome", "island", ct).WaitAsync(TimeSpan.FromSeconds(60), ct);

            string[] log;
            lock (_http.Log) log = _http.Log.ToArray();
            var alert = Array.IndexOf(log, "alert: logins enabled");
            var firstContent = Array.FindIndex(log, l => l.StartsWith(regionPort + " terrain", StringComparison.Ordinal));
            Assert.True(alert >= 0, "no alert was sent");
            Assert.True(firstContent > alert, "content was loaded before logins were enabled: " + string.Join(" | ", log));
            Assert.True(_store.LoadOrCreate().Setup.RegionCreated);
        }
        finally { await EndLeftoverRegionAsync(fake); }
    }

    [Fact]
    public async Task Region_retry_switching_to_island_loads_island()
    {
        // The first attempt loaded the starter OAR (hilly terrain) and was interrupted; the retry picks the island.
        var ct = TestContext.Current.CancellationToken;
        AddRegion(created: false); // Content: "starter"
        var regionPort = _store.LoadOrCreate().RegionConsolePort;
        var fake = StartLeftoverRegion();
        const string starterStats = "max/min/avg/sum: 48.0/2.0/22.1/1441792";
        var terrain = starterStats;
        var loadWorks = false;
        _http.Reply = (port, command) =>
        {
            if (port != regionPort) return [];
            if (command == "login status") return ["Login in Welcome are enabled"];
            if (command == "terrain stats") return [terrain];
            if (command.StartsWith("terrain fill ", StringComparison.Ordinal)) terrain = "max/min/avg/sum: 0/0/0/0";
            if (command.StartsWith("terrain load ", StringComparison.Ordinal) && loadWorks) terrain = IslandStats;
            return [];
        };
        try
        {
            // The region could not read the island file: the starter's relief must not pass for the island.
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.CreateRegionAsync("Welcome", "island", ct).WaitAsync(TimeSpan.FromSeconds(60), ct));
            Assert.Contains("island terrain", e.Message);
            Assert.False(_store.LoadOrCreate().Setup.RegionCreated);

            loadWorks = true;
            await _grid.CreateRegionAsync("Welcome", "island", ct).WaitAsync(TimeSpan.FromSeconds(60), ct);

            var commands = _http.Commands(regionPort);
            Assert.Contains(commands, c => c.StartsWith("terrain load ", StringComparison.Ordinal) && c.Contains("island.r32"));
            Assert.DoesNotContain(commands, c => c.StartsWith("load oar", StringComparison.Ordinal));
            var saved = _store.LoadOrCreate();
            Assert.True(saved.Setup.RegionCreated);
            Assert.Equal("island", RegionModel.First(saved)?.Content);
        }
        finally { await EndLeftoverRegionAsync(fake); }
    }

    /// <summary>The region <see cref="LoadContent"/> loads into; the archiver's alerts name it.</summary>
    private static readonly Guid ContentRegion = Guid.Parse("50000000-0000-0000-0000-000000000004");

    /// <summary>Runs the controller's content step for a region "Welcome" of the given size, as an added region would.</summary>
    private Task LoadContent(string content, int size)
    {
        var s = _store.LoadOrCreate();
        var client = new RestConsoleClient(_http.CreateClient("console"), new Uri($"http://127.0.0.1:{s.RegionConsolePort}/"), "u", "p");
        var region = new RegionSettings("Welcome", "Welcome", ContentRegion, Size: size, UdpPort: s.RegionPort);
        var method = typeof(GridController).GetMethod("LoadContentCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(_grid, [client, region, content, s, TestContext.Current.CancellationToken])!;
    }

    /// <summary>Puts a stand-in OAR in the region-types folder and answers the load with the archiver's "oarloaded" alert.</summary>
    private string ProvideOar(string id)
    {
        var dir = Path.Combine(_root, "content", "region-types");
        Directory.CreateDirectory(dir);
        var oar = Path.Combine(dir, id + ".oar");
        File.WriteAllBytes(oar, [1]);
        _http.OnCommand = port =>
        {
            if (_http.Commands(port).Any(c => c.StartsWith("load oar", StringComparison.Ordinal)))
                _grid.OnRegionAlert(new RegionAlert("region_ready", "oarloaded", "Welcome", ContentRegion, null));
        };
        return oar.Replace('\\', '/');
    }

    [Fact]
    public async Task Size_2_region_gets_512_terrain()
    {
        var regionPort = _store.LoadOrCreate().RegionConsolePort;
        var terrain = "max/min/avg/sum: 0/0/0/0";
        Directory.CreateDirectory(_paths.RegionDir("Welcome")); // the region's program made it when it started
        _http.Reply = (_, command) =>
        {
            if (command == "terrain stats") return [terrain];
            if (command.StartsWith("terrain load ", StringComparison.Ordinal)) terrain = IslandStats;
            return [];
        };

        await LoadContent("meadow", 2);

        Assert.Contains(_http.Commands(regionPort), c => c.StartsWith("terrain load ", StringComparison.Ordinal));
        Assert.Equal(512L * 512 * 4, new FileInfo(Path.Combine(_paths.RegionDir("Welcome"), "island.r32")).Length);
    }

    [Fact]
    public async Task Size_3_oar_loads_in_the_middle()
    {
        var regionPort = _store.LoadOrCreate().RegionConsolePort;
        var oar = ProvideOar("forest");

        await LoadContent("forest", 3);

        Assert.Contains($"load oar --force-terrain --force-parcels --displacement <256,256,0> \"{oar}\"", _http.Commands(regionPort));
        Assert.DoesNotContain(_http.Commands(regionPort), c => c.StartsWith("terrain load ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Size_1_oar_loads_without_a_displacement()
    {
        var regionPort = _store.LoadOrCreate().RegionConsolePort;
        var oar = ProvideOar("beach");

        await LoadContent("beach", 1);

        Assert.Contains($"load oar --force-terrain --force-parcels \"{oar}\"", _http.Commands(regionPort));
    }

    [Fact]
    public async Task Custom_type_loads_its_file_without_displacement()
    {
        var regionPort = _store.LoadOrCreate().RegionConsolePort;
        Directory.CreateDirectory(_paths.Oars);
        var file = Path.Combine(_paths.Oars, "castle.oar");
        File.WriteAllBytes(file, [1]);
        _store.Save(_store.LoadOrCreate() with { CustomTypes = [new CustomRegionType("c-0000abcd", "My Castle", "castle.oar")] });
        _http.OnCommand = port =>
        {
            if (_http.Commands(port).Any(c => c.StartsWith("load oar", StringComparison.Ordinal)))
                _grid.OnRegionAlert(new RegionAlert("region_ready", "oarloaded", "Welcome", ContentRegion, null));
        };

        var expected = Path.GetFullPath(file).Replace("\\", "/");

        await LoadContent("c-0000abcd", 2);

        Assert.Contains($"load oar --force-terrain --force-parcels \"{expected}\"", _http.Commands(regionPort));
    }

    [Fact]
    public async Task Custom_type_whose_file_is_gone_makes_island_land()
    {
        var regionPort = _store.LoadOrCreate().RegionConsolePort;
        _store.Save(_store.LoadOrCreate() with { CustomTypes = [new CustomRegionType("c-0000abcd", "My Castle", "castle.oar")] });
        var terrain = "max/min/avg/sum: 0/0/0/0";
        Directory.CreateDirectory(_paths.RegionDir("Welcome"));
        _http.Reply = (_, command) =>
        {
            if (command == "terrain stats") return [terrain];
            if (command.StartsWith("terrain load ", StringComparison.Ordinal)) terrain = IslandStats;
            return [];
        };

        await LoadContent("c-0000abcd", 1);

        Assert.Contains(_http.Commands(regionPort), c => c.StartsWith("terrain load ", StringComparison.Ordinal));
        Assert.DoesNotContain(_http.Commands(regionPort), c => c.StartsWith("load oar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Added_region_needs_content_until_it_loads()
    {
        var ct = TestContext.Current.CancellationToken;
        AddRegion(created: false);
        var regionPort = _store.LoadOrCreate().RegionConsolePort;
        var fake = StartLeftoverRegion();
        var terrain = "max/min/avg/sum: 48.0/2.0/22.1/1441792";
        var loadWorks = false;
        _http.Reply = (port, command) =>
        {
            if (port != regionPort) return [];
            if (command == "login status") return ["Login in Welcome are enabled"];
            if (command == "terrain stats") return [terrain];
            if (command.StartsWith("terrain fill ", StringComparison.Ordinal)) terrain = "max/min/avg/sum: 0/0/0/0";
            if (command.StartsWith("terrain load ", StringComparison.Ordinal) && loadWorks) terrain = IslandStats;
            return [];
        };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.CreateRegionAsync("Welcome", "island", ct).WaitAsync(TimeSpan.FromSeconds(60), ct));
            Assert.True(_store.LoadOrCreate().Regions.Single().NeedsContent);

            loadWorks = true;
            await _grid.CreateRegionAsync("Welcome", "island", ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
            Assert.False(_store.LoadOrCreate().Regions.Single().NeedsContent);
        }
        finally { await EndLeftoverRegionAsync(fake); }
    }

    [Fact]
    public async Task A_MariaDB_without_our_datadir_is_not_adopted()
    {
        // I5: Debian's own mariadbd has the same image as ours; only --datadir=<our datadir> makes a process ours.
        var ct = TestContext.Current.CancellationToken;
        var s = _store.LoadOrCreate();
        _paths.EnsureCreated();
        PidFile.Write(Path.Combine(_paths.Pids, "mariadb.pid"), Environment.ProcessId);
        var self = Environment.ProcessPath!;
        var grid = new GridController(_paths, _store, new MariaDbBinaries(self, self, null), [new LogLevelModule()], _robust, new FakeGridJoin(), new FakeOtherGrid(), _http, TimeProvider.System);
        var holder = new TcpListener(IPAddress.Loopback, s.MariaDbPort);
        holder.Start();
        try
        {
            // Not adoptable, so its port is checked like any other program's.
            var e = await Assert.ThrowsAsync<PortConflictException>(() => grid.StartAsync(ct));
            Assert.Contains(s.MariaDbPort.ToString(), e.Message);
        }
        finally { holder.Stop(); }
    }

    [Fact]
    public async Task A_settings_change_made_during_CreateOwner_survives_it()
    {
        // I7: a long operation saves only its own fields onto the latest settings, not onto the copy it started with.
        var gate = new TaskCompletionSource();
        _robust.Block = gate.Task;
        var ct = TestContext.Current.CancellationToken;
        var owner = _grid.CreateOwnerAsync("Test", "Owner", "pw1", null, ct);
        await _robust.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        var changed = _grid.UpdateSettings(s => s with { GridName = "Renamed meanwhile" });
        Assert.Equal("Renamed meanwhile", changed.GridName);
        gate.SetResult();
        await owner;

        var saved = _store.LoadOrCreate();
        Assert.Equal("Renamed meanwhile", saved.GridName);
        Assert.True(saved.Setup.OwnerCreated);
        Assert.Equal("Renamed meanwhile", _grid.CurrentSettings().GridName);
    }

    // ---- OAR jobs -----------------------------------------------------------------------------------

    private static readonly Guid Home = Guid.Parse("50000000-0000-0000-0000-000000000001");

    /// <summary>The console commands, other than the adoption's queries, of <see cref="RunningHomeAndBeach"/>: (port, command) → output.</summary>
    private Func<int, string, string[]>? _console;

    /// <summary>Home (at 1000, 1000) and Beach (at 1010, 1000), both in Home's program or each in its own, both still needing content.</summary>
    private void HomeAndBeach(bool shared)
    {
        var s = _store.LoadOrCreate();
        var home = new ProgramSettings("Home", s.RegionPort, s.RegionConsolePort);
        _store.Save(s with
        {
            Programs = shared ? [home] : [home, new ProgramSettings("Beach", _secondPort, _secondConsole)],
            Regions =
            [
                new RegionSettings("Home", "Home", Home, Content: "island", UdpPort: s.RegionPort, NeedsContent: true),
                new RegionSettings(shared ? "Home" : "Beach", "Beach", Beach, X: 1010, Content: "island", UdpPort: _secondPort, NeedsContent: true),
            ],
            Setup = s.Setup with { RegionCreated = false }, // the leftover stand-ins are adopted
        });
    }

    /// <summary>
    /// <see cref="HomeAndBeach"/> with every program running: a leftover stand-in that <paramref name="grid"/> adopts, which
    /// says it loaded its regions and that they accept logins. Like OpenSim, a console switched with <c>change region</c>
    /// answers <c>show regions</c> and <c>login status</c> for that region only.
    /// </summary>
    private async Task<Process[]> RunningHomeAndBeach(GridController grid, bool shared, Func<LauncherSettings, LauncherSettings>? change = null)
    {
        HomeAndBeach(shared);
        if (change is not null) _store.Save(change(_store.LoadOrCreate()));
        var homeConsole = _store.LoadOrCreate().RegionConsolePort;
        var scenes = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
        _http.Reply = (port, command) =>
        {
            if (command.StartsWith("change region ", StringComparison.Ordinal))
            {
                var scene = command["change region ".Length..];
                if (scene == "root") scenes.TryRemove(port, out _); else scenes[port] = scene;
            }
            (string Name, Guid Id)[] regions = port == homeConsole ? (shared ? [("Home", Home), ("Beach", Beach)] : [("Home", Home)])
                : port == _secondConsole ? [("Beach", Beach)] : [];
            if (scenes.TryGetValue(port, out var current)) regions = regions.Where(r => r.Name == current).ToArray();
            if (command == "show regions") return regions.Select(r => $"{r.Name}  {r.Id}  1000,1000").ToArray();
            if (command == "login status") return regions.Select(r => $"Login in {r.Name} are enabled").ToArray();
            return _console?.Invoke(port, command) ?? [];
        };
        var fakes = shared ? [StartLeftoverRegion("Home")] : new[] { StartLeftoverRegion("Home"), StartLeftoverRegion("Beach") };
        // MariaDB cannot start in this rig; the programs are adopted first.
        Assert.IsNotType<PortConflictException>(await Record.ExceptionAsync(() => grid.StartAsync(TestContext.Current.CancellationToken)));
        Assert.All(grid.GetStatus().Regions, r => Assert.True(r.LoginsEnabled, r.Name + " does not accept logins"));
        return fakes;
    }

    private static async Task EndAllAsync(GridController grid, Process[] fakes)
    {
        await grid.DetachAsync();
        foreach (var fake in fakes)
        {
            if (!fake.HasExited) fake.Kill();
            fake.Dispose();
        }
    }

    /// <summary>Puts a file in the OAR library; returns its full path as the console gets it.</summary>
    private string LibraryOar(string name = "castle.oar")
    {
        Directory.CreateDirectory(_paths.Oars);
        var path = Path.Combine(_paths.Oars, name);
        File.WriteAllBytes(path, [1]);
        return Path.GetFullPath(path).Replace('\\', '/');
    }

    /// <summary>Writes what OpenSim's save writes: a gzip tar whose first entry is archive.xml.</summary>
    private static void WriteOar(string path)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "archive.xml") { DataStream = new MemoryStream("<archive/>"u8.ToArray()) });
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "terrains/Home.r32") { DataStream = new MemoryStream(new byte[4096]) });
    }

    /// <summary>The path in a <c>save oar "&lt;path&gt;"</c> command, or null for any other command.</summary>
    private static string? SavePath(string command) =>
        command.StartsWith("save oar \"", StringComparison.Ordinal) ? command["save oar \"".Length..^1] : null;

    private string RegionLog(string instance) => Path.Combine(_paths.Logs, $"region-{instance}.log");

    private void AppendLog(string instance, string line) => File.AppendAllText(RegionLog(instance), $"12:00:00 - [INFO] {line}\n");

    private static async Task Until(Func<bool> condition, string what)
    {
        var guard = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > guard) throw new TimeoutException(what);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Real time from midday UTC of <paramref name="day"/>, in UTC, so the local date is that day.</summary>
    private sealed class DatedClock(DateOnly day) : TimeProvider
    {
        private readonly Stopwatch _since = Stopwatch.StartNew();
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero) + _since.Elapsed;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>Keeps every report, on the reporting thread.</summary>
    private sealed class Reports<T> : IProgress<T>
    {
        public List<T> All { get; } = new();
        public void Report(T value) { lock (All) All.Add(value); }
    }

    [Fact]
    public async Task Import_switches_region_loads_and_waits_for_its_alert()
    {
        var ct = TestContext.Current.CancellationToken;
        var oar = LibraryOar();
        var fakes = await RunningHomeAndBeach(_grid, shared: true);
        var port = _store.LoadOrCreate().RegionConsolePort;
        _console = (_, command) =>
        {
            // Another region of the program finishing a load must not end this one.
            if (command.StartsWith("load oar", StringComparison.Ordinal))
                _grid.OnRegionAlert(new RegionAlert("region_ready", "oarloaded", "Home", Home, null));
            return [];
        };
        try
        {
            var job = _grid.RunOarJobAsync(new OarJobRequest(Beach, OarJobKind.Import, "castle.oar"), ct);
            await Until(() => _http.Commands(port).Any(c => c.StartsWith("load oar", StringComparison.Ordinal)), "the load was never sent");
            await Task.Delay(3000, ct);
            Assert.False(job.IsCompleted, "the job ended on Home's alert");
            Assert.Equal("Importing castle.oar into Beach", _grid.BusyWith("Home"));

            _grid.OnRegionAlert(new RegionAlert("region_ready", "oarloaded", "Beach", Beach, null));
            Assert.Null(await job.WaitAsync(TimeSpan.FromSeconds(30), ct));

            var commands = _http.Commands(port).ToList();
            var load = commands.IndexOf($"load oar --force-terrain --force-parcels \"{oar}\"");
            Assert.True(load > 0, string.Join(" | ", commands));
            Assert.Equal("change region Beach", commands[load - 1]);
            Assert.Equal("change region root", commands[load + 1]);
            Assert.Null(_grid.BusyWith("Home"));
            Assert.False(RegionModel.Find(_store.LoadOrCreate(), Beach)!.NeedsContent);
            Assert.True(RegionModel.Find(_store.LoadOrCreate(), Home)!.NeedsContent);
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Import_refuses_a_stopped_region()
    {
        LibraryOar();
        HomeAndBeach(shared: true);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _grid.RunOarJobAsync(new OarJobRequest(Beach, OarJobKind.Import, "castle.oar"), TestContext.Current.CancellationToken));

        Assert.Equal("Start Beach first; an OAR needs it running.", e.Message);
        Assert.Empty(_http.CommandPorts);
        Assert.Null(_grid.BusyWith("Home"));
    }

    [Fact]
    public async Task Import_refuses_an_unsafe_or_missing_file()
    {
        var ct = TestContext.Current.CancellationToken;
        LibraryOar();
        Directory.CreateDirectory(Path.Combine(_paths.Data, "elsewhere"));
        File.WriteAllBytes(Path.Combine(_paths.Data, "elsewhere", "secret.oar"), [1]);
        HomeAndBeach(shared: true);

        foreach (var file in new[] { "missing.oar", "../elsewhere/secret.oar", "..\\elsewhere\\secret.oar", "castle.txt", "", null })
        {
            var e = await Assert.ThrowsAsync<ArgumentException>(() =>
                _grid.RunOarJobAsync(new OarJobRequest(Beach, OarJobKind.Import, file), ct));
            Assert.Equal("That file is not in the OAR library.", e.Message);
        }
        Assert.Empty(_http.CommandPorts);
    }

    [Fact]
    public async Task Second_job_in_same_program_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        LibraryOar();
        var fakes = await RunningHomeAndBeach(_grid, shared: true);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var save = _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), stop.Token); // never finishes here
            await Until(() => _grid.BusyWith("Home") is not null, "the save never started");

            var import = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _grid.RunOarJobAsync(new OarJobRequest(Beach, OarJobKind.Import, "castle.oar"), ct));
            var again = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.MakeLand), ct));

            Assert.Equal("Saving Home as an OAR is still running. Try again when it finishes.", import.Message);
            Assert.Equal("Saving Home as an OAR is still running. Try again when it finishes.", again.Message);
            Assert.Equal("Saving Home as an OAR", _grid.BusyWith("Home"));

            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save.WaitAsync(TimeSpan.FromSeconds(30), ct));
            Assert.Null(_grid.BusyWith("Home"));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Jobs_in_two_programs_run_together()
    {
        var ct = TestContext.Current.CancellationToken;
        LibraryOar();
        var fakes = await RunningHomeAndBeach(_grid, shared: false);
        _console = (port, command) =>
        {
            if (port == _secondConsole && command.StartsWith("load oar", StringComparison.Ordinal))
                _grid.OnRegionAlert(new RegionAlert("region_ready", "oarloaded", "Beach", Beach, null));
            return [];
        };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var save = _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), stop.Token);
            await Until(() => _grid.BusyWith("Home") is not null, "the save never started");

            var import = _grid.RunOarJobAsync(new OarJobRequest(Beach, OarJobKind.Import, "castle.oar"), ct);
            await Until(() => _grid.BusyWith("Beach") is not null || import.IsCompleted, "the import never started");
            Assert.Null(await import.WaitAsync(TimeSpan.FromSeconds(30), ct));

            Assert.False(save.IsCompleted);
            Assert.Equal("Saving Home as an OAR", _grid.BusyWith("Home"));
            Assert.Null(_grid.BusyWith("Beach"));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save.WaitAsync(TimeSpan.FromSeconds(30), ct));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Power_action_refused_while_job_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: true);
        var port = _store.LoadOrCreate().RegionConsolePort;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var save = _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), stop.Token);
            await Until(() => _grid.BusyWith("Home") is not null, "the save never started");
            const string busy = "Saving Home as an OAR is still running. Try again when it finishes.";

            Assert.Equal(busy, (await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.RegionPowerAsync(Beach, RegionPower.Stop, ct))).Message);
            Assert.Equal(busy, (await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.MoveRegionAsync(Beach, 1020, 1000, ct))).Message);
            Assert.Equal(busy, (await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.ChangeRunsWithAsync(Beach, null, ct))).Message);

            Assert.DoesNotContain("quit", _http.Commands(port));
            var saved = RegionModel.Find(_store.LoadOrCreate(), Beach)!;
            Assert.Equal(("Home", 1010), (saved.Instance, saved.X));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save.WaitAsync(TimeSpan.FromSeconds(30), ct));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    /// <summary>Kills the stand-in behind a console port when that port is told to "quit", as a region program does.</summary>
    private void QuitKills(Process[] fakes, bool shared)
    {
        var homeConsole = _store.LoadOrCreate().RegionConsolePort;
        _http.OnCommand = port =>
        {
            var fake = port == homeConsole ? fakes[0] : !shared && port == _secondConsole ? fakes[1] : null;
            if (fake is not null && _http.Commands(port).Contains("quit") && !fake.HasExited) fake.Kill();
        };
    }

    [Fact]
    public async Task Make_land_again_fails_when_world_stops()
    {
        var ct = TestContext.Current.CancellationToken;
        LibraryOar();
        var fakes = await RunningHomeAndBeach(_grid, shared: false);
        var port = _store.LoadOrCreate().RegionConsolePort;
        _grid.UpdateSettings(l => l with
        {
            CustomTypes = [new CustomRegionType("c-0000abcd", "My Castle", "castle.oar")],
            Regions = l.Regions.Select(r => r.Uuid == Home ? r with { Content = "c-0000abcd" } : r).ToList(),
        });
        try
        {
            var job = _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.MakeLand), ct); // its alert never comes
            await Until(() => _http.Commands(port).Any(c => c.StartsWith("load oar", StringComparison.Ordinal)), "the load was never sent");
            QuitKills(fakes, shared: false);

            await _grid.StopAllAsync(ct);

            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => job.WaitAsync(TimeSpan.FromSeconds(15), ct));
            Assert.Equal("The region stopped before the land loaded.", e.Message);
            Assert.Null(_grid.BusyWith("Home"));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task World_start_during_shared_job_keeps_other_regions_logins()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: true);
        var port = _store.LoadOrCreate().RegionConsolePort;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var save = _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), stop.Token); // the console is on Home
            await Until(() => _http.Commands(port).Any(c => c.StartsWith("save oar", StringComparison.Ordinal)), "the save was never sent");
            var queries = _http.Commands(port).Count(c => c is "show regions" or "login status");

            // The world's start is not refused; MariaDB cannot start in this rig, after the programs are adopted.
            Assert.NotNull(await Record.ExceptionAsync(() => _grid.StartWorldAsync(false, ct)));

            Assert.Equal(queries, _http.Commands(port).Count(c => c is "show regions" or "login status"));
            Assert.All(_grid.GetStatus().Regions, r =>
            {
                Assert.True(r.LoginsEnabled, r.Name + " lost its logins");
                Assert.Equal(RegionState.Running, r.State);
            });
            Assert.False(save.IsCompleted);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save.WaitAsync(TimeSpan.FromSeconds(30), ct));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Save_keeps_a_finished_oar_whose_name_is_taken_at_the_move()
    {
        var ct = TestContext.Current.CancellationToken;
        var grid = NewGrid(new DatedClock(new DateOnly(2026, 10, 28)));
        var fakes = await RunningHomeAndBeach(grid, shared: false);
        // Something the library's free-name check doesn't see as a file holds the name, so every move fails.
        Directory.CreateDirectory(Path.Combine(_paths.Oars, "Home-2026-10-28.oar"));
        _console = (_, command) =>
        {
            if (SavePath(command) is { } path)
            {
                WriteOar(path);
                AppendLog("Home", "[ARCHIVER]: Finished writing out OAR for Home");
            }
            return [];
        };
        try
        {
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), ct).WaitAsync(TimeSpan.FromSeconds(60), ct));

            var kept = Assert.Single(Directory.GetFiles(_paths.OarsSaving));
            Assert.True(OarFile.LooksLikeOar(kept));
            Assert.Equal($"The OAR was saved but could not be moved into the library. It is at {kept} until the launcher restarts; see the launcher log.", e.Message);
            Assert.Null(grid.BusyWith("Home"));
        }
        finally { await EndAllAsync(grid, fakes); }
    }

    [Fact]
    public async Task Add_sharing_a_busy_program_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: false);
        var port = _store.LoadOrCreate().RegionConsolePort;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var save = _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), stop.Token);
            await Until(() => _grid.BusyWith("Home") is not null, "the save never started");
            var before = _store.LoadOrCreate();

            var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _grid.AddRegionAsync(new NewRegion("Cove", 1020, 1000, 1, "island", "Home"), ct));

            Assert.Equal("Saving Home as an OAR is still running. Try again when it finishes.", e.Message);
            var after = _store.LoadOrCreate();
            Assert.Equal(before.Regions, after.Regions);
            Assert.Equal(before.Programs, after.Programs);
            Assert.DoesNotContain("quit", _http.Commands(port));
            Assert.False(save.IsCompleted);
            Assert.Equal("Saving Home as an OAR", _grid.BusyWith("Home"));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save.WaitAsync(TimeSpan.FromSeconds(30), ct));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Change_runs_with_refuses_a_busy_target_program()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: false);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var save = _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), stop.Token);
            await Until(() => _grid.BusyWith("Home") is not null, "the save never started");

            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.ChangeRunsWithAsync(Beach, "Home", ct));

            Assert.Equal("Saving Home as an OAR is still running. Try again when it finishes.", e.Message);
            Assert.Equal("Beach", RegionModel.Find(_store.LoadOrCreate(), Beach)!.Instance);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save.WaitAsync(TimeSpan.FromSeconds(30), ct));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Save_moves_a_finished_oar_into_the_library()
    {
        var ct = TestContext.Current.CancellationToken;
        var grid = NewGrid(new DatedClock(new DateOnly(2026, 10, 28)));
        var fakes = await RunningHomeAndBeach(grid, shared: true);
        var port = _store.LoadOrCreate().RegionConsolePort;
        // Lines from an earlier save are older than this one.
        AppendLog("Home", "[ARCHIVER]: Error closing archive: disk full");
        AppendLog("Home", "[ARCHIVER]: Finished writing out OAR for Home");
        _console = (_, command) =>
        {
            if (SavePath(command) is { } path)
            {
                WriteOar(path);
                AppendLog("Home", "[ARCHIVER]: Finished writing out OAR for Home");
            }
            return [];
        };
        try
        {
            var name = await grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), ct).WaitAsync(TimeSpan.FromSeconds(60), ct);

            Assert.Equal("Home-2026-10-28.oar", name);
            Assert.True(OarFile.LooksLikeOar(Path.Combine(_paths.Oars, name!)));
            Assert.Empty(Directory.GetFileSystemEntries(_paths.OarsSaving));
            var commands = _http.Commands(port).ToList();
            var save = commands.FindIndex(c => SavePath(c) is not null);
            Assert.StartsWith($"save oar \"{_paths.OarsSaving.Replace('\\', '/')}/", commands[save]);
            Assert.Equal("change region Home", commands[save - 1]);
            Assert.Equal("change region root", commands[save + 1]);
            Assert.Null(grid.BusyWith("Home"));

            // A second save the same day gets the next free name.
            Assert.Equal("Home-2026-10-28-2.oar", await grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), ct).WaitAsync(TimeSpan.FromSeconds(60), ct));
        }
        finally { await EndAllAsync(grid, fakes); }
    }

    [Fact]
    public async Task Save_reports_bytes_written()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: false);
        string? staging = null;
        _console = (port, command) =>
        {
            if (SavePath(command) is { } path)
            {
                staging = path;
                WriteOar(path);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000, CancellationToken.None);
                    AppendLog("Home", "[ARCHIVER]: Finished writing out OAR for Home");
                }, CancellationToken.None);
            }
            return [];
        };
        var reports = new Reports<OarJobProgress>();
        try
        {
            var name = await _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), ct, reports).WaitAsync(TimeSpan.FromSeconds(60), ct);

            var length = new FileInfo(Path.Combine(_paths.Oars, name!)).Length;
            Assert.NotNull(staging);
            lock (reports.All) Assert.Contains(reports.All, p => p.BytesWritten == length);
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Save_fails_cleanly_when_program_stops()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: false);
        var homeConsole = _store.LoadOrCreate().RegionConsolePort;
        string? staging = null;
        _console = (_, command) =>
        {
            if (SavePath(command) is { } path) File.WriteAllBytes(staging = path, new byte[1000]); // still writing
            return [];
        };
        try
        {
            var save = _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), ct);
            await Until(() => staging is not null, "the save was never sent");
            _http.OnCommand = port =>
            {
                // What a region program does on "quit".
                if (port == homeConsole && _http.Commands(port).Contains("quit") && !fakes[0].HasExited) fakes[0].Kill();
                if (port == _secondConsole && _http.Commands(port).Contains("quit") && !fakes[1].HasExited) fakes[1].Kill();
            };

            await _grid.StopAllAsync(ct); // the world's stop is not refused

            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => save.WaitAsync(TimeSpan.FromSeconds(30), ct));
            Assert.Equal("The region stopped before the save finished.", e.Message);
            Assert.Empty(Directory.GetFileSystemEntries(_paths.OarsSaving));
            Assert.Null(_grid.BusyWith("Home"));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Save_fails_on_archiver_error_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: false);
        _console = (_, command) =>
        {
            if (SavePath(command) is { } path)
            {
                WriteOar(path);
                AppendLog("Home", "[ARCHIVER]: Error closing archive: disk full");
                AppendLog("Home", "[ARCHIVER]: Finished writing out OAR for Home");
            }
            return [];
        };
        try
        {
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), ct).WaitAsync(TimeSpan.FromSeconds(60), ct));

            Assert.Equal("The save did not finish; see the region log.", e.Message);
            Assert.Empty(Directory.GetFileSystemEntries(_paths.OarsSaving));
            Assert.Empty(Directory.GetFiles(_paths.Oars));
            Assert.Null(_grid.BusyWith("Home"));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Save_fails_on_an_exception_after_the_finished_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: false);
        _console = (port, command) =>
        {
            if (SavePath(command) is { } path)
            {
                // OpenSim closes the tar cleanly and logs the end, and only then rethrows what went wrong.
                WriteOar(path);
                AppendLog("Home", "[ARCHIVER]: Finished writing out OAR for Home");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000, CancellationToken.None);
                    AppendLog("Home", "System.IO.IOException: Disk full");
                }, CancellationToken.None);
            }
            return [];
        };
        try
        {
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), ct).WaitAsync(TimeSpan.FromSeconds(60), ct));

            Assert.Equal("The save did not finish; see the region log.", e.Message);
            Assert.Empty(Directory.GetFileSystemEntries(_paths.OarsSaving));
            Assert.Empty(Directory.GetFiles(_paths.Oars));
            Assert.Null(_grid.BusyWith("Home"));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    /// <summary>Real time, moved forward on demand.</summary>
    private sealed class ShiftedClock : TimeProvider
    {
        private long _shift;
        public void Shift(TimeSpan by) => Interlocked.Add(ref _shift, by.Ticks);
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _shift));
    }

    [Fact]
    public async Task Save_that_never_starts_fails_after_a_minute()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ShiftedClock();
        var grid = NewGrid(clock);
        var fakes = await RunningHomeAndBeach(grid, shared: false);
        var port = _store.LoadOrCreate().RegionConsolePort;
        try
        {
            var save = grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), ct); // the file never appears
            await Until(() => _http.Commands(port).Any(c => SavePath(c) is not null), "the save was never sent");
            await Task.Delay(3000, ct);
            Assert.False(save.IsCompleted, "the save failed before a minute passed");

            clock.Shift(TimeSpan.FromSeconds(61));

            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => save.WaitAsync(TimeSpan.FromSeconds(30), ct));
            Assert.Equal("The region did not start the save; see the region log.", e.Message);
            Assert.Null(grid.BusyWith("Home"));
        }
        finally { await EndAllAsync(grid, fakes); }
    }

    [Fact]
    public async Task Import_fails_when_world_stops()
    {
        var ct = TestContext.Current.CancellationToken;
        LibraryOar();
        var fakes = await RunningHomeAndBeach(_grid, shared: false);
        var port = _store.LoadOrCreate().RegionConsolePort;
        try
        {
            var job = _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Import, "castle.oar"), ct); // its alert never comes
            await Until(() => _http.Commands(port).Any(c => c.StartsWith("load oar", StringComparison.Ordinal)), "the load was never sent");
            QuitKills(fakes, shared: false);

            await _grid.StopAllAsync(ct);

            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => job.WaitAsync(TimeSpan.FromSeconds(15), ct));
            Assert.Equal("The region stopped before the import finished.", e.Message);
            Assert.Null(_grid.BusyWith("Home"));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Delete_and_restore_keep_a_region_parked()
    {
        var ct = TestContext.Current.CancellationToken;
        HomeAndBeach(shared: false);
        _grid.UpdateSettings(l => l with { Regions = l.Regions.Select(r => r.Uuid == Beach ? r with { Parked = true } : r).ToList() });

        await _grid.DeleteRegionAsync(Beach, ct);
        Assert.True(Saved(Beach).Parked);
        await _grid.RestoreRegionAsync(Beach, ct);

        Assert.True(Saved(Beach).Parked);
        Assert.Equal(RegionState.Parked, _grid.GetStatus().Regions.Single(r => r.Id == Beach).State);
    }

    [Fact]
    public async Task Make_land_again_loads_the_type_and_clears_needs_content()
    {
        var ct = TestContext.Current.CancellationToken;
        var oar = LibraryOar();
        var fakes = await RunningHomeAndBeach(_grid, shared: true);
        var port = _store.LoadOrCreate().RegionConsolePort;
        _grid.UpdateSettings(l => l with
        {
            CustomTypes = [new CustomRegionType("c-0000abcd", "My Castle", "castle.oar")],
            Regions = l.Regions.Select(r => r.Uuid == Home ? r with { Content = "c-0000abcd" } : r).ToList(),
        });
        _console = (_, command) =>
        {
            if (command.StartsWith("load oar", StringComparison.Ordinal))
                _grid.OnRegionAlert(new RegionAlert("region_ready", "oarloaded", "Home", Home, null));
            return [];
        };
        try
        {
            Assert.Null(await _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.MakeLand), ct).WaitAsync(TimeSpan.FromSeconds(60), ct));

            var commands = _http.Commands(port).ToList();
            var load = commands.IndexOf($"load oar --force-terrain --force-parcels \"{oar}\"");
            Assert.True(load > 0, string.Join(" | ", commands));
            Assert.Equal("change region Home", commands[load - 1]);
            Assert.Equal("change region root", commands[load + 1]);
            Assert.False(RegionModel.Find(_store.LoadOrCreate(), Home)!.NeedsContent);
            Assert.True(RegionModel.Find(_store.LoadOrCreate(), Beach)!.NeedsContent);
            Assert.Null(_grid.BusyWith("Home"));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    // ---- delete, restore and delete forever -----------------------------------------------------------

    private RegionSettings Saved(Guid id) => RegionModel.Find(_store.LoadOrCreate(), id)!;

    [Fact]
    public async Task Delete_alone_in_program_stops_and_removes_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: false);
        var homeConsole = _store.LoadOrCreate().RegionConsolePort;
        QuitKills(fakes, shared: false);
        _databaseUp = true;
        try
        {
            await _grid.DeleteRegionAsync(Beach, ct);

            var saved = _store.LoadOrCreate();
            var beach = RegionModel.Find(saved, Beach)!;
            Assert.NotNull(beach.DeletedAt);
            Assert.False(beach.Parked);
            Assert.Equal("Beach", beach.Instance); // the folder name stays the region's
            Assert.DoesNotContain(saved.Programs, p => p.Instance == "Beach");
            Assert.DoesNotContain(_grid.GetStatus().Programs, p => p.Instance == "Beach");
            Assert.DoesNotContain(_grid.GetStatus().Regions, r => r.Id == Beach);
            Assert.Contains("quit", _http.Commands(_secondConsole));
            Assert.DoesNotContain("quit", _http.Commands(homeConsole));
            var dir = _paths.RegionDefinitionDir("Beach");
            Assert.True(File.Exists(Path.Combine(dir, "Beach.ini.off")));
            Assert.False(File.Exists(Path.Combine(dir, "Beach.ini")));
            Assert.Equal([$"Deregister {Beach}"], _regionStore.Snapshot());
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Delete_in_shared_program_restarts_it_without_the_region()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: true);
        var homeConsole = _store.LoadOrCreate().RegionConsolePort;
        QuitKills(fakes, shared: true);
        try
        {
            // The restart itself can't finish here (the stand-in OpenSim is one byte).
            Assert.IsNotType<PortConflictException>(await Record.ExceptionAsync(() => _grid.DeleteRegionAsync(Beach, ct)));

            var saved = _store.LoadOrCreate();
            Assert.NotNull(RegionModel.Find(saved, Beach)!.DeletedAt);
            Assert.Contains(saved.Programs, p => p.Instance == "Home");
            Assert.Contains("quit", _http.Commands(homeConsole)); // stopped to start again without Beach
            var dir = _paths.RegionDefinitionDir("Home");
            Assert.True(File.Exists(Path.Combine(dir, "Beach.ini.off")));
            Assert.True(File.Exists(Path.Combine(dir, "Home.ini")));
            Assert.Empty(_regionStore.Snapshot()); // the database isn't running: nothing to deregister from
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Delete_works_when_program_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: false);
        // Beach's program dies, and its restart fails at once: its runtime folder is away meanwhile.
        var runtime = _paths.RuntimeOpenSim;
        Directory.Move(runtime, runtime + "-away");
        try
        {
            fakes[1].Kill();
            await Until(() => _grid.GetStatus().Programs.Single(p => p.Instance == "Beach").Process.State == ProcessState.Failed,
                "Beach's program never failed");
        }
        finally { Directory.Move(runtime + "-away", runtime); }
        _http.Override = r => r.Uri.Port == _secondConsole ? throw new HttpRequestException("Connection refused") : null;
        var commands = _http.Commands(_secondConsole).Length;
        _databaseUp = true;
        try
        {
            await _grid.DeleteRegionAsync(Beach, ct).WaitAsync(TimeSpan.FromSeconds(30), ct);

            Assert.NotNull(Saved(Beach).DeletedAt);
            Assert.DoesNotContain(_store.LoadOrCreate().Programs, p => p.Instance == "Beach");
            Assert.Equal(commands, _http.Commands(_secondConsole).Length); // no stop or restart was tried
            Assert.Equal([$"Deregister {Beach}"], _regionStore.Snapshot());
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Delete_refused_while_its_program_has_a_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: true);
        var port = _store.LoadOrCreate().RegionConsolePort;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var save = _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), stop.Token);
            await Until(() => _grid.BusyWith("Home") is not null, "the save never started");

            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.DeleteRegionAsync(Beach, ct));

            Assert.Equal("Saving Home as an OAR is still running. Try again when it finishes.", e.Message);
            Assert.Null(Saved(Beach).DeletedAt);
            Assert.DoesNotContain("quit", _http.Commands(port));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save.WaitAsync(TimeSpan.FromSeconds(30), ct));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Deleted_region_reserves_its_folder_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = _store.LoadOrCreate();
        var other = Guid.NewGuid();
        _store.Save(s with
        {
            Programs = [new ProgramSettings("Home", s.RegionPort, s.RegionConsolePort), new ProgramSettings("Be_ach", _secondPort, _secondConsole)],
            Regions =
            [
                new RegionSettings("Home", "Home", Home, UdpPort: s.RegionPort),
                new RegionSettings("Be_ach", "Be_ach", Beach, X: 1010, UdpPort: _secondPort),
                new RegionSettings("Home", "Be ach", other, X: 1020, UdpPort: FreePorts(1)[0]),
            ],
            Setup = s.Setup with { RegionCreated = true },
        });
        await _grid.DeleteRegionAsync(Beach, ct);

        await _grid.ChangeRunsWithAsync(other, null, ct); // "Be ach" gets a folder of its own

        Assert.Equal("Be_ach_2", Saved(other).Instance);
        Assert.Equal("Be_ach", Saved(Beach).Instance);
    }

    [Fact]
    public async Task Folder_of_a_live_removed_program_is_not_reused()
    {
        HomeAndBeach(shared: true);
        var fake = StartLeftoverRegion("Beach"); // a program called Beach still runs, though the settings no longer have it
        try
        {
            await _grid.ChangeRunsWithAsync(Beach, null, TestContext.Current.CancellationToken);

            Assert.Equal("Beach_2", Saved(Beach).Instance);
        }
        finally { await EndLeftoverRegionAsync(fake); }
    }

    [Fact]
    public async Task Restore_after_program_removed_recreates_its_folder()
    {
        var ct = TestContext.Current.CancellationToken;
        HomeAndBeach(shared: false);
        await _grid.DeleteRegionAsync(Beach, ct);
        Assert.DoesNotContain(_store.LoadOrCreate().Programs, p => p.Instance == "Beach");
        var before = _store.LoadOrCreate();

        await _grid.RestoreRegionAsync(Beach, ct);

        var saved = _store.LoadOrCreate();
        var beach = RegionModel.Find(saved, Beach)!;
        Assert.Null(beach.DeletedAt);
        Assert.Equal("Beach", beach.Instance);
        var program = Assert.Single(saved.Programs, p => p.Instance == "Beach");
        var taken = PortAllocator.Taken(before);
        Assert.DoesNotContain(program.HttpPort, taken);
        Assert.DoesNotContain(program.ConsolePort, taken);
        Assert.NotEqual(program.HttpPort, program.ConsolePort);
        Assert.Equal(program.HttpPort, beach.UdpPort);
        Assert.True(File.Exists(Path.Combine(_paths.RegionDefinitionDir("Beach"), "Beach.ini")));
        Assert.Equal(RegionState.Stopped, _grid.GetStatus().Regions.Single(r => r.Id == Beach).State);
        Assert.Contains(_grid.GetStatus().Programs, p => p.Instance == "Beach");
        Assert.Empty(_http.CommandPorts);
    }

    [Fact]
    public async Task Restore_into_running_program_comes_back_stopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: true);
        var port = _store.LoadOrCreate().RegionConsolePort;
        QuitKills(fakes, shared: true);
        // Deleted earlier; the program still runs with Home.
        _grid.UpdateSettings(l => l with { Regions = l.Regions.Select(r => r.Uuid == Beach ? r with { DeletedAt = DateTimeOffset.UtcNow } : r).ToList() });
        var commands = _http.Commands(port).Length;
        try
        {
            await _grid.RestoreRegionAsync(Beach, ct);

            Assert.Null(Saved(Beach).DeletedAt);
            Assert.Equal("Home", Saved(Beach).Instance);
            Assert.Equal(commands, _http.Commands(port).Length); // nothing was sent to the shared program: no stop, no restart
            var status = _grid.GetStatus();
            Assert.Equal(ProcessState.Running, status.Programs.Single().Process.State);
            Assert.Equal(RegionState.Stopped, status.Regions.Single(r => r.Id == Beach).State);
            Assert.Equal(RegionState.Running, status.Regions.Single(r => r.Id == Home).State);
            Assert.False(File.Exists(Path.Combine(_paths.RegionDefinitionDir("Home"), "Beach.ini"))); // not loaded until its Start
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Restore_refuses_a_live_region()
    {
        HomeAndBeach(shared: false);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.RestoreRegionAsync(Beach, TestContext.Current.CancellationToken));

        Assert.Equal("That region isn't deleted.", e.Message);
    }

    [Fact]
    public async Task Erase_needs_a_deleted_region_and_the_database()
    {
        var ct = TestContext.Current.CancellationToken;
        HomeAndBeach(shared: false);

        var live = await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.EraseRegionAsync(Beach, ct));
        await _grid.DeleteRegionAsync(Beach, ct);
        var noDatabase = await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.EraseRegionAsync(Beach, ct));

        Assert.Equal("That region isn't deleted.", live.Message);
        Assert.Equal("Start the database first.", noDatabase.Message);
        Assert.NotNull(Saved(Beach).DeletedAt);
        Assert.Empty(_regionStore.Snapshot());
    }

    [Fact]
    public async Task Erase_removes_the_region_and_frees_its_name()
    {
        var ct = TestContext.Current.CancellationToken;
        HomeAndBeach(shared: false);
        _grid.RegenerateConfig(); // Beach's folder exists
        await _grid.DeleteRegionAsync(Beach, ct);
        _databaseUp = true;
        _regionStore.Clear();

        await _grid.EraseRegionAsync(Beach, ct);

        Assert.Equal([$"Erase {Beach}"], _regionStore.Snapshot());
        var saved = _store.LoadOrCreate();
        Assert.Null(RegionModel.Find(saved, Beach));
        Assert.Null(RegionModel.FindByName(saved, "Beach")); // the name can be used again
        Assert.True(Directory.Exists(_paths.RegionDir("Beach"))); // its folder (with the logs) stays

        // The folder name is free again too: a new Beach given a folder of its own gets "Beach", not "Beach_2".
        _grid.UpdateSettings(l => l with
        {
            Regions = [.. l.Regions, new RegionSettings("Home", "Beach", Guid.NewGuid(), X: 1010, UdpPort: _secondPort)],
        });
        var again = RegionModel.FindByName(_store.LoadOrCreate(), "Beach")!.Uuid;
        await _grid.ChangeRunsWithAsync(again, null, ct);
        Assert.Equal("Beach", Saved(again).Instance);
    }

    [Fact]
    public async Task Deleted_space_needs_the_database()
    {
        var ct = TestContext.Current.CancellationToken;
        HomeAndBeach(shared: false);
        await _grid.DeleteRegionAsync(Beach, ct);

        Assert.Null(await _grid.DeletedSpaceAsync(ct));
        _databaseUp = true;
        _regionStore.Space[Beach] = 1234;
        var space = await _grid.DeletedSpaceAsync(ct);

        Assert.Equal(1234, Assert.Single(space!, kv => kv.Key == Beach).Value);
        Assert.Contains($"Space {Beach}", _regionStore.Snapshot());
    }

    // ---- region and program settings ----------------------------------------------------------------

    [Fact]
    public async Task Rename_saves_the_name_and_turns_the_old_file_off()
    {
        var id = TwoRegions();
        _grid.RegenerateConfig();
        var dir = _paths.RegionDefinitionDir("Welcome");
        Assert.True(File.Exists(Path.Combine(dir, "Welcome.ini")));

        await _grid.RenameRegionAsync(id, "Harbour", TestContext.Current.CancellationToken);

        var saved = Saved(id);
        Assert.Equal("Harbour", saved.Name);
        Assert.Equal(("Welcome", 2, 1000), (saved.Instance, saved.Size, saved.X)); // only the name changes
        Assert.True(File.Exists(Path.Combine(dir, "Harbour.ini")));
        Assert.True(File.Exists(Path.Combine(dir, "Welcome.ini.off")));
        Assert.False(File.Exists(Path.Combine(dir, "Welcome.ini")));
        Assert.Equal("Harbour", RegionModel.Find(_grid.CurrentSettings(), id)!.Name);
        Assert.Empty(_http.CommandPorts);
    }

    [Fact]
    public async Task Rename_refused_while_loaded()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: true);
        try
        {
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.RenameRegionAsync(Home, "Harbour", ct));

            Assert.Equal("Stop Home first; a region is renamed while it is offline.", e.Message);
            Assert.Equal("Home", Saved(Home).Name);
            Assert.DoesNotContain("quit", _http.Commands(_store.LoadOrCreate().RegionConsolePort));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Rename_case_only_is_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = TwoRegions();

        await _grid.RenameRegionAsync(id, "WELCOME", ct);
        var taken = await Assert.ThrowsAsync<ArgumentException>(() => _grid.RenameRegionAsync(id, "beach", ct));

        Assert.Equal("WELCOME", Saved(id).Name);
        Assert.Equal("There is already a region called beach.", taken.Message);
    }

    [Fact]
    public async Task Rename_to_a_deleted_regions_name_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = TwoRegions();
        _grid.UpdateSettings(s => s with { Regions = s.Regions.Select(r => r.Uuid == Beach ? r with { DeletedAt = DateTimeOffset.UtcNow } : r).ToList() });

        var taken = await Assert.ThrowsAsync<ArgumentException>(() => _grid.RenameRegionAsync(id, "Beach", ct));
        var bad = await Assert.ThrowsAsync<ArgumentException>(() => _grid.RenameRegionAsync(id, " Bad/Name", ct));
        var deleted = await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.RenameRegionAsync(Beach, "Shore", ct));

        Assert.Equal("There is already a region called Beach.", taken.Message);
        Assert.Equal("A region name is 2-32 letters, digits, spaces, '-', '_' or '\\'' with no leading or trailing space.", bad.Message);
        Assert.Equal("That region has been deleted.", deleted.Message);
        Assert.Equal("Welcome", Saved(id).Name);
    }

    [Fact]
    public async Task Rename_refused_while_job_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: true);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var save = _grid.RunOarJobAsync(new OarJobRequest(Home, OarJobKind.Save), stop.Token);
            await Until(() => _grid.BusyWith("Home") is not null, "the save never started");
            const string busy = "Saving Home as an OAR is still running. Try again when it finishes.";

            Assert.Equal(busy, (await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.RenameRegionAsync(Beach, "Shore", ct))).Message);
            Assert.Equal(busy, (await Assert.ThrowsAsync<InvalidOperationException>(() => _grid.ResizeRegionAsync(Beach, 2, ct))).Message);

            Assert.Equal(("Beach", 1), (Saved(Beach).Name, Saved(Beach).Size));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save.WaitAsync(TimeSpan.FromSeconds(30), ct));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Resize_refuses_a_taken_area()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = TwoRegions(); // Welcome 2x2 at 1000, Beach at 1010

        var taken = await Assert.ThrowsAsync<ArgumentException>(() => _grid.ResizeRegionAsync(id, 11, ct));
        var tooBig = await Assert.ThrowsAsync<ArgumentException>(() => _grid.ResizeRegionAsync(id, 17, ct));
        var tooSmall = await Assert.ThrowsAsync<ArgumentException>(() => _grid.ResizeRegionAsync(id, 0, ct));

        Assert.Equal(RegionAdvice.Placement(RegionPlacement.Check(_store.LoadOrCreate(), 1000, 1000, 11, ignore: id)), taken.Message);
        Assert.Contains("That spot is taken by Beach.", taken.Message);
        Assert.Equal("A region is 1 to 16 squares on each side.", tooBig.Message);
        Assert.Equal("A region is 1 to 16 squares on each side.", tooSmall.Message);
        Assert.Equal(2, Saved(id).Size);
    }

    [Fact]
    public async Task Resize_saves_and_restarts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: false);
        try
        {
            await _grid.ResizeRegionAsync(Beach, 3, ct);

            Assert.Equal(3, Saved(Beach).Size);
            Assert.DoesNotContain("quit", _http.Commands(_secondConsole));
            Assert.DoesNotContain("quit", _http.Commands(_store.LoadOrCreate().RegionConsolePort));
            Assert.All(_grid.GetStatus().Programs, p => Assert.Equal(ProcessState.Running, p.Process.State));
            Assert.True(_grid.NeedsRestart("Beach")); // the new size applies at its next start
            Assert.False(_grid.NeedsRestart("Home"));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Saving_options_marks_the_running_program_for_restart()
    {
        // RegionPowerAsync(Restart) needs Robust running, which this rig can't do; a stop and an adoption stand in for it.
        var ct = TestContext.Current.CancellationToken;
        var fakes = await RunningHomeAndBeach(_grid, shared: true);
        var home = _store.LoadOrCreate().RegionConsolePort;
        Process? again = null;
        try
        {
            Assert.False(_grid.NeedsRestart("Home"));

            await _grid.SaveRegionOptionsAsync(Beach, new RegionOptions(MaxAgents: 20), ct);

            Assert.Equal(20, Saved(Beach).Options!.MaxAgents);
            Assert.True(_grid.NeedsRestart("Home"));
            Assert.DoesNotContain("quit", _http.Commands(home)); // saving restarts nothing
            Assert.Contains("MaxAgents=20", File.ReadAllText(Path.Combine(_paths.RegionDefinitionDir("Home"), "Beach.ini")));
            var invalid = await Assert.ThrowsAsync<ArgumentException>(() => _grid.SaveRegionOptionsAsync(Beach, new RegionOptions(MaxAgents: 0), ct));
            Assert.Equal(RegionSettingsRules.Options(new RegionOptions(MaxAgents: 0)).Errors["MaxAgents"], invalid.Message);

            QuitKills(fakes, shared: true);
            await _grid.StopAllAsync(ct);
            Assert.False(_grid.NeedsRestart("Home")); // not running
            again = StartLeftoverRegion("Home");
            Assert.IsNotType<PortConflictException>(await Record.ExceptionAsync(() => _grid.StartAsync(ct)));

            Assert.Equal(ProcessState.Running, _grid.GetStatus().Programs.Single().Process.State);
            Assert.False(_grid.NeedsRestart("Home"));
        }
        finally { await EndAllAsync(_grid, again is null ? fakes : [.. fakes, again]); }
    }

    [Fact]
    public async Task Cpu_change_applies_at_once_without_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var applied = new List<(int Pid, int[] Cores, CpuPriority Priority)>();
        var grid = NewGrid(TimeProvider.System, (pid, cores, priority) => { lock (applied) applied.Add((pid, cores.ToArray(), priority)); });
        var fakes = await RunningHomeAndBeach(grid, shared: false);
        try
        {
            Assert.Empty(applied); // default CPU settings: nothing to apply at adoption

            var now = await grid.SaveProgramOptionsAsync("Home", null, new ProgramOptions(Cores: [0], Priority: CpuPriority.BelowNormal), ct);

            Assert.True(now);
            var call = Assert.Single(applied);
            Assert.Equal((fakes[0].Id, CpuPriority.BelowNormal), (call.Pid, call.Priority));
            Assert.Equal([0], call.Cores);
            Assert.False(grid.NeedsRestart("Home"));
            Assert.DoesNotContain("quit", _http.Commands(_store.LoadOrCreate().RegionConsolePort));
            var saved = Assert.Single(_store.LoadOrCreate().Programs, p => p.Instance == "Home");
            Assert.Equal(CpuPriority.BelowNormal, saved.Options!.Priority);

            // Anything else applies at the next restart.
            Assert.False(await grid.SaveProgramOptionsAsync("Home", null, new ProgramOptions(FramesPerSecond: 30, Cores: [0], Priority: CpuPriority.BelowNormal), ct));
            Assert.Single(applied);
            Assert.True(grid.NeedsRestart("Home"));

            var bad = await Assert.ThrowsAsync<ArgumentException>(() => grid.SaveProgramOptionsAsync("Home", "Havok", new ProgramOptions(), ct));
            Assert.Equal("Pick ubODE, BulletSim or BulletSimThreaded, or use the grid default.", bad.Message);
        }
        finally { await EndAllAsync(grid, fakes); }
    }

    [Fact]
    public async Task Cpu_change_of_a_stopped_program_is_only_saved()
    {
        var applied = 0;
        var grid = NewGrid(TimeProvider.System, (_, _, _) => Interlocked.Increment(ref applied));
        HomeAndBeach(shared: false);

        Assert.False(await grid.SaveProgramOptionsAsync("Beach", "BulletSim", new ProgramOptions(Priority: CpuPriority.BelowNormal), TestContext.Current.CancellationToken));

        Assert.Equal(0, applied);
        var saved = Assert.Single(_store.LoadOrCreate().Programs, p => p.Instance == "Beach");
        Assert.Equal(("BulletSim", CpuPriority.BelowNormal), (saved.Physics, saved.Options!.Priority));
        Assert.False(grid.NeedsRestart("Beach"));
    }

    [Fact]
    public async Task Saved_cores_this_computer_lacks_do_not_block_other_settings()
    {
        var ct = TestContext.Current.CancellationToken;
        var grid = NewGrid(TimeProvider.System, (_, _, _) => { });
        HomeAndBeach(shared: false);
        // Settings brought from a bigger computer: cores past this one's last.
        int[] stale = [Environment.ProcessorCount + 4, Environment.ProcessorCount + 5];
        var s = _store.LoadOrCreate();
        _store.Save(s with { Programs = [.. s.Programs.Select(p => p.Instance == "Beach" ? p with { Options = new ProgramOptions(Cores: stale) } : p)] });

        // The map changes; the cores are posted back unchanged.
        await grid.SaveProgramOptionsAsync("Beach", null, new ProgramOptions(Map: MapQuality.Best, Cores: stale), ct);

        var saved = Assert.Single(_store.LoadOrCreate().Programs, p => p.Instance == "Beach");
        Assert.Equal(MapQuality.Best, saved.Options!.Map);
        Assert.Equal(stale, saved.Options.Cores!);
        // New cores this computer lacks are still refused.
        var bad = await Assert.ThrowsAsync<ArgumentException>(() =>
            grid.SaveProgramOptionsAsync("Beach", null, new ProgramOptions(Cores: [Environment.ProcessorCount + 6]), ct));
        Assert.Contains("This computer has cores", bad.Message);
    }

    [Fact]
    public async Task Adopted_program_has_no_pending_restart()
    {
        var applied = new List<(int Pid, CpuPriority Priority)>();
        var grid = NewGrid(TimeProvider.System, (pid, _, priority) => { lock (applied) applied.Add((pid, priority)); });
        var fakes = await RunningHomeAndBeach(grid, shared: false, s => s with
        {
            Programs = s.Programs.Select(p => p.Instance == "Home"
                ? p with { Physics = "BulletSim", Options = new ProgramOptions(FramesPerSecond: 30, Priority: CpuPriority.BelowNormal) } : p).ToList(),
            Regions = s.Regions.Select(r => r with { Options = new RegionOptions(MaxPrims: 5000) }).ToList(),
        });
        try
        {
            Assert.False(grid.NeedsRestart("Home"));
            Assert.False(grid.NeedsRestart("Beach"));
            Assert.Equal([(fakes[0].Id, CpuPriority.BelowNormal)], applied); // non-default CPU settings applied at adoption
        }
        finally { await EndAllAsync(grid, fakes); }
    }

    [Fact]
    public async Task A_cpu_failure_at_adoption_is_only_logged()
    {
        var grid = NewGrid(TimeProvider.System, (_, _, _) => throw new InvalidOperationException("Not allowed."));
        var fakes = await RunningHomeAndBeach(grid, shared: false, s => s with
        {
            Programs = s.Programs.Select(p => p with { Options = new ProgramOptions(Priority: CpuPriority.BelowNormal) }).ToList(),
        });
        try
        {
            Assert.All(grid.GetStatus().Programs, p => Assert.Equal(ProcessState.Running, p.Process.State));
            Assert.False(grid.NeedsRestart("Home"));
        }
        finally { await EndAllAsync(grid, fakes); }
    }

    private static void KillPid(int? pid)
    {
        if (pid is not { } p) return;
        try
        {
            using var process = Process.GetProcessById(p);
            process.Kill();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
    }

    [Fact]
    public async Task Crash_restart_applies_cpu_again_and_keys_the_new_process()
    {
        var ct = TestContext.Current.CancellationToken;
        var applied = new List<(int Pid, CpuPriority Priority)>();
        var grid = NewGrid(TimeProvider.System, (pid, _, priority) => { lock (applied) applied.Add((pid, priority)); });
        var fakes = await RunningHomeAndBeach(grid, shared: false, s => s with
        {
            Programs = s.Programs.Select(p => p.Instance == "Home" ? p with { Options = new ProgramOptions(Priority: CpuPriority.BelowNormal) } : p).ToList(),
        });
        Process? again = null;
        ComponentStatus HomeProgram() => grid.GetStatus().Programs.Single(p => p.Instance == "Home").Process;
        try
        {
            await grid.SaveRegionOptionsAsync(Home, new RegionOptions(MaxAgents: 20), ct);
            Assert.True(grid.NeedsRestart("Home"));

            fakes[0].Kill();
            await Until(() => HomeProgram().State == ProcessState.Crashed, "the crash was never seen");
            again = StartLeftoverRegion("Home"); // what the monitor's restart finds and adopts
            await Until(() => { lock (applied) return applied.Any(a => a.Pid == again.Id); }, "the restarted program never got its CPU settings");

            Assert.Equal((ProcessState.Running, again.Id), (HomeProgram().State, HomeProgram().Pid));
            lock (applied) Assert.Equal([(fakes[0].Id, CpuPriority.BelowNormal), (again.Id, CpuPriority.BelowNormal)], applied);
            Assert.False(grid.NeedsRestart("Home")); // it read the region files as they are now
        }
        finally { await EndAllAsync(grid, again is null ? fakes : [.. fakes, again]); }
    }

    [Fact]
    public async Task Controller_restart_keys_the_new_process()
    {
        var ct = TestContext.Current.CancellationToken;
        var applied = new List<(int Pid, CpuPriority Priority)>();
        var grid = NewGrid(TimeProvider.System, (pid, _, priority) => { lock (applied) applied.Add((pid, priority)); }, launchStandIns: true);
        var fakes = await RunningHomeAndBeach(grid, shared: true, s => s with
        {
            Programs = s.Programs.Select(p => p with { Options = new ProgramOptions(Priority: CpuPriority.BelowNormal) }).ToList(),
        });
        var home = _store.LoadOrCreate().RegionConsolePort;
        var pidFile = Path.Combine(_paths.Pids, "Home.pid");
        // What the program does on "quit": whichever process the PID file names goes.
        _http.OnCommand = port =>
        {
            if (port == home && _http.Commands(port).LastOrDefault() == "quit") KillPid(PidFile.Read(pidFile));
        };
        int? launched = null;
        try
        {
            await grid.SaveRegionOptionsAsync(Beach, new RegionOptions(MaxAgents: 20), ct);
            Assert.True(grid.NeedsRestart("Home"));

            await grid.MoveRegionAsync(Beach, 1020, 1000, ct); // restarts Home's program, which loads Beach

            var program = grid.GetStatus().Programs.Single().Process;
            launched = program.Pid;
            Assert.Equal(ProcessState.Running, program.State);
            Assert.NotNull(launched);
            Assert.NotEqual(fakes[0].Id, launched);
            Assert.False(grid.NeedsRestart("Home"));
            await Task.Delay(1000, ct); // the Running event of the controller's own start must not apply it twice
            lock (applied) Assert.Equal([(fakes[0].Id, CpuPriority.BelowNormal), (launched.Value, CpuPriority.BelowNormal)], applied);
        }
        finally
        {
            await EndAllAsync(grid, fakes);
            KillPid(launched);
        }
    }

    [Fact]
    public async Task Grid_default_physics_change_marks_programs_that_use_it()
    {
        var fakes = await RunningHomeAndBeach(_grid, shared: false, s => s with
        {
            DefaultPhysics = "ubODE",
            Programs = s.Programs.Select(p => p.Instance == "Beach" ? p with { Physics = "ubODE" } : p).ToList(),
        });
        try
        {
            Assert.False(_grid.NeedsRestart("Home"));

            _grid.UpdateSettings(s => s with { DefaultPhysics = "BulletSim" });

            Assert.True(_grid.NeedsRestart("Home")); // it uses the grid default
            Assert.False(_grid.NeedsRestart("Beach")); // its own ubODE
            Assert.False(_grid.NeedsRestart("Nowhere"));
        }
        finally { await EndAllAsync(_grid, fakes); }
    }

    [Fact]
    public async Task Own_program_copies_physics_and_options()
    {
        WelcomeAndIsle(shared: true, physics: "BulletSim");
        var options = new ProgramOptions(Scripts: false, FramesPerSecond: 30, Map: MapQuality.Good, Cores: [0], Priority: CpuPriority.BelowNormal);
        var before = _store.LoadOrCreate();
        _store.Save(before with { Programs = before.Programs.Select(p => p with { Options = options }).ToList() });

        await _grid.ChangeRunsWithAsync(Isle, null, TestContext.Current.CancellationToken);

        var saved = _store.LoadOrCreate();
        var own = Assert.Single(saved.Programs, p => p.Instance == "Isle");
        Assert.Equal("BulletSim", own.Physics);
        Assert.Equal((false, 30, MapQuality.Good, CpuPriority.BelowNormal),
            (own.Options!.Scripts, own.Options.FramesPerSecond, own.Options.Map, own.Options.Priority));
        Assert.Equal([0], own.Options.Cores);
    }

    /// <summary>Records each call as "Deregister|Erase|Space &lt;ids&gt;"; space comes from <see cref="Space"/>.</summary>
    private sealed class FakeRegionStore : IRegionStore
    {
        private readonly List<string> _calls = new();
        public Dictionary<Guid, long> Space { get; } = new();

        public string[] Snapshot() { lock (_calls) return _calls.ToArray(); }
        public void Clear() { lock (_calls) _calls.Clear(); }
        private void Record(string call) { lock (_calls) _calls.Add(call); }

        public Task<IReadOnlyDictionary<Guid, long>> SpaceAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
        {
            Record("Space " + string.Join(",", ids));
            return Task.FromResult<IReadOnlyDictionary<Guid, long>>(ids.ToDictionary(id => id, id => Space.GetValueOrDefault(id)));
        }

        public Task DeregisterAsync(Guid id, CancellationToken ct) { Record($"Deregister {id}"); return Task.CompletedTask; }

        public Task EraseAsync(Guid id, CancellationToken ct) { Record($"Erase {id}"); return Task.CompletedTask; }
    }

    private sealed class FakeRobust : IRobustClient
    {
        public Task Block { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(false);

        /// <summary>Replies returned (in order) before the default level-200 account, standing in for a slow Robust.</summary>
        public Queue<int?> Levels { get; } = new();

        /// <summary>Robust never shows the account.</summary>
        public bool Missing { get; set; }
        public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
        public List<DateTimeOffset> Calls { get; } = new();

        public Task<RobustAccount?> FindAccountAsync(string first, string last, CancellationToken ct) => GetAccountAsync(first, last, ct);

        public async Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Block;
            lock (Calls) Calls.Add(Clock());
            if (Missing) return null;
            int? level = 200;
            lock (Levels) if (Levels.Count > 0) level = Levels.Dequeue();
            return level is { } l ? new RobustAccount(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), first, last, l, "") : null;
        }

        public Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MapRegion>>([]);
        public Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(MapArea area, CancellationToken ct) => GetMapRegionsAsync(ct);
    }

    /// <summary>
    /// A console server that accepts every command and records which port each command went to. It prints nothing
    /// unless <see cref="Reply"/> gives output lines for a command, which the port's next read returns.
    /// </summary>
    private sealed class ConsoleHttp : IHttpClientFactory
    {
        private readonly Dictionary<int, Queue<string>> _output = new();

        public List<int> CommandPorts { get; } = new();
        /// <summary>Every command with its port, in order; tests may add their own markers.</summary>
        public List<string> Log { get; } = new();
        /// <summary>Called with the console port of each command, after it is recorded.</summary>
        public Action<int>? OnCommand { get; set; }
        /// <summary>The output lines for (port, command).</summary>
        public Func<int, string, string[]>? Reply { get; set; }
        /// <summary>Answers a request instead of the normal server when it returns a reply (it may also throw).</summary>
        public Func<RecordedRequest, (HttpStatusCode, string)?>? Override { get; set; }

        public void Mark(string text) { lock (Log) Log.Add(text); }

        public string[] Commands(int port)
        {
            var prefix = port + " ";
            lock (Log) return Log.Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).Select(l => l[prefix.Length..]).ToArray();
        }

        public HttpClient CreateClient(string name) => new(new StubHandler(r =>
        {
            var port = r.Uri.Port;
            if (Override?.Invoke(r) is { } overridden) return overridden;
            switch (r.Uri.AbsolutePath)
            {
                case "/StartSession/":
                    return (HttpStatusCode.OK, "<ConsoleSession><SessionID>sid</SessionID></ConsoleSession>");
                case "/SessionCommand/":
                    var command = r.Form("COMMAND");
                    lock (CommandPorts) CommandPorts.Add(port);
                    Mark(port + " " + command);
                    if (Reply?.Invoke(port, command) is { } lines)
                        lock (_output)
                        {
                            if (!_output.TryGetValue(port, out var queue)) _output[port] = queue = new();
                            foreach (var line in lines) queue.Enqueue(line);
                        }
                    OnCommand?.Invoke(port);
                    return (HttpStatusCode.OK, "<ConsoleSession><Result>OK</Result></ConsoleSession>");
                case "/CloseSession/":
                    return (HttpStatusCode.OK, "<ConsoleSession></ConsoleSession>");
                default:
                    var output = new List<string>();
                    lock (_output)
                        if (_output.TryGetValue(port, out var pending))
                            while (pending.Count > 0) output.Add(pending.Dequeue());
                    return (HttpStatusCode.OK, "<ConsoleSession>" + string.Concat(output.Select(l =>
                        $"<Line Number=\"1\" Level=\"normal\" Prompt=\"false\" Command=\"false\" Input=\"false\">{System.Security.SecurityElement.Escape(l)}</Line>"))
                        + "</ConsoleSession>");
            }
        }));
    }
}
