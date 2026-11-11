using System.Security.Claims;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace DreamGrid.Launcher.Resident;

/// <summary>Picks the language a resident page is shown in.</summary>
public static class LanguagePick
{
    public const string Cookie = "dg-lang";

    private const int MostAcceptEntries = 10;

    private static readonly TimeSpan CookieLife = TimeSpan.FromDays(365);

    /// <summary>Remembers <paramref name="code"/> (already matched against the offered list) in the language cookie, for a year.</summary>
    public static void SetCookie(HttpContext ctx, string code) =>
        ctx.Response.Cookies.Append(Cookie, code, new CookieOptions
        {
            MaxAge = CookieLife,
            HttpOnly = true,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            IsEssential = true,
            Path = "/",
        });

    /// <summary>
    /// The offered language for this request. The welcome page uses only the <c>lang=</c> query, then the default. Other pages use
    /// the cookie, then the signed-in guest's saved language, then the browser's Accept-Language (highest q first), then the default.
    /// Every candidate, saved values included, goes through <see cref="Languages.Match"/>, so junk is skipped and the result is
    /// always a language <see cref="Core.Languages.Culture"/> knows.
    /// </summary>
    public static string For(HttpContext ctx, OfferedLanguages offeredLanguages, ResidentLanguages saved, ClaimsPrincipal? guest, bool welcome)
    {
        var offered = offeredLanguages.Current();
        string? Try(string? tag) => Core.Languages.Match(tag, offered);

        if (welcome)
        {
            if (Try(ctx.Request.Query["lang"].ToString()) is { } q) return q;
        }
        else
        {
            if (ctx.Request.Cookies.TryGetValue(Cookie, out var cookie) && Try(cookie) is { } c) return c;
            if (guest is not null && GuestSignIn.Read(guest)?.Principal is { } who && Try(saved.Get(who)) is { } mine) return mine;
            if (StringWithQualityHeaderValue.TryParseList(ctx.Request.Headers.AcceptLanguage, out var accepted))
            {
                // Parsing skips what it can't read (an entry with q=abc); the cap keeps a hostile header cheap.
                foreach (var entry in accepted.Take(MostAcceptEntries).Where(e => e.Quality is null or > 0).OrderByDescending(e => e.Quality ?? 1))
                    if (Try(entry.Value.Value) is { } a) return a;
            }
        }
        return offeredLanguages.Default();
    }
}
