using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Tests;

/// <summary>An <see cref="IGridController"/> that records calls; the region alert handling is the real rule (logins on/off).</summary>
internal sealed class FakeGridController : IGridController
{
    private volatile bool _loginsEnabled;

    public List<string> Calls { get; } = new();
    public ProcessState RegionState { get; set; } = ProcessState.Running;
    public bool HasRegion { get; set; } = true;

    /// <summary>Runs inside <see cref="StartAsync"/> (ordering probes, thrown exceptions).</summary>
    public Func<CancellationToken, Task> OnStart { get; set; } = _ => Task.CompletedTask;

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Record(string call) { lock (Calls) Calls.Add(call); }

    public string[] Snapshot() { lock (Calls) return Calls.ToArray(); }

    public async Task StartAsync(CancellationToken ct)
    {
        Record("StartAsync");
        try { await OnStart(ct); }
        finally { Started.TrySetResult(); }
    }

    public Task StopAllAsync(CancellationToken ct) { Record("StopAllAsync"); return Task.CompletedTask; }

    public Task DetachAsync() { Record("DetachAsync"); return Task.CompletedTask; }

    private int _createOwnerCalls;
    public int CreateOwnerCalls => Volatile.Read(ref _createOwnerCalls);

    /// <summary>The last owner created: (first, last, password, email).</summary>
    public (string First, string Last, string Password, string? Email)? CreatedOwner { get; private set; }

    /// <summary>Thrown by <see cref="CreateOwnerAsync"/> when set.</summary>
    public Exception? OwnerError { get; set; }

    /// <summary>Runs inside <see cref="CreateOwnerAsync"/> before the owner is saved (blocking probes).</summary>
    public Func<CancellationToken, Task> OnCreateOwner { get; set; } = _ => Task.CompletedTask;

    /// <summary>Like the controller: saves Setup.OwnerCreated and Setup.Owner through <see cref="UpdateSettings"/>.</summary>
    public async Task<RobustAccount> CreateOwnerAsync(string first, string last, string password, string? email, CancellationToken ct)
    {
        Record("CreateOwner " + first + " " + last);
        Interlocked.Increment(ref _createOwnerCalls);
        await OnCreateOwner(ct);
        if (OwnerError is { } e) throw e;
        CreatedOwner = (first, last, password, email);
        var account = new RobustAccount(Guid.NewGuid(), first, last, 200, email ?? "");
        UpdateSettings(l => l with { Setup = l.Setup with { OwnerCreated = true, Owner = new OwnerInfo(first, last, account.PrincipalId) } });
        return account;
    }

    /// <summary>
    /// Runs inside <see cref="CreateRegionAsync"/> before the region is saved (blocking probes, thrown exceptions);
    /// the default reports the controller's three stages.
    /// </summary>
    public Func<IProgress<string>?, CancellationToken, Task> OnCreateRegion { get; set; } = (progress, _) =>
    {
        progress?.Report("Creating your region");
        progress?.Report("Loading the region content");
        progress?.Report("Waiting for the region to accept logins");
        return Task.CompletedTask;
    };

    /// <summary>Like the controller: saves the region, then Setup.RegionCreated, through <see cref="UpdateSettings"/>.</summary>
    public async Task CreateRegionAsync(string name, string content, CancellationToken ct, IProgress<string>? progress = null)
    {
        Record("CreateRegion " + name + " " + content);
        await OnCreateRegion(progress, ct);
        UpdateSettings(l => l with
        {
            Programs = [new ProgramSettings(GridController.SafeFolderName(name), l.RegionPort, l.RegionConsolePort)],
            Regions = [new RegionSettings(GridController.SafeFolderName(name), name, Guid.NewGuid(), Content: content, UdpPort: l.RegionPort)],
            Setup = l.Setup with { RegionCreated = true },
        });
    }

    /// <summary>The <see cref="GridStatus.StartFailure"/> of the status built from the other properties.</summary>
    public StartFailure? StartFailure { get; set; }

    /// <summary>A program's own state, by instance; the others are in <see cref="RegionState"/>.</summary>
    public Dictionary<string, ProcessState> ProgramStates { get; } = new();

    /// <summary>When set, <see cref="GetStatus"/> returns it instead of the status built from the other properties.</summary>
    public GridStatus? Status { get; set; }

