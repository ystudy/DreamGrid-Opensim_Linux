using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Ini;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// GridController on a Region Server: it never starts Robust, reads the Grid Asset Server's join information on each
/// start, watches whether that server answers, finds the estate owner by name and places regions clear of other
/// computers' regions. The rig is GridControllerWiringTests': a fake program dir, stand-in children and a console server
/// that answers everything; the database is a stand-in that counts as healthy.
/// </summary>
public sealed class RegionServerControllerTests : IAsyncLifetime
{
    private static readonly JoinedGrid Avalon =
        new("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", "7.1.0", false, 8004, 8006);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-regionserver-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;
    private readonly SettingsStore _store;
    private readonly GridRobust _robust = new();
    private readonly FakeGridJoin _join = new() { Result = Avalon };
    private readonly int[] _ports;
    private readonly List<GridController> _grids = new();

    public RegionServerControllerTests()
    {
        var program = Path.Combine(_root, "program");
        _paths = new LauncherPaths(Path.Combine(_root, "data"), program, null);
        CopyPrototypes(program);
        File.WriteAllBytes(Path.Combine(program, "OpenSim.dll"), [1]);
        new Core.Runtime.RuntimeStager(_paths).StageIfNeeded();

        _store = new SettingsStore(_paths.SettingsFile);
        var p = _ports = FreePorts(7);
        _store.Save(_store.LoadOrCreate() with
        {
            Hostname = "127.0.0.1",
            GridName = "Avalon",
            Plan = OwnerPlan.RegionServer,
            Join = Avalon,
            WebPort = p[0], RobustPublicPort = p[1], RobustPrivatePort = p[2], RegionPort = p[3],
            MariaDbPort = p[4], RobustConsolePort = p[5], RegionConsolePort = p[6],
        });
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var grid in _grids) await grid.DetachAsync();
        foreach (var pid in Directory.Exists(_paths.Pids) ? Directory.GetFiles(_paths.Pids, "*.pid") : [])
            if (PidFile.Read(pid) is { } id)
                try { using var p = Process.GetProcessById(id); if (p.Id != Environment.ProcessId) p.Kill(); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A controller whose database is a stand-in that counts as healthy, and whose programs crash at once.</summary>
    private GridController NewGrid(TimeSpan? watchEvery = null)
    {
        var missing = Path.Combine(_root, "no-such-dir", "mariadbd");
        var marker = Path.Combine(_root, "stand-in-mariadb");
        var grid = new GridController(_paths, _store, new MariaDbBinaries(missing, missing, null),
            [new LogLevelModule()], _robust, _join, new AnswerAll(), TimeProvider.System, cpu: (_, _, _) => { })
        {
            DatabaseSpecForTests = _ => new ProcessSpec("MariaDB", () => StandIn(marker), Path.Combine(_paths.Pids, "mariadb.pid"),
                _ => Task.FromResult(true), _ => Task.CompletedTask, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), marker),
            ProgramStartForTests = _ => Crash(),
            GridAssetWatchInterval = watchEvery ?? TimeSpan.FromSeconds(30),
        };
        _grids.Add(grid);
        return grid;
    }

    private void Save(Func<LauncherSettings, LauncherSettings> change) => _store.Save(change(_store.LoadOrCreate()));

    /// <summary>Welcome at 1000, 1000 in a program of its own.</summary>
    private Guid AddWelcome(bool created = true)
    {
        var id = Guid.NewGuid();
        Save(s => s with
        {
            Programs = [new ProgramSettings("Welcome", s.RegionPort, s.RegionConsolePort)],
            Regions = [new RegionSettings("Welcome", "Welcome", id, UdpPort: s.RegionPort)],
            Setup = s.Setup with { RegionCreated = created },
        });
        return id;
    }

    [Fact]
    public async Task Region_server_start_never_starts_robust()
    {
        var grid = NewGrid();

        await grid.StartAsync(TestContext.Current.CancellationToken);

        var status = grid.GetStatus();
        Assert.Equal(ProcessState.Running, status.Database.State);
        Assert.Equal(ProcessState.Running, status.Robust.State); // the Grid Asset Server answered
        Assert.False(status.RunsRobust);
        Assert.Equal("192.168.1.5", status.GridAssetHost);
        Assert.Equal("http://192.168.1.5:8002/", status.LoginUri);
        Assert.False(status.RobustOutdated);
        Assert.Null(status.StartFailure);
        Assert.False(File.Exists(Path.Combine(_paths.Pids, "robust.pid")));
        Assert.Equal([("192.168.1.5", 8002)], _join.Reads);
    }

