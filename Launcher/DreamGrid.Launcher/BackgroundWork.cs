using System.Globalization;

namespace DreamGrid.Launcher;

/// <summary>
/// Runs the work that must outlive a request (a start, a restart, the region setup) and tracks it. On shutdown the
/// shared token is cancelled and the host waits for the running work, so nothing is cut off mid-step. A failure is
/// logged with the work's name and never thrown.
/// </summary>
public sealed class BackgroundWork(ILogger<BackgroundWork> log) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _running = [];

    /// <summary>Starts <paramref name="work"/> now; it sees a token cancelled when the launcher stops.</summary>
    public void Run(string name, Func<CancellationToken, Task> work)
    {
        var ct = _stopping.Token;
        lock (_gate)
        {
            var task = Task.Run(async () =>
            {
                UseStaffCulture();
                try
                {
                    await work(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    log.LogError(e, "Background work \"{Name}\" failed: {Message}", name, e.Message);
                }
            }, CancellationToken.None);
            _running.Add(task);
            _ = task.ContinueWith(t => { lock (_gate) _running.Remove(t); }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Sets English for the work this thread runs next. <c>Task.Run</c> carries the culture of the request that started it, and a
    /// resident page runs in the resident's language (<c>ResidentLanguageFilter</c>), so without this a German resident's restart
    /// would leave a long-lived monitor formatting in German. An email that needs a language is given it explicitly.
    /// Call it first thing inside work started from a request.
    /// </summary>
    public static void UseStaffCulture()
    {
        var en = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = en;
        CultureInfo.CurrentUICulture = en;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] pending;
        lock (_gate) pending = [.. _running];
        await _stopping.CancelAsync();
        try
        {
            await Task.WhenAll(pending).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.LogWarning("Background work was still running when the shutdown time ran out.");
        }
    }
}
