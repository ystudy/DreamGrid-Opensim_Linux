using System.Security.Cryptography;
using System.Text;

namespace DreamGrid.Launcher.Security;

/// <summary>The setup link's token and the cookie that carries it once it has left the URL.</summary>
public static class SetupTokenCookie
{
    public const string Name = "dg-setup";

    /// <summary>Constant-time comparison; false when either side is missing.</summary>
    public static bool Matches(string? expected, string? given)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(given)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));
    }

    /// <summary>
    /// Lax, not Strict: a setup link clicked inside webmail or a chat app arrives cross-site, and the redirect that
    /// follows would otherwise drop the cookie. Every setup form also carries an antiforgery token.
    /// </summary>
    public static void Issue(HttpResponse response, string token) =>
        response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
        });
}
