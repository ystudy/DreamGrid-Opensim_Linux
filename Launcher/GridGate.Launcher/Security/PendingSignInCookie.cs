using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace GridGate.Launcher.Security;

/// <summary>What the pending two-factor sign-in cookies have in common: who it is and when it runs out.</summary>
public interface IPendingSignIn
{
    string First { get; }

    string Last { get; }

    /// <summary>Unix seconds; checked from the protected payload, not left to the cookie's own expiry.</summary>
    long Expires { get; }
}

/// <summary>
/// A data-protected cookie that carries a pending sign-in between the password and the code steps: HttpOnly, SameSite=Strict,
/// only sent to <c>path</c> and below, and good for <c>lifetime</c> by the time inside it. The staff and the guest door each
/// have their own name, path and purpose.
/// </summary>
/// <param name="accept">An extra check a readable payload must pass; null accepts every one.</param>
public sealed class PendingSignInCookie<T>(string name, string path, string purpose, TimeSpan lifetime, Func<T, bool>? accept = null)
    where T : class, IPendingSignIn
{
    /// <param name="make">Builds the payload from its expiry in Unix seconds.</param>
    public void Issue(HttpContext ctx, Func<long, T> make)
    {
        var now = Now(ctx);
        var value = Protector(ctx).Protect(JsonSerializer.Serialize(make((now + lifetime).ToUnixTimeSeconds())));
        ctx.Response.Cookies.Append(name, value, Options(ctx, now + lifetime));
    }

    /// <summary>The pending sign-in, or null when there is none, it can't be read, or its time is over.</summary>
    public T? Read(HttpContext ctx)
    {
        if (ctx.Request.Cookies[name] is not { Length: > 0 } value) return null;
        try
        {
            var pending = JsonSerializer.Deserialize<T>(Protector(ctx).Unprotect(value));
            if (pending is null || accept?.Invoke(pending) == false || pending.First.Length == 0 || pending.Last.Length == 0) return null;
            return Now(ctx).ToUnixTimeSeconds() < pending.Expires ? pending : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException or NotSupportedException)
        {
            return null;
        }
    }

    public void Delete(HttpContext ctx) => ctx.Response.Cookies.Delete(name, Options(ctx, null));

    private IDataProtector Protector(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose);

    private static DateTimeOffset Now(HttpContext ctx) => ctx.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();

    private CookieOptions Options(HttpContext ctx, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = path,
        Secure = ctx.Request.IsHttps,
        IsEssential = true,
        Expires = expires,
    };
}
