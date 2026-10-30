using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>
/// The Grid Asset Server a Region Server uses doesn't answer, so its regions wait rather than start against nothing. The
/// message is for the owner and names the server's address.
/// </summary>
public sealed class GridAssetUnreachableException(string host, int port, Exception? inner = null)
    : InvalidOperationException(MessageFor(host, port), inner)
{
    /// <summary>The message for the Grid Asset Server at <paramref name="host"/>:<paramref name="port"/>.</summary>
    public static string MessageFor(string host, int port) =>
        $"The Grid Asset Server at {host}:{port} isn't answering. Your regions will start once it does: check that computer is on and running DreamGrid, then press Start.";
}

// The controller on a plan that uses another computer's grid service (a Region Server): Robust never starts here; the
// Grid Asset Server's join information is read again at each start and the server is watched while the plan lasts.
public sealed partial class GridController
{
    private const string NoGridService = "This computer doesn't run a grid service; it uses the Grid Asset Server's.";
    private const string GridAssetWatch = "grid-asset";

    // What the Grid Asset Server last did: Stopped before the first check, Running when it answered, Failed when it didn't.
    private volatile ProcessState _gridAsset = ProcessState.Stopped;

    /// <summary>How often a Region Server checks that the Grid Asset Server answers (tests shorten it).</summary>
    internal TimeSpan GridAssetWatchInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Under the operation lock, once the database runs: on a plan that runs Robust, starts it. On a Region Server, first
    /// stops a Robust of this computer's own that still runs (one a start before the plan was chosen began), then reads
    /// the Grid Asset Server's join information again and saves it (keeping the address the owner chose), then watches
    /// that server. When nothing answers, the last values stay and <see cref="GridAssetUnreachableException"/> is thrown;
    /// when something else answered (another plan, not DreamGrid), the last values stay and the
    /// <see cref="GridJoinException"/> is thrown with its own message. Returns the settings the rest of the start uses.
    /// </summary>
    private async Task<LauncherSettings> StartGridServiceAsync(LauncherSettings s, CancellationToken ct)
    {
        if (PlanCatalog.RunsRobust(s))
        {
            await StartComponentAsync(_robust!, ct);
            return s;
        }
        if (_robust is { } local && (local.Process.State != ProcessState.Stopped || WillAdopt(local)))
        {
            _log?.LogInformation("Stopping this computer's own grid service: a Region Server uses the Grid Asset Server's.");
            await local.Process.StopAsync(ct);
        }
        if (s.Join is not { } joined) return s;

        JoinedGrid read;
        try { read = await join.ReadAsync(joined.Host, joined.PublicPort, ct); }
        catch (GridJoinException e) when (!e.NoAnswer && !ct.IsCancellationRequested)
        {
            // Something answered, but not a Grid Asset Server this computer can join: say so, not "isn't answering".
            _gridAsset = ProcessState.Failed;
            _log?.LogWarning("The Grid Asset Server at {Host}:{Port} can't be joined: {Reason}", joined.Host, joined.PublicPort, e.Message);
            throw;
        }
        catch (Exception e) when (e is GridJoinException or HttpRequestException && !ct.IsCancellationRequested)
        {
            _gridAsset = ProcessState.Failed;
            WatchGridAsset();
            _log?.LogWarning("The Grid Asset Server at {Host}:{Port} could not be read: {Reason}", joined.Host, joined.PublicPort, e.Message);
            throw new GridAssetUnreachableException(joined.Host, joined.PublicPort, e);
        }
        _gridAsset = ProcessState.Running;
        WatchGridAsset();
        // Saved only while the join still names the server this start read: an Advanced re-point saved meanwhile wins.
        return UpdateSettings(l => l.Join is not { } current || current.Host != joined.Host || current.PublicPort != joined.PublicPort
            ? l
            : l with
            {
                GridName = read.GridName,
                Join = read with { Host = current.Host, PublicPort = current.PublicPort },
            });
    }

    /// <summary>Starts the Grid Asset Server watch unless one runs; it ends with the monitors (<see cref="DetachAsync"/>).</summary>
    private void WatchGridAsset()
    {
        lock (_monitors)
        {
            if (_monitorCts.IsCancellationRequested) return;
            if (_monitors.TryGetValue(GridAssetWatch, out var live) && !live.IsCompleted) return;
            var token = _monitorCts.Token;
            _monitors[GridAssetWatch] = Task.Run(() => WatchGridAssetAsync(token), CancellationToken.None);
        }
    }

