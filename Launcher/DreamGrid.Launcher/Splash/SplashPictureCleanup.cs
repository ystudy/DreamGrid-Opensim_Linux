using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Pictures;
using DreamGrid.Launcher.Core.Splash;

namespace DreamGrid.Launcher.Splash;

/// <summary>
/// Removes splash pictures that neither the draft nor the published design has used for <see cref="KeepUnused"/> (spec
/// 2026-11-01-viewer-splash-design §4.4). Each pass first records what is in use now, so a picture chosen again before its
/// time is up starts over. Each removal is audited as done by DreamGrid.
/// </summary>
public sealed class SplashPictureCleanup([FromKeyedServices("splash")] PictureStore pictures, SplashStore store, AuditLog audit,
    TimeProvider time, ILogger<SplashPictureCleanup> log) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromHours(1);
    public static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan KeepUnused = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, time, stoppingToken);
            using var timer = new PeriodicTimer(Period, time);
            do
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "Unused splash pictures could not be cleaned up: {Message}", e.Message);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>One pass (the loop's work; tests call it directly).</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            var inUse = SplashPictures.InUse(store);
            pictures.MarkUse(inUse);
            var now = time.GetUtcNow();
            foreach (var p in pictures.List())
            {
                if (p.UnusedSince is not { } since || now - since < KeepUnused || inUse.Contains(p.Name)) continue;
                // Checked again under the store's lock: a draft saved since this pass began may use it now.
                if (store.RemovePictureIfUnused(p.Name, pictures.Delete))
                    audit.Write(new AuditEntry("DreamGrid", null, "system", null, "splash-image-deleted", $"{p.Name} ({p.Label})", "unused for 30 days"));
            }
        }
        finally
        {
            _one.Release();
        }
    }
}
