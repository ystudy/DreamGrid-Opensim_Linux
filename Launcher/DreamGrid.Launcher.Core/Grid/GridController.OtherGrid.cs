using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>
/// The grid service on another computer that this computer's regions use doesn't answer, so its regions wait rather than
/// start against nothing. The message is for the owner and names where that grid service was asked.
/// </summary>
public abstract class GridUnreachableException(string message, Exception? inner) : InvalidOperationException(message, inner);

/// <summary>The Grid Asset Server a Region Server uses doesn't answer; the message names the server's address.</summary>
public sealed class GridAssetUnreachableException(string host, int port, Exception? inner = null)
    : GridUnreachableException(MessageFor(host, port), inner)
{
    /// <summary>The message for the Grid Asset Server at <paramref name="host"/>:<paramref name="port"/>.</summary>
    public static string MessageFor(string host, int port) =>
        $"The Grid Asset Server at {host}:{port} isn't answering. Your regions will start once it does: check that computer is on and running DreamGrid, then press Start.";
}

/// <summary>The other grid a Connected Server's regions are on doesn't answer; the message names the grid and its address.</summary>
public sealed class OtherGridUnreachableException(string gridName, string address, Exception? inner = null)
    : GridUnreachableException(MessageFor(gridName, address), inner)
{
    /// <summary>The message for the grid called <paramref name="gridName"/> at <paramref name="address"/>.</summary>
    public static string MessageFor(string gridName, string address) =>
        $"{gridName} isn't answering at {address}. Your regions will start once it does: check this computer's internet connection, then press Start.";
}

// The controller on a plan that uses another computer's grid service (a Region Server or a Connected Server): Robust never
// starts here; the other grid's information (a Grid Asset Server's join information, another grid's get_grid_info) is read
// again at each start and the grid service is watched while the plan lasts.
public sealed partial class GridController
{
    private const string GridAssetWatch = "grid-asset";

    // What the other grid's service last did: Stopped before the first check, Running when it answered, Failed when it didn't.
    private volatile ProcessState _gridAsset = ProcessState.Stopped;

