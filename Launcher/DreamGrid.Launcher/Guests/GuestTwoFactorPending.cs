using System.Security.Cryptography;
using System.Text.Json;
using DreamGrid.Launcher.Core.OpenSim;
using Microsoft.AspNetCore.DataProtection;

namespace DreamGrid.Launcher.Guests;

/// <summary>A resident who passed the password step at the guest door and is still to give their two-factor code.</summary>
/// <param name="Expires">Unix seconds; checked here from the protected payload, not left to the cookie's own expiry.</param>
public sealed record GuestTwoFactorPendingSignIn(Guid Principal, string First, string Last, int Level, long Expires)
{
    public string Name => $"{First} {Last}";

    public RobustAccount Account => new(Principal, First, Last, Level, "");
}

/// <summary>
/// The <c>dg-guest-2fa</c> cookie between the guest door's password and code steps: data-protected (purpose
/// <c>DreamGrid.GuestTwoFactorPending.v1</c>), HttpOnly, SameSite=Strict, only sent to <c>/guest</c> and below, good for
/// <see cref="Lifetime"/> by the time inside it. It is no sign-in: nothing but <c>/guest/sign-in/code</c> reads it.
/// </summary>
public static class GuestTwoFactorPending
{
    public const string CookieName = "dg-guest-2fa";
    public const string CookiePath = "/guest";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const string Purpose = "DreamGrid.GuestTwoFactorPending.v1";

    public static void Issue(HttpContext ctx, RobustAccount account)
    {
        var now = Now(ctx);
        var pending = new GuestTwoFactorPendingSignIn(account.PrincipalId, account.FirstName, account.LastName, account.UserLevel,
            (now + Lifetime).ToUnixTimeSeconds());
        ctx.Response.Cookies.Append(CookieName, Protector(ctx).Protect(JsonSerializer.Serialize(pending)), Options(ctx, now + Lifetime));
    }

    /// <summary>The pending sign-in, or null when there is none, it can't be read, or its five minutes are over.</summary>
    public static GuestTwoFactorPendingSignIn? Read(HttpContext ctx)
    {
        if (ctx.Request.Cookies[CookieName] is not { Length: > 0 } value) return null;
        try
        {
            var pending = JsonSerializer.Deserialize<GuestTwoFactorPendingSignIn>(Protector(ctx).Unprotect(value));
            if (pending is null || pending.First.Length == 0 || pending.Last.Length == 0) return null;
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
