using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Setup;

namespace DreamGrid.Launcher;

/// <summary>
/// The add-a-region work behind /regions/add: one add at a time, in the background, so it outlives the request.
/// It keeps its own <see cref="SetupProgress"/> (the stages and percentages are the same ones), the region's name, and
/// the saved region's id once it is known (also after a failure, when the region stays saved).
/// </summary>
public sealed class RegionJob(
    BackgroundWork background,
    LogReader logs,
    Func<LauncherSettings> settings,
    ILogger<RegionJob> log)
{
    private const int ExcerptLines = 40;

    private readonly SetupProgress _progress = new();
    private readonly object _gate = new();
    private string? _name;
    private Guid? _regionId;

    /// <summary>The region being (or last) added; null before the first add.</summary>
    public string? Name { get { lock (_gate) return _name; } }

    /// <summary>The saved region's id: set once the add has returned, or, after a failure, when the region was saved anyway.</summary>
    public Guid? RegionId { get { lock (_gate) return _regionId; } }

    public SetupProgressSnapshot Snapshot() => _progress.Snapshot();

    /// <summary>Starts adding <paramref name="request"/> in the background; false (nothing changes) while another add is running.</summary>
    public bool TryStart(NewRegion request, Func<NewRegion, CancellationToken, IProgress<string>, Task<RegionSettings>> add)
    {
        if (!_progress.TryBegin()) return false;
        lock (_gate) (_name, _regionId) = (request.Name, null);
        background.Run("add region", ct => AddAsync(request, add, ct));
        return true;
    }

    private async Task AddAsync(NewRegion request, Func<NewRegion, CancellationToken, IProgress<string>, Task<RegionSettings>> add, CancellationToken ct)
    {
        try
        {
            var region = await add(request, ct, _progress);
            lock (_gate) _regionId = region.Uuid;
            _progress.Complete();
            log.LogInformation("The region {Name} is ready.", request.Name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Saved(request.Name);
            log.LogWarning("Adding the region {Name} was cut off at \"{Stage}\" because the launcher stopped.", request.Name, _progress.Stage);
            // The page says what to do next, depending on whether the region was saved.
            _progress.Fail("The launcher stopped before your region was ready.", null);
        }
        catch (Exception e)
        {
            log.LogError(e, "Adding the region {Name} failed at \"{Stage}\": {Message}", request.Name, _progress.Stage, e.Message);
            var saved = Saved(request.Name);
            // The controller refuses a request with exactly ArgumentException, in words meant for the page; its
            // "(Parameter 'request')" suffix is not. Anything else is plain text, with the log lines as details.
            if (e.GetType() == typeof(ArgumentException))
            {
                var suffix = e is ArgumentException { ParamName: { Length: > 0 } param } ? $" (Parameter '{param}')" : "";
                _progress.Fail(e.Message.EndsWith(suffix, StringComparison.Ordinal) ? e.Message[..^suffix.Length] : e.Message, null);
                return;
            }
            _progress.Fail("Your region could not be created.", Excerpt(saved));
        }
        finally
        {
            // Never leave the run marked as going: that would refuse every later add until a restart.
            if (_progress.Running)
                _progress.Fail("Your region could not be created.", null);
        }
    }

    /// <summary>A region whose add failed stays saved: remembers its id so the page can open it.</summary>
    private RegionSettings? Saved(string name)
    {
        var region = RegionModel.FindByName(settings(), name);
        if (region is not null) lock (_gate) _regionId = region.Uuid;
        return region;
    }

    /// <summary>The new region's log's last lines, else Robust's, else the launcher's own; a log that cannot be read is skipped.</summary>
    private string? Excerpt(RegionSettings? region)
    {
        try
        {
            var sources = new List<string>();
            if (region is not null) sources.Add("region-" + region.Instance);
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
                    log.LogWarning("The {Source} log could not be read for the excerpt: {Message}", source, e.Message);
                }
            }
            return null;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "The log excerpt could not be read: {Message}", e.Message);
            return null;
        }
    }
}
