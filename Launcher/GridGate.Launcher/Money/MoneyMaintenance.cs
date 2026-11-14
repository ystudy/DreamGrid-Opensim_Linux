using GridGate.Launcher.Core.Money;

namespace GridGate.Launcher.Money;

/// <summary>
/// Keeps the books honest in the background (money design §7.1, §7.2): the self-check at start and every 24 hours, and
/// expired holds returned to their buyers every 30 seconds. Does nothing while the effective mode is Off.
/// </summary>
public sealed class MoneyMaintenance(MoneyLedger ledger, Func<MoneyMode> effective, TimeProvider time, ILogger<MoneyMaintenance> log) : BackgroundService
{
    public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);
    private static readonly TimeSpan UnavailableQuiet = TimeSpan.FromMinutes(5);

    private DateTimeOffset _lastUnavailable = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (effective() == MoneyMode.Off) return;
        // Made before the first await, so a tick that falls due during the start-up check isn't missed.
        using var timer = new PeriodicTimer(SweepEvery, time);
        await CheckAsync(stoppingToken);
        var lastCheck = time.GetUtcNow();
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (effective() == MoneyMode.Off) continue;
                await SweepAsync(stoppingToken);
                if (time.GetUtcNow() - lastCheck >= CheckEvery)
                {
                    await CheckAsync(stoppingToken);
                    lastCheck = time.GetUtcNow();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            var returned = await ledger.ReturnExpiredHoldsAsync(ct);
            if (returned > 0)
                log.LogInformation("Returned {Count} purchase holds that were never completed.", returned);
        }
        catch (MoneyUnavailableException)
        {
            Unavailable();
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            var result = await ledger.SelfCheckAsync(ct);
            if (!result.Ok)
                log.LogError("The money self-check found a difference and froze all payments. Sum of balances {Sum}; accounts that differ: {Accounts}.",
                    result.Sum, string.Join(", ", result.Mismatches.Select(m => $"{m.AccountId} (stored {m.Stored}, entries {m.FromEntries})")));
        }
        catch (MoneyUnavailableException)
        {
            Unavailable();
        }
    }

    private void Unavailable()
    {
        var now = time.GetUtcNow();
        if (now - _lastUnavailable < UnavailableQuiet) return;
        _lastUnavailable = now;
        log.LogWarning("The money database can't be reached; purchase holds and the self-check will be tried again.");
    }
}
