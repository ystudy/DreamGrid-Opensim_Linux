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

    public static void Issue(HttpResponse response, string token) =>
        response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });
}
