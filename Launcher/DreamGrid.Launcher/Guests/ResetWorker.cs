using System.Threading.Channels;

namespace DreamGrid.Launcher.Guests;

/// <summary>Where a password-reset request hands what it must do after its reply.</summary>
public interface IResetWork
{
    void Post(Func<Task> work);
}

/// <summary>
/// Runs password-reset work after the request has answered, one item at a time, in order. A request for a matching name and
/// email has more to do than one that doesn't (make a link, queue the email, count the send); doing it here keeps the reply's
/// time the same either way. At most 1,000 items wait; beyond that new ones are dropped (and logged), which the per-address
/// limits make unlikely. A failing item is logged and the next one runs.
/// </summary>
public sealed class ResetWorker(ILogger<ResetWorker> log) : BackgroundService, IResetWork
{
    private readonly Channel<Func<Task>> _work = Channel.CreateBounded<Func<Task>>(
        new BoundedChannelOptions(1000) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    public void Post(Func<Task> work)
    {
        if (!_work.Writer.TryWrite(work)) log.LogWarning("Password reset: too much waiting work; a request was dropped.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in _work.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await work();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Password reset: handling a request failed");
            }
        }
    }
}
