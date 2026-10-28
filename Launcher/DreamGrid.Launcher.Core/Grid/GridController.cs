using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Logs;
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
    ILoggerFactory? loggers = null,
    Func<LauncherSettings, IRegionStore>? regionStores = null,
    Action<int, IReadOnlyList<int>, CpuPriority>? cpu = null) : IGridController
{
    private static readonly TimeSpan ConsoleSettle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LoginWait = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan LoginPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AccountWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AccountPoll = TimeSpan.FromMilliseconds(500);
    // How long an OAR import or save may take, and how often a save is checked.
    private static readonly TimeSpan OarJobWait = TimeSpan.FromHours(2);
    private static readonly TimeSpan SavePoll = TimeSpan.FromSeconds(2);
    // How long a save may take to create its file before it counts as never started.
    private static readonly TimeSpan SaveStartWait = TimeSpan.FromSeconds(60);

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
    // The world name in the config last written, and in the one Robust read when this launcher started it (null: not
    // started by this launcher, or adopted, so unknown).
    private volatile string? _writtenGridName, _robustGridName;
    private volatile GridActivity _activity;
    private int? _regionAlertPid;
    // The archiver's "oarloaded"/"oarnotloaded" alert awaited for each region: two programs may load at once.
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> _oarLoaded = new();
    // The OAR job running in each program (its text), by instance. Added and checked under _ops; the job removes its own.
    private readonly ConcurrentDictionary<string, string> _jobs = new();
    // A region's rows in the database (Delete, Delete forever, the space a deleted region holds).
    private readonly Func<LauncherSettings, IRegionStore> _regionStores = regionStores ?? (s => new RegionStore(s));
    // Sets a running program's cores and priority (CpuTuning.Apply; tests see the calls instead).
    private readonly Action<int, IReadOnlyList<int>, CpuPriority> _cpu = cpu ?? CpuTuning.Apply;
    // What each running program was started (or adopted) from: its StartKey. Written under _ops, read by NeedsRestart.
    private readonly ConcurrentDictionary<string, string> _startedWith = new();
    // The PID whose start each program's key and CPU settings were last handled for, so a process is handled once.
    private readonly ConcurrentDictionary<string, int> _handledPid = new();
    // Programs a controller start (under _ops) is bringing up: it handles them itself once they run.
    private readonly ConcurrentDictionary<string, byte> _starting = new();

    /// <summary>Replaces the database's state for tests, whose MariaDB cannot start.</summary>
    internal Func<bool>? DatabaseRunningForTests { get; init; }

    /// <summary>Replaces how a program is launched for tests, whose OpenSim cannot start (null: OpenSim itself).</summary>
    internal Func<string, ProcessStartInfo>? ProgramStartForTests { get; init; }

    private bool DatabaseRunning => DatabaseRunningForTests?.Invoke() ?? _db?.Process.State == ProcessState.Running;

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
            ThrowIfBusy(RegionModel.Find(s, regionId)?.Instance);
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
            ThrowIfBusy(region.Instance);
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

    public async Task RenameRegionAsync(Guid regionId, string name, CancellationToken ct)
    {
        if (!IsValidRegionName(name)) throw new ArgumentException(NameRule);
        await _ops.WaitAsync(ct);
        try
        {
            var s = LoadSettings();
            var region = LiveRegion(s, regionId);
            ThrowIfBusy(region.Instance);
            // A running program keeps the scene under its old name until it restarts.
            if (HasLoaded(s, _programs.GetValueOrDefault(region.Instance), regionId))
                throw new InvalidOperationException($"Stop {region.Name} first; a region is renamed while it is offline.");
            // Its own name, in other letter case, is not taken.
            if (RegionModel.FindByName(s, name) is { } other && other.Uuid != regionId) throw new ArgumentException(NameTaken(name));
            if (name == region.Name) return;

            // The writer turns the old region file off; the region registers under its new name at its next start.
            UpdateSettings(l => l with { Regions = l.Regions.Select(r => r.Uuid == regionId ? r with { Name = name } : r).ToList() });
        }
        finally { _ops.Release(); }
    }

    public async Task ResizeRegionAsync(Guid regionId, int size, CancellationToken ct)
    {
        if (size is < 1 or > RegionPlacement.MaxSize) throw new ArgumentException(SizeRule);
        await _ops.WaitAsync(ct);
        try
        {
            var s = LoadSettings();
            var region = LiveRegion(s, regionId);
            ThrowIfBusy(region.Instance);
            if (!RegionPlacement.OnMap(region.X, region.Y, size)) throw new ArgumentException("That spot is off the map.");
            if (RegionPlacement.Check(s, region.X, region.Y, size, ignore: regionId) is { Ok: false } taken)
                throw new ArgumentException(RegionAdvice.Placement(taken));

            // Nothing restarts (Ruling E): the new size applies at the program's next start.
            UpdateSettings(l => l with { Regions = l.Regions.Select(r => r.Uuid == regionId ? r with { Size = size } : r).ToList() });
        }
        finally { _ops.Release(); }
    }

    public async Task SaveRegionOptionsAsync(Guid regionId, RegionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (RegionSettingsRules.Options(options) is { Ok: false } invalid) throw new ArgumentException(invalid.Errors.Values.First());
        await _ops.WaitAsync(ct);
        try
        {
            LiveRegion(LoadSettings(), regionId);
            UpdateSettings(l => l with { Regions = l.Regions.Select(r => r.Uuid == regionId ? r with { Options = options } : r).ToList() });
        }
        finally { _ops.Release(); }
    }

    public async Task<bool> SaveProgramOptionsAsync(string instance, string? physics, ProgramOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (RegionSettingsRules.Program(physics, options, Environment.ProcessorCount) is { Ok: false } invalid)
            throw new ArgumentException(invalid.Errors.Values.First());
        await _ops.WaitAsync(ct);
        try
        {
            var s = LoadSettings();
            var before = (s.Programs.FirstOrDefault(p => p.Instance == instance)
                ?? throw new ArgumentException("There is no such group of regions.")).Options ?? new ProgramOptions();
            UpdateSettings(l => l with
            {
                Programs = l.Programs.Select(p => p.Instance == instance ? p with { Physics = physics, Options = options } : p).ToList(),
            });

            // The operating system changes a running process's cores and priority at once; the rest waits for a restart.
            var cores = RegionSettingsRules.UsableCores(options.Cores, Environment.ProcessorCount);
            var changed = options.Priority != before.Priority
                || !cores.Order().SequenceEqual(RegionSettingsRules.UsableCores(before.Cores, Environment.ProcessorCount).Order());
            if (!changed || _programs.GetValueOrDefault(instance) is not { Process.State: ProcessState.Running } program
                || program.Process.Pid is not { } pid)
                return false;
            _cpu(pid, cores, options.Priority);
            return true;
        }
        finally { _ops.Release(); }
    }

    /// <summary>Reads only cached state, so it never waits behind an operation.</summary>
    public bool NeedsRestart(string instance)
    {
        if (_programs.GetValueOrDefault(instance) is not { Process.State: ProcessState.Running }
            || !_startedWith.TryGetValue(instance, out var started))
            return false;
        var s = CurrentSettings();
        return s.Programs.FirstOrDefault(p => p.Instance == instance) is { } program && StartKey(s, program) != started;
    }

    /// <summary>
    /// Everything a program's start reads from the settings, other than ports and CPU: its effective physics and scripts,
    /// its other options, and each loadable region's name, size and options.
    /// </summary>
    private string StartKey(LauncherSettings s, ProgramSettings p)
    {
        var options = (p.Options ?? new ProgramOptions()) with { Scripts = null, Cores = null, Priority = CpuPriority.Normal };
        var regions = RegionModel.Loadable(s, p.Instance, _held).Select(r => $"{r.Name}|{r.Size}|{r.Options ?? new RegionOptions()}");
        return string.Join('\n',
            [RegionSettingsRules.EffectivePhysics(s, p), RegionSettingsRules.EffectiveScripts(s, p).ToString(), options.ToString(), .. regions]);
    }

    /// <summary>
    /// Once a program has just started, restarted or been adopted, from <paramref name="s"/>: records what it started from, and
    /// applies its cores and priority unless they are the defaults. A failure there is logged; the program keeps running.
    /// </summary>
    private void ProgramUp(LauncherSettings s, string instance, int? pid)
    {
        if (pid is { } handled) _handledPid[instance] = handled;
        if (s.Programs.FirstOrDefault(p => p.Instance == instance) is not { } settings) return;
        _startedWith[instance] = StartKey(s, settings);

        var options = settings.Options ?? new ProgramOptions();
        var cores = RegionSettingsRules.UsableCores(options.Cores, Environment.ProcessorCount);
        if ((cores.Count == 0 && options.Priority == CpuPriority.Normal) || pid is not { } target) return;
        try { _cpu(target, cores, options.Priority); }
        catch (Exception e)
        {
            _log?.LogWarning(e, "The cores and priority of {Program} could not be set: {Reason}", instance, e.Message);
        }
    }

    /// <summary>
    /// A program's process reached Running on its own: the monitor restarted it after a crash. The new process read the
    /// region files as they are now (its spec names only the folder, ports and console), so it is keyed from the current
    /// settings and gets its CPU settings again. Runs off the process's event thread and never takes the operation lock: a
    /// controller start holds it while it waits for Running, and handles its own start (<see cref="_starting"/>).
    /// </summary>
    private void OnProgramRunning(string instance, ManagedProcess process)
    {
        if (_starting.ContainsKey(instance) || _programs.GetValueOrDefault(instance)?.Process != process) return;
        if (process.Pid is not { } pid || (_handledPid.TryGetValue(instance, out var handled) && handled == pid)) return;
        _log?.LogInformation("{Program} came back after a crash; its CPU settings are applied again.", instance);
        ProgramUp(CurrentSettings(), instance, pid);
    }

    /// <summary>The region, which must exist and not be deleted.</summary>
    private static RegionSettings LiveRegion(LauncherSettings s, Guid regionId)
    {
        var region = RegionModel.Find(s, regionId) ?? throw new InvalidOperationException("There is no such region.");
        if (region.DeletedAt is not null) throw new InvalidOperationException("That region has been deleted.");
        return region;
    }

    public async Task ChangeRunsWithAsync(Guid regionId, string? shareWith, CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        try
        {
            var s = LoadSettings();
            var region = RegionModel.Find(s, regionId) ?? throw new InvalidOperationException("There is no such region.");
            if (region.DeletedAt is not null) throw new InvalidOperationException("That region has been deleted.");
            var from = region.Instance;
            ThrowIfBusy(from, shareWith);
            if (shareWith == from) throw new ArgumentException("It already runs with them.");
            if (shareWith is not null && s.Programs.All(p => p.Instance != shareWith))
                throw new ArgumentException("There is no such group of regions.");
            if (shareWith is null && RegionModel.In(s, from).All(r => r.Uuid == regionId))
                throw new ArgumentException("It already runs on its own.");

            var oldProgram = _programs.GetValueOrDefault(from);
            var ran = HasLoaded(s, oldProgram, regionId);

            var to = shareWith!;
            s = UpdateSettings(l =>
            {
                var programs = l.Programs.ToList();
                if (shareWith is null)
                {
                    // A program of its own with the old one's physics, so nothing about the region itself changes.
                    to = UniqueFolderName(l, SafeFolderName(region.Name));
                    var httpPort = PortAllocator.Next(l, l.RegionPort);
                    var consolePort = PortAllocator.Next(l, l.RegionConsolePort, new HashSet<int> { httpPort });
                    var program = new ProgramSettings(to, httpPort, consolePort);
                    programs.Add(RegionModel.ProgramOf(l, region) is { } old ? program with { Physics = old.Physics, Options = old.Options } : program);
                }
                var regions = l.Regions.Select(r => r.Uuid == regionId ? r with { Instance = to } : r).ToList();
                // A program left with no regions goes; one with only parked regions stays for them.
                if (!regions.Any(r => r.DeletedAt is null && r.Instance == from)) programs.RemoveAll(p => p.Instance == from);
                return l with { Programs = programs, Regions = regions };
            });
            if (shareWith is null)
                lock (_configGate) new RuntimeStager(paths).WriteLogConfigs(s.Programs.Select(p => p.Instance), LogLevelModule.LevelFor(s));

            // Only a running region moves at once (the page asks first, naming the same regions). Otherwise only the
            // settings change: the region loads at its new program's next start, and a stopped world stays stopped.
            var removed = !s.Programs.Any(p => p.Instance == from);
            var affected = new List<string>();
            var steps = new List<Func<Task>>();
            if (LeftProgramStep(s, from, oldProgram, ran, removed, affected, ct) is { } leave) steps.Add(leave);
            if (ran)
            {
                EnsureComponents(s);
                var newProgram = _programs[to];
                if (RegionPowerPlan.IsActive(newProgram.Process.State))
                {
                    affected.AddRange(RegionModel.Loadable(s, to, _held).Where(r => r.Uuid != regionId).Select(r => r.Name));
                    steps.Add(() => RestartProgramAsync(s, newProgram, ct));
                }
                else
                    steps.Add(async () => { EnsureComponents(s); await StartProgramAsync(s, _programs[to], ct); });
            }

            if (affected.Count > 0)
                _log?.LogInformation("Change how {Region} runs: also affected {Others}.", region.Name, string.Join(", ", affected));
            _activity = GridActivity.Restarting;
            await RunProgramStepsAsync(s, steps, removed ? from : null);
        }
        finally { _activity = GridActivity.None; _ops.Release(); }
    }

    /// <summary>True when the program is up and loaded the region (an unknown list counts as loaded).</summary>
    private bool HasLoaded(LauncherSettings s, Component? program, Guid regionId) =>
        program is not null && RegionPowerPlan.IsActive(program.Process.State)
        && RegionModel.Loadable(s, program.Instance!, _held).Any(r => r.Uuid == regionId)
        && (_loaded.GetValueOrDefault(program.Instance!) is not { } ids || ids.Contains(regionId));

    /// <summary>
    /// What a program does once a region has left it (moved out or deleted), with <paramref name="s"/> already saved: with
    /// nothing left to load it stops, and a <paramref name="removed"/> one stops whole even at shutdown, since nothing would
    /// adopt it later; one that <paramref name="ran"/> the region restarts without it, adding the regions that restart with it
    /// to <paramref name="affected"/>. A failed or stopped program is left alone (null).
    /// </summary>
    private Func<Task>? LeftProgramStep(LauncherSettings s, string instance, Component? program, bool ran, bool removed,
        List<string> affected, CancellationToken ct)
    {
        if (program is null || !RegionPowerPlan.IsActive(program.Process.State)) return null;
        var left = RegionModel.Loadable(s, instance, _held).Select(r => r.Name).ToList();
        if (removed || left.Count == 0)
        {
            var stop = removed ? CancellationToken.None : ct;
            return () => StopProgramAsync(program, stop);
        }
        if (!ran) return null;
        affected.AddRange(left);
        return () => RestartProgramAsync(s, program, ct);
    }

    /// <summary>
    /// Runs a change's stop, restart and start steps. One program that fails to come back doesn't keep another down; the first
    /// failure is thrown afterwards. A program <paramref name="removed"/> from the settings, now stopped, then leaves the status,
    /// also on cancellation.
    /// </summary>
    private async Task RunProgramStepsAsync(LauncherSettings s, List<Func<Task>> steps, string? removed)
    {
        try
        {
            Exception? first = null;
            foreach (var step in steps)
            {
                try { await step(); }
                catch (Exception e) when (e is not OperationCanceledException) { first ??= e; }
            }
            if (first is not null) throw first;
        }
        finally
        {
            if (removed is not null)
            {
                EnsureComponents(s);
                _loaded = _loaded.Where(kv => kv.Key != removed).ToDictionary(kv => kv.Key, kv => kv.Value);
            }
        }
    }

    // ---- delete, restore and delete forever ---------------------------------------------------------

    public async Task DeleteRegionAsync(Guid regionId, CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        try
        {
            var s = LoadSettings();
            var region = RegionModel.Find(s, regionId) ?? throw new InvalidOperationException("There is no such region.");
            if (region.DeletedAt is not null) throw new InvalidOperationException("That region has been deleted.");
            var instance = region.Instance;
            ThrowIfBusy(instance);
            var program = _programs.GetValueOrDefault(instance);
            var ran = HasLoaded(s, program, regionId);

            var now = time.GetUtcNow();
            s = UpdateSettings(l =>
            {
                var regions = l.Regions.Select(r => r.Uuid == regionId ? r with { DeletedAt = now } : r).ToList();
                var programs = l.Programs.ToList();
                // A program left with no live region goes; the deleted region keeps its folder name.
                if (!regions.Any(r => r.DeletedAt is null && r.Instance == instance)) programs.RemoveAll(p => p.Instance == instance);
                return l with { Programs = programs, Regions = regions };
            });
            _logins.Clear([regionId]);

            var removed = !s.Programs.Any(p => p.Instance == instance);
            var affected = new List<string>();
            var steps = new List<Func<Task>>();
            if (LeftProgramStep(s, instance, program, ran, removed, affected, ct) is { } leave) steps.Add(leave);
            if (affected.Count > 0)
                _log?.LogInformation("Delete {Region}: also affected {Others}.", region.Name, string.Join(", ", affected));
            if (steps.Count > 0) _activity = affected.Count > 0 ? GridActivity.Restarting : GridActivity.Stopping;
            try { await RunProgramStepsAsync(s, steps, removed ? instance : null); }
            // Also after a program that failed to come back: the region is deleted either way.
            finally { await DeregisterAsync(s, region, ct); }
        }
        finally { _activity = GridActivity.None; _ops.Release(); }
    }

    /// <summary>
    /// Removes a deleted region's row from Robust's map, so one that crashed leaves the map too. Skipped (and logged) while
    /// the database isn't running; a failure is logged, not thrown, since the delete itself is done.
    /// </summary>
    private async Task DeregisterAsync(LauncherSettings s, RegionSettings region, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        if (!DatabaseRunning)
        {
            _log?.LogInformation("Delete {Region}: the database isn't running, so its row in Robust's regions table stays.", region.Name);
            return;
        }
        try { await _regionStores(s).DeregisterAsync(region.Uuid, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log?.LogWarning(e, "Delete {Region}: its row in Robust's regions table could not be removed.", region.Name);
        }
    }

    public async Task RestoreRegionAsync(Guid regionId, CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        try
        {
            var s = LoadSettings();
            var region = RegionModel.Find(s, regionId) ?? throw new InvalidOperationException("There is no such region.");
            if (region.DeletedAt is null) throw new InvalidOperationException("That region isn't deleted.");
            var instance = region.Instance;

            // It comes back stopped and nothing restarts: a running program holds it until its Start or the world's.
            if (_programs.GetValueOrDefault(instance) is { } program && RegionPowerPlan.IsActive(program.Process.State))
                _held = new HashSet<Guid>(_held) { regionId };
            var added = false;
            s = UpdateSettings(l =>
            {
                var programs = l.Programs.ToList();
                int? udpPort = null;
                if (programs.All(p => p.Instance != instance))
                {
                    // Its program went: a new one under the same folder name, with fresh ports.
                    var httpPort = PortAllocator.Next(l, l.RegionPort);
                    var consolePort = PortAllocator.Next(l, l.RegionConsolePort, new HashSet<int> { httpPort });
                    programs.Add(new ProgramSettings(instance, httpPort, consolePort));
                    udpPort = httpPort;
                    added = true;
                }
                var regions = l.Regions.Select(r => r.Uuid == regionId ? r with { DeletedAt = null, UdpPort = udpPort ?? r.UdpPort } : r).ToList();
                return l with { Programs = programs, Regions = regions };
            });
            if (added)
                lock (_configGate) new RuntimeStager(paths).WriteLogConfigs(s.Programs.Select(p => p.Instance), LogLevelModule.LevelFor(s));
            _logins.Clear([regionId]);
            EnsureComponents(s);
        }
        finally { _ops.Release(); }
    }

    public async Task EraseRegionAsync(Guid regionId, CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        try
        {
            var s = LoadSettings();
            var region = RegionModel.Find(s, regionId) ?? throw new InvalidOperationException("There is no such region.");
            if (region.DeletedAt is null) throw new InvalidOperationException("That region isn't deleted.");
            if (!DatabaseRunning) throw new InvalidOperationException("Start the database first.");

            await _regionStores(s).EraseAsync(regionId, ct);
            // Its name, spot, ports and folder name are free again; the folder itself stays, with its logs.
            _held = _held.Where(id => id != regionId).ToHashSet();
            UpdateSettings(l => l with { Regions = l.Regions.Where(r => r.Uuid != regionId).ToList() });
        }
        finally { _ops.Release(); }
    }

    /// <summary>Reads the database without the operation lock, so the page never waits behind a start or stop.</summary>
    public async Task<IReadOnlyDictionary<Guid, long>?> DeletedSpaceAsync(CancellationToken ct)
    {
        if (!DatabaseRunning) return null;
        var s = CurrentSettings();
        var deleted = s.Regions.Where(r => r.DeletedAt is not null).Select(r => r.Uuid).ToList();
        if (deleted.Count == 0) return new Dictionary<Guid, long>();
        return await _regionStores(s).SpaceAsync(deleted, ct);
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
        $"Content must be starter, a built-in type ({string.Join(", ", RegionTypes.BuiltIn.Select(t => t.Id))}) or one of your region types.";

    /// <summary>True for starter, a built-in type and one of the settings' custom types.</summary>
    private bool IsContent(string content)
    {
        lock (_configGate) return RegionTypes.IsContent(ReloadSettings(), content);
    }

    public async Task CreateRegionAsync(string name, string content, CancellationToken ct, IProgress<string>? progress = null)
    {
        if (!IsValidRegionName(name))
            throw new ArgumentException(NameRule, nameof(name));
        if (!IsContent(content))
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
            await AddCoreAsync(s, new NewRegion(name, 1000, 1000, 1, content, null), uuid, progress, ct, adoptLeftover: true);
            UpdateSettings(l => l with { Setup = l.Setup with { RegionCreated = true } }, writeConfig: false);
        }
        finally { _activity = GridActivity.None; _ops.Release(); }
    }

    public async Task<RegionSettings> AddRegionAsync(NewRegion request, CancellationToken ct, IProgress<string>? progress = null)
    {
        if (!IsValidRegionName(request.Name))
            throw new ArgumentException(NameRule, nameof(request));
        if (!IsContent(request.Content))
            throw new ArgumentException(ContentError, nameof(request));

        await _ops.WaitAsync(ct);
        _activity = GridActivity.Starting;
        try
        {
            LauncherSettings s;
            lock (_configGate) s = ReloadSettings();
            if (RegionRequestError(s, request) is { } error) throw new ArgumentException(error, nameof(request));
            // Sharing restarts that program, which would cut off its OAR job.
            ThrowIfBusy(request.ShareWith);
            progress?.Report(SetupProgress.CreatingRegion);
            s = LoadSettings();
            if (_robust!.Process.State != ProcessState.Running)
                throw new InvalidOperationException("Robust is not running; start it first.");
            return await AddCoreAsync(s, request, Guid.NewGuid(), progress, ct);
        }
        finally { _activity = GridActivity.None; _ops.Release(); }
    }

    private const string NameRule = "A region name is 2-32 letters, digits, spaces, '-', '_' or '\\'' with no leading or trailing space.";
    private const string SizeRule = "A region is 1 to 16 squares on each side.";

    private static string NameTaken(string name) => $"There is already a region called {name}.";

    /// <summary>Why a region can't be added as asked (in words the website shows as they are), or null.</summary>
    private static string? RegionRequestError(LauncherSettings s, NewRegion r)
    {
        if (RegionModel.FindByName(s, r.Name) is not null) return NameTaken(r.Name);
        if (r.Size is < 1 or > RegionPlacement.MaxSize) return SizeRule;
        if (RegionPlacement.Check(s, r.X, r.Y, r.Size) is { Ok: false } check) return RegionAdvice.Placement(check);
        if (r.ShareWith is { } share && s.Programs.All(p => p.Instance != share)) return "That group of regions no longer exists.";
        return null;
    }

    /// <summary>
    /// Under the operation lock: saves the region (and its own program, unless it shares one) with ports from
    /// <see cref="PortAllocator"/>, starts it (a shared program restarts to load it), loads its content, and waits until
    /// it accepts logins. <paramref name="adoptLeftover"/> lets a program of its own take over the folder of one an earlier
    /// attempt left running (see <see cref="UniqueFolderName"/>).
    /// </summary>
    private async Task<RegionSettings> AddCoreAsync(LauncherSettings s, NewRegion r, Guid uuid, IProgress<string>? progress, CancellationToken ct,
        bool adoptLeftover = false)
    {
        RegionSettings region = null!;
        s = UpdateSettings(l =>
        {
            if (r.ShareWith is { } share)
            {
                region = new RegionSettings(share, r.Name, uuid, r.X, r.Y, r.Content, r.Size, PortAllocator.Next(l, l.RegionPort), NeedsContent: true);
                return l with { Regions = [.. l.Regions, region] };
            }
            var instance = UniqueFolderName(l, SafeFolderName(r.Name), adoptLeftover);
            var httpPort = PortAllocator.Next(l, l.RegionPort);
            var consolePort = PortAllocator.Next(l, l.RegionConsolePort, new HashSet<int> { httpPort });
            region = new RegionSettings(instance, r.Name, uuid, r.X, r.Y, r.Content, r.Size, httpPort, NeedsContent: true);
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
        ClearNeedsContent(uuid);
        progress?.Report(SetupProgress.WaitingForLogins);
        await WaitForLoginsAsync(program, uuid, ct);
        return region;
    }

    /// <summary>
    /// A program folder name nobody holds: the safe name, else with "_2", "_3", … added. A program holds its name, a deleted
    /// region its program's (until it is erased), and so does a live process its PID file names (a removed program that
    /// still runs). With <paramref name="adoptLeftover"/>, a live process under the safe name itself doesn't count: setup's
    /// retry takes over the program an earlier attempt left running.
    /// </summary>
    private string UniqueFolderName(LauncherSettings s, string name, bool adoptLeftover = false)
    {
        var taken = s.Programs.Select(p => p.Instance)
            .Concat(s.Regions.Where(r => r.DeletedAt is not null).Select(r => r.Instance))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dll = Path.Combine(paths.RuntimeOpenSim, "OpenSim.dll");
        bool Held(string candidate) => taken.Contains(candidate)
            || (!(adoptLeftover && candidate == name) && PidFile.ReadLive(Path.Combine(paths.Pids, candidate + ".pid"), dll) is not null);
        var candidate = name;
        for (var n = 2; Held(candidate); n++) candidate = $"{name}_{n}";
        return candidate;
    }

    // ---- OAR jobs -----------------------------------------------------------------------------------

    private const string SaveFailed = "The save did not finish; see the region log.";
    private const int MoveAttempts = 3;

    public async Task<string?> RunOarJobAsync(OarJobRequest request, CancellationToken ct, IProgress<OarJobProgress>? progress = null)
    {
        if (!Enum.IsDefined(request.Kind)) throw new ArgumentException("There is no such OAR job.");
        string? oar = null;
        if (request.Kind == OarJobKind.Import)
            oar = (request.File is { } file ? new OarLibrary(paths).Resolve(file) : null)
                ?? throw new ArgumentException("That file is not in the OAR library.");
        else if (request.File is not null)
            throw new ArgumentException("Only an import names a file.");

        var (s, region, program) = await BeginOarJobAsync(request, ct);
        try
        {
            switch (request.Kind)
            {
                case OarJobKind.Import:
                    await ImportOarAsync(s, region, program, request.File!, oar!, progress, ct);
                    return null;
                case OarJobKind.Save:
                    return await SaveOarAsync(s, region, program, progress, ct);
                default:
                    await MakeLandAsync(s, region, program, progress, ct);
                    return null;
            }
        }
        finally { _jobs.TryRemove(region.Instance, out _); }
    }

    public string? BusyWith(string instance) => _jobs.TryGetValue(instance, out var job) ? job : null;

    /// <summary>
    /// Under the operation lock, briefly: checks that the region runs and its program has no job, then records the job.
    /// Returns the settings, the region and its program.
    /// </summary>
    private async Task<(LauncherSettings, RegionSettings, Component)> BeginOarJobAsync(OarJobRequest request, CancellationToken ct)
    {
        await _ops.WaitAsync(ct);
        try
        {
            var s = LoadSettings();
            var region = RegionModel.Find(s, request.RegionId) ?? throw new ArgumentException("There is no such region.");
            if (region.DeletedAt is not null) throw new ArgumentException("That region has been deleted.");
            // Running means: its program runs and loaded it (an unknown list counts as loaded), and it accepts logins.
            if (_programs.GetValueOrDefault(region.Instance) is not { Process.State: ProcessState.Running } program
                || RegionModel.Loadable(s, region.Instance, _held).All(r => r.Uuid != region.Uuid)
                || (_loaded.GetValueOrDefault(region.Instance) is { } ids && !ids.Contains(region.Uuid))
                || !_logins.Enabled(region.Uuid))
                throw new InvalidOperationException($"Start {region.Name} first; an OAR needs it running.");
            ThrowIfBusy(region.Instance);
            _jobs[region.Instance] = request.Kind switch
            {
                OarJobKind.Import => $"Importing {request.File} into {region.Name}",
                OarJobKind.Save => $"Saving {region.Name} as an OAR",
                _ => $"Making the land of {region.Name} again",
            };
            return (s, region, program);
        }
        finally { _ops.Release(); }
    }

    /// <summary>Under the operation lock: refuses to change a program (or programs) while an OAR job runs in it.</summary>
    private void ThrowIfBusy(params string?[] instances)
    {
        foreach (var instance in instances)
            if (instance is not null && _jobs.TryGetValue(instance, out var job))
                throw new InvalidOperationException($"{job} is still running. Try again when it finishes.");
    }

    /// <summary>
    /// Loads the region's own type again, then waits until it accepts logins. A program that stops meanwhile ends the job at
    /// once with "The region stopped before the land loaded.".
    /// </summary>
    private async Task MakeLandAsync(LauncherSettings s, RegionSettings region, Component program,
        IProgress<OarJobProgress>? progress, CancellationToken ct)
    {
        const string stopped = "The region stopped before the land loaded.";
        progress?.Report(new OarJobProgress(SetupProgress.LoadingContent, null));
        try
        {
            await OnRegionConsoleAsync(program, region, s,
                console => LoadRegionContentAsync(console, region, region.Content, s, (program, stopped), ct), ct);
        }
        // A console that stops answering (the program went away) fails with its own error: the stop is the reason.
        catch (Exception e) when (e is not OperationCanceledException && program.Process.State != ProcessState.Running)
        {
            throw new InvalidOperationException(stopped, e);
        }
        ClearNeedsContent(region.Uuid);
        progress?.Report(new OarJobProgress(SetupProgress.WaitingForLogins, null));
        await WaitForLoginsAsync(program, region.Uuid, ct, stopped);
    }

    /// <summary>
    /// Waits for the archiver's alert for an OAR load and returns it. Fails with <paramref name="stopped"/> as soon as the
    /// program leaves Running, and with <paramref name="timedOut"/> after <see cref="OarJobWait"/>.
    /// </summary>
    private async Task<string> WaitForOarAlertAsync(OarAlertWait loaded, Component program, string stopped, string timedOut, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + OarJobWait;
        while (!loaded.Result.IsCompleted)
        {
            if (program.Process.State != ProcessState.Running) throw new InvalidOperationException(stopped);
            if (time.GetUtcNow() >= deadline) throw new InvalidOperationException(timedOut);
            await Task.WhenAny(loaded.Result, Task.Delay(LoginPoll, time, ct));
            ct.ThrowIfCancellationRequested();
        }
        return await loaded.Result;
    }

    /// <summary>Loads a library OAR into the region at the OAR's own size and place, then waits until it accepts logins.</summary>
    private async Task ImportOarAsync(LauncherSettings s, RegionSettings region, Component program, string file, string oar,
        IProgress<OarJobProgress>? progress, CancellationToken ct)
    {
        const string stopped = "The region stopped before the import finished.";
        progress?.Report(new OarJobProgress("Loading the OAR", null));
        await OnRegionConsoleAsync(program, region, s, async console =>
        {
            using var loaded = ExpectOarAlert(region.Uuid);
            await console.RunAsync($"load oar --force-terrain --force-parcels \"{Fwd(oar)}\"", ConsoleSettle, ct);
            if (await WaitForOarAlertAsync(loaded, program, stopped, $"The region did not load {file}; see the region log.", ct) != "oarloaded")
                throw new InvalidOperationException($"The region could not load {file}; see the region log.");
        }, ct);
        ClearNeedsContent(region.Uuid);
        progress?.Report(new OarJobProgress(SetupProgress.WaitingForLogins, null));
        await WaitForLoginsAsync(program, region.Uuid, ct, stopped);
    }

    /// <summary>
    /// Saves the region into <see cref="LauncherPaths.OarsSaving"/>, then moves the finished file into the library. OpenSim
    /// reports the end of a save only in the region log (ArchiveWriteRequest.CloseArchive), so the lines the log gains are
    /// read about every 2 s. OpenSim logs that end even for a save that failed, and only then rethrows the failure, so the log
    /// is read once more after it. A failed save's file is removed; when the launcher stops instead, OpenSim may still be
    /// writing it, and the next start's <see cref="OarLibrary.ClearStaging"/> removes it.
    /// </summary>
    private async Task<string> SaveOarAsync(LauncherSettings s, RegionSettings region, Component program,
        IProgress<OarJobProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.OarsSaving);
        var staging = Path.Combine(paths.OarsSaving, Guid.NewGuid().ToString("N") + ".oar");
        var log = new LogFollower(Path.Combine(paths.Logs, $"region-{region.Instance}.log"));
        var finished = $"[ARCHIVER]: Finished writing out OAR for {region.Name}";
        var moved = false;
        // Once the save is whole, the file is kept even when the move fails.
        var whole = false;
        try
        {
            await OnRegionConsoleAsync(program, region, s, async console =>
            {
                await console.RunAsync($"save oar \"{Fwd(staging)}\"", ConsoleSettle, ct);
                var sent = time.GetUtcNow();
                var deadline = sent + OarJobWait;
                // Set once the Finished line is read; the save is whole if the next read shows no failure after it.
                var ended = false;
                while (true)
                {
                    var started = File.Exists(staging);
                    progress?.Report(new OarJobProgress("Saving the OAR", started ? new FileInfo(staging).Length : null));
                    var followed = ended;
                    foreach (var line in log.ReadNew())
                    {
                        if (line.Contains("[ARCHIVER]: Error", StringComparison.Ordinal)
                            || (ended && line.Contains("Exception", StringComparison.Ordinal)))
                            throw new InvalidOperationException(SaveFailed);
                        if (line.TrimEnd().EndsWith(finished, StringComparison.Ordinal)) ended = true;
                    }
                    if (followed)
                    {
                        if (!OarFile.LooksLikeOar(staging)) throw new InvalidOperationException(SaveFailed);
                        whole = true;
                        return;
                    }
                    if (!ended)
                    {
                        if (program.Process.State != ProcessState.Running)
                            throw new InvalidOperationException("The region stopped before the save finished.");
                        if (!started && time.GetUtcNow() >= sent + SaveStartWait)
                            throw new InvalidOperationException("The region did not start the save; see the region log.");
                        if (time.GetUtcNow() >= deadline) throw new InvalidOperationException(SaveFailed);
                    }
                    await Task.Delay(SavePoll, time, ct);
                }
            }, ct);

            // The free name can be taken before the move (an upload of the same name): a few tries, each with a fresh name.
            var library = new OarLibrary(paths);
            var wanted = $"{region.Name}-{time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.oar";
            for (var attempt = 1; ; attempt++)
            {
                var name = library.FreeName(wanted);
                try
                {
                    File.Move(staging, Path.Combine(paths.Oars, name));
                    moved = true;
                    return name;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (attempt < MoveAttempts) continue;
                    _log?.LogError(e, "The saved OAR of {Region} could not be moved into the library as {Name}; it is at {Path}.", region.Name, name, staging);
                    throw new InvalidOperationException(
                        $"The OAR was saved but could not be moved into the library. It is at {staging} until the launcher restarts; see the launcher log.", e);
                }
            }
        }
        finally
        {
            if (!moved && !whole && !ct.IsCancellationRequested)
            {
                try { File.Delete(staging); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
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
            _activity,
            _robustGridName is { } started && _robust?.Process.State == ProcessState.Running && started != s.GridName);
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
            case "oarloaded" or "oarnotloaded":
                if (_oarLoaded.TryGetValue(alert.RegionId, out var waiting)) waiting.TrySetResult(alert.Login.ToLowerInvariant());
                break;
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

    private void WriteConfig(LauncherSettings s)
    {
        new GridConfigWriter(paths).Write(s, _modules.Cast<IIniContributor>().ToList(), _held);
        _writtenGridName = s.GridName;
    }

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
            // The latest settings, not the ones this start read: a change saved while staging ran (setup's world name,
            // which waited for the config lock) has written its config already and must not be overwritten.
            var latest = ReloadSettings();
            stager.WriteLogConfigs(latest.Programs.Select(p => p.Instance), LogLevelModule.LevelFor(latest));
            WriteConfig(latest);
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
        // An OAR job may have switched this program's console to one region, and then "show regions" and "login status"
        // answer for that region only: what is known about the running program stays as it is.
        if (running && _jobs.ContainsKey(instance)) return;
        var adopted = running || WillAdopt(program);
        var regions = RegionModel.In(s, instance).ToList();
        if (!running)
        {
            if (!adopted) _logins.Clear(regions.Select(r => r.Uuid));
            _starting[instance] = 0;
            try
            {
                await StartComponentAsync(program, ct);
                ProgramUp(s, instance, program.Process.Pid);
            }
            finally { _starting.TryRemove(instance, out _); }
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
        // Robust reads the world name only when it starts; an adopted one may have read any.
        if (c == _robust) _robustGridName = WillAdopt(c) ? null : _writtenGridName;
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

    /// <summary>Waits until the region accepts logins; with <paramref name="stopped"/>, a program that leaves Running fails at once with it.</summary>
    private async Task WaitForLoginsAsync(Component program, Guid region, CancellationToken ct, string? stopped = null)
    {
        var deadline = time.GetUtcNow() + LoginWait;
        while (!_logins.Enabled(region))
        {
            if (stopped is not null && program.Process.State != ProcessState.Running)
                throw new InvalidOperationException(stopped);
            if (program.Process.State == ProcessState.Failed)
                throw new InvalidOperationException($"The region failed: {program.Process.LastError}");
            if (time.GetUtcNow() >= deadline)
                throw new TimeoutException($"The region did not enable logins within {LoginWait.TotalSeconds:0} s.");
            await Task.Delay(LoginPoll, time, ct);
        }
    }

    /// <summary>Loads a region's content through its program's console; in a shared program the console switches to it first.</summary>
    private Task LoadContentAsync(Component program, RegionSettings region, string content, LauncherSettings s, CancellationToken ct) =>
        OnRegionConsoleAsync(program, region, s, console => LoadContentCoreAsync(console, region, content, s, ct), ct);

    /// <summary>
    /// Runs <paramref name="work"/> on the program's console. In a shared program the console switches to the region first
    /// and back to root afterwards, unless the program stopped meanwhile.
    /// </summary>
    private async Task OnRegionConsoleAsync(Component program, RegionSettings region, LauncherSettings s, Func<RestConsoleClient, Task> work, CancellationToken ct)
    {
        var console = program.Console!;
        var shared = RegionModel.Loadable(s, region.Instance, _held).Count() > 1;
        if (shared) await console.RunAsync($"change region {region.Name}", ConsoleSettle, ct);
        try { await work(console); }
        finally
        {
            if (shared && program.Process.State == ProcessState.Running)
                await console.RunAsync("change region root", ConsoleSettle, CancellationToken.None);
        }
    }

    /// <summary>Starts waiting for the archiver's alert for <paramref name="region"/>; dispose the result to stop.</summary>
    private OarAlertWait ExpectOarAlert(Guid region)
    {
        var loaded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _oarLoaded[region] = loaded;
        return new OarAlertWait(loaded.Task, () => _oarLoaded.TryRemove(new KeyValuePair<Guid, TaskCompletionSource<string>>(region, loaded)));
    }

    private sealed class OarAlertWait(Task<string> result, Action stop) : IDisposable
    {
        public Task<string> Result => result;
        public void Dispose() => stop();
    }

    private void ClearNeedsContent(Guid region) =>
        UpdateSettings(l => l with { Regions = [.. l.Regions.Select(x => x.Uuid == region ? x with { NeedsContent = false } : x)] }, writeConfig: false);

    private Task LoadContentCoreAsync(RestConsoleClient console, RegionSettings region, string content, LauncherSettings s, CancellationToken ct) =>
        LoadRegionContentAsync(console, region, content, s, null, ct);

    /// <summary>
    /// <see cref="LoadContentCoreAsync"/>; with <paramref name="watch"/>, the OAR alert wait ends as soon as that program leaves
    /// Running, failing with its text.
    /// </summary>
    private async Task LoadRegionContentAsync(RestConsoleClient console, RegionSettings region, string content, LauncherSettings s,
        (Component Program, string Stopped)? watch, CancellationToken ct)
    {
        var oar = content == "starter" ? StarterContent.StarterOar(paths) : RegionTypes.Oar(paths, s, content);
        if (oar is not null)
        {
            // RegionReady reports the archiver's result as an "oarloaded" or "oarnotloaded" alert.
            using var loaded = ExpectOarAlert(region.Uuid);
            // A 256 m OAR goes in the middle of a larger region; the squares around it keep the flat land OpenSim makes.
            // A custom type, like an import, loads at the OAR's own size and place: it may be a larger region's OAR.
            var middle = 128 * (region.Size - 1);
            var displacement = region.Size > 1 && !RegionTypes.IsCustom(content) ? $" --displacement <{middle},{middle},0>" : "";
            await console.RunAsync($"load oar --force-terrain --force-parcels{displacement} \"{Fwd(oar)}\"", ConsoleSettle, ct);
            var failed = $"The region could not load {oar}; see the region log.";
            var result = watch is { } w
                ? await WaitForOarAlertAsync(loaded, w.Program, w.Stopped, failed, ct)
                : await loaded.Result.WaitAsync(LoginWait, time, ct);
            if (result != "oarloaded") throw new InvalidOperationException(failed);
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
        var instance = p.Instance;
        var spec = new ProcessSpec("Region " + (loadable.FirstOrDefault()?.Name ?? instance),
            () => ProgramStartForTests?.Invoke(instance) ?? Dotnet(dll, "-inidirectory=" + paths.RegionDir(instance), "-console=rest",
                $"-logconfig=dreamgrid-region-{instance}.log4net.xml"),
            Path.Combine(paths.Pids, instance + ".pid"), console.IsUpAsync, ct => console.SendAsync("quit", ct),
            TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(60), dll);
        var process = new ManagedProcess(spec, new RestartPolicy(), time);
        // A crash restart by the monitor brings the program back without the controller.
        process.StateChanged += state =>
        {
            if (state != ProcessState.Running) return;
            _ = Task.Run(() =>
            {
                try { OnProgramRunning(instance, process); }
                catch (Exception e) { _log?.LogWarning(e, "{Program} came back, but its settings could not be checked.", instance); }
            });
        };
        return new Component("program-" + instance, process, spec,
            [p.HttpPort, p.ConsolePort, .. loadable.Select(r => r.UdpPort)], console, instance) { Key = ProgramKey(s, p) };
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
