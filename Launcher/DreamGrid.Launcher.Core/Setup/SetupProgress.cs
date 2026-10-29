namespace DreamGrid.Launcher.Core.Setup;

/// <summary>One consistent reading of <see cref="SetupProgress"/> (what the progress API returns).</summary>
public sealed record SetupProgressSnapshot(string Stage, int Percent, bool Running, string? Error, string? LogExcerpt);

/// <summary>
/// What the setup checklist's region step is doing, in plain words, for the progress view. One run at a time:
/// <see cref="TryBegin"/> starts a run, <see cref="Complete"/> or <see cref="Fail"/> ends it. As an
/// <see cref="IProgress{T}"/> it takes a stage name and looks up its percentage.
/// </summary>
public sealed class SetupProgress : IProgress<string>
{
    public const string PreparingDatabase = "Preparing the database";
    public const string StartingGrid = "Starting the grid service";
    /// <summary>A Region Server's stand-in for <see cref="StartingGrid"/>: it uses the Grid Asset Server's grid service.</summary>
    public const string ReachingGridAsset = "Reaching the Grid Asset Server";
    public const string CreatingRegion = "Creating your region";
    public const string LoadingContent = "Loading the region content";
    public const string WaitingForLogins = "Waiting for the region to accept logins";
    public const string Ready = "Ready";

    private static readonly Dictionary<string, int> Percents = new()
    {
        [PreparingDatabase] = 10,
        [StartingGrid] = 25,
        [ReachingGridAsset] = 25,
        [CreatingRegion] = 40,
        [LoadingContent] = 65,
        [WaitingForLogins] = 85,
        [Ready] = 100,
    };

    private readonly object _gate = new();
    private string _stage = "";
    private int _percent;
    private bool _running;
    private string? _error;
    private string? _logExcerpt;

    public string Stage { get { lock (_gate) return _stage; } }
    public int Percent { get { lock (_gate) return _percent; } }
    public bool Running { get { lock (_gate) return _running; } }
    public string? Error { get { lock (_gate) return _error; } }
    /// <summary>The last log lines when the run failed.</summary>
    public string? LogExcerpt { get { lock (_gate) return _logExcerpt; } }

    public SetupProgressSnapshot Snapshot()
    {
        lock (_gate) return new SetupProgressSnapshot(_stage, _percent, _running, _error, _logExcerpt);
    }

    /// <summary>Starts a fresh run; false (and nothing changes) while another run is still going.</summary>
    public bool TryBegin()
    {
        lock (_gate)
        {
            if (_running) return false;
            (_running, _stage, _percent, _error, _logExcerpt) = (true, "", 0, null, null);
            return true;
        }
    }

    /// <summary>The percentage never goes back within a run.</summary>
    public void Report(string stage, int percent)
    {
        lock (_gate)
        {
            _stage = stage;
            _percent = Math.Max(_percent, Math.Clamp(percent, 0, 100));
        }
    }

    /// <summary>A known stage gets its own percentage; an unknown one keeps the current percentage.</summary>
    public void Report(string stage)
    {
        lock (_gate) Report(stage, Percents.GetValueOrDefault(stage, _percent));
    }

    public void Complete()
    {
        lock (_gate)
        {
            Report(Ready, 100);
            _running = false;
        }
    }

    public void Fail(string error, string? logExcerpt)
    {
        lock (_gate)
        {
            (_error, _logExcerpt) = (error, logExcerpt);
            _running = false;
        }
    }
}
