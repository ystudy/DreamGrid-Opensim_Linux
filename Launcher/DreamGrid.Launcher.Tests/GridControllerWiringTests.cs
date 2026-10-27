using System.Diagnostics;
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
        var p = FreePorts(7);
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

    private GridController NewGrid(TimeProvider time)
    {
        // A MariaDB that cannot start: anything past the port check fails with a non-port error.
        var missing = Path.Combine(_root, MissingMariaDb, "mariadbd");
        return new GridController(_paths, _store, new MariaDbBinaries(missing, missing, null),
            [new LogLevelModule()], _robust, _http, time);
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
            Programs = TestSettings.ProgramFor(new RegionSettings("Welcome", "Welcome", Guid.NewGuid())), Regions = TestSettings.RegionsFor(new RegionSettings("Welcome", "Welcome", Guid.NewGuid())),
            Setup = s.Setup with { RegionCreated = created },
        });
    }

    [Fact]
    public async Task StartComponent_region_needs_a_finished_region()
    {
        AddRegion(created: false);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _grid.StartComponentAsync("region", TestContext.Current.CancellationToken));

        Assert.Contains("No region has been created yet", e.Message);
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
            [new LogLevelModule()], _robust, _http, TimeProvider.System);

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
        psi.ArgumentList.Add(FreePorts(1)[0].ToString());
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

    /// <summary>
    /// A stand-in for the region "Welcome" an earlier launcher run left behind: its command line names the staged
    /// OpenSim.dll and its PID file is written after it started, so the controller would adopt it.
    /// </summary>
    private Process StartLeftoverRegion()
    {
        var psi = new ProcessStartInfo(FakeChildExe()) { UseShellExecute = false };
        psi.ArgumentList.Add("--serve");
        psi.ArgumentList.Add(FreePorts(1)[0].ToString());
        psi.ArgumentList.Add(Path.Combine(_paths.RuntimeOpenSim, "OpenSim.dll"));
        var fake = Process.Start(psi)!;
        _paths.EnsureCreated();
        PidFile.Write(Path.Combine(_paths.Pids, "Welcome.pid"), fake.Id);
        return fake;
    }

    /// <summary>Stops watching (so no monitor restarts the stand-in), then kills it.</summary>
    private async Task EndLeftoverRegionAsync(Process fake)
    {
        await _grid.DetachAsync();
        if (!fake.HasExited) fake.Kill();
        fake.Dispose();
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
            var region = _grid.GetStatus().Region;
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
            Assert.Equal(ProcessState.Running, _grid.GetStatus().Region?.State); // adopted all the same
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

    [Fact]
    public async Task A_MariaDB_without_our_datadir_is_not_adopted()
    {
        // I5: Debian's own mariadbd has the same image as ours; only --datadir=<our datadir> makes a process ours.
        var ct = TestContext.Current.CancellationToken;
        var s = _store.LoadOrCreate();
        _paths.EnsureCreated();
        PidFile.Write(Path.Combine(_paths.Pids, "mariadb.pid"), Environment.ProcessId);
        var self = Environment.ProcessPath!;
        var grid = new GridController(_paths, _store, new MariaDbBinaries(self, self, null), [new LogLevelModule()], _robust, _http, TimeProvider.System);
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