    public GridStatus GetStatus()
    {
        if (Status is { } fixedStatus) return fixedStatus;
        var running = new ComponentStatus("x", ProcessState.Running, null, null, null, null);
        // Every live region in its settings, each in the state RegionState gives its program; without any (or without
        // settings) the fake has one of its own, Welcome, as version 1's did.
        List<RegionSettings> regions = !HasRegion ? []
            : SettingsSource is not null && RegionModel.Live(CurrentSettings()).ToList() is { Count: > 0 } live ? live
            : [new RegionSettings("Welcome", "Welcome", Guid.Empty)];
        ComponentStatus Program(string instance) =>
            new("region", ProgramStates.GetValueOrDefault(instance, RegionState), null, null, null, null);
        return new GridStatus(running with { Name = "database" }, running with { Name = "robust" },
            regions.GroupBy(r => r.Instance).Select(g => new ProgramStatus(g.Key, Program(g.Key), g.Select(r => r.Uuid).ToList())).ToList(),
            regions.Select(r => new RegionStatus(r.Uuid, r.Name, r.Instance, RegionStates.Derive(r, Program(r.Instance), false, null, true), _loginsEnabled,
                r.X, r.Y, r.Size, regions.Where(o => o.Instance == r.Instance && o.Uuid != r.Uuid).Select(o => o.Name).ToList())).ToList(),
            _loginsEnabled, "http://127.0.0.1:8002", StartFailure);
    }

    /// <summary>Thrown by the component operations when set.</summary>
    public Exception? ComponentError { get; set; }

    /// <summary>Runs inside every component operation with the token it was given.</summary>
    public Func<CancellationToken, Task> OnComponent { get; set; } = _ => Task.CompletedTask;

    /// <summary>What <see cref="CurrentSettings"/> returns (the controller's cached settings in the real host).</summary>
    public Func<LauncherSettings>? SettingsSource { get; set; }

    /// <summary>Where <see cref="UpdateSettings"/> saves (the settings file in the test host); nothing is saved when null.</summary>
    public Action<LauncherSettings>? SettingsSink { get; set; }

    /// <summary>The writeConfig argument of every <see cref="UpdateSettings"/> call, in order.</summary>
    public List<bool> UpdateWriteConfig { get; } = new();

    public LauncherSettings CurrentSettings() =>
        (SettingsSource ?? throw new InvalidOperationException("No settings source set on the fake grid."))();

    public Task RestartComponentAsync(string name, CancellationToken ct) => Component("Restart " + name, ct);

    public Task StartComponentAsync(string name, CancellationToken ct) => Component("Start " + name, ct);

    public Task StopComponentAsync(string name, CancellationToken ct) => Component("Stop " + name, ct);

    private async Task Component(string call, CancellationToken ct)
    {
        Record(call);
        await OnComponent(ct);
        if (ComponentError is { } e) throw e;
    }

    public void RegenerateConfig() => Record("RegenerateConfig");

    public Task StartWorldAsync(bool everything, CancellationToken ct)
    {
        Record(everything ? "StartWorld everything" : "StartWorld");
        return Task.CompletedTask;
    }

    /// <summary>Every request <see cref="AddRegionAsync"/> got, in order.</summary>
    public List<NewRegion> AddedRegions { get; } = new();

    /// <summary>Runs inside <see cref="AddRegionAsync"/> before the region is saved (blocking probes, thrown exceptions).</summary>
    public Func<NewRegion, IProgress<string>?, CancellationToken, Task> OnAddRegion { get; set; } = (_, progress, _) =>
    {
        progress?.Report("Creating your region");
        progress?.Report("Loading the region content");
        progress?.Report("Waiting for the region to accept logins");
        return Task.CompletedTask;
    };

    /// <summary>The region <see cref="AddRegionAsync"/> saves; each added region gets a program of its own unless shared.</summary>
    public async Task<RegionSettings> AddRegionAsync(NewRegion request, CancellationToken ct, IProgress<string>? progress = null)
    {
        Record($"AddRegion {request.Name}");
        lock (AddedRegions) AddedRegions.Add(request);
        await OnAddRegion(request, progress, ct);
        var region = new RegionSettings(request.ShareWith ?? GridController.SafeFolderName(request.Name), request.Name, Guid.NewGuid(),
            request.X, request.Y, request.Content, request.Size);
        UpdateSettings(l => l with { Regions = [.. l.Regions, region] });
        return region;
    }

    public Task RegionPowerAsync(Guid regionId, RegionPower action, CancellationToken ct) => Component($"RegionPower {action} {regionId}", ct);

