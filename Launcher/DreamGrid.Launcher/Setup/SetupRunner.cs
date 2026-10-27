using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Setup;
using DreamGrid.Launcher.Endpoints;

namespace DreamGrid.Launcher.Setup;

/// <summary>
/// The grid work behind the setup checklist that must outlive a request: warming the grid up after step 1 and
/// creating the region in step 3. Background work is cancelled only when the launcher stops. It also keeps owner
/// creation (step 2) to one request at a time.
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
    /// already running or starting. A failure is only logged; the region step starts what is still missing.
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
            progress.Fail(PortConflictText(e) ?? $"Your region could not be set up.{where} Check the details, then try again.",
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
    /// The region log's last lines, else Robust's, else the launcher's own. A log that cannot be read is skipped
    /// (and logged): the failure must still be reported.
    /// </summary>
    private string? Excerpt()
    {
        try
        {
            var sources = new List<string>();
            sources.AddRange(settings().Programs.Select(p => "region-" + p.Instance));
            sources.Add("robust");
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
