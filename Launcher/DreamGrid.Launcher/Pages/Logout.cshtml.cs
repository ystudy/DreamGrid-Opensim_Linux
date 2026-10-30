using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

/// <summary>
/// Signing out is a POST (with the antiforgery token), so a link or an image cannot do it. It ends this browser's session
/// only; the Sessions page signs out everywhere.
/// </summary>
public sealed class LogoutModel(StaffSessions sessions) : PageModel
{
    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync()
    {
        if (StaffSignIn.Read(User) is { } claims)
            sessions.End(claims.Principal, claims.SessionId);
        await HttpContext.SignOutAsync(AuthSchemes.Admin);
        return Redirect("/login");
    }
}