    /// <summary>Records the move and, like the controller, saves the new spot; <see cref="OnMoveRegion"/> may throw instead.</summary>
    public Func<Guid, int, int, CancellationToken, Task> OnMoveRegion { get; set; } = (_, _, _, _) => Task.CompletedTask;

    public async Task MoveRegionAsync(Guid regionId, int x, int y, CancellationToken ct)
    {
        Record($"MoveRegion {regionId} {x} {y}");
        await OnMoveRegion(regionId, x, y, ct);
        UpdateSettings(l => l with { Regions = l.Regions.Select(r => r.Uuid == regionId ? r with { X = x, Y = y } : r).ToList() });
    }

    /// <summary>Records "ChangeRunsWith &lt;id&gt; &lt;group or own&gt;"; <see cref="OnChangeRunsWith"/> may throw instead.</summary>
    public Func<Guid, string?, CancellationToken, Task> OnChangeRunsWith { get; set; } = (_, _, _) => Task.CompletedTask;

    public async Task ChangeRunsWithAsync(Guid regionId, string? shareWith, CancellationToken ct)
    {
        Record($"ChangeRunsWith {regionId} {shareWith ?? "own"}");
        await OnChangeRunsWith(regionId, shareWith, ct);
    }

    /// <summary>
    /// Runs inside the rename, resize and save-options calls with the recorded call text, before anything is saved (blocking
    /// probes, thrown exceptions).
    /// </summary>
    public Func<string, CancellationToken, Task> OnRegionSettings { get; set; } = (_, _) => Task.CompletedTask;

    /// <summary>What <see cref="SaveProgramOptionsAsync"/> returns: whether the cores and priority were applied at once.</summary>
    public bool CpuAppliedNow { get; set; }

    /// <summary>The programs <see cref="NeedsRestart"/> answers true for.</summary>
    public HashSet<string> PendingRestart { get; } = new();

    /// <summary>Every region options request, in order.</summary>
    public List<(Guid Region, RegionOptions Options)> SavedRegionOptions { get; } = new();

    /// <summary>Every program options request, in order.</summary>
    public List<(string Instance, string? Physics, ProgramOptions Options)> SavedProgramOptions { get; } = new();

    /// <summary>Records "RenameRegion &lt;id&gt; &lt;name&gt;"; like the controller, saves the name when a settings source is set.</summary>
    public async Task RenameRegionAsync(Guid regionId, string name, CancellationToken ct)
    {
        var call = $"RenameRegion {regionId} {name}";
        Record(call);
        await OnRegionSettings(call, ct);
        if (SettingsSource is not null)
            UpdateSettings(l => l with { Regions = l.Regions.Select(r => r.Uuid == regionId ? r with { Name = name } : r).ToList() });
    }

    /// <summary>Records "ResizeRegion &lt;id&gt; &lt;size&gt;"; like the controller, saves the size when a settings source is set.</summary>
    public async Task ResizeRegionAsync(Guid regionId, int size, CancellationToken ct)
    {
        var call = $"ResizeRegion {regionId} {size}";
        Record(call);
        await OnRegionSettings(call, ct);
        if (SettingsSource is not null)
            UpdateSettings(l => l with { Regions = l.Regions.Select(r => r.Uuid == regionId ? r with { Size = size } : r).ToList() });
    }

    /// <summary>Records "SaveRegionOptions &lt;id&gt;"; like the controller, saves the options when a settings source is set.</summary>
    public async Task SaveRegionOptionsAsync(Guid regionId, RegionOptions options, CancellationToken ct)
    {
        var call = $"SaveRegionOptions {regionId}";
        Record(call);
        lock (SavedRegionOptions) SavedRegionOptions.Add((regionId, options));
        await OnRegionSettings(call, ct);
        if (SettingsSource is not null)
            UpdateSettings(l => l with { Regions = l.Regions.Select(r => r.Uuid == regionId ? r with { Options = options } : r).ToList() });
    }

    /// <summary>
    /// Records "SaveProgramOptions &lt;instance&gt;"; like the controller, saves physics and options when a settings source is
    /// set, and returns <see cref="CpuAppliedNow"/>.
    /// </summary>
    public async Task<bool> SaveProgramOptionsAsync(string instance, string? physics, ProgramOptions options, CancellationToken ct)
    {
        var call = $"SaveProgramOptions {instance}";
        Record(call);
        lock (SavedProgramOptions) SavedProgramOptions.Add((instance, physics, options));
        await OnRegionSettings(call, ct);
        if (SettingsSource is not null)
            UpdateSettings(l => l with
            {
                Programs = l.Programs.Select(p => p.Instance == instance ? p with { Physics = physics, Options = options } : p).ToList(),
            });
        return CpuAppliedNow;
    }

