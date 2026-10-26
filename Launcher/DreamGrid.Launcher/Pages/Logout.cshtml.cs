using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

/// <summary>Signing out is a POST (with the antiforgery token), so a link or an image cannot do it.</summary>
public sealed class LogoutModel : PageModel
{
    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(AuthSchemes.Admin);
        return Redirect("/login");
    }
}
