using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Endpoints;

/// <summary>
/// A region's power menu on the website. Like <see cref="GridActions"/>, each action runs in the background (cancelled
/// when the launcher stops) and the request returns at once; the live status shows the progress and failures are logged.
/// </summary>
public sealed class RegionActions(IGridController grid, BackgroundWork background, ILogger<RegionActions> log)
{
    /// <summary>What the page tells the person once the action is under way.</summary>
    public static string Notice(RegionPower action, string name) => action switch
    {
        RegionPower.Start or RegionPower.UnparkAndStart => $"Starting {name}. This can take a few minutes.",
        RegionPower.Restart => $"Restarting {name}. This can take a few minutes.",
        RegionPower.Stop => $"Stopping {name}.",
        RegionPower.Park => $"Parking {name}.",
        RegionPower.Unpark => $"{name} is no longer parked. It stays stopped until you start it.",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>
    /// The other regions a restart of this region's program drops: those sharing it that are running or starting, while
    /// that program is up.
    /// </summary>
    public static IReadOnlyList<string> SharingProgram(GridStatus status, Guid id)
    {
        var self = status.Regions.FirstOrDefault(x => x.Id == id);
        if (self is null) return [];
        var program = status.Programs.FirstOrDefault(p => p.Instance == self.Instance);
        if (program is null || !RegionPowerPlan.IsActive(program.Process.State)) return [];
        return status.Regions
            .Where(x => x.Id != id && x.Instance == self.Instance && x.State is RegionState.Running or RegionState.Starting)
            .Select(x => x.Name).ToList();
    }

    /// <summary>Moves the region in the background; false when there is no such (live) region. Failures are logged.</summary>
    public bool TryMove(Guid id, int x, int y)
    {
        if (RegionModel.Find(grid.CurrentSettings(), id) is not { DeletedAt: null } region) return false;

        log.LogInformation("Regions: move requested for {Region} to {X}, {Y}.", region.Name, x, y);
        background.Run($"region move {region.Name}", async stopping =>
        {
            try
            {
                await grid.MoveRegionAsync(id, x, y, stopping);
                log.LogInformation("Regions: move finished for {Region}.", region.Name);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                log.LogInformation("Regions: the launcher stopped during the move of {Region}.", region.Name);
            }
            catch (Exception e)
            {
                log.LogError(e, "Regions: the move of {Region} failed: {Message}", region.Name, e.Message);
            }
        });
        return true;
    }

    /// <summary>
    /// Changes how the region runs in the background (with the program <paramref name="shareWith"/>, or on its own when
    /// null); false when there is no such (live) region. Failures are logged.
    /// </summary>
    public bool TryChangeRunsWith(Guid id, string? shareWith)
    {
        if (RegionModel.Find(grid.CurrentSettings(), id) is not { DeletedAt: null } region) return false;

        log.LogInformation("Regions: change of how {Region} runs requested ({With}).", region.Name, shareWith ?? "on its own");
        background.Run($"region runs-with {region.Name}", async stopping =>
        {
            try
            {
                await grid.ChangeRunsWithAsync(id, shareWith, stopping);
                log.LogInformation("Regions: {Region} now runs {With}.", region.Name, shareWith is null ? "on its own" : "with " + shareWith);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                log.LogInformation("Regions: the launcher stopped while changing how {Region} runs.", region.Name);
            }
            catch (Exception e)
            {
                log.LogError(e, "Regions: changing how {Region} runs failed: {Message}", region.Name, e.Message);
            }
        });
        return true;
    }

    /// <summary>Starts <paramref name="action"/> in the background; false when there is no such (live) region.</summary>
    public bool TryRun(Guid id, RegionPower action)
    {
        if (RegionModel.Find(grid.CurrentSettings(), id) is not { DeletedAt: null } region) return false;

        log.LogInformation("Regions: {Action} requested for {Region}.", action, region.Name);
        background.Run($"region {action} {region.Name}", async stopping =>
        {
            try
            {
                await grid.RegionPowerAsync(id, action, stopping);
                log.LogInformation("Regions: {Action} finished for {Region}.", action, region.Name);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                log.LogInformation("Regions: the launcher stopped during {Action} of {Region}.", action, region.Name);
            }
            catch (PortConflictException e)
            {
                log.LogCritical("{Message}", e.Message);
            }
            catch (Exception e)
            {
                log.LogError(e, "Regions: {Action} of {Region} failed: {Message}", action, region.Name, e.Message);
            }
        });
        return true;
    }
}
