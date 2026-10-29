using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Setup;
using DreamGrid.Launcher.Endpoints;

namespace DreamGrid.Launcher.Setup;

/// <summary>
/// The grid work behind the setup checklist that must outlive a request: warming the grid up after step 1 and
/// creating the region in step 3. Background work is cancelled only when the launcher stops. It also keeps owner
/// creation (step 2), or naming the owner on a Region Server, to one request at a time.
/// </summary>
public sealed class SetupRunner(
    IGridController grid,
    SetupProgress progress,
    LogReader logs,
    Func<LauncherSettings> settings,
    BackgroundWork background,
    ILogger<SetupRunner> log)
{
    private const int ExcerptLines = 40;

    /// <summary>What to do after a port conflict on a setup page (Advanced Settings is out of reach before setup ends).</summary>
    public const string CloseThatProgram = " Close that program, then try again.";

    /// <summary>The plain port sentence when <paramref name="e"/> (or the last failed start) was a port conflict; else null.</summary>
    public string? PortConflictText(Exception e) =>
        StatusApi.ConflictPort(e, grid.GetStatus()) is { } port ? StatusApi.PortInUse(port) + CloseThatProgram : null;
    private int _warming;

    /// <summary>
    /// Step 1: starts the grid in the background so MariaDB and Robust warm up while the person types, unless it is
    /// already running or starting. On a Region Server the grid service is the Grid Asset Server, which the start
    /// reads again; this computer's Robust is never started. A failure is only logged; the region step starts what is
    /// still missing.
    /// </summary>
    public void WarmUp()
    {
        var s = grid.GetStatus();
        var starting = s.Database.State == ProcessState.Starting || s.Robust.State == ProcessState.Starting;
        var running = s.Database.State == ProcessState.Running && s.Robust.State == ProcessState.Running;
        if (starting || running || Interlocked.Exchange(ref _warming, 1) == 1) return;

        background.Run("setup warm-up", async ct =>
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
        });
    }

    /// <summary>
    /// Starts the database, then the grid service, when they are not running; each start is reported first. A grid
    /// service that started before the world was named restarts, so it says the new name. On a Region Server, which has
    /// no grid service of its own, a database that isn't running or a Grid Asset Server not known to answer starts the
    /// grid instead: that reads the join information again, and throws <see cref="GridAssetUnreachableException"/> when
    /// the Grid Asset Server doesn't answer.
    /// </summary>
    public async Task EnsureGridAsync(IProgress<string>? stages, CancellationToken ct)
    {
        if (!PlanCatalog.RunsRobust(settings()))
        {
            var current = grid.GetStatus();
            var databaseUp = current.Database.State == ProcessState.Running;
            if (databaseUp && current.Robust.State == ProcessState.Running) return;
            stages?.Report(databaseUp ? SetupProgress.ReachingGridAsset : SetupProgress.PreparingDatabase);
            await grid.StartAsync(ct);
            return;
        }

        if (grid.GetStatus().Database.State != ProcessState.Running)
        {
            stages?.Report(SetupProgress.PreparingDatabase);
            await grid.StartComponentAsync("database", ct);
        }
        var status = grid.GetStatus();
        if (status.Robust.State != ProcessState.Running)
        {
            stages?.Report(SetupProgress.StartingGrid);
            await grid.StartComponentAsync("robust", ct);
        }
        else if (status.RobustOutdated)
        {
            stages?.Report(SetupProgress.StartingGrid);
            await grid.RestartComponentAsync("robust", ct);
        }
    }

    private int _creatingOwner;

    /// <summary>True while a request is creating the owner avatar.</summary>
    public bool CreatingOwner => Volatile.Read(ref _creatingOwner) == 1;

    /// <summary>
    /// Step 2: starts what the grid still needs, then creates the owner avatar; null, without touching the grid,
    /// while another request is already creating it (a double submit).
    /// </summary>
    public async Task<RobustAccount?> TryCreateOwnerAsync(string first, string last, string password, string? email, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _creatingOwner, 1) == 1) return null;
        try
        {
            await EnsureGridAsync(null, ct);
            return await grid.CreateOwnerAsync(first, last, password, email, ct);
        }
        finally { Volatile.Write(ref _creatingOwner, 0); }
    }

    /// <summary>
    /// The avatar step on a Region Server: starts what the grid still needs, then makes the existing avatar called
    /// <paramref name="first"/> <paramref name="last"/> on the Grid Asset Server the owner of this computer's regions;
    /// null, without touching the grid, while another request is already doing it (a double submit). Throws
    /// <see cref="ArgumentException"/> when there is no such avatar and <see cref="GridAssetUnreachableException"/> when
    /// the Grid Asset Server doesn't answer; both messages are for the owner.
    /// </summary>
    public async Task<RobustAccount?> TrySetEstateOwnerAsync(string first, string last, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _creatingOwner, 1) == 1) return null;
        try
        {
            await EnsureGridAsync(null, ct);
            return await grid.SetEstateOwnerAsync(first, last, ct);
        }
        finally { Volatile.Write(ref _creatingOwner, 0); }
    }

    /// <summary>Step 3: creates the region in the background, reporting to <see cref="SetupProgress"/>; false while one is already being created.</summary>
    public bool TryStartRegion(string name, string content)
    {
        if (!progress.TryBegin()) return false;
        background.Run("setup region", ct => CreateRegionAsync(name, content, ct));
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
            // The headline stays plain newcomer text; the technical message leads the details, above the log lines.
            var where = stage == "" ? "" : $" It stopped at \"{stage}\".";
            var excerpt = Excerpt();
            // A Grid Asset Server that doesn't answer is said as is: the owner can act on it.
            var headline = e is GridAssetUnreachableException ? e.Message : PortConflictText(e);
            progress.Fail(headline ?? $"Your region could not be set up.{where} Check the details, then try again.",
                excerpt is null ? e.Message : e.Message + "\n\n" + excerpt);
        }
        finally
        {
            // Never leave the run marked as going: that would refuse every retry until a restart.
            if (progress.Running)
                progress.Fail("Your region could not be set up. Try again.", null);
        }
    }

    /// <summary>
    /// The region log's last lines, else Robust's (on a plan that runs it), else the launcher's own. A log that cannot be read is skipped
    /// (and logged): the failure must still be reported.
    /// </summary>
    private string? Excerpt()
    {
        try
        {
            var sources = new List<string>();
            var s = settings();
            sources.AddRange(s.Programs.Select(p => "region-" + p.Instance));
            if (PlanCatalog.RunsRobust(s)) sources.Add("robust"); // a Region Server has no Robust log of its own
            sources.Add("launcher");
            foreach (var source in sources)
            {
                try
                {
                    var tail = logs.Tail(source, ExcerptLines);
                    if (tail.Lines.Count > 0) return string.Join('\n', tail.Lines);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log.LogWarning("Setup: the {Source} log could not be read for the excerpt: {Message}", source, e.Message);
                }
            }
            return null;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Setup: the log excerpt could not be read: {Message}", e.Message);
            return null;
        }
    }
}
