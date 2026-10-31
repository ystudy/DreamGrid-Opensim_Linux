using System.Globalization;
using System.Security.Claims;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Authentication;

namespace DreamGrid.Launcher.Guests;

/// <summary>
/// The guest cookie's claims: the name, the principal id, the role <see cref="AuthSchemes.GuestRole"/>, the person's stamp
/// from <see cref="GuestStamps"/> and when the level was last checked. <see cref="GuestPrincipalValidator"/> checks them on every
/// request. The claim types are the staff cookie's, but the cookie is another one (<see cref="AuthSchemes.Guest"/>), so a staff
/// cookie never opens a guest page and the other way round.
/// </summary>
public static class GuestSignIn
{
    /// <summary>Issues the guest cookie for <paramref name="account"/>: a session cookie, 8 hours sliding (set on the scheme).</summary>
    public static async Task SignInAsync(HttpContext ctx, RobustAccount account)
    {
        var services = ctx.RequestServices;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        List<Claim> claims =
        [
            new(ClaimTypes.Name, $"{account.FirstName} {account.LastName}"),
            new(StaffSignIn.PrincipalClaim, account.PrincipalId.ToString()),
            // Antiforgery binds its tokens to this claim when present, and otherwise to every claim, including the check time
            // that the recheck renews: without it a form open across a recheck would be refused.
            new(ClaimTypes.NameIdentifier, account.PrincipalId.ToString()),
            new(ClaimTypes.Role, AuthSchemes.GuestRole),
            new(StaffSignIn.StampClaim, services.GetRequiredService<GuestStamps>().StampOf(account.PrincipalId)),
            new(StaffSignIn.CheckedClaim, UnixSeconds(now)),
        ];
        await ctx.SignInAsync(AuthSchemes.Guest, new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Guest)));
    }

    /// <summary>The cookie's claims, or null when any is missing or malformed (a foreign cookie).</summary>
    public static (Guid Principal, string Stamp, DateTimeOffset Checked)? Read(ClaimsPrincipal user)
    {
        if (!user.IsInRole(AuthSchemes.GuestRole) || !Guid.TryParse(user.FindFirstValue(StaffSignIn.PrincipalClaim), out var principal)) return null;
        if (user.FindFirstValue(StaffSignIn.StampClaim) is not { Length: > 0 } stamp) return null;
        // A missing or unreadable time only means the level is read again at once.
        var at = long.TryParse(user.FindFirstValue(StaffSignIn.CheckedClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(Math.Min(seconds, DateTimeOffset.MaxValue.ToUnixTimeSeconds()))
            : DateTimeOffset.UnixEpoch;
        return (principal, stamp, at);
    }

    /// <summary>A copy of <paramref name="user"/> with the level check moved to <paramref name="now"/>.</summary>
    public static ClaimsPrincipal WithChecked(ClaimsPrincipal user, DateTimeOffset now)
    {
        var identity = new ClaimsIdentity(user.Claims.Where(c => c.Type != StaffSignIn.CheckedClaim), AuthSchemes.Guest);
        identity.AddClaim(new Claim(StaffSignIn.CheckedClaim, UnixSeconds(now)));
        return new ClaimsPrincipal(identity);
    }

    private static string UnixSeconds(DateTimeOffset t) => t.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
