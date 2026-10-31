using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DreamGrid.Launcher.Guests;

/// <summary>
/// Checks the guest cookie on every request (the guest scheme's <c>OnValidatePrincipal</c>). A cookie whose stamp isn't the
/// person's current one (<see cref="GuestStamps"/>: they were suspended) is refused and removed. Every
/// <see cref="StaffPrincipalValidator.RecheckEvery"/> the account is read again with the strict lookup: an account that is gone,
/// or at a level below 0 (suspended), ends the session. When Robust doesn't answer, or answers with anything unusable, nothing
/// changes and the check is tried again a minute later: a Robust failure never signs a guest out.
/// </summary>
public sealed class GuestPrincipalValidator(GuestStamps stamps, IRobustClient robust, TimeProvider time, ILogger<GuestPrincipalValidator> log)
{
    /// <summary>A Robust that doesn't answer must not hold every request for the HttpClient's whole timeout.</summary>
    private static readonly TimeSpan RobustTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    /// <summary>When the recheck of a person last failed (Robust didn't answer), so it is not tried on every request.</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _failedAt = new();

    public async Task ValidateAsync(CookieValidatePrincipalContext c)
    {
        if (c.Principal is not { } user || GuestSignIn.Read(user) is not { } claims || !StampIsCurrent(claims.Principal, claims.Stamp))
        {
            await RejectAsync(c);
            return;
        }

        var now = time.GetUtcNow();
        if (!StaffPrincipalValidator.IsDue(claims.Checked, now)
            || _failedAt.TryGetValue(claims.Principal, out var failed) && now - failed < RetryAfterFailure)
            return;

        var level = await CurrentLevelAsync(user.Identity?.Name, claims.Principal, c.HttpContext.RequestAborted);
        if (level is null)
        {
            _failedAt[claims.Principal] = now;
            return;
        }
        _failedAt.TryRemove(claims.Principal, out _);
        if (level < 0)
        {
            log.LogInformation("Guest session of first name {First} ended: the account is gone or suspended.", AvatarName.Parse(user.Identity?.Name).ForLog);
            await RejectAsync(c);
            return;
        }
        c.ReplacePrincipal(GuestSignIn.WithChecked(user, now));
        c.ShouldRenew = true;
    }

    private bool StampIsCurrent(Guid principal, string stamp) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stamps.StampOf(principal)), Encoding.UTF8.GetBytes(stamp));

    /// <summary>
    /// The account's level now (-1 when it is gone or is not the same account), or null when Robust didn't answer usefully
    /// (whatever the failure). Only the request's own abort is let through.
    /// </summary>
    private async Task<int?> CurrentLevelAsync(string? name, Guid principal, CancellationToken aborted)
    {
        var parsed = AvatarName.Parse(name);
        if (!parsed.IsComplete) return -1;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        cts.CancelAfter(RobustTimeout);
        try
        {
            var account = await robust.FindAccountAsync(parsed.First, parsed.Last, cts.Token);
            return account is null || account.PrincipalId != principal ? -1 : account.UserLevel;
        }
        catch (Exception e) when (!(e is OperationCanceledException && aborted.IsCancellationRequested))
        {
            log.LogDebug("Guest level check: the account service did not answer: {Message}", e.Message);
            return null;
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext c)
    {
        c.RejectPrincipal();
        await c.HttpContext.SignOutAsync(AuthSchemes.Guest);
    }
}
