using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;

namespace DreamGrid.Launcher.Guests;

/// <summary>
/// Every <see cref="Period"/> (the first time <see cref="FirstRun"/> after start), deletes the sign-ups nobody confirmed: avatars on
/// the <see cref="PendingSignUps"/> list whose last link went out more than <see cref="SignUps.LinkLifetime"/> ago (spec
/// 2026-10-31-sign-up-design §5 step 6), so their names are free again. Only listed avatars are ever touched: a listed one that
/// is gone or no longer at level −1 is just taken off the list. A database or Robust that doesn't answer leaves the list as it was
/// for the next run. Each deletion is audited as done by DreamGrid.
/// </summary>
public sealed class SignUpCleanup(PendingSignUps pending, IRobustClient robust, IAccountRemover remover, AuditLog audit, TimeProvider time,
    ILogger<SignUpCleanup> log) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromHours(1);
    public static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, time, stoppingToken);
            using var timer = new PeriodicTimer(Period, time);
            do
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "The sign-ups waiting for email could not be cleaned up: {Message}", e.Message);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>One pass (the loop's work; tests call it directly). Passes run one at a time, so no avatar is deleted twice.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try { await PassAsync(ct); }
        finally { _one.Release(); }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        foreach (var principal in pending.OlderThan(SignUps.LinkLifetime))
        {
            try
            {
                var account = await robust.FindAccountByIdAsync(principal, ct);
                if (account is { UserLevel: -1 })
                {
                    await remover.DeleteAsync(principal, ct);
                    audit.Write(new AuditEntry("DreamGrid", null, "system", null, "signup-expired",
                        LogText.Clean($"{account.FirstName} {account.LastName}", 64), "ok"));
                }
                pending.Remove(principal);
            }
            catch (Exception e) when (e is StaffDirectoryUnavailableException or HttpRequestException or TaskCanceledException or IOException
                or InvalidOperationException or InvalidDataException && !ct.IsCancellationRequested)
            {
                log.LogWarning("Sign-up clean-up: {Principal} is kept for the next run: {Message}", principal, e.InnerException?.Message ?? e.Message);
            }
        }
    }
}
