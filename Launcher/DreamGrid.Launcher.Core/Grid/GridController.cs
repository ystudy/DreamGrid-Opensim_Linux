using System.Diagnostics;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Runtime;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Setup;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>
/// Owns the grid: stages the runtime, writes the INIs, and starts (or adopts) MariaDB, Robust and the region programs
/// in that order, each one healthy before the next. A program is one OpenSim process hosting one or more regions. Children outlive the launcher (spec 4.12): only
/// <see cref="StopAllAsync"/>, <see cref="StopComponentAsync"/> and <see cref="RestartComponentAsync"/> stop them.
/// </summary>
public sealed partial class GridController(
    LauncherPaths paths,
    SettingsStore store,
    MariaDbBinaries mariaBin,
    IEnumerable<IFeatureModule> modules,
    IRobustClient robust,
    IHttpClientFactory http,
    TimeProvider time,
    ILoggerFactory? loggers = null) : IGridController
{
    private static readonly TimeSpan ConsoleSettle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LoginWait = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan LoginPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AccountWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AccountPoll = TimeSpan.FromMilliseconds(500);

    private readonly IReadOnlyList<IFeatureModule> _modules = modules.ToList();
    private readonly SemaphoreSlim _ops = new(1, 1);
    private readonly CancellationTokenSource _monitorCts = new();
    private readonly Dictionary<string, Task> _monitors = new();
    private readonly object _alertGate = new();
    // Short lock around settings reads/writes and INI generation only; long operations hold _ops instead.
    private readonly object _configGate = new();

    private volatile LauncherSettings? _settings;
    private volatile string _host = "127.0.0.1";
    private volatile Component? _db, _robust;
    // Replaced whole, never changed in place, so GetStatus reads them without a lock.
    private volatile IReadOnlyDictionary<string, Component> _programs = new Dictionary<string, Component>();
    private volatile IReadOnlyDictionary<string, IReadOnlyCollection<Guid>?> _loaded = new Dictionary<string, IReadOnlyCollection<Guid>?>();
    // Regions stopped until the next world start: their program runs without them.
    private volatile IReadOnlySet<Guid> _held = new HashSet<Guid>();
    private readonly RegionLogins _logins = new();
    private readonly ILogger? _log = loggers?.CreateLogger<GridController>();
    private volatile StartFailure? _startFailure;
    private volatile GridActivity _activity;
    private int? _regionAlertPid;
    private TaskCompletionSource<string>? _oarLoaded;


    /// <summary>One supervised child with what the controller needs to talk to it; <see cref="Key"/> is the settings it was built from.</summary>
    private sealed record Component(string Name, ManagedProcess Process, ProcessSpec Spec, int[] Ports, RestConsoleClient? Console, string? Instance = null)
    {
        public string Key { get; init; } = "";
    }

    /// <summary>True when there is a loadable region and every one has said (alert or console) that it accepts logins.</summary>
    public bool LoginsEnabled
    {
        get
        {
            var s = CurrentSettings();
            var loadable = s.Programs.SelectMany(p => RegionModel.Loadable(s, p.Instance, _held)).ToList();
            return loadable.Count > 0 && loadable.All(r => _logins.Enabled(r.Uuid));
        }
    }

    /// <summary>The PID the region last reported through its "regionpid" alert.</summary>
    public int? RegionAlertPid { get { lock (_alertGate) return _regionAlertPid; } }

    // ---- start / stop -------------------------------------------------------------------------------

    public async Task StartAsync(CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        _activity = GridActivity.Starting;
        try { await StartCoreAsync(ct); }
        finally { _activity = GridActivity.None; _ops.Release(); }
    }

    public async Task StartWorldAsync(bool everything, CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        _activity = GridActivity.Starting;
        try
        {
            _held = new HashSet<Guid>();
            if (everything) UpdateSettings(RegionPowerPlan.Unparked, writeConfig: false);
            await StartCoreAsync(ct);

            // A program adopted (or left running) without some of its regions loads them only when it starts again.
            var s = CurrentSettings();
            var running = _programs.Where(p => p.Value.Process.State == ProcessState.Running).Select(p => p.Key).ToHashSet();
            foreach (var instance in RegionPowerPlan.MissingRegions(s, _held, running, _loaded))
                await RestartProgramAsync(s, _programs[instance], ct);
        }
        finally { _activity = GridActivity.None; _ops.Release(); }
    }

    public async Task RegionPowerAsync(Guid regionId, RegionPower action, CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        _activity = action is RegionPower.Stop or RegionPower.Park ? GridActivity.Stopping
            : action == RegionPower.Restart ? GridActivity.Restarting : GridActivity.Starting;
        try
        {
            var s = LoadSettings();
            var running = _programs.Where(p => RegionPowerPlan.IsActive(p.Value.Process.State)).Select(p => p.Key).ToHashSet();
            var steps = RegionPowerPlan.For(s, _held, running, regionId, action, _loaded);
            if ((steps.Start.Count > 0 || steps.Restart.Count > 0) && _robust!.Process.State != ProcessState.Running)
                throw new InvalidOperationException("Robust is not running; start it first.");

            var parked = RegionModel.Find(steps.Settings, regionId)!.Parked;
            _held = steps.Held;
            s = UpdateSettings(l => l with { Regions = l.Regions.Select(r => r.Uuid == regionId ? r with { Parked = parked } : r).ToList() });
            var name = RegionModel.Find(s, regionId)!.Name;
            if (steps.Affected.Count > 0)
                _log?.LogInformation("{Action} {Region}: also affected {Others}.", action, name, string.Join(", ", steps.Affected));

            foreach (var instance in steps.Stop) await StopProgramAsync(_programs[instance], ct);
            foreach (var instance in steps.Restart) await RestartProgramAsync(s, _programs[instance], ct);
            foreach (var instance in steps.Start)
            {
                EnsureComponents(s);
                await StartProgramAsync(s, _programs[instance], ct);
            }
        }
        finally { _activity = GridActivity.None; _ops.Release(); }
    }

    public async Task MoveRegionAsync(Guid regionId, int x, int y, CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        try
        {
            var s = LoadSettings();
            var region = RegionModel.Find(s, regionId) ?? throw new InvalidOperationException("There is no such region.");
            if (region.DeletedAt is not null) throw new InvalidOperationException("That region has been deleted.");
            if (!RegionPlacement.OnMap(x, y, region.Size)) throw new ArgumentException("That spot is off the map.");
            // The region's own square doesn't count: it may move to an overlapping spot.
            if (RegionPlacement.Check(s, x, y, region.Size, ignore: regionId) is { Ok: false } taken)
                throw new ArgumentException(RegionAdvice.Placement(taken));

            s = UpdateSettings(l => l with { Regions = l.Regions.Select(r => r.Uuid == regionId ? r with { X = x, Y = y } : r).ToList() });

            // The program reads its region files when it starts: a running one that loads this region restarts to apply the move.
            if (_programs.GetValueOrDefault(region.Instance) is not { } program || !RegionPowerPlan.IsActive(program.Process.State)) return;
            var loadable = RegionModel.Loadable(s, region.Instance, _held).ToList();
            if (loadable.All(r => r.Uuid != regionId)) return;
            if (_loaded.GetValueOrDefault(region.Instance) is { } ids && !ids.Contains(regionId)) return;

            var others = loadable.Where(r => r.Uuid != regionId).Select(r => r.Name).ToList();
            if (others.Count > 0)
                _log?.LogInformation("Move {Region}: also affected {Others}.", region.Name, string.Join(", ", others));
            _activity = GridActivity.Restarting;
            await RestartProgramAsync(s, program, ct);
        }
        finally { _activity = GridActivity.None; _ops.Release(); }
    }

    /// <summary>Stops a program and starts it again with its current region files.</summary>
    private async Task RestartProgramAsync(LauncherSettings s, Component program, CancellationToken ct)
    {
        await StopProgramAsync(program, ct);
        EnsureComponents(s);
        await StartProgramAsync(s, _programs[program.Instance!], ct);
    }

    /// <summary>Under the operation lock: the boot and adoption start (see <see cref="StartAsync"/>).</summary>
    private async Task StartCoreAsync(CancellationToken ct)
    {
        try
        {
            var s = LoadSettings();
            await PrepareAsync(s, ct);

            // A region an interrupted region step left running is adopted, never started (CreateRegionAsync finishes
            // it). It is live already, so it is watched first, whatever happens to the rest of this start. Whatever goes
            // wrong with it (a console that refuses the login or times out, too) must not keep the database and Robust
            // down: it is kept as this start's failure instead, unless a later step fails too.
            StartFailure? adoptFailure = null;
            if (!s.Setup.RegionCreated)
                foreach (var program in _programs.Values.Where(WillAdopt))
                {
                    try { await StartProgramAsync(s, program, ct); }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        adoptFailure ??= new StartFailure($"The region left running by an interrupted setup could not be adopted: {e.Message}", null);
                    }
                }
            await StartDatabaseAsync(s, ct);
            await StartComponentAsync(_robust!, ct);
            if (s.Setup.RegionCreated) await StartProgramsAsync(s, ct);
            _startFailure = adoptFailure;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A failure before any component runs (a port conflict, MariaDB missing) shows in no component state.
            _startFailure = new StartFailure(e.Message, (e as PortConflictException)?.Port);
            throw;
        }
    }

    public async Task StopAllAsync(CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        _activity = GridActivity.Stopping;
        try
        {
            LoadSettings();
            await StopAllCoreAsync(ct);
            // A deliberate stop ends what an earlier start reported: the dashboard shows a stopped world, not a failed one.
            _startFailure = null;
        }
        finally { _activity = GridActivity.None; _ops.Release(); }
    }

    public async Task DetachAsync()
    {
        _monitorCts.Cancel();
        Task[] monitors;
        lock (_monitors) monitors = _monitors.Values.ToArray();
        await Task.WhenAll(monitors);
        foreach (var c in _programs.Values.Append(_robust))
            if (c?.Console is { } console) await console.CloseAsync();
    }

    public async Task RestartComponentAsync(string name, CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        _activity = GridActivity.Restarting;
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
                    if (s.Setup.RegionCreated) await StartProgramsAsync(s, ct);
                    break;
                case "robust":
                    await _robust!.Process.StopAsync(ct);
                    EnsureComponents(s);
                    await StartComponentAsync(_robust!, ct);
                    break;
                default:
                    throw UnknownComponent(name);
            }
        }
        finally { _activity = GridActivity.None; _ops.Release(); }
    }

    public async Task StartComponentAsync(string name, CancellationToken ct)
    {
        var component = name.ToLowerInvariant();
        if (component is not ("database" or "robust")) throw UnknownComponent(name);

        await _ops.WaitAsync(ct);
        _activity = GridActivity.Starting;
        try
        {
            var s = LoadSettings();
            switch (component)
            {
                case "robust" when _db!.Process.State != ProcessState.Running:
                    throw new InvalidOperationException("The database is not running; start it first.");
            }

            // On an idle grid this is the first start since the launcher came up: same preparation as StartAsync.
            if (_programs.Values.Append(_db).Append(_robust).All(c => c is null || c.Process.State != ProcessState.Running))
            {
                try { await PrepareAsync(s, ct); }
                catch (PortConflictException e)
                {
                    _startFailure = new StartFailure(e.Message, e.Port);
                    throw;
                }
            }

            switch (component)
            {
                case "database": await StartDatabaseAsync(s, ct); break;
                default: await StartComponentAsync(_robust!, ct); break;
            }
        }
        finally { _activity = GridActivity.None; _ops.Release(); }
    }

    public async Task StopComponentAsync(string name, CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        _activity = GridActivity.Stopping;
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
                default:
                    throw UnknownComponent(name);
            }
        }
        finally { _activity = GridActivity.None; _ops.Release(); }
    }

    private static ArgumentException UnknownComponent(string name) =>
        new($"Unknown component '{name}'; expected database or robust (regions use RegionPowerAsync).", nameof(name));

    /// <summary>Takes only the short config lock, never waiting behind a start, stop or provisioning.</summary>
    public void RegenerateConfig()
    {
        lock (_configGate) WriteConfig(ReloadSettings());
    }

    /// <summary>Changes only what <paramref name="change"/> touches, on the latest saved settings (see <see cref="IGridController.UpdateSettings"/>).</summary>
    public LauncherSettings UpdateSettings(Func<LauncherSettings, LauncherSettings> change, bool writeConfig = true)
    {
        lock (_configGate)
        {
            var next = change(store.LoadOrCreate());
            store.Save(next);
            _host = HostAddress.Effective(next.Hostname);
            _settings = next;
            if (writeConfig) WriteConfig(next);
            return next;
        }
    }

    // ---- provisioning -------------------------------------------------------------------------------

    public async Task<RobustAccount> CreateOwnerAsync(string first, string last, string password, string? email, CancellationToken ct)
    {
        GridConsoleCommands.ValidateOwner(first, last, password, email);

        await _ops.WaitAsync(ct);
        try
        {
            LoadSettings();
            var console = _robust!.Console!;
            var transcript = new List<string>();

            if (await robust.GetAccountAsync(first, last, ct) is null)
                transcript.AddRange(await GridConsoleCommands.CreateUserAsync(console, first, last, password, email, Guid.NewGuid(), ConsoleSettle, ct));
            else // an earlier attempt created the account: make the password the one the owner just chose
                transcript.AddRange(await console.RunAsync($"reset user password {first} {last} {password}", ConsoleSettle, ct));
            // Robust runs console commands on its own thread, so under load the account (and then its level)
            // appears only after the console output has settled: wait for each before going on.
            _ = await WaitForAccountAsync(first, last, _ => true, ct)
                ?? throw new InvalidOperationException($"Robust did not create {first} {last} within {AccountWait.TotalSeconds:0} s. Console: {string.Join(" | ", transcript)}");
            transcript.AddRange(await console.RunAsync($"set user level {first} {last} 200", ConsoleSettle, ct));

            var account = await WaitForAccountAsync(first, last, a => a.UserLevel == 200, ct)
                ?? throw new InvalidOperationException($"Robust did not create {first} {last} within {AccountWait.TotalSeconds:0} s. Console: {string.Join(" | ", transcript)}");
            if (account.UserLevel != 200)
                throw new InvalidOperationException($"{first} {last} has level {account.UserLevel}, not 200. Console: {string.Join(" | ", transcript)}");

            // The region INI now gets the [Estates] keys.
            UpdateSettings(l => l with { Setup = l.Setup with { OwnerCreated = true, Owner = new OwnerInfo(first, last, account.PrincipalId) } });
            return account;
        }
        finally { _ops.Release(); }
    }

    private static readonly string ContentError =
        $"Content must be one of: starter, {string.Join(", ", RegionTypes.BuiltIn.Select(t => t.Id))}.";

    public async Task CreateRegionAsync(string name, string content, CancellationToken ct, IProgress<string>? progress = null)
    {
        if (!IsValidRegionName(name))
            throw new ArgumentException("A region name is 2-32 letters, digits, spaces, '-', '_' or '\\'' with no leading or trailing space.", nameof(name));
        if (!RegionTypes.IsContent(content))
            throw new ArgumentException(ContentError, nameof(content));

        await _ops.WaitAsync(ct);
        _activity = GridActivity.Starting;
        try
        {
            progress?.Report(SetupProgress.CreatingRegion);
            var s = LoadSettings();
            // A retry for the same name keeps the region's UUID.
            var uuid = RegionModel.FindByName(s, name) is { } old && old.Name == name ? old.Uuid : Guid.NewGuid();
            if (!s.Setup.RegionCreated)
            {
                // What an earlier attempt left goes: its program stops unless the new region takes over its folder.
                foreach (var other in _programs.Values.Where(c => c.Instance != SafeFolderName(name)))
                    await StopProgramAsync(other, ct);
                s = UpdateSettings(l => l with { Programs = [], Regions = [] });
            }
            await AddCoreAsync(s, new NewRegion(name, 1000, 1000, 1, content, null), uuid, progress, ct);
            UpdateSettings(l => l with { Setup = l.Setup with { RegionCreated = true } }, writeConfig: false);
        }
        finally { _activity = GridActivity.None; _ops.Release(); }
    }

    public async Task<RegionSettings> AddRegionAsync(NewRegion request, CancellationToken ct, IProgress<string>? progress = null)
    {
        if (!IsValidRegionName(request.Name))
            throw new ArgumentException("A region name is 2-32 letters, digits, spaces, '-', '_' or '\\'' with no leading or trailing space.", nameof(request));
        if (!RegionTypes.IsContent(request.Content))
            throw new ArgumentException(ContentError, nameof(request));

        await _ops.WaitAsync(ct);
        _activity = GridActivity.Starting;
        try
        {
            LauncherSettings s;
            lock (_configGate) s = ReloadSettings();
            if (RegionRequestError(s, request) is { } error) throw new ArgumentException(error, nameof(request));
            progress?.Report(SetupProgress.CreatingRegion);
            s = LoadSettings();
            if (_robust!.Process.State != ProcessState.Running)
                throw new InvalidOperationException("Robust is not running; start it first.");
            return await AddCoreAsync(s, request, Guid.NewGuid(), progress, ct);
        }
        finally { _activity = GridActivity.None; _ops.Release(); }
    }

    /// <summary>Why a region can't be added as asked (in words the website shows as they are), or null.</summary>
    private static string? RegionRequestError(LauncherSettings s, NewRegion r)
    {
        if (RegionModel.FindByName(s, r.Name) is not null) return $"There is already a region called {r.Name}.";
        if (r.Size is < 1 or > RegionPlacement.MaxSize) return "A region is 1 to 16 squares on each side.";
        if (RegionPlacement.Check(s, r.X, r.Y, r.Size) is { Ok: false } check) return $"That spot overlaps {check.OverlapsWith}.";
        if (r.ShareWith is { } share && s.Programs.All(p => p.Instance != share)) return $"There is no region group {share}.";
        return null;
    }

    /// <summary>
    /// Under the operation lock: saves the region (and its own program, unless it shares one) with ports from
    /// <see cref="PortAllocator"/>, starts it (a shared program restarts to load it), loads its content, and waits until
    /// it accepts logins.
    /// </summary>
    private async Task<RegionSettings> AddCoreAsync(LauncherSettings s, NewRegion r, Guid uuid, IProgress<string>? progress, CancellationToken ct)
    {
        RegionSettings region = null!;
        s = UpdateSettings(l =>
        {
            if (r.ShareWith is { } share)
            {
                region = new RegionSettings(share, r.Name, uuid, r.X, r.Y, r.Content, r.Size, PortAllocator.Next(l, l.RegionPort));
                return l with { Regions = [.. l.Regions, region] };
            }
            var instance = UniqueFolderName(l, SafeFolderName(r.Name));
            var httpPort = PortAllocator.Next(l, l.RegionPort);
            var consolePort = PortAllocator.Next(l, l.RegionConsolePort, new HashSet<int> { httpPort });
            region = new RegionSettings(instance, r.Name, uuid, r.X, r.Y, r.Content, r.Size, httpPort);
            return l with { Programs = [.. l.Programs, new ProgramSettings(instance, httpPort, consolePort)], Regions = [.. l.Regions, region] };
        });
        lock (_configGate) new RuntimeStager(paths).WriteLogConfigs(s.Programs.Select(p => p.Instance), LogLevelModule.LevelFor(s));

        _logins.Clear([uuid]);
        // A shared program loads the new region file only when it starts. A program of its own is new, or the one an
        // interrupted setup left running for this very region, which is adopted.
        if (r.ShareWith is not null && _programs.GetValueOrDefault(region.Instance) is { Process.State: ProcessState.Running } shared)
            await StopProgramAsync(shared, ct);
        EnsureComponents(s);
        var program = _programs[region.Instance];

        await StartProgramAsync(s, program, ct);
        await WaitForLoginsAsync(program, uuid, ct); // the scene is up and accepts console commands for it
        progress?.Report(SetupProgress.LoadingContent);
        await LoadContentAsync(program, region, r.Content, s, ct);
        progress?.Report(SetupProgress.WaitingForLogins);
        await WaitForLoginsAsync(program, uuid, ct);
        return region;
    }

    /// <summary>A program folder name no other program uses: the safe name, else with "_2", "_3", … added.</summary>
    private static string UniqueFolderName(LauncherSettings s, string name)
    {
        var taken = s.Programs.Select(p => p.Instance).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var n = 2; taken.Contains(candidate); n++) candidate = $"{name}_{n}";
        return candidate;
    }

    // ---- status and alerts --------------------------------------------------------------------------

    /// <summary>Reads only cached state (components are built under the operation lock), so it never waits.</summary>
    public GridStatus GetStatus()
    {
        var s = CurrentSettings();
        var held = _held;
        var programs = _programs;
        var loaded = _loaded;
        var processes = s.Programs.ToDictionary(p => p.Instance, p => Describe("region", programs.GetValueOrDefault(p.Instance)));
        var live = RegionModel.Live(s).ToList();
        var regions = live.Select(r => new RegionStatus(r.Uuid, r.Name, r.Instance,
                RegionStates.Derive(r, processes.GetValueOrDefault(r.Instance), held.Contains(r.Uuid), loaded.GetValueOrDefault(r.Instance),
                    _logins.Enabled(r.Uuid)),
                _logins.Enabled(r.Uuid), r.X, r.Y, r.Size,
                live.Where(o => o.Instance == r.Instance && o.Uuid != r.Uuid).Select(o => o.Name).ToList()))
            .ToList();
        return new GridStatus(
            Describe("database", _db),
            Describe("robust", _robust),
            s.Programs.Select(p => new ProgramStatus(p.Instance, processes[p.Instance],
                live.Where(r => r.Instance == p.Instance).Select(r => r.Uuid).ToList())).ToList(),
            regions,
            GridStatus.ComputeLoginsEnabled(regions, held),
            $"http://{_host}:{s.RobustPublicPort}",
            _startFailure,
            _activity);
    }

    /// <summary>The settings the controller last loaded (the file only on first use); never waits behind an operation.</summary>
    public LauncherSettings CurrentSettings()
    {
        // On a first launch LoadOrCreate generates and saves fresh secrets, so the fallback load is serialised
        // with the controller's own (re-checking the cache inside the lock).
        lock (_configGate) return _settings ?? ReloadSettings();
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
            case "enabled" or "disabled":
                // An alert for a region the launcher doesn't know (a hand-made region file) changes nothing.
                _logins.Apply(alert, CurrentSettings().Regions);
                break;
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
        _host = HostAddress.Effective(s.Hostname);
        _settings = s;
        return s;
    }

    private void WriteConfig(LauncherSettings s) =>
        new GridConfigWriter(paths).Write(s, _modules.Cast<IIniContributor>().ToList(), _held);

    /// <summary>Stages the runtime, writes the configs and checks the ports (the web port is the host's; adoptable children keep theirs).</summary>
    private async Task PrepareAsync(LauncherSettings s, CancellationToken ct)
    {
        await StageAndWriteConfigAsync(s, ct);

        // The host owns and binds the web port before StartAsync (RegionReady posts its alerts there).
        var skip = new HashSet<int> { s.WebPort };
        foreach (var c in _programs.Values.Append(_db).Append(_robust))
            if (c is not null && WillAdopt(c)) skip.UnionWith(c.Ports);
        var toStart = s.Programs.Where(p => RegionModel.Loadable(s, p.Instance, _held).Any()).Select(p => p.Instance).ToHashSet();
        if (PortCheck.FindConflictPort(s, skip, toStart) is { } conflict)
            throw new PortConflictException(conflict.Message, conflict.Port);
    }

    /// <summary>
    /// Stages the runtime and writes the configs. A Robust or region that runs from the old runtime copy is stopped
    /// before an upgrade restages it (on Linux the delete would succeed under it, leaving it on a deleted directory);
    /// R1 stays as the backstop: when the copy still fails because a child holds its DLLs, those are stopped and
    /// staging is retried once.
    /// </summary>
    private async Task StageAndWriteConfigAsync(LauncherSettings s, CancellationToken ct)
    {
        paths.EnsureCreated();
        var stager = new RuntimeStager(paths);
        bool upgrade;
        lock (_configGate) upgrade = stager.NeedsStaging();
        if (upgrade)
        {
            foreach (var program in _programs.Values.Where(WillAdopt))
                await StopProgramAsync(program, ct);
            if (WillAdopt(_robust!)) await _robust!.Process.StopAsync(ct);
        }
        try
        {
            lock (_configGate) stager.StageIfNeeded();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await StopProgramsAsync(ct);
            await _robust!.Process.StopAsync(ct);
            lock (_configGate) stager.StageIfNeeded();
        }
        lock (_configGate)
        {
            stager.WriteLogConfigs(s.Programs.Select(p => p.Instance), LogLevelModule.LevelFor(s));
            WriteConfig(s);
        }
    }

    private async Task StartDatabaseAsync(LauncherSettings s, CancellationToken ct)
    {
        if (mariaBin.NotFoundMessage is { } missing) throw new MariaDbNotFoundException(missing);
        if (!WillAdopt(_db!))
            await new MariaDbServer(mariaBin, paths, s).InitializeAsync(ct);
        await StartComponentAsync(_db!, ct);
        await new DatabaseProvisioner(s).EnsureAsync(ct);
    }

    /// <summary>
    /// Starts every program that has a loadable region, one after another. One that fails doesn't keep the others
    /// down; the first failure is thrown once all have been tried.
    /// </summary>
    private async Task StartProgramsAsync(LauncherSettings s, CancellationToken ct)
    {
        Exception? first = null;
        foreach (var p in s.Programs.Where(p => RegionModel.Loadable(s, p.Instance, _held).Any()))
        {
            try { await StartProgramAsync(s, _programs[p.Instance], ct); }
            catch (Exception e) when (e is not OperationCanceledException) { first ??= e; }
        }
        if (first is not null) throw first;
    }

    /// <summary>
    /// Starts or adopts one program. A started program loads its loadable regions. An adopted one announced its regions'
    /// logins before this launcher was listening, so it is asked which regions it loads and whether they accept logins.
    /// One still booting answers "disabled" and may send its alert while that answer is on its way: the alert is newer
    /// and wins.
    /// </summary>
    private async Task StartProgramAsync(LauncherSettings s, Component program, CancellationToken ct)
    {
        var instance = program.Instance!;
        var running = program.Process.State == ProcessState.Running;
        var adopted = running || WillAdopt(program);
        var regions = RegionModel.In(s, instance).ToList();
        if (!running)
        {
            if (!adopted) _logins.Clear(regions.Select(r => r.Uuid));
            await StartComponentAsync(program, ct);
        }
        if (!adopted)
        {
            SetLoaded(instance, RegionModel.Loadable(s, instance, _held).Select(r => r.Uuid).ToList());
            return;
        }

        var seen = regions.ToDictionary(r => r.Uuid, r => _logins.Version(r.Uuid));
        SetLoaded(instance, await GridConsoleCommands.LoadedRegionsAsync(program.Console!, ConsoleSettle, ct));
        var lines = await program.Console!.RunAsync("login status", ConsoleSettle, ct);
        var byName = GridConsoleCommands.ParseLoginStatus(lines);
        var any = GridConsoleCommands.ParseLoginsEnabled(lines) ?? false;
        foreach (var r in regions)
            _logins.SetIfUnchanged(r.Uuid, seen[r.Uuid], byName.TryGetValue(r.Name, out var enabled) ? enabled : any && regions.Count == 1);
    }

    private void SetLoaded(string instance, IReadOnlyCollection<Guid>? ids) =>
        _loaded = new Dictionary<string, IReadOnlyCollection<Guid>?>(_loaded) { [instance] = ids };

    private async Task StopProgramAsync(Component program, CancellationToken ct)
    {
        await program.Process.StopAsync(ct);
        _logins.Clear(RegionModel.In(CurrentSettings(), program.Instance!).Select(r => r.Uuid));
    }

    /// <summary>Stops every program, the last started first.</summary>
    private async Task StopProgramsAsync(CancellationToken ct)
    {
        foreach (var program in _programs.Values.Reverse())
            await StopProgramAsync(program, ct);
    }

    private async Task StopAllCoreAsync(CancellationToken ct)
    {
        await StopProgramsAsync(ct);
        await _robust!.Process.StopAsync(ct);
        await _db!.Process.StopAsync(ct); // graceful stop = MariaDB SHUTDOWN
    }

    private async Task StartComponentAsync(Component c, CancellationToken ct)
    {
        // Already running (a Start* command, or a retry): nothing to start, and its monitor keeps watching.
        if (c.Process.State == ProcessState.Running)
        {
            EnsureMonitor(c, onlyIfMissing: true);
            return;
        }
        await c.Process.StartAsync(ct);
        EnsureMonitor(c);
        if (c.Process.State != ProcessState.Running)
            throw new InvalidOperationException($"{c.Name} did not start: {c.Process.LastError ?? c.Process.State.ToString()}");
    }

    /// <summary>
    /// Starts monitoring the component. A previous monitor that has not finished yet (it is about to see the stop)
    /// is chained onto rather than skipped, so a component is never left unmonitored.
    /// </summary>
    private void EnsureMonitor(Component c, bool onlyIfMissing = false)
    {
        lock (_monitors)
        {
            if (_monitorCts.IsCancellationRequested) return;
            if (onlyIfMissing && _monitors.TryGetValue(c.Name, out var live) && !live.IsCompleted) return;
            var token = _monitorCts.Token;
            _monitors[c.Name] = _monitors.TryGetValue(c.Name, out var previous) && !previous.IsCompleted
                ? previous.ContinueWith(_ => c.Process.MonitorAsync(token), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default).Unwrap()
                : Task.Run(() => c.Process.MonitorAsync(token), CancellationToken.None);
        }
    }

    /// <summary>True when the component's PID file names a live process with its image: starting it will adopt.</summary>
    private static bool WillAdopt(Component c) =>
        PidFile.ReadLive(c.Spec.PidFile, c.Spec.ExpectedImage) is not null;

    /// <summary>Polls Robust until the account satisfies <paramref name="done"/> or <see cref="AccountWait"/> passes; returns the last account seen.</summary>
    private async Task<RobustAccount?> WaitForAccountAsync(string first, string last, Func<RobustAccount, bool> done, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + AccountWait;
        while (true)
        {
            var account = await robust.GetAccountAsync(first, last, ct);
            if ((account is not null && done(account)) || time.GetUtcNow() >= deadline) return account;
            await Task.Delay(AccountPoll, time, ct);
        }
    }

    private async Task WaitForLoginsAsync(Component program, Guid region, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + LoginWait;
        while (!_logins.Enabled(region))
        {
            if (program.Process.State == ProcessState.Failed)
                throw new InvalidOperationException($"The region failed: {program.Process.LastError}");
            if (time.GetUtcNow() >= deadline)
                throw new TimeoutException($"The region did not enable logins within {LoginWait.TotalSeconds:0} s.");
            await Task.Delay(LoginPoll, time, ct);
        }
    }

    /// <summary>Loads a region's content through its program's console; in a shared program the console switches to it first.</summary>
    private async Task LoadContentAsync(Component program, RegionSettings region, string content, LauncherSettings s, CancellationToken ct)
    {
        var console = program.Console!;
        var shared = RegionModel.Loadable(s, region.Instance, _held).Count() > 1;
        if (shared) await console.RunAsync($"change region {region.Name}", ConsoleSettle, ct);
        try { await LoadContentCoreAsync(console, region, content, ct); }
        finally
        {
            if (shared) await console.RunAsync("change region root", ConsoleSettle, CancellationToken.None);
        }
    }

    private async Task LoadContentCoreAsync(RestConsoleClient console, RegionSettings region, string content, CancellationToken ct)
    {
        var oar = content == "starter" ? StarterContent.StarterOar(paths) : RegionTypes.Oar(paths, content);
        if (oar is not null)
        {
            // RegionReady reports the archiver's result as an "oarloaded" or "oarnotloaded" alert.
            var loaded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _oarLoaded = loaded;
            // A 256 m OAR goes in the middle of a larger region; the squares around it keep the flat land OpenSim makes.
            var middle = 128 * (region.Size - 1);
            var displacement = region.Size > 1 ? $" --displacement <{middle},{middle},0>" : "";
            await console.RunAsync($"load oar --force-terrain --force-parcels{displacement} \"{Fwd(oar)}\"", ConsoleSettle, ct);
            if (await loaded.Task.WaitAsync(LoginWait, time, ct) != "oarloaded")
                throw new InvalidOperationException($"The region could not load {oar}; see the region log.");
            return;
        }

        // Without an OAR the land is generated at the region's size; a starter without its OAR is the default island.
        var shape = RegionTypes.Find(content)?.Shape ?? TerrainShape.Island;
        var r32 = Path.Combine(paths.RegionDir(region.Instance), "island.r32");
        TerrainShapes.WriteR32(r32, shape, 256 * region.Size);
        // Flatten first: the check below tells the island from flat land, and a starter OAR an earlier attempt
        // loaded has relief of its own that would pass for the island if this load failed.
        await console.RunAsync("terrain fill 0", ConsoleSettle, ct);
        await console.RunAsync($"terrain load \"{Fwd(r32)}\"", ConsoleSettle, ct);
        if (!await GridConsoleCommands.IslandTerrainLoadedAsync(console, ConsoleSettle, time, ct))
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
        var current = _programs;
        var next = new Dictionary<string, Component>();
        foreach (var p in s.Programs)
            next[p.Instance] = Refresh(current.GetValueOrDefault(p.Instance), ProgramKey(s, p), () => MakeProgram(s, p));
        // A program dropped from the settings is kept while it still runs, so it can be stopped.
        foreach (var (instance, c) in current)
            if (!next.ContainsKey(instance) && c.Process.State != ProcessState.Stopped) next[instance] = c;
        _programs = next;
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
        _ => throw UnknownComponent(component),
    };

    /// <summary>A program's spec depends on its ports, the console credentials and the regions it loads (their UDP ports).</summary>
    private string ProgramKey(LauncherSettings s, ProgramSettings p) =>
        $"{p.Instance}|{p.HttpPort}|{p.ConsolePort}|{s.Console.User}|{s.Console.Password}|" +
        string.Join(',', RegionModel.Loadable(s, p.Instance, _held).Select(r => $"{r.Uuid}:{r.UdpPort}"));

    private Component MakeDatabase(LauncherSettings s)
    {
        var server = new MariaDbServer(mariaBin, paths, s);
        var spec = new ProcessSpec("MariaDB", server.StartInfo, Path.Combine(paths.Pids, "mariadb.pid"),
            server.PingAsync, server.ShutdownAsync, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30),
            // Our datadir, not the server binary: a system mariadbd (Debian's) has the same image.
            "--datadir=" + paths.MariaDbData);
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

    private Component MakeProgram(LauncherSettings s, ProgramSettings p)
    {
        var console = MakeConsole(s, p.ConsolePort);
        var dll = Path.Combine(paths.RuntimeOpenSim, "OpenSim.dll");
        var loadable = RegionModel.Loadable(s, p.Instance, _held).ToList();
        // Same PID file the program writes itself ([Startup] PIDFile).
        var spec = new ProcessSpec("Region " + (loadable.FirstOrDefault()?.Name ?? p.Instance),
            () => Dotnet(dll, "-inidirectory=" + paths.RegionDir(p.Instance), "-console=rest",
                $"-logconfig=dreamgrid-region-{p.Instance}.log4net.xml"),
            Path.Combine(paths.Pids, p.Instance + ".pid"), console.IsUpAsync, ct => console.SendAsync("quit", ct),
            TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(60), dll);
        return new Component("program-" + p.Instance, new ManagedProcess(spec, new RestartPolicy(), time), spec,
            [p.HttpPort, p.ConsolePort, .. loadable.Select(r => r.UdpPort)], console, p.Instance) { Key = ProgramKey(s, p) };
    }

    private RestConsoleClient MakeConsole(LauncherSettings s, int port) =>
        new(http.CreateClient("DreamGrid.Console"), new Uri($"http://127.0.0.1:{port}"), s.Console.User, s.Console.Password,
            loggers?.CreateLogger<RestConsoleClient>());

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