    [Fact]
    public async Task Region_server_start_rereads_the_join_file()
    {
        AddWelcome(created: false);
        _join.Result = Avalon with { GridName = "Avalon 2", Hypergrid = true, Host = "elsewhere", PublicPort = 9999 };
        var grid = NewGrid();

        await grid.StartAsync(TestContext.Current.CancellationToken);

        var s = _store.LoadOrCreate();
        Assert.Equal("Avalon 2", s.GridName);
        Assert.Equal("Avalon 2", s.Join!.GridName);
        Assert.True(s.Join.Hypergrid);
        // The address is the one the owner chose.
        Assert.Equal(("192.168.1.5", 8002), (s.Join.Host, s.Join.PublicPort));
        var os = IniDocument.Load(Path.Combine(_paths.RegionDir("Welcome"), "OpenSim.ini"));
        Assert.Equal("config-include/GridHypergrid.ini", os.Get("Architecture", "Include-Architecture"));
    }

    [Fact]
    public async Task Region_server_start_waits_when_the_grid_asset_server_is_off()
    {
        AddWelcome();
        _join.Error = new GridJoinException("Nothing answered at 192.168.1.5:8002.");
        var before = _store.LoadOrCreate().Join;
        var grid = NewGrid();

        await Assert.ThrowsAsync<GridAssetUnreachableException>(() => grid.StartAsync(TestContext.Current.CancellationToken));

        var status = grid.GetStatus();
        Assert.Contains("192.168.1.5:8002", status.StartFailure!.Message);
        Assert.Equal(ProcessState.Running, status.Database.State);
        Assert.Equal(ProcessState.Failed, status.Robust.State);
        Assert.All(status.Programs, p => Assert.Equal(ProcessState.Stopped, p.Process.State));
        Assert.False(File.Exists(Path.Combine(_paths.Pids, "Welcome.pid")));
        Assert.Equal(before, _store.LoadOrCreate().Join);
    }

    [Fact]
    public void Region_server_status_before_the_first_check_is_stopped()
    {
        var grid = NewGrid();

        var status = grid.GetStatus();

        Assert.Equal(ProcessState.Stopped, status.Robust.State);
        Assert.False(status.RunsRobust);
    }

    [Fact]
    public async Task Region_server_watches_the_grid_asset_server()
    {
        var ct = TestContext.Current.CancellationToken;
        var grid = NewGrid(watchEvery: TimeSpan.FromMilliseconds(50));
        await grid.StartAsync(ct);
        Assert.Equal(ProcessState.Running, grid.GetStatus().Robust.State);

        _robust.Ready = false;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (grid.GetStatus().Robust.State != ProcessState.Failed && DateTime.UtcNow < deadline) await Task.Delay(20, ct);
        Assert.Equal(ProcessState.Failed, grid.GetStatus().Robust.State);

        _robust.Ready = true;
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (grid.GetStatus().Robust.State != ProcessState.Running && DateTime.UtcNow < deadline) await Task.Delay(20, ct);
        Assert.Equal(ProcessState.Running, grid.GetStatus().Robust.State);
    }

    [Fact]
    public async Task Region_server_refuses_robust_operations()
    {
        var ct = TestContext.Current.CancellationToken;
        var grid = NewGrid();

        foreach (var operation in new Func<Task>[]
                 {
                     () => grid.StartComponentAsync("robust", ct),
                     () => grid.StopComponentAsync("robust", ct),
                     () => grid.RestartComponentAsync("robust", ct),
                     () => grid.CreateOwnerAsync("Ann", "Lee", "secret1", null, ct),
                 })
        {
            var e = await Assert.ThrowsAnyAsync<InvalidOperationException>(operation);
            Assert.Contains("doesn't run a grid service", e.Message);
        }
        Assert.False(File.Exists(Path.Combine(_paths.Pids, "robust.pid")));
    }

    [Fact]
    public async Task Estate_owner_is_found_by_name()
    {
        var id = Guid.NewGuid();
        _robust.Accounts.Add(new RobustAccount(id, "Ann", "Lee", 0, ""));
        var grid = NewGrid();

        var account = await grid.SetEstateOwnerAsync("ann", "lee", TestContext.Current.CancellationToken);

        Assert.Equal(id, account.PrincipalId);
        var setup = _store.LoadOrCreate().Setup;
        Assert.True(setup.OwnerCreated);
        Assert.Equal(new OwnerInfo("Ann", "Lee", id), setup.Owner);
    }

