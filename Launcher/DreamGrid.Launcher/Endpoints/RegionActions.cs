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
