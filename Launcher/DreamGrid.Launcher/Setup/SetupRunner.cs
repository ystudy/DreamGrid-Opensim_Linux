using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Setup;

namespace DreamGrid.Launcher.Setup;

/// <summary>
/// The grid work behind the setup checklist that must outlive a request: warming the grid up after step 1 and
/// creating the region in step 3. Background work is cancelled only when the launcher stops.
/// </summary>
public sealed class SetupRunner(
    IGridController grid,
    SetupProgress progress,
    LogReader logs,
    Func<LauncherSettings> settings,
    IHostApplicationLifetime lifetime,
    ILogger<SetupRunner> log)
{
    private const int ExcerptLines = 40;
    private int _warming;

    /// <summary>
    /// Step 1: starts the grid in the background so MariaDB and Robust warm up while the person types, unless it is
    /// already running or starting. A failure is only logged; the region step starts what is still missing.
    /// </summary>
    public void WarmUp()
    {
        var s = grid.GetStatus();
        var starting = s.Database.State == ProcessState.Starting || s.Robust.State == ProcessState.Starting;
        var running = s.Database.State == ProcessState.Running && s.Robust.State == ProcessState.Running;
        if (starting || running || Interlocked.Exchange(ref _warming, 1) == 1) return;

        var ct = lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            try
            {
                await grid.StartAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                log.LogWarning(e, "The grid did not start during setup: {Message}", e.Message);
            }
            finally { Volatile.Write(ref _warming, 0); }
        }, CancellationToken.None);
    }

    /// <summary>Starts the database, then the grid service, when they are not running; each start is reported first.</summary>
    public async Task EnsureGridAsync(IProgress<string>? stages, CancellationToken ct)
    {
        if (grid.GetStatus().Database.State != ProcessState.Running)
        {
            stages?.Report(SetupProgress.PreparingDatabase);
            await grid.StartComponentAsync("database", ct);
        }
        if (grid.GetStatus().Robust.State != ProcessState.Running)
        {
            stages?.Report(SetupProgress.StartingGrid);
            await grid.StartComponentAsync("robust", ct);
        }
    }

    /// <summary>Step 3: creates the region in the background, reporting to <see cref="SetupProgress"/>; false while one is already being created.</summary>
    public bool TryStartRegion(string name, string content)
    {
        if (!progress.TryBegin()) return false;
        var ct = lifetime.ApplicationStopping;
        _ = Task.Run(() => CreateRegionAsync(name, content, ct), CancellationToken.None);
        return true;
    }

    private async Task CreateRegionAsync(string name, string content, CancellationToken ct)
    {
        try
        {
            await EnsureGridAsync(progress, ct);
            await grid.CreateRegionAsync(name, content, ct, progress);
            progress.Complete();
            log.LogInformation("Setup: the region {Name} is ready.", name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            progress.Fail("The launcher stopped before your region was ready. Try again once it is running.", null);
        }
        catch (Exception e)
        {
            var stage = progress.Stage;
            log.LogError(e, "Setup: creating the region {Name} failed at \"{Stage}\": {Message}", name, stage, e.Message);
            var where = stage == "" ? "" : $" It stopped at \"{stage}\".";
            progress.Fail($"Your region could not be set up.{where} {e.Message}", Excerpt());
        }
    }

    /// <summary>The region log's last lines, else Robust's, else the launcher's own.</summary>
    private string? Excerpt()
    {
        var sources = new List<string>();
        if (settings().Region is { } region) sources.Add("region-" + region.Instance);
        sources.Add("robust");
        sources.Add("launcher");
        foreach (var source in sources)
        {
            var tail = logs.Tail(source, ExcerptLines);
            if (tail.Lines.Count > 0) return string.Join('\n', tail.Lines);
        }
        return null;
    }
}
