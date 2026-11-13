using GridGate.Launcher.Core.ModelImport;

namespace GridGate.Launcher.ModelImport;

/// <summary>Deletes model-upload job folders older than an hour, every ten minutes.</summary>
public sealed class ImportJobSweep(ImportJobs jobs, TimeProvider time, ILogger<ImportJobSweep> log) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Period, time);
            do
            {
                try { jobs.Sweep(); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "Model import: clearing old upload folders failed: {Message}", e.Message);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