    [Fact]
    public async Task Estate_owner_not_on_the_grid_is_refused()
    {
        var grid = NewGrid();
        var before = File.ReadAllText(_paths.SettingsFile);

        var e = await Assert.ThrowsAsync<ArgumentException>(() => grid.SetEstateOwnerAsync("Ann", "Lee", TestContext.Current.CancellationToken));

        Assert.Equal("There's no avatar called Ann Lee on Avalon. Check the spelling, or create the avatar on the Grid Asset Server first.", e.Message);
        Assert.Equal(before, File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public async Task First_region_avoids_squares_used_elsewhere()
    {
        _robust.Map.Add(new MapRegion(Guid.NewGuid(), "Hub", 1000, 1000, 1));
        var grid = NewGrid();

        // The program can't come up in this rig; the region is saved before it starts.
        await Record.ExceptionAsync(() => grid.CreateRegionAsync("Welcome", "island", TestContext.Current.CancellationToken));

        var region = Assert.Single(_store.LoadOrCreate().Regions);
        Assert.Equal((1001, 1000), (region.X, region.Y));
    }

    [Fact]
    public async Task First_region_is_not_placed_blindly_when_the_grid_asset_server_is_off()
    {
        _robust.MapError = new HttpRequestException("Connection refused");
        var grid = NewGrid();

        var e = await Assert.ThrowsAsync<GridAssetUnreachableException>(() =>
            grid.CreateRegionAsync("Welcome", "island", TestContext.Current.CancellationToken));

        Assert.Contains("192.168.1.5:8002", e.Message);
        Assert.Empty(_store.LoadOrCreate().Regions);
    }

    [Fact]
    public async Task Adding_a_region_on_a_used_square_is_refused_on_a_region_server()
    {
        AddWelcome();
        _robust.Map.Add(new MapRegion(Guid.NewGuid(), "Hub", 1001, 1000, 1));
        var grid = NewGrid();
        var before = File.ReadAllText(_paths.SettingsFile);

        var e = await Assert.ThrowsAsync<ArgumentException>(() =>
            grid.AddRegionAsync(new NewRegion("Beach", 1001, 1000, 1, "island", null), TestContext.Current.CancellationToken));

        Assert.Contains("Hub", e.Message);
        Assert.Equal(before, File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public async Task Joined_region_count_ignores_our_own()
    {
        var ours = AddWelcome();
        Save(s => s with { Plan = OwnerPlan.GridAsset, Join = null });
        _robust.Map.Add(new MapRegion(ours, "Welcome", 1000, 1000, 1));
        _robust.Map.Add(new MapRegion(Guid.NewGuid(), "Hub", 1001, 1000, 1));
        var grid = NewGrid();

        Assert.Equal(1, await grid.JoinedRegionCountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Joined_region_count_needs_robust_answering()
    {
        Save(s => s with { Plan = OwnerPlan.GridAsset, Join = null });
        _robust.MapError = new HttpRequestException("Connection refused");
        var grid = NewGrid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => grid.JoinedRegionCountAsync(TestContext.Current.CancellationToken));
    }

    // ---- rig ----------------------------------------------------------------------------------------

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

    /// <summary>A free port for a stand-in to serve on, never one the settings already hold.</summary>
    private int StandInPort()
    {
        while (true)
            if (FreePorts(1)[0] is var port && !_ports.Contains(port)) return port;
    }

    private static string FakeChildExe()
    {
        var dir = typeof(RegionServerControllerTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "FakeChildDir").Value!;
        return Path.Combine(dir, OperatingSystem.IsWindows() ? "DreamGrid.FakeChild.exe" : "DreamGrid.FakeChild");
    }

    /// <summary>A stand-in that serves until killed; <paramref name="marker"/> on its command line makes it adoptable.</summary>
    private ProcessStartInfo StandIn(string marker)
    {
        var psi = new ProcessStartInfo(FakeChildExe()) { UseShellExecute = false };
        psi.ArgumentList.Add("--serve");
        psi.ArgumentList.Add(StandInPort().ToString());
        psi.ArgumentList.Add(marker);
        return psi;
    }

    /// <summary>A program that exits at once, as OpenSim would without its files.</summary>
    private static ProcessStartInfo Crash()
    {
        var psi = new ProcessStartInfo(FakeChildExe()) { UseShellExecute = false };
        psi.ArgumentList.Add("--crash");
        return psi;
    }

    /// <summary>The Grid Asset Server's grid service: answers while <see cref="Ready"/>, knows <see cref="Accounts"/> (any letter case) and <see cref="Map"/>.</summary>
    private sealed class GridRobust : IRobustClient
    {
        public volatile bool Ready = true;
        public List<RobustAccount> Accounts { get; } = new();
        public List<MapRegion> Map { get; } = new();
        public Exception? MapError { get; set; }

        public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(Ready);

        public Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct) =>
            Task.FromResult(Accounts.FirstOrDefault(a => string.Equals(a.FirstName, first, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.LastName, last, StringComparison.OrdinalIgnoreCase)));

        public Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(CancellationToken ct) =>
            MapError is { } e ? Task.FromException<IReadOnlyList<MapRegion>>(e) : Task.FromResult<IReadOnlyList<MapRegion>>(Map.ToList());
    }

    /// <summary>Consoles that accept everything and print nothing.</summary>
    private sealed class AnswerAll : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler());

        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.AbsolutePath == "/StartSession/"
                        ? "<ConsoleSession><SessionID>sid</SessionID></ConsoleSession>" : "<ConsoleSession></ConsoleSession>"),
                });
        }
    }
}
