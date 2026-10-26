using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace DreamGrid.Launcher.Security;

/// <summary>The admin cookie's claims, the same for the setup checklist and the sign-in page.</summary>
public static class AdminSignIn
{
    public const string PrincipalClaim = "principal";
    public static readonly TimeSpan Expiry = TimeSpan.FromHours(8); // sliding, set on the cookie scheme

    public static Task SignInAsync(HttpContext ctx, string first, string last, Guid principal)
    {
        var id = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, $"{first} {last}"),
            new Claim(PrincipalClaim, principal.ToString()),
            new Claim(ClaimTypes.Role, AuthSchemes.AdminRole),
        ], AuthSchemes.Admin);
        return ctx.SignInAsync(AuthSchemes.Admin, new ClaimsPrincipal(id));
    }
}
