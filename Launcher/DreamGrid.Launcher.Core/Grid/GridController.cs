using System.Diagnostics;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Runtime;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>
/// Owns the grid: stages the runtime, writes the INIs, and starts (or adopts) MariaDB, Robust and the region in
/// that order, each one healthy before the next. Children outlive the launcher (spec 4.12): only
/// <see cref="StopAllAsync"/>, <see cref="StopComponentAsync"/> and <see cref="RestartComponentAsync"/> stop them.
/// </summary>
public sealed partial class GridController(
    LauncherPaths paths,
    SettingsStore store,
    MariaDbBinaries mariaBin,
    IEnumerable<IFeatureModule> modules,
    IRobustClient robust,
    IHttpClientFactory http,
    TimeProvider time) : IGridController
{
    private static readonly TimeSpan ConsoleSettle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LoginWait = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan LoginPoll = TimeSpan.FromMilliseconds(500);

    private readonly IReadOnlyList<IFeatureModule> _modules = modules.ToList();
    private readonly SemaphoreSlim _ops = new(1, 1);
    private readonly CancellationTokenSource _monitorCts = new();
    private readonly Dictionary<string, Task> _monitors = new();
    private readonly object _alertGate = new();
    // Short lock around settings reads/writes and INI generation only; long operations hold _ops instead.
    private readonly object _configGate = new();

    private volatile LauncherSettings? _settings;
    private volatile string _host = "127.0.0.1";
    private volatile Component? _db, _robust, _region;
    private volatile bool _loginsEnabled;
    private int? _regionAlertPid;
    private TaskCompletionSource<string>? _oarLoaded;


    /// <summary>One supervised child with what the controller needs to talk to it; <see cref="Key"/> is the settings it was built from.</summary>
    private sealed record Component(string Name, ManagedProcess Process, ProcessSpec Spec, int[] Ports, RestConsoleClient? Console, string? Instance = null)
    {
        public string Key { get; init; } = "";
    }

    public bool LoginsEnabled => _loginsEnabled;

    /// <summary>The PID the region last reported through its "regionpid" alert.</summary>
    public int? RegionAlertPid { get { lock (_alertGate) return _regionAlertPid; } }

    // ---- start / stop -------------------------------------------------------------------------------

    public async Task StartAsync(CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        try
        {
            var s = LoadSettings();
            await StageAndWriteConfigAsync(s, ct);

            // The host owns and binds the web port before StartAsync (RegionReady posts its alerts there).
            var skip = new HashSet<int> { s.WebPort };
            foreach (var c in new[] { _db, _robust, _region })
                if (c is not null && WillAdopt(c)) skip.UnionWith(c.Ports);
            if (PortCheck.FindConflict(s, skip) is { } conflict)
                throw new PortConflictException(conflict);

            await StartDatabaseAsync(s, ct);
            await StartComponentAsync(_robust!, ct);
            if (s.Setup.RegionCreated && _region is not null)
                await StartRegionAsync(ct);
        }
        finally { _ops.Release(); }
    }

    public async Task StopAllAsync(CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        try
        {
            LoadSettings();
            await StopAllCoreAsync(ct);
        }
        finally { _ops.Release(); }
    }

    public async Task DetachAsync()
    {
        _monitorCts.Cancel();
        Task[] monitors;
        lock (_monitors) monitors = _monitors.Values.ToArray();
        await Task.WhenAll(monitors);
        foreach (var c in new[] { _region, _robust })
            if (c?.Console is { } console) await console.CloseAsync();
    }

    public async Task RestartComponentAsync(string name, CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        try
        {
            var s = LoadSettings();
            switch (name.ToLowerInvariant())
            {
                case "database":
                    // Robust and the region hold database connections, so they go down and come back with it.
                    await StopAllCoreAsync(ct);
                    EnsureComponents(s); // stopped components pick up changed ports
                    await StartDatabaseAsync(s, ct);
                    await StartComponentAsync(_robust!, ct);
                    if (s.Setup.RegionCreated && _region is not null) await StartRegionAsync(ct);
                    break;
                case "robust":
                    await _robust!.Process.StopAsync(ct);
                    EnsureComponents(s);
                    await StartComponentAsync(_robust!, ct);
                    break;
                case "region":
                    if (_region is null) throw new InvalidOperationException("No region has been created yet.");
                    await _region.Process.StopAsync(ct);
                    _loginsEnabled = false;
                    EnsureComponents(s);
                    await StartRegionAsync(ct);
                    break;
                default:
                    throw UnknownComponent(name);
            }
        }
        finally { _ops.Release(); }
    }

    public async Task StartComponentAsync(string name, CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        try
        {
            var s = LoadSettings();
            switch (name.ToLowerInvariant())
            {
                case "database":
                    await StartDatabaseAsync(s, ct);
                    break;
                case "robust":
                    if (_db!.Process.State != ProcessState.Running)
                        throw new InvalidOperationException("The database is not running; start it first.");
                    await StartComponentAsync(_robust!, ct);
                    break;
                case "region":
                    if (_region is null) throw new InvalidOperationException("No region has been created yet.");
                    if (_robust!.Process.State != ProcessState.Running)
                        throw new InvalidOperationException("Robust is not running; start it first.");
                    await StartRegionAsync(ct);
                    break;
                default:
                    throw UnknownComponent(name);
            }
        }
        finally { _ops.Release(); }
    }

    public async Task StopComponentAsync(string name, CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        try
        {
            LoadSettings();
            switch (name.ToLowerInvariant())
            {
                case "database":
                    // Robust and the region hold database connections, so they go down first.
                    await StopAllCoreAsync(ct);
                    break;
                case "robust":
                    await _robust!.Process.StopAsync(ct);
                    break;
                case "region":
                    if (_region is not null) await _region.Process.StopAsync(ct);
                    _loginsEnabled = false;
                    break;
                default:
                    throw UnknownComponent(name);
            }
        }
        finally { _ops.Release(); }
    }

    private static ArgumentException UnknownComponent(string name) =>
        new($"Unknown component '{name}'; expected database, robust or region.", nameof(name));

    /// <summary>Takes only the short config lock, never waiting behind a start, stop or provisioning.</summary>
    public void RegenerateConfig()
    {
        lock (_configGate) WriteConfig(ReloadSettings());
    }

    // ---- provisioning -------------------------------------------------------------------------------

    public async Task<RobustAccount> CreateOwnerAsync(string first, string last, string password, string? email, CancellationToken ct)
    {
        GridConsoleCommands.ValidateOwner(first, last, password, email);

        await _ops.WaitAsync(ct);
        try
        {
            var s = LoadSettings();
            var console = _robust!.Console!;
            var transcript = new List<string>();

            if (await robust.GetAccountAsync(first, last, ct) is null)
                transcript.AddRange(await GridConsoleCommands.CreateUserAsync(console, first, last, password, email, Guid.NewGuid(), ConsoleSettle, ct));
            else // an earlier attempt created the account: make the password the one the owner just chose
                transcript.AddRange(await console.RunAsync($"reset user password {first} {last} {password}", ConsoleSettle, ct));
            transcript.AddRange(await console.RunAsync($"set user level {first} {last} 200", ConsoleSettle, ct));

            var account = await robust.GetAccountAsync(first, last, ct)
                ?? throw new InvalidOperationException($"Robust did not create {first} {last}. Console: {string.Join(" | ", transcript)}");
            if (account.UserLevel != 200)
                throw new InvalidOperationException($"{first} {last} has level {account.UserLevel}, not 200. Console: {string.Join(" | ", transcript)}");

            // The region INI now gets the [Estates] keys.
            SaveAndWriteConfig(s with { Setup = s.Setup with { OwnerCreated = true, Owner = new OwnerInfo(first, last, account.PrincipalId) } });
            return account;
        }
        finally { _ops.Release(); }
    }

    public async Task CreateRegionAsync(string name, string content, CancellationToken ct)
    {
        if (!IsValidRegionName(name))
            throw new ArgumentException("A region name is 2-32 letters, digits, spaces, '-', '_' or '\\'' with no leading or trailing space.", nameof(name));
        if (content is not ("starter" or "island"))
            throw new ArgumentException("Content must be \"starter\" or \"island\".", nameof(content));

        await _ops.WaitAsync(ct);
        try
        {
            var s = LoadSettings();
            // A retry for the same name keeps the region's UUID.
            var uuid = s.Region is { } old && old.Name == name ? old.Uuid : Guid.NewGuid();
            var region = new RegionSettings(Instance: SafeFolderName(name), Name: name, Uuid: uuid, Content: content);
            if (_region is not null && _region.Instance != region.Instance)
                await _region.Process.StopAsync(ct);

            s = s with { Region = region };
            lock (_configGate) new RuntimeStager(paths).WriteLogConfigs(region.Instance, LogLevelModule.LevelFor(s));
            SaveAndWriteConfig(s);
            EnsureComponents(s);

            _loginsEnabled = false;
            await StartRegionAsync(ct);
            await WaitForLoginsAsync(ct); // the scene is up and accepts console commands for it
            await LoadContentAsync(region, content, ct);
            await WaitForLoginsAsync(ct);

            lock (_configGate)
            {
                var latest = _settings!;
                Save(latest with { Setup = latest.Setup with { RegionCreated = true } });
            }
        }
        finally { _ops.Release(); }
    }

    // ---- status and alerts --------------------------------------------------------------------------

    /// <summary>Reads only cached state (components are built under the operation lock), so it never waits.</summary>
    public GridStatus GetStatus()
    {
        LauncherSettings s;
        // On a first launch LoadOrCreate generates and saves fresh secrets, so the fallback load is serialised
        // with the controller's own (re-checking the cache inside the lock).
        lock (_configGate) s = _settings ?? ReloadSettings();
        return new GridStatus(
            Describe("database", _db),
            Describe("robust", _robust),
            s.Region is null ? null : Describe("region", _region),
            _loginsEnabled,
            $"http://{_host}:{s.RobustPublicPort}");
    }

    public void OnRegionAlert(RegionAlert alert)
    {
        if (alert.Alert.Equals("regionpid", StringComparison.OrdinalIgnoreCase))
        {
            lock (_alertGate) _regionAlertPid = alert.Pid;
            return;
        }
        switch (alert.Login.ToLowerInvariant())
        {
            case "enabled": _loginsEnabled = true; break;
            case "disabled": _loginsEnabled = false; break;
            case "oarloaded" or "oarnotloaded": _oarLoaded?.TrySetResult(alert.Login.ToLowerInvariant()); break;
        }
    }

    // ---- names --------------------------------------------------------------------------------------

    /// <summary>2-32 letters, digits, spaces, '-', '_' or '\''; no leading or trailing space. The name becomes a file name.</summary>
    public static bool IsValidRegionName(string name) =>
        name is not null && RegionNamePattern().IsMatch(name) && name == name.Trim();

    /// <summary>The instance folder name: ASCII letters, digits, '-' and '_'; anything else becomes '_'.</summary>
    public static string SafeFolderName(string name) =>
        new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    [GeneratedRegex(@"^[\p{L}\p{Nd} _'\-]{2,32}$")]
    private static partial Regex RegionNamePattern();

    // ---- internals ----------------------------------------------------------------------------------

    /// <summary>Under the operation lock: reloads the settings and (re)builds the components.</summary>
    private LauncherSettings LoadSettings()
    {
        LauncherSettings s;
        lock (_configGate) s = ReloadSettings();
        EnsureComponents(s);
        return s;
    }

    /// <summary>Reads the settings into the cache; never touches components.</summary>
    private LauncherSettings ReloadSettings()
    {
        var s = store.LoadOrCreate();
        _host = s.Hostname ?? HostAddress.Detect();
        _settings = s;
        return s;
    }

    private void Save(LauncherSettings s)
    {
        lock (_configGate)
        {
            store.Save(s);
            _settings = s;
        }
    }

    private void SaveAndWriteConfig(LauncherSettings s)
    {
        lock (_configGate)
        {
            store.Save(s);
            _settings = s;
            WriteConfig(s);
        }
    }

    private void WriteConfig(LauncherSettings s) =>
        new GridConfigWriter(paths).Write(s, _modules.Cast<IIniContributor>().ToList());

    /// <summary>Stages the runtime and writes the configs (R1: adopted Robust/region holding its DLLs are stopped and staging retried once).</summary>
    private async Task StageAndWriteConfigAsync(LauncherSettings s, CancellationToken ct)
    {
        paths.EnsureCreated();
        var stager = new RuntimeStager(paths);
        try
        {
            lock (_configGate) stager.StageIfNeeded();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (_region is not null) await _region.Process.StopAsync(ct);
            await _robust!.Process.StopAsync(ct);
            lock (_configGate) stager.StageIfNeeded();
        }
        lock (_configGate)
        {
            stager.WriteLogConfigs(s.Region?.Instance, LogLevelModule.LevelFor(s));
            WriteConfig(s);
        }
    }

    private async Task StartDatabaseAsync(LauncherSettings s, CancellationToken ct)
    {
        if (!WillAdopt(_db!))
            await new MariaDbServer(mariaBin, paths, s).InitializeAsync(ct);
        await StartComponentAsync(_db!, ct);
        await new DatabaseProvisioner(s).EnsureAsync(ct);
    }

    private async Task StartRegionAsync(CancellationToken ct)
    {
        var adopted = WillAdopt(_region!);
        await StartComponentAsync(_region!, ct);
        // An adopted region announced its logins before this launcher was listening, so ask it.
        if (adopted)
            _loginsEnabled = await GridConsoleCommands.LoginsEnabledAsync(_region!.Console!, ConsoleSettle, ct) ?? false;
    }

    private async Task StopAllCoreAsync(CancellationToken ct)
    {
        if (_region is not null) await _region.Process.StopAsync(ct);
        _loginsEnabled = false;
        await _robust!.Process.StopAsync(ct);
        await _db!.Process.StopAsync(ct); // graceful stop = MariaDB SHUTDOWN
    }

    private async Task StartComponentAsync(Component c, CancellationToken ct)
    {
        await c.Process.StartAsync(ct);
        EnsureMonitor(c);
        if (c.Process.State != ProcessState.Running)
            throw new InvalidOperationException($"{c.Name} did not start: {c.Process.LastError ?? c.Process.State.ToString()}");
    }

    /// <summary>
    /// Starts monitoring the component. A previous monitor that has not finished yet (it is about to see the stop)
    /// is chained onto rather than skipped, so a component is never left unmonitored.
    /// </summary>
    private void EnsureMonitor(Component c)
    {
        lock (_monitors)
        {
            if (_monitorCts.IsCancellationRequested) return;
            var token = _monitorCts.Token;
            _monitors[c.Name] = _monitors.TryGetValue(c.Name, out var previous) && !previous.IsCompleted
                ? previous.ContinueWith(_ => c.Process.MonitorAsync(token), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default).Unwrap()
                : Task.Run(() => c.Process.MonitorAsync(token), CancellationToken.None);
        }
    }

    /// <summary>True when the component's PID file names a live process with its image: starting it will adopt.</summary>
    private static bool WillAdopt(Component c) =>
        PidFile.Read(c.Spec.PidFile) is { } pid && ProcessImage.Matches(pid, c.Spec.ExpectedImage);

    private async Task WaitForLoginsAsync(CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + LoginWait;
        while (!_loginsEnabled)
        {
            if (_region!.Process.State == ProcessState.Failed)
                throw new InvalidOperationException($"The region failed: {_region.Process.LastError}");
            if (time.GetUtcNow() >= deadline)
                throw new TimeoutException($"The region did not enable logins within {LoginWait.TotalSeconds:0} s.");
            await Task.Delay(LoginPoll, time, ct);
        }
    }

    private async Task LoadContentAsync(RegionSettings region, string content, CancellationToken ct)
    {
        var console = _region!.Console!;
        var programRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.OpenSimProgram)))
            ?? paths.OpenSimProgram;
        var oar = content == "starter" ? StarterContent.StarterOar(programRoot) : null;
        if (oar is not null)
        {
            // RegionReady reports the archiver's result as an "oarloaded" or "oarnotloaded" alert.
            var loaded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _oarLoaded = loaded;
            await console.RunAsync($"load oar --force-terrain --force-parcels \"{Fwd(oar)}\"", ConsoleSettle, ct);
            if (await loaded.Task.WaitAsync(LoginWait, time, ct) != "oarloaded")
                throw new InvalidOperationException($"The region could not load {oar}; see the region log.");
            return;
        }

        var r32 = Path.Combine(paths.RegionDir(region.Instance), "island.r32");
        IslandTerrain.WriteR32(r32);
        await console.RunAsync($"terrain load \"{Fwd(r32)}\"", ConsoleSettle, ct);
        if (!await GridConsoleCommands.IslandTerrainLoadedAsync(console, ConsoleSettle, ct))
            throw new InvalidOperationException($"The region did not load the island terrain {r32}; see the region log.");
    }

    private static string Fwd(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Builds missing components and rebuilds one whose settings (ports, console credentials, instance) changed,
    /// but only while it is neither running nor adoptable; a running child keeps its spec until it is stopped.
    /// </summary>
    private void EnsureComponents(LauncherSettings s)
    {
        _db = Refresh(_db, KeyOf(s, "database"), () => MakeDatabase(s));
        _robust = Refresh(_robust, KeyOf(s, "robust"), () => MakeRobust(s));
        if (s.Region is { } r)
            _region = _region?.Instance != r.Instance ? MakeRegion(s, r) : Refresh(_region, KeyOf(s, "region"), () => MakeRegion(s, r));
    }

    private static Component Refresh(Component? current, string key, Func<Component> make)
    {
        if (current is null) return make();
        if (current.Key == key || current.Process.State is not (ProcessState.Stopped or ProcessState.Failed) || WillAdopt(current))
            return current;
        return make();
    }

    private static string KeyOf(LauncherSettings s, string component) => component switch
    {
        "database" => $"{s.MariaDbPort}|{s.Db.RootPassword}",
        "robust" => $"{s.RobustPublicPort}|{s.RobustPrivatePort}|{s.RobustConsolePort}|{s.Console.User}|{s.Console.Password}",
        _ => $"{s.Region?.Instance}|{s.RegionPort}|{s.RegionConsolePort}|{s.Console.User}|{s.Console.Password}",
    };

    private Component MakeDatabase(LauncherSettings s)
    {
        var server = new MariaDbServer(mariaBin, paths, s);
        var spec = new ProcessSpec("MariaDB", server.StartInfo, Path.Combine(paths.Pids, "mariadb.pid"),
            server.PingAsync, server.ShutdownAsync, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), mariaBin.Server);
        return new Component("database", new ManagedProcess(spec, new RestartPolicy(), time), spec, [s.MariaDbPort], null) { Key = KeyOf(s, "database") };
    }

    private Component MakeRobust(LauncherSettings s)
    {
        var console = MakeConsole(s, s.RobustConsolePort);
        var dll = Path.Combine(paths.RuntimeOpenSim, "Robust.dll");
        var spec = new ProcessSpec("Robust",
            () => Dotnet(dll, "-inifile=Robust.HG.ini", "-console=rest", "-logconfig=dreamgrid-robust.log4net.xml"),
            Path.Combine(paths.Pids, "robust.pid"), robust.IsReadyAsync, ct => console.SendAsync("quit", ct),
            TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), dll);
        return new Component("robust", new ManagedProcess(spec, new RestartPolicy(), time), spec,
            [s.RobustPublicPort, s.RobustPrivatePort, s.RobustConsolePort], console) { Key = KeyOf(s, "robust") };
    }

    private Component MakeRegion(LauncherSettings s, RegionSettings r)
    {
        var console = MakeConsole(s, s.RegionConsolePort);
        var dll = Path.Combine(paths.RuntimeOpenSim, "OpenSim.dll");
        // Same PID file the region writes itself ([Startup] PIDFile).
        var spec = new ProcessSpec("Region " + r.Name,
            () => Dotnet(dll, "-inidirectory=" + paths.RegionDir(r.Instance), "-console=rest",
                $"-logconfig=dreamgrid-region-{r.Instance}.log4net.xml"),
            Path.Combine(paths.Pids, r.Instance + ".pid"), console.IsUpAsync, ct => console.SendAsync("quit", ct),
            TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(60), dll);
        return new Component("region", new ManagedProcess(spec, new RestartPolicy(), time), spec,
            [s.RegionPort, s.RegionConsolePort], console, r.Instance) { Key = KeyOf(s, "region") };
    }

    private RestConsoleClient MakeConsole(LauncherSettings s, int port) =>
        new(http.CreateClient("DreamGrid.Console"), new Uri($"http://127.0.0.1:{port}"), s.Console.User, s.Console.Password);

    private ProcessStartInfo Dotnet(string dll, params string[] args)
    {
        var psi = new ProcessStartInfo(DotnetHost()) { WorkingDirectory = paths.RuntimeOpenSim };
        psi.ArgumentList.Add(dll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>DOTNET_HOST_PATH, else this process when it is the dotnet host, else "dotnet" on PATH.</summary>
    internal static string DotnetHost()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var self = Environment.ProcessPath;
        if (self is not null && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return self;
        return "dotnet";
    }

    private ComponentStatus Describe(string name, Component? c)
    {
        if (c is null) return new ComponentStatus(name, ProcessState.Stopped, null, null, null, null);
        var pid = c.Process.Pid;
        TimeSpan? uptime = null;
        long? memory = null;
        if (pid is { } p)
        {
            try
            {
                using var proc = Process.GetProcessById(p);
                uptime = time.GetUtcNow() - new DateTimeOffset(proc.StartTime.ToUniversalTime());
                memory = proc.WorkingSet64;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
        return new ComponentStatus(name, c.Process.State, pid, uptime, memory, c.Process.LastError);
    }
}
