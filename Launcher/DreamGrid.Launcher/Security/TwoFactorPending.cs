using System.Security.Cryptography;
using System.Text.Json;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using Microsoft.AspNetCore.DataProtection;

namespace DreamGrid.Launcher.Security;

/// <summary>Who passed the password step and is still to give their two-factor code.</summary>
/// <param name="Expires">Unix seconds; checked here from the protected payload, not left to the cookie's own expiry.</param>
public sealed record TwoFactorPendingSignIn(Guid Principal, string First, string Last, int Level, StaffRole Role, string? ReturnUrl, long Expires)
{
    public string Name => $"{First} {Last}";

    public RobustAccount Account => new(Principal, First, Last, Level, "");
}

/// <summary>
/// The <c>dg-2fa</c> cookie between the password and the code steps: data-protected (purpose <c>DreamGrid.TwoFactorPending.v1</c>),
/// HttpOnly, SameSite=Strict, only sent to <c>/login</c> and below, and good for <see cref="Lifetime"/> by the time inside it.
/// It carries the account (principal, name, level), the role and the return address, so the code step needs no second Robust
/// lookup. It is no sign-in: nothing but <c>/login/code</c> reads it.
/// </summary>
public static class TwoFactorPending
{
    public const string CookieName = "dg-2fa";
    public const string CookiePath = "/login";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const string Purpose = "DreamGrid.TwoFactorPending.v1";

    public static void Issue(HttpContext ctx, RobustAccount account, StaffRole role, string? returnUrl)
    {
        var now = Now(ctx);
        var pending = new TwoFactorPendingSignIn(account.PrincipalId, account.FirstName, account.LastName, account.UserLevel, role,
            returnUrl, (now + Lifetime).ToUnixTimeSeconds());
        var value = Protector(ctx).Protect(JsonSerializer.Serialize(pending));
        ctx.Response.Cookies.Append(CookieName, value, Options(ctx, now + Lifetime));
    }

    /// <summary>The pending sign-in, or null when there is none, it can't be read, or its five minutes are over.</summary>
    public static TwoFactorPendingSignIn? Read(HttpContext ctx)
    {
        if (ctx.Request.Cookies[CookieName] is not { Length: > 0 } value) return null;
        try
        {
            var pending = JsonSerializer.Deserialize<TwoFactorPendingSignIn>(Protector(ctx).Unprotect(value));
            if (pending is null || pending.Role == StaffRole.None || pending.First.Length == 0 || pending.Last.Length == 0) return null;
            return Now(ctx).ToUnixTimeSeconds() < pending.Expires ? pending : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException or NotSupportedException)
        {
            return null;
        }
    }

    public static void Delete(HttpContext ctx) => ctx.Response.Cookies.Delete(CookieName, Options(ctx, null));

    private static IDataProtector Protector(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    private static DateTimeOffset Now(HttpContext ctx) => ctx.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();

    private static CookieOptions Options(HttpContext ctx, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        Secure = ctx.Request.IsHttps,
        IsEssential = true,
        Expires = expires,
    };
}
