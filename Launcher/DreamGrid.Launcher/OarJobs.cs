using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher;

/// <summary>Where an OAR job is, for the job page and its JSON.</summary>
/// <param name="RegionId">The region the job was started for.</param>
/// <param name="Text">What the job does, in words ("Saving Home as an OAR").</param>
/// <param name="Stage">The stage in plain words.</param>
/// <param name="BytesWritten">While a save runs, the bytes written so far.</param>
/// <param name="Elapsed">How long the job has run, or ran.</param>
/// <param name="Running">False once the job has finished, with or without an error.</param>
/// <param name="Error">Why the job failed, in plain words; null when it did not.</param>
/// <param name="SavedAs">The library name a finished save wrote.</param>
public sealed record OarJobSnapshot(Guid RegionId, string Text, string Stage, long? BytesWritten, TimeSpan Elapsed,
    bool Running, string? Error, string? SavedAs)
{
    /// <summary>The elapsed time in words ("5 s", "1 min 5 s").</summary>
    public string ElapsedText => Elapsed.TotalMinutes >= 1
        ? $"{(int)Elapsed.TotalMinutes} min {Elapsed.Seconds} s"
        : $"{(int)Elapsed.TotalSeconds} s";

    /// <summary>"1.0 MB written so far" while a job reports bytes; null otherwise.</summary>
    public string? WrittenText => Running && BytesWritten is { } bytes ? $"{RegionAdvice.FileSize(bytes)} written so far" : null;
}

/// <summary>
/// The OAR jobs behind the region pages: one at a time for each program, in the background so they outlive the browser
/// (they get the launcher-lifetime token, never a request's). The last job of a program is kept until the next one starts,
/// so its page can still say how it ended.
/// </summary>
public sealed class OarJobs(IGridController grid, BackgroundWork background, TimeProvider time, ILogger<OarJobs> log)
{
    private const string Starting = "Starting";
    private const string Stopped = "The launcher stopped before the job finished.";
    private const string Failed = "The OAR job failed; see the launcher log.";

    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = [];

    /// <summary>What a job does, in words, for the job page and the controller's refusals alike.</summary>
    public static string Describe(OarJobRequest request, string regionName) => request.Kind switch
    {
        OarJobKind.Import => $"Importing {request.File} into {regionName}",
        OarJobKind.Save => $"Saving {regionName} as an OAR",
        _ => $"Making the land of {regionName} again",
    };

    /// <summary>Starts <paramref name="request"/> in the background; false (nothing changes) while a job runs in that region's program.</summary>
    public bool TryStart(OarJobRequest request, string text)
    {
        var key = KeyFor(request.RegionId);
        Job job;
        lock (_gate)
        {
            if (_jobs.TryGetValue(key, out var current) && current.Running) return false;
            job = new Job(request, text, time.GetTimestamp());
            _jobs[key] = job;
        }
        background.Run("OAR job", ct => RunAsync(job, ct));
        return true;
    }

    /// <summary>The running job of the region's program, else the last one that finished there; null if there has been none.</summary>
    public OarJobSnapshot? For(Guid regionId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(KeyFor(regionId), out var job)) return null;
            var end = job.Ended ?? time.GetTimestamp();
            return new OarJobSnapshot(job.Request.RegionId, job.Text, job.Stage, job.Bytes, time.GetElapsedTime(job.Started, end),
                job.Running, job.Error, job.SavedAs);
        }
    }

    /// <summary>
    /// Runs <paramref name="after"/> once the region's program's job has succeeded, with the launcher-lifetime token; it
    /// never runs after a failure. Meant to be called right after <see cref="TryStart"/>: when that job has already
    /// succeeded, <paramref name="after"/> runs at once. If it throws, that is logged and becomes the job's error.
    /// </summary>
    public void Then(Guid regionId, Func<CancellationToken, Task> after)
    {
        Job job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(KeyFor(regionId), out job!)) return;
            if (job.Running)
            {
                job.After.Add(after);
                return;
            }
            if (job.Error is not null) return;
            // The job has just ended; its error slot is free for a follow-up that fails.
            job.Running = true;
            job.Ended = null;
            job.After.Add(after);
        }
        background.Run("OAR follow-up", ct => FinishAsync(job, ct));
    }

    private async Task RunAsync(Job job, CancellationToken ct)
    {
        try
        {
            var saved = await grid.RunOarJobAsync(job.Request, ct, new Reporter(this, job));
            lock (_gate) job.SavedAs = saved;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.LogWarning("The OAR job \"{Text}\" was cut off because the launcher stopped.", job.Text);
            lock (_gate) job.Error = Stopped;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "The OAR job \"{Text}\" failed: {Message}", job.Text, e.Message);
            lock (_gate) job.Error = Words(e);
        }
        await FinishAsync(job, ct);
    }

    /// <summary>Runs the follow-ups of a job that succeeded, then marks the job ended.</summary>
    private async Task FinishAsync(Job job, CancellationToken ct)
    {
        while (true)
        {
            Func<CancellationToken, Task>? next = null;
            lock (_gate)
            {
                if (job.Error is null && job.After.Count > 0)
                {
                    next = job.After[0];
                    job.After.RemoveAt(0);
                }
                else
                {
                    job.After.Clear();
                    job.Running = false;
                    job.Ended = time.GetTimestamp();
                    return;
                }
            }
            try
            {
                await next(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (_gate) job.Error = Stopped;
            }
            catch (Exception e)
            {
                log.LogError(e, "The step after the OAR job \"{Text}\" failed: {Message}", job.Text, e.Message);
                lock (_gate) job.Error = Words(e);
            }
        }
    }

    /// <summary>
    /// A refusal in the words the controller chose: an ArgumentException's message without its "(Parameter '…')" suffix, an
    /// InvalidOperationException's as it is. Anything else gets a general line (the log has the details).
    /// </summary>
    private static string Words(Exception e)
    {
        if (e is InvalidOperationException) return e.Message;
        if (e is ArgumentException { ParamName: var param })
        {
            var suffix = string.IsNullOrEmpty(param) ? "" : $" (Parameter '{param}')";
            return e.Message.EndsWith(suffix, StringComparison.Ordinal) ? e.Message[..^suffix.Length] : e.Message;
        }
        return Failed;
    }

    /// <summary>The program's name, else the region's id for a region that is not in the settings.</summary>
    private string KeyFor(Guid regionId) =>
        RegionModel.Find(grid.CurrentSettings(), regionId)?.Instance ?? regionId.ToString();

    private sealed class Job(OarJobRequest request, string text, long started)
    {
        public OarJobRequest Request { get; } = request;
        public string Text { get; } = text;
        public long Started { get; } = started;
        public long? Ended { get; set; }
        public string Stage { get; set; } = Starting;
        public long? Bytes { get; set; }
        public bool Running { get; set; } = true;
        public string? Error { get; set; }
        public string? SavedAs { get; set; }
        public List<Func<CancellationToken, Task>> After { get; } = [];
    }

    private sealed class Reporter(OarJobs owner, Job job) : IProgress<OarJobProgress>
    {
        public void Report(OarJobProgress value)
        {
            lock (owner._gate) (job.Stage, job.Bytes) = (value.Stage, value.BytesWritten);
        }
    }
}
