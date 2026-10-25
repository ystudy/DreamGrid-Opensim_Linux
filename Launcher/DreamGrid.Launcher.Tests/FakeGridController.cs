using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Processes;

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

    public Task<RobustAccount> CreateOwnerAsync(string first, string last, string password, string? email, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task CreateRegionAsync(string name, string content, CancellationToken ct) => throw new NotSupportedException();

    public GridStatus GetStatus()
    {
        var running = new ComponentStatus("x", ProcessState.Running, null, null, null, null);
        return new GridStatus(running with { Name = "database" }, running with { Name = "robust" },
            HasRegion ? new ComponentStatus("region", RegionState, null, null, null, null) : null,
            _loginsEnabled, "http://127.0.0.1:8002");
    }

    /// <summary>Thrown by the component operations when set.</summary>
    public Exception? ComponentError { get; set; }

    public Task RestartComponentAsync(string name, CancellationToken ct) => Component("Restart " + name);

    public Task StartComponentAsync(string name, CancellationToken ct) => Component("Start " + name);

    public Task StopComponentAsync(string name, CancellationToken ct) => Component("Stop " + name);

    private Task Component(string call)
    {
        Record(call);
        return ComponentError is { } e ? Task.FromException(e) : Task.CompletedTask;
    }

    public void RegenerateConfig() => Record("RegenerateConfig");

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

    public FakeRobustAccounts Add(string first, string last, int level, string password)
    {
        _accounts[(first, last)] = (new RobustAccount(Guid.NewGuid(), first, last, level, ""), password);
        return this;
    }

    public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken ct)
    {
        Lookups++;
        return Task.FromResult(_accounts.TryGetValue((first, last), out var a) ? a.Account : null);
    }

    public Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken ct) =>
        Task.FromResult(_accounts.Values.Any(a => a.Account.PrincipalId == principal && a.Password == plainPassword));
}