    public bool NeedsRestart(string instance) { lock (PendingRestart) return PendingRestart.Contains(instance); }

    /// <summary>Runs inside the delete, restore and erase calls (blocking probes, thrown exceptions).</summary>
    public Func<string, Guid, CancellationToken, Task> OnDeleted { get; set; } = (_, _, _) => Task.CompletedTask;

    /// <summary>Records "DeleteRegion &lt;id&gt;"; like the controller, saves DeletedAt (Parked kept) when a settings source is set.</summary>
    public async Task DeleteRegionAsync(Guid regionId, CancellationToken ct)
    {
        Record($"DeleteRegion {regionId}");
        await OnDeleted("Delete", regionId, ct);
        if (SettingsSource is not null)
            UpdateSettings(l => l with { Regions = l.Regions.Select(r => r.Uuid == regionId ? r with { DeletedAt = DateTimeOffset.UtcNow } : r).ToList() });
    }

    /// <summary>Records "RestoreRegion &lt;id&gt;"; like the controller, clears DeletedAt when a settings source is set.</summary>
    public async Task RestoreRegionAsync(Guid regionId, CancellationToken ct)
    {
        Record($"RestoreRegion {regionId}");
        await OnDeleted("Restore", regionId, ct);
        if (SettingsSource is not null)
            UpdateSettings(l => l with { Regions = l.Regions.Select(r => r.Uuid == regionId ? r with { DeletedAt = null } : r).ToList() });
    }

    /// <summary>Records "EraseRegion &lt;id&gt;"; like the controller, removes the region when a settings source is set.</summary>
    public async Task EraseRegionAsync(Guid regionId, CancellationToken ct)
    {
        Record($"EraseRegion {regionId}");
        await OnDeleted("Erase", regionId, ct);
        if (SettingsSource is not null)
            UpdateSettings(l => l with { Regions = l.Regions.Where(r => r.Uuid != regionId).ToList() });
    }

    /// <summary>What <see cref="DeletedSpaceAsync"/> returns; null stands for a database that isn't running.</summary>
    public IReadOnlyDictionary<Guid, long>? DeletedSpace { get; set; } = new Dictionary<Guid, long>();

    /// <summary>When set, <see cref="DeletedSpaceAsync"/> throws it (a query that fails while the database runs).</summary>
    public Exception? DeletedSpaceFailure { get; set; }

    public Task<IReadOnlyDictionary<Guid, long>?> DeletedSpaceAsync(CancellationToken ct) =>
        DeletedSpaceFailure is { } e ? Task.FromException<IReadOnlyDictionary<Guid, long>?>(e) : Task.FromResult(DeletedSpace);

    /// <summary>Every request <see cref="RunOarJobAsync"/> got, in order.</summary>
    public List<OarJobRequest> OarJobs { get; } = new();

    /// <summary>Runs inside <see cref="RunOarJobAsync"/> (blocking probes, progress, thrown exceptions).</summary>
    public Func<OarJobRequest, IProgress<OarJobProgress>?, CancellationToken, Task> OnOarJob { get; set; } = (_, _, _) => Task.CompletedTask;

    /// <summary>What <see cref="RunOarJobAsync"/> returns: the library name a save wrote, or null.</summary>
    public string? OarJobResult { get; set; }

    public async Task<string?> RunOarJobAsync(OarJobRequest request, CancellationToken ct, IProgress<OarJobProgress>? progress = null)
    {
        Record($"OarJob {request.Kind} {request.RegionId} {request.File}".TrimEnd());
        lock (OarJobs) OarJobs.Add(request);
        await OnOarJob(request, progress, ct);
        return OarJobResult;
    }

    /// <summary>The job text <see cref="BusyWith"/> gives for each instance.</summary>
    public Dictionary<string, string> Busy { get; } = new();

    public string? BusyWith(string instance) { lock (Busy) return Busy.GetValueOrDefault(instance); }

    public LauncherSettings UpdateSettings(Func<LauncherSettings, LauncherSettings> change, bool writeConfig = true)
    {
        lock (UpdateWriteConfig)
        {
            Record("UpdateSettings");
            UpdateWriteConfig.Add(writeConfig);
            var next = change(CurrentSettings());
            SettingsSink?.Invoke(next);
            return next;
        }
    }