    /// <summary>How often a plan that uses another computer's grid checks that its grid service answers (tests shorten it).</summary>
    internal TimeSpan GridAssetWatchInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Under the operation lock, once the database runs: on a plan that runs Robust, starts it. On a plan that uses another
    /// computer's grid, first stops a Robust of this computer's own that still runs (one a start before the plan was chosen
    /// began), then reads the other grid's information again and saves it (keeping the address the owner chose), then
    /// watches its grid service. When nothing answers, the last values stay and a <see cref="GridUnreachableException"/>
    /// for the plan is thrown; when something else answered (not a grid this computer can use), the last values stay and
    /// the <see cref="GridJoinException"/> is thrown with its own message. Returns the settings the rest of the start uses.
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
            _log?.LogInformation("Stopping this computer's own grid service: this plan uses another computer's.");
            await local.Process.StopAsync(ct);
        }
        return s.Plan == OwnerPlan.Connected ? await ReadOtherGridAsync(s, ct) : await ReadJoinAsync(s, ct);
    }

    /// <summary>A Region Server's part of <see cref="StartGridServiceAsync"/>: reads the Grid Asset Server's join information.</summary>
    private async Task<LauncherSettings> ReadJoinAsync(LauncherSettings s, CancellationToken ct)
    {
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

    /// <summary>
    /// A Connected Server's part of <see cref="StartGridServiceAsync"/>: reads the other grid's get_grid_info and saves its
    /// name, login address and gatekeeper (the preset, the address and the address for region servers are the owner's).
    /// </summary>
    private async Task<LauncherSettings> ReadOtherGridAsync(LauncherSettings s, CancellationToken ct)
    {
        if (s.Connected is not { } connected) return s;

        OtherGridInfo read;
        try { read = await otherGrid.ReadAsync(new Uri(connected.Address), ct); }
        catch (GridJoinException e) when (!e.NoAnswer && !ct.IsCancellationRequested)
        {
            // Something answered, but not like a grid: say so, not "isn't answering".
            _gridAsset = ProcessState.Failed;
            _log?.LogWarning("{Grid} at {Address} can't be used: {Reason}", connected.GridName, connected.Address, e.Message);
            throw;
        }
        catch (Exception e) when (e is GridJoinException or HttpRequestException && !ct.IsCancellationRequested)
        {
            _gridAsset = ProcessState.Failed;
            WatchGridAsset();
            _log?.LogWarning("{Grid} at {Address} could not be read: {Reason}", connected.GridName, connected.Address, e.Message);
            throw new OtherGridUnreachableException(connected.GridName, connected.Address, e);
        }
        _gridAsset = ProcessState.Running;
        WatchGridAsset();
        // Saved only while the settings still name the address this start read: one the owner changed meanwhile wins.
        return UpdateSettings(l => l.Connected is not { } current || current.Address != connected.Address
            ? l
            : l with
            {
                GridName = read.GridName,
                Connected = current with { GridName = read.GridName, LoginUri = read.LoginUri, GatekeeperUri = read.GatekeeperUri },
            });
    }

    /// <summary>Starts the grid service watch unless one runs; it ends with the monitors (<see cref="DetachAsync"/>).</summary>
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

    /// <summary>While the plan doesn't run Robust: asks the other grid's service every <see cref="GridAssetWatchInterval"/> whether it answers.</summary>
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
                    _log?.LogInformation("{Grid} at {Address} {State}.", s.Plan == OwnerPlan.Connected ? OtherGridName(s) : "The Grid Asset Server",
                        OtherGridAddressOf(s), up ? "answers again" : "stopped answering");
                _gridAsset = now;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// Where the other grid is: the Grid Asset Server's <c>host:port</c> on a Region Server, the address the owner chose on a
    /// Connected Server; null on a plan that runs Robust and before the other grid is chosen.
    /// </summary>
    private static string? OtherGridAddressOf(LauncherSettings s) => s.Plan switch
    {
        OwnerPlan.Connected => s.Connected?.Address,
        OwnerPlan.RegionServer => s.Join is { } j ? $"{j.Host}:{j.PublicPort}" : null,
        _ => null,
    };

    /// <summary>The name of the grid the regions are on: the other grid's on a plan that uses one, else the world's.</summary>
    private static string OtherGridName(LauncherSettings s) => s.Plan switch
    {
        OwnerPlan.Connected => s.Connected?.GridName,
        OwnerPlan.RegionServer => s.Join?.GridName,
        _ => null,
    } ?? s.GridName;

    /// <summary>
    /// Why the other grid's service can't be used: <see cref="OtherGridUnreachableException"/> on a Connected Server,
    /// <see cref="GridAssetUnreachableException"/> on a Region Server; null on a plan that runs Robust and before the other
    /// grid is chosen.
    /// </summary>
    private static GridUnreachableException? Unreachable(LauncherSettings s, Exception? inner = null) => s.Plan switch
    {
        OwnerPlan.Connected when s.Connected is { } c => new OtherGridUnreachableException(c.GridName, c.Address, inner),
        OwnerPlan.RegionServer when s.Join is { } j => new GridAssetUnreachableException(j.Host, j.PublicPort, inner),
        _ => null,
    };

    /// <summary>The other grid's service as the status's grid service: the state its last check left (see <see cref="GridStatus"/>).</summary>
    private ComponentStatus DescribeGridAsset(LauncherSettings s)
    {
        var state = _gridAsset;
        var error = state == ProcessState.Failed ? Unreachable(s)?.Message : null;
        return new ComponentStatus("robust", state, null, null, null, error);
    }

    /// <summary>
    /// True when the grid service answers: this computer's Robust runs, or (on a plan that uses another computer's grid)
    /// that grid service's last check answered. A Robust of this computer's own never counts on such a plan.
    /// </summary>
    private bool GridServiceUp() =>
        PlanCatalog.RunsRobust(CurrentSettings())
            ? _robust?.Process.State == ProcessState.Running
            : _gridAsset == ProcessState.Running;

    /// <summary>Why a region can't start while the grid service is down.</summary>
    private static InvalidOperationException GridServiceDown(LauncherSettings s) =>
        Unreachable(s) ?? new InvalidOperationException("Robust is not running; start it first.");

    /// <summary>Refuses an operation on this computer's Robust when the plan doesn't run one.</summary>
    private static void ThrowIfNoGridService(LauncherSettings s)
    {
        if (PlanCatalog.RunsRobust(s)) return;
        throw new InvalidOperationException(s.Plan == OwnerPlan.Connected
            ? $"This computer doesn't run a grid service; it uses {OtherGridName(s)}'s."
            : "This computer doesn't run a grid service; it uses the Grid Asset Server's.");
    }

    /// <summary>
    /// Asks the grid service; on a plan that uses another computer's grid, a call that gets no answer becomes that plan's
    /// <see cref="GridUnreachableException"/>.
    /// </summary>
    private static async Task<T> AskGridServiceAsync<T>(LauncherSettings s, Func<Task<T>> call, CancellationToken ct)
    {
        try { return await call(); }
        catch (Exception e) when ((e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
                                  && Unreachable(s, e) is { } unreachable)
        {
            throw unreachable;
        }
    }

    /// <summary>
    /// The other computers' regions that count for placing a region in <paramref name="area"/>, when the plan uses another
    /// computer's grid: a Region Server reads the Grid Asset Server's whole map, a Connected Server only the area (its
    /// grid's map may hold many thousands of regions). Null on a plan that runs Robust, which makes no map call.
    /// </summary>
    private async Task<IReadOnlyList<MapRegion>?> RegionsElsewhereAsync(LauncherSettings s, MapArea area, CancellationToken ct) =>
        PlanCatalog.RunsRobust(s)
            ? null
            : await AskGridServiceAsync(s,
                () => s.Plan == OwnerPlan.Connected ? robust.GetMapRegionsAsync(area, ct) : robust.GetMapRegionsAsync(ct), ct);

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
            var grid = OtherGridName(s);
            var account = await AskGridServiceAsync(s, () => robust.GetAccountAsync(first, last, ct), ct)
                ?? throw new ArgumentException(s.Plan == OwnerPlan.Connected
                    ? $"There's no avatar called {first} {last} on {grid}. Check the spelling, or sign up with {grid} first."
                    : $"There's no avatar called {first} {last} on {grid}. Check the spelling, or create the avatar on the Grid Asset Server first.");
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
