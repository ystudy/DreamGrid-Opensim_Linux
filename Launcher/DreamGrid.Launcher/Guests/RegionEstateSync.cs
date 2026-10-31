using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;

namespace DreamGrid.Launcher.Guests;

/// <summary>
/// Every <see cref="Period"/>, moves each running region whose right is waiting for it (<see cref="RegionRights.PendingMoves"/>):
/// into its owner's estate, made the first time one of their regions moves (<c>"&lt;First Last&gt; Estate"</c>), or back to the
/// estate it came from. A region that isn't running waits. A move that fails stays pending and is logged once per region until
/// it succeeds. Each move is audited as done by DreamGrid. The owner's estate is never deleted.
/// </summary>
public sealed class RegionEstateSync(IGridController grid, RegionRights rights, AuditLog audit, TimeProvider time, ILogger<RegionEstateSync> log)
    : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(60);

    // Regions whose failing move was logged; one goes when its move succeeds. Only the one sync loop touches it.
    private readonly HashSet<Guid> _told = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await SyncOnceAsync(stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "The regions' estates could not be checked: {Message}", e.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>One pass over the pending moves (the loop's work; tests call it directly). Nothing moves on a plan without a
    /// guest side (a Region Server or a Connected Server, see <see cref="AdminRule.HasStaffPages"/>): rights left from before
    /// the plan changed wait.</summary>
    public async Task SyncOnceAsync(CancellationToken ct)
    {
        if (!AdminRule.HasStaffPages(grid.CurrentSettings())) return;
        var pending = rights.PendingMoves();
        if (pending.Count == 0) return;
        var regions = grid.GetStatus().Regions.ToDictionary(r => r.Id);
        foreach (var right in pending)
        {
            if (regions.GetValueOrDefault(right.Region) is not { State: RegionState.Running } region) continue;
            // Taken away or handed on since the pass began: the right as it is now waits for the next pass.
            if (rights.Of(right.Region) != right) continue;
            try
            {
                string? estate;
                if (right.Pending == EstateMove.ToOwner)
                {
                    if (await IntoOwnersAsync(right, ct) is not { } owners) continue;
                    estate = owners;
                }
                else estate = await BackAsync(right, region, ct);
                if (estate is not null)
                    audit.Write(new AuditEntry("DreamGrid", null, "system", null, "estate-move", $"{region.Name} → {estate}", "ok"));
                rights.MoveDone(right);
                _told.Remove(right.Region);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var to = right.Pending == EstateMove.ToOwner ? $"{right.OwnerName}'s estate" : "the estate it came from";
                if (_told.Add(right.Region))
                    log.LogWarning("{Region} could not move into {Estate}; it is tried again every minute: {Message}", region.Name, to, e.Message);
            }
        }
    }

    /// <summary>
    /// Moves the region into its owner's estate, making it when they have none (or it is gone); returns its name, or null
    /// (nothing moved) when the right was taken away or handed on before the first move began.
    /// </summary>
    private async Task<string?> IntoOwnersAsync(RegionRight right, CancellationToken ct)
    {
        // Remembered before the region leaves, and only while the right is still this one, so a right taken away meanwhile
        // either never moves or knows where it goes back to.
        if (rights.HomeEstateOf(right.Region) is null)
        {
            var home = await grid.EstateOfAsync(right.Region, ct) ?? throw new InvalidOperationException("The region is in no estate yet.");
            if (!rights.BeginMove(right, home)) return null;
        }

        if (rights.OwnerEstateOf(right.Owner) is { } owned)
        {
            try
            {
                await grid.LinkEstateAsync(right.Region, owned.EstateId, ct);
                return owned.Name;
            }
            // Removed outside DreamGrid: the owner gets a new one.
            catch (InvalidOperationException e) when (e.Message.Contains($"No estate found with ID {owned.EstateId}", StringComparison.Ordinal)) { }
        }
        var (id, name) = await grid.CreateEstateAsync(right.Region, right.Owner, $"{right.OwnerName} Estate", ct);
        rights.SetEstate(right.Owner, id, name);
        await grid.LinkEstateAsync(right.Region, id, ct);
        return name;
    }

    /// <summary>
    /// Moves the region back to the estate it came from and returns its name, or null when where it came from isn't known:
    /// then it stays where it is and the right goes.
    /// </summary>
    private async Task<string?> BackAsync(RegionRight right, RegionStatus region, CancellationToken ct)
    {
        if (rights.HomeEstateOf(right.Region) is not { } home)
        {
            log.LogWarning("{Region} has no estate remembered to go back to, so it stays in the estate it is in.", region.Name);
            return null;
        }
        await grid.LinkEstateAsync(right.Region, home, ct);
        // The grid's regions are in the estate their INI names: "<world name> Estate".
        return $"{grid.CurrentSettings().GridName} Estate";
    }
}
