using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
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

    public int CreateOwnerCalls { get; private set; }

    /// <summary>The last owner created: (first, last, password, email).</summary>
    public (string First, string Last, string Password, string? Email)? CreatedOwner { get; private set; }

    /// <summary>Thrown by <see cref="CreateOwnerAsync"/> when set.</summary>
    public Exception? OwnerError { get; set; }

    /// <summary>Like the controller: saves Setup.OwnerCreated and Setup.Owner through <see cref="UpdateSettings"/>.</summary>
    public Task<RobustAccount> CreateOwnerAsync(string first, string last, string password, string? email, CancellationToken ct)
    {
        Record("CreateOwner " + first + " " + last);
        CreateOwnerCalls++;
        if (OwnerError is { } e) throw e;
        CreatedOwner = (first, last, password, email);
        var account = new RobustAccount(Guid.NewGuid(), first, last, 200, email ?? "");
        UpdateSettings(l => l with { Setup = l.Setup with { OwnerCreated = true, Owner = new OwnerInfo(first, last, account.PrincipalId) } });
        return Task.FromResult(account);
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
            Region = new RegionSettings(GridController.SafeFolderName(name), name, Guid.NewGuid(), Content: content),
            Setup = l.Setup with { RegionCreated = true },
        });
    }

    /// <summary>When set, <see cref="GetStatus"/> returns it instead of the status built from the other properties.</summary>
    public GridStatus? Status { get; set; }

    public GridStatus GetStatus()
    {
        if (Status is { } fixedStatus) return fixedStatus;
        var running = new ComponentStatus("x", ProcessState.Running, null, null, null, null);
        return new GridStatus(running with { Name = "database" }, running with { Name = "robust" },
            HasRegion ? new ComponentStatus("region", RegionState, null, null, null, null) : null,
            _loginsEnabled, "http://127.0.0.1:8002");
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
}

/// <summary>Robust with a fixed set of avatars: (first, last) → (level, password).</summary>
internal sealed class FakeRobustAccounts : IRobustClient
{
    private readonly Dictionary<(string, string), (RobustAccount Account, string Password)> _accounts = new();

    public int Lookups { get; private set; }

    /// <summary>Simulates Robust not running: lookups and sign-in checks fail like a refused connection.</summary>
    public bool Down { get; set; }

    public FakeRobustAccounts Add(string first, string last, int level, string password)
    {
        _accounts[(first, last)] = (new RobustAccount(Guid.NewGuid(), first, last, level, ""), password);
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
}
