using DreamGrid.Launcher.Core.Grid;

namespace DreamGrid.Launcher;

/// <summary>
/// Starts the grid once the web port is listening (the region posts its RegionReady alert there while it boots),
/// and on shutdown only detaches: the children keep running (spec 4.12). A failed start is logged and the host
/// stays up so the website can show the grid status.
/// </summary>
public sealed class GridHostedService(IGridController grid, IHostApplicationLifetime lifetime, ILogger<GridHostedService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hosted services start before the server listens; ApplicationStarted fires once it does.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        {
            try { await started.Task.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        try
        {
            await grid.StartAsync(stoppingToken);
            log.LogInformation("The grid is running.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            log.LogInformation("The launcher stopped while the grid was starting.");
        }
        catch (PortConflictException e)
        {
            log.LogCritical("{Message}", e.Message);
        }
        catch (Exception e)
        {
            log.LogError(e, "The grid did not start: {Message}", e.Message);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await grid.DetachAsync();
    }
}