    /// <summary>While the plan doesn't run Robust: asks the Grid Asset Server every <see cref="GridAssetWatchInterval"/> whether it answers.</summary>
    private async Task WatchGridAssetAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(GridAssetWatchInterval, time, ct);
                var s = CurrentSettings();
                if (PlanCatalog.RunsRobust(s)) return;
                bool up;
                try { up = await robust.IsReadyAsync(ct); }
                catch (Exception e) when (e is not OperationCanceledException) { up = false; }
                var now = up ? ProcessState.Running : ProcessState.Failed;
                if (_gridAsset != now)
                    _log?.LogInformation("The Grid Asset Server at {Address} {State}.", GridAssetAddress(s), up ? "answers again" : "stopped answering");
                _gridAsset = now;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static string? GridAssetAddress(LauncherSettings s) => s.Join is { } j ? $"{j.Host}:{j.PublicPort}" : null;

    /// <summary>The Grid Asset Server as the status's grid service: the state its last check left (see <see cref="GridStatus"/>).</summary>
    private ComponentStatus DescribeGridAsset(LauncherSettings s)
    {
        var state = _gridAsset;
        var error = state == ProcessState.Failed && s.Join is { } j ? GridAssetUnreachableException.MessageFor(j.Host, j.PublicPort) : null;
        return new ComponentStatus("robust", state, null, null, null, error);
    }

    /// <summary>
    /// True when the grid service answers: this computer's Robust runs, or (on a Region Server) the Grid Asset Server's
    /// last check answered. A Robust of this computer's own never counts on a Region Server.
    /// </summary>
    private bool GridServiceUp() =>
        PlanCatalog.RunsRobust(CurrentSettings())
            ? _robust?.Process.State == ProcessState.Running
            : _gridAsset == ProcessState.Running;

    /// <summary>Why a region can't start while the grid service is down.</summary>
    private static InvalidOperationException GridServiceDown(LauncherSettings s) =>
        !PlanCatalog.RunsRobust(s) && s.Join is { } j
            ? new GridAssetUnreachableException(j.Host, j.PublicPort)
            : new InvalidOperationException("Robust is not running; start it first.");

    /// <summary>Refuses an operation on this computer's Robust when the plan doesn't run one.</summary>
    private static void ThrowIfNoGridService(LauncherSettings s)
    {
        if (!PlanCatalog.RunsRobust(s)) throw new InvalidOperationException(NoGridService);
    }

    /// <summary>
    /// Asks the grid service; on a Region Server a call that gets no answer becomes <see cref="GridAssetUnreachableException"/>.
    /// </summary>
    private static async Task<T> AskGridServiceAsync<T>(LauncherSettings s, Func<Task<T>> call, CancellationToken ct)
    {
        try { return await call(); }
        catch (Exception e) when ((e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
                                  && !PlanCatalog.RunsRobust(s) && s.Join is { } j)
        {
            throw new GridAssetUnreachableException(j.Host, j.PublicPort, e);
        }
    }

    /// <summary>The grid's map when the plan uses another computer's grid service (whose regions count for placement); null otherwise.</summary>
    private async Task<IReadOnlyList<MapRegion>?> RegionsElsewhereAsync(LauncherSettings s, CancellationToken ct) =>
        PlanCatalog.RunsRobust(s) ? null : await AskGridServiceAsync(s, () => robust.GetMapRegionsAsync(ct), ct);

    public async Task<RobustAccount> SetEstateOwnerAsync(string first, string last, CancellationToken ct)
    {
        first = (first ?? "").Trim();
        last = (last ?? "").Trim();
        if (first.Length == 0 || last.Length == 0) throw new ArgumentException("Type your avatar's first and last name.");

        await _ops.WaitAsync(ct);
        try
        {
            LauncherSettings s;
            lock (_configGate) s = ReloadSettings();
            var grid = (PlanCatalog.RunsRobust(s) ? null : s.Join?.GridName) ?? s.GridName;
            var account = await AskGridServiceAsync(s, () => robust.GetAccountAsync(first, last, ct), ct)
                ?? throw new ArgumentException(
                    $"There's no avatar called {first} {last} on {grid}. Check the spelling, or create the avatar on the Grid Asset Server first.");
            // The region INI now gets the [Estates] keys, with the account's own spelling.
            UpdateSettings(l => l with
            {
                Setup = l.Setup with { OwnerCreated = true, Owner = new OwnerInfo(account.FirstName, account.LastName, account.PrincipalId) },
            });
            return account;
        }
        finally { _ops.Release(); }
    }

    public async Task<int> JoinedRegionCountAsync(CancellationToken ct)
    {
        var s = CurrentSettings();
        if (!PlanCatalog.RunsRobust(s)) return 0;
        IReadOnlyList<MapRegion> map;
        try { map = await robust.GetMapRegionsAsync(ct); }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new InvalidOperationException("Robust isn't answering, so the regions joined to this grid can't be counted.", e);
        }
        var ours = s.Regions.Select(r => r.Uuid).ToHashSet();
        return map.Count(m => !ours.Contains(m.Uuid));
    }
}
