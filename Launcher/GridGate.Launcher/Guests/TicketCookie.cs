using System.Globalization;
using Microsoft.AspNetCore.DataProtection;

namespace GridGate.Launcher.Guests;

/// <summary>
/// The <c>dg-ticket</c> cookie (design 2026-11-05-support-tickets, "After sending"): which one ticket a private link opened, for
/// 8 hours from its last use. Protected with data protection, so it can't be made or changed by hand, and separate from the guest
/// sign-in cookie, so it opens that ticket and nothing else.
/// </summary>
public sealed class TicketCookie(IDataProtectionProvider protection, TimeProvider time)
{
    public const string Name = "dg-ticket";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    private readonly IDataProtector _protector = protection.CreateProtector("GridGate.Launcher.TicketCookie.v1");

    /// <summary>Sets (or renews) the cookie for <paramref name="ticket"/>.</summary>
    public void Set(HttpContext ctx, Guid ticket)
    {
        var until = time.GetUtcNow() + Lifetime;
        var value = _protector.Protect(ticket.ToString("N") + "|" + until.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        ctx.Response.Cookies.Append(Name, value, new CookieOptions
        {
            HttpOnly = true,
            // Lax: the private link arrives from an email, a different site; the cookie it sets must come along on the redirect after.
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/support",
            IsEssential = true,
        });
    }

    /// <summary>The ticket the cookie names, while it hasn't run out; null otherwise.</summary>
    public Guid? Read(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(Name, out var raw) || string.IsNullOrEmpty(raw)) return null;
        try
        {
            var parts = _protector.Unprotect(raw).Split('|');
            if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var id)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var until)
                || DateTimeOffset.FromUnixTimeSeconds(until) <= time.GetUtcNow()) return null;
            return id;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
