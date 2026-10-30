using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// The rig for GridController on a plan that uses another computer's grid (a Region Server or a Connected Server). It is
/// GridControllerWiringTests': a fake program dir, stand-in children and a console server that answers everything; the
/// database is a stand-in that counts as healthy, and the other grid's service is <see cref="GridRobust"/>.
/// </summary>
public abstract class OtherGridControllerRig : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-othergrid-" + Guid.NewGuid().ToString("N"));
    private protected readonly LauncherPaths _paths;
    private protected readonly SettingsStore _store;
    private protected readonly GridRobust _robust = new();
    private protected readonly FakeGridJoin _join = new();
    private protected readonly FakeOtherGrid _otherGrid = new();
    private protected readonly AnswerAll _consoles = new();
    private protected readonly int[] _ports;
    private readonly List<GridController> _grids = new();

    /// <summary>Saves fresh settings on free ports, then <paramref name="plan"/>'s changes (the plan and the other grid).</summary>
    private protected OtherGridControllerRig(Func<LauncherSettings, LauncherSettings> plan)
    {
        var program = Path.Combine(_root, "program");
        _paths = new LauncherPaths(Path.Combine(_root, "data"), program, null);
        CopyPrototypes(program);
        File.WriteAllBytes(Path.Combine(program, "OpenSim.dll"), [1]);
        new Core.Runtime.RuntimeStager(_paths).StageIfNeeded();

        _store = new SettingsStore(_paths.SettingsFile);
        var p = _ports = FreePorts(7);
        _store.Save(plan(_store.LoadOrCreate() with
        {
            Hostname = "127.0.0.1",
            WebPort = p[0], RobustPublicPort = p[1], RobustPrivatePort = p[2], RegionPort = p[3],
            MariaDbPort = p[4], RobustConsolePort = p[5], RegionConsolePort = p[6],
        }));
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
    private protected GridController NewGrid(TimeSpan? watchEvery = null)
    {
        var missing = Path.Combine(_root, "no-such-dir", "mariadbd");
        var marker = Path.Combine(_root, "stand-in-mariadb");
        var grid = new GridController(_paths, _store, new MariaDbBinaries(missing, missing, null),
            [new LogLevelModule()], _robust, _join, _otherGrid, _consoles, TimeProvider.System, cpu: (_, _, _) => { })
        {
            DatabaseSpecForTests = _ => new ProcessSpec("MariaDB", () => StandIn(marker), Path.Combine(_paths.Pids, "mariadb.pid"),
                _ => Task.FromResult(true), _ => Task.CompletedTask, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), marker),
            ProgramStartForTests = _ => Crash(),
            GridAssetWatchInterval = watchEvery ?? TimeSpan.FromSeconds(30),
        };
        _grids.Add(grid);
        return grid;
    }

    private protected void Save(Func<LauncherSettings, LauncherSettings> change) => _store.Save(change(_store.LoadOrCreate()));

    /// <summary>Welcome at 1000, 1000 in a program of its own.</summary>
    private protected Guid AddWelcome(bool created = true)
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

    private static int[] FreePorts(int count) => TestPorts.Free(count);

    /// <summary>A free port for a stand-in to serve on, never one the settings already hold.</summary>
    private int StandInPort()
    {
        while (true)
            if (FreePorts(1)[0] is var port && !_ports.Contains(port)) return port;
    }

    private static string FakeChildExe()
    {
        var dir = typeof(OtherGridControllerRig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "FakeChildDir").Value!;
        return Path.Combine(dir, OperatingSystem.IsWindows() ? "DreamGrid.FakeChild.exe" : "DreamGrid.FakeChild");
    }

    /// <summary>A stand-in that serves until killed; <paramref name="marker"/> on its command line makes it adoptable.</summary>
    private protected ProcessStartInfo StandIn(string marker)
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

    /// <summary>
    /// The other grid's service: answers while <see cref="Ready"/>, knows <see cref="Accounts"/> (any letter case) and
    /// <see cref="Map"/>, or fails with <see cref="AccountError"/> or <see cref="MapError"/>. <see cref="MapQueries"/> lists
    /// each map read: its area, or null for the whole map.
    /// </summary>
    private protected sealed class GridRobust : IRobustClient
    {
        public volatile bool Ready = true;
        public List<RobustAccount> Accounts { get; } = new();
        public List<MapRegion> Map { get; } = new();
        public Exception? MapError { get; set; }
        public List<MapArea?> MapQueries { get; } = new();

        public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(Ready);

        public Exception? AccountError { get; set; }

        public Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct) =>
            AccountError is { } e ? Task.FromException<RobustAccount?>(e) : Task.FromResult(Accounts.FirstOrDefault(a => string.Equals(a.FirstName, first, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.LastName, last, StringComparison.OrdinalIgnoreCase)));

        public Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(CancellationToken ct) => ReadMap(null);

        public Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(MapArea area, CancellationToken ct) => ReadMap(area);

        private Task<IReadOnlyList<MapRegion>> ReadMap(MapArea? area)
        {
            lock (MapQueries) MapQueries.Add(area);
            return MapError is { } e ? Task.FromException<IReadOnlyList<MapRegion>>(e) : Task.FromResult<IReadOnlyList<MapRegion>>(Map.ToList());
        }
    }

    /// <summary>Consoles that accept everything and print nothing; <see cref="OnCommand"/> sees the console port of each command.</summary>
    private protected sealed class AnswerAll : IHttpClientFactory
    {
        public Action<int>? OnCommand { get; set; }

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(AnswerAll owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.RequestUri!.AbsolutePath == "/SessionCommand/") owner.OnCommand?.Invoke(request.RequestUri.Port);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri.AbsolutePath == "/StartSession/"
                        ? "<ConsoleSession><SessionID>sid</SessionID></ConsoleSession>" : "<ConsoleSession></ConsoleSession>"),
                });
            }
        }
    }
}
