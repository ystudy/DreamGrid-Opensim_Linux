namespace DreamGrid.Launcher.Email;

/// <summary>Sends the outbox in the background: whenever something is queued, and every 30 seconds for emails that had to wait.</summary>
public sealed class EmailWorker(EmailSender sender, ILogger<EmailWorker> log) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await sender.DrainAsync(stoppingToken);
                await sender.WaitForWorkAsync(Every, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // One bad round must not stop email for good; the next round tries again.
                log.LogError(e, "Sending email failed");
                await Task.Delay(Every, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }
}
