using System.Globalization;
using System.Security.Claims;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using Microsoft.AspNetCore.Authentication;

namespace DreamGrid.Launcher.Security;

/// <summary>What a staff cookie carries, read back by <see cref="StaffSignIn.Read"/>.</summary>
/// <param name="Checked">When the account's level was last read from Robust (at sign-in, then at most every 5 minutes).</param>
public sealed record StaffClaims(Guid Principal, string SessionId, string Stamp, DateTimeOffset Checked, StaffRole Role);

/// <summary>
/// The staff cookie's claims, the same for the setup checklist, the sign-in page and the tests: the name, the principal id,
/// the role claims (<see cref="AuthSchemes"/>), the session id and the person's stamp from <see cref="StaffSessions"/>, and
/// when the level was last checked. <see cref="StaffPrincipalValidator"/> checks them on every request.
/// </summary>
public static class StaffSignIn
{
    public const string PrincipalClaim = "principal";
    public const string SessionClaim = "sid";
    public const string StampClaim = "stamp";
    /// <summary>Unix seconds.</summary>
    public const string CheckedClaim = "checked";
    /// <summary>Present when two-factor must be set up before anything else.</summary>
    public const string EnrolClaim = "enrol";

    public static readonly TimeSpan Expiry = TimeSpan.FromHours(8); // sliding, set on the cookie scheme

    /// <summary>
    /// Starts a session for <paramref name="account"/> and issues the cookie for <paramref name="role"/>. Returns the cookie's
    /// principal, for a page rendered in the same request (<c>HttpContext.User</c> is not changed here).
    /// </summary>
    public static async Task<ClaimsPrincipal> SignInAsync(HttpContext ctx, RobustAccount account, StaffRole role, bool mustEnrolTwoFactor = false)
    {
        if (role == StaffRole.None) throw new ArgumentOutOfRangeException(nameof(role), "Only staff can sign in here.");
        var services = ctx.RequestServices;
        var sessions = services.GetRequiredService<StaffSessions>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        // Signing in again in the same browser replaces its session rather than leaving the old one listed.
        if (Read(ctx.User) is { } previous)
            sessions.End(previous.Principal, previous.SessionId);
        var session = sessions.Start(account.PrincipalId, ctx.Connection.RemoteIpAddress?.ToString(), ctx.Request.Headers.UserAgent.ToString());

        List<Claim> claims =
        [
            new(ClaimTypes.Name, $"{account.FirstName} {account.LastName}"),
            new(PrincipalClaim, account.PrincipalId.ToString()),
            // Antiforgery binds its tokens to this claim when present, and otherwise to every claim, including the session id
            // and the check time that the 5-minute recheck renews: without it a form open across a recheck would be refused.
            new(ClaimTypes.NameIdentifier, account.PrincipalId.ToString()),
            .. RoleClaims(role).Select(r => new Claim(ClaimTypes.Role, r)),
            new(SessionClaim, session.Id),
            new(StampClaim, sessions.StampOf(account.PrincipalId)),
            new(CheckedClaim, UnixSeconds(now)),
        ];
        if (mustEnrolTwoFactor) claims.Add(new Claim(EnrolClaim, "1"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Admin));
        await ctx.SignInAsync(AuthSchemes.Admin, principal);
        return principal;
    }

    /// <summary>Where a person lands after signing in: a supervisor has no dashboard, so the People page.</summary>
    public static string Home(StaffRole role) => role == StaffRole.Supervisor ? "/people" : "/";

    /// <summary>The role claims for <paramref name="role"/>: each role carries the ones below it.</summary>
    public static IEnumerable<string> RoleClaims(StaffRole role) => role switch
    {
        StaffRole.Owner => [AuthSchemes.OwnerRole, AuthSchemes.AdminRole, AuthSchemes.StaffRole],
        StaffRole.Admin => [AuthSchemes.AdminRole, AuthSchemes.StaffRole],
        StaffRole.Supervisor => [AuthSchemes.StaffRole],
        _ => [],
    };

    /// <summary>The highest role <paramref name="user"/>'s claims give.</summary>
    public static StaffRole RoleOf(ClaimsPrincipal user) =>
        user.IsInRole(AuthSchemes.OwnerRole) ? StaffRole.Owner
        : user.IsInRole(AuthSchemes.AdminRole) ? StaffRole.Admin
        : user.IsInRole(AuthSchemes.StaffRole) ? StaffRole.Supervisor
        : StaffRole.None;

    /// <summary>The session claims, or null when any is missing or malformed (an old-style or foreign cookie).</summary>
    public static StaffClaims? Read(ClaimsPrincipal user)
    {
        if (!Guid.TryParse(user.FindFirstValue(PrincipalClaim), out var principal)) return null;
        if (user.FindFirstValue(SessionClaim) is not { Length: > 0 } sid || user.FindFirstValue(StampClaim) is not { Length: > 0 } stamp)
            return null;
        // A missing or unreadable time only means the level is read again at once.
        var at = long.TryParse(user.FindFirstValue(CheckedClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(Math.Min(seconds, DateTimeOffset.MaxValue.ToUnixTimeSeconds()))
            : DateTimeOffset.UnixEpoch;
        return new StaffClaims(principal, sid, stamp, at, RoleOf(user));
    }

    /// <summary>A copy of <paramref name="user"/> with the level check moved to <paramref name="now"/>.</summary>
    public static ClaimsPrincipal WithChecked(ClaimsPrincipal user, DateTimeOffset now)
    {
        var identity = new ClaimsIdentity(user.Claims.Where(c => c.Type != CheckedClaim), AuthSchemes.Admin);
        identity.AddClaim(new Claim(CheckedClaim, UnixSeconds(now)));
        return new ClaimsPrincipal(identity);
    }

    private static string UnixSeconds(DateTimeOffset t) => t.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