    public void OnRegionAlert(RegionAlert alert)
    {
        Record("Alert " + alert.Alert + " " + alert.Login);
        if (alert.Alert == "region_ready") _loginsEnabled = alert.Login == "enabled";
    }

    /// <summary>Thrown by <see cref="SetEstateOwnerAsync"/> when set.</summary>
    public Exception? EstateOwnerError { get; set; }

    /// <summary>The account's own spelling of the name <see cref="SetEstateOwnerAsync"/> finds; the typed name when null.</summary>
    public (string First, string Last)? EstateOwnerFound { get; set; }

    /// <summary>Like the controller: saves Setup.OwnerCreated and Setup.Owner through <see cref="UpdateSettings"/>.</summary>
    public Task<RobustAccount> SetEstateOwnerAsync(string first, string last, CancellationToken ct)
    {
        Record("SetEstateOwner " + first + " " + last);
        if (EstateOwnerError is { } e) throw e;
        (first, last) = EstateOwnerFound ?? (first, last);
        var account = new RobustAccount(Guid.NewGuid(), first, last, 0, "");
        UpdateSettings(l => l with { Setup = l.Setup with { OwnerCreated = true, Owner = new OwnerInfo(first, last, account.PrincipalId) } });
        return Task.FromResult(account);
    }

    /// <summary>What <see cref="JoinedRegionCountAsync"/> returns; it throws <see cref="JoinedRegionError"/> instead when set.</summary>
    public int JoinedRegions { get; set; }
    public Exception? JoinedRegionError { get; set; }

    public Task<int> JoinedRegionCountAsync(CancellationToken ct)
    {
        Record("JoinedRegionCount");
        return JoinedRegionError is { } e ? Task.FromException<int>(e) : Task.FromResult(JoinedRegions);
    }
}

/// <summary>A Grid Asset Server's join information without a network: <see cref="Result"/>, or <see cref="Error"/> thrown.</summary>
internal sealed class FakeGridJoin : IGridJoinClient
{
    public JoinedGrid? Result { get; set; }
    public Exception? Error { get; set; }

    /// <summary>Every (host, port) read, in order.</summary>
    public List<(string Host, int Port)> Reads { get; } = new();

    public Task<JoinedGrid> ReadAsync(string host, int port, CancellationToken ct)
    {
        lock (Reads) Reads.Add((host, port));
        if (Error is { } e) return Task.FromException<JoinedGrid>(e);
        return Result is { } joined ? Task.FromResult(joined)
            : Task.FromException<JoinedGrid>(new GridJoinException($"Nothing answered at {host}:{port}."));
    }
}

/// <summary>Robust with a fixed set of avatars: (first, last) → (level, password).</summary>
internal sealed class FakeRobustAccounts : IRobustClient
{
    private readonly Dictionary<(string, string), (RobustAccount Account, string Password)> _accounts = new();

    public int Lookups { get; private set; }

    /// <summary>Simulates Robust not running: lookups and sign-in checks fail like a refused connection.</summary>
    public bool Down { get; set; }

    public FakeRobustAccounts Add(string first, string last, int level, string password, Guid? id = null)
    {
        _accounts[(first, last)] = (new RobustAccount(id ?? Guid.NewGuid(), first, last, level, ""), password);
        return this;
    }

    public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct)
    {
        Lookups++;
        if (Down) throw new HttpRequestException("Connection refused");
        return Task.FromResult(_accounts.TryGetValue((first, last), out var a) ? a.Account : null);
    }

    public Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken ct) =>
        Down ? throw new HttpRequestException("Connection refused") : Task.FromResult(_accounts.Values.Any(a => a.Account.PrincipalId == principal && a.Password == plainPassword));

    /// <summary>The grid's map this fake reports.</summary>
    public List<MapRegion> MapRegions { get; } = [];

    public Task<IReadOnlyList<MapRegion>> GetMapRegionsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MapRegion>>(MapRegions.ToList());
}

/// <summary>Who's online without a database: returns <see cref="Result"/>.</summary>
internal sealed class FakeOnlineUsers : IOnlineUsers
{
    public OnlineList Result { get; set; } = new([], true);

    public Task<OnlineList> ListAsync(CancellationToken ct) => Task.FromResult(Result);
}
