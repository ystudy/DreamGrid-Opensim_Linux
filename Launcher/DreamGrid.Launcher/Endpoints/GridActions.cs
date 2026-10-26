using DreamGrid.Launcher.Core.Grid;

namespace DreamGrid.Launcher.Endpoints;

/// <summary>
/// The dashboard's controls. A start or restart takes minutes, so each runs in the background (cancelled when
/// the launcher stops) and the request returns at once; the live status shows the progress and failures are logged.
/// </summary>
public sealed class GridActions(IGridController grid, BackgroundWork background, ILogger<GridActions> log)
{
    /// <summary>What the dashboard tells the person once an action is under way.</summary>
    public static readonly IReadOnlyDictionary<string, string> Notices = new Dictionary<string, string>
    {
        ["start"] = "Starting your world. This can take a few minutes.",
        ["stop"] = "Stopping your world.",
        ["restart"] = "Restarting your world. This can take a few minutes.",
        ["restart-database"] = "Restarting the database. The grid service and your region restart with it.",
        ["restart-robust"] = "Restarting the grid service.",
        ["restart-region"] = "Restarting your region. This can take a few minutes.",
    };

    /// <summary>Starts <paramref name="action"/> in the background; false for an unknown action.</summary>
    public bool TryRun(string action)
    {
        Func<CancellationToken, Task>? run = action switch
        {
            "start" => grid.StartAsync,
            "stop" => grid.StopAllAsync,
            "restart" => async ct =>
            {
                await grid.StopAllAsync(ct);
                await grid.StartAsync(ct);
            },
            "restart-database" => ct => grid.RestartComponentAsync("database", ct),
            "restart-robust" => ct => grid.RestartComponentAsync("robust", ct),
            "restart-region" => ct => grid.RestartComponentAsync("region", ct),
            _ => null,
        };
        if (run is null) return false;

        log.LogInformation("Dashboard: {Action} requested.", action);
        background.Run("dashboard " + action, async stopping =>
        {
            try
            {
                await run(stopping);
                log.LogInformation("Dashboard: {Action} finished.", action);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                log.LogInformation("Dashboard: the launcher stopped during {Action}.", action);
            }
            catch (PortConflictException e)
            {
                log.LogCritical("{Message}", e.Message);
            }
            catch (Exception e)
            {
                log.LogError(e, "Dashboard: {Action} failed: {Message}", action, e.Message);
            }
        });
        return true;
    }
}
