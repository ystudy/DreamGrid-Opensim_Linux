using System.Collections.Concurrent;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DreamGrid.Launcher.Security;

/// <summary>
/// Checks the staff cookie on every request (the cookie scheme's <c>OnValidatePrincipal</c>). A cookie without a session id
/// and stamp, or whose session was ended or whose stamp is old (signed out everywhere), is refused and removed. Every
/// <see cref="RecheckEvery"/> the account is read again from Robust: a lower role than the cookie's ends all the person's
/// sessions (audited as <c>session-ended</c>, "role changed"); the same or a higher role renews the check time. When Robust
/// doesn't answer nothing changes and the check is tried again a minute later: a Robust failure is never a demotion. The
/// owner named in setup keeps the owner role whatever Robust says, so is never read again.
/// </summary>
public sealed class StaffPrincipalValidator(StaffSessions sessions, IRobustClient robust, Func<LauncherSettings> settings,
    AuditLog audit, TimeProvider time, ILogger<StaffPrincipalValidator> log)
{
    public static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(5);

    /// <summary>A Robust that doesn't answer must not hold every request for the HttpClient's whole timeout.</summary>
    private static readonly TimeSpan RobustTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    /// <summary>When the recheck of a person last failed (Robust didn't answer), so it is not tried on every request.</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _failedAt = new();

    public async Task ValidateAsync(CookieValidatePrincipalContext c)
    {
        if (c.Principal is not { } user || StaffSignIn.Read(user) is not { Role: not StaffRole.None } claims
            || !sessions.IsValid(claims.Principal, claims.SessionId, claims.Stamp))
        {
            await RejectAsync(c);
            return;
        }

        var now = time.GetUtcNow();
        if (now - claims.Checked >= RecheckEvery
            && !(_failedAt.TryGetValue(claims.Principal, out var failed) && now - failed < RetryAfterFailure))
        {
            var role = await CurrentRoleAsync(user.Identity?.Name, claims.Principal, c.HttpContext.RequestAborted);
            if (role is null)
            {
                _failedAt[claims.Principal] = now;
            }
            else
            {
                _failedAt.TryRemove(claims.Principal, out _);
                if (role < claims.Role)
                {
                    sessions.EndAll(claims.Principal);
                    audit.Write(new AuditEntry(LogText.Clean(user.Identity?.Name, 64), claims.Principal, claims.Role.AuditName(),
                        c.HttpContext.Connection.RemoteIpAddress?.ToString(), "session-ended", "role changed", "ok"));
                    log.LogInformation("Staff sessions of {First} ended: their role is now {Role}.",
                        AvatarName.Parse(user.Identity?.Name).ForLog, role.Value.AuditName());
                    await RejectAsync(c);
                    return;
                }
                c.ReplacePrincipal(StaffSignIn.WithChecked(user, now));
                c.ShouldRenew = true;
            }
        }
        sessions.Touch(claims.Principal, claims.SessionId);
    }

    /// <summary>The account's role now, or null when Robust didn't answer. An account that is gone has no role.</summary>
    private async Task<StaffRole?> CurrentRoleAsync(string? name, Guid principal, CancellationToken aborted)
    {
        var s = settings();
        if (s.Setup.Owner?.PrincipalId == principal) return StaffRole.Owner;
        var parsed = AvatarName.Parse(name);
        if (!parsed.IsComplete) return StaffRole.None;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        cts.CancelAfter(RobustTimeout);
        try
        {
            var account = await robust.GetAccountAsync(parsed.First, parsed.Last, cts.Token);
            return account is null || account.PrincipalId != principal ? StaffRole.None : AdminRule.RoleOf(s, account);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            log.LogDebug("Staff level check: the account service did not answer: {Message}", e.Message);
            return null;
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext c)
    {
        c.RejectPrincipal();
        await c.HttpContext.SignOutAsync(AuthSchemes.Admin);
    }
}
