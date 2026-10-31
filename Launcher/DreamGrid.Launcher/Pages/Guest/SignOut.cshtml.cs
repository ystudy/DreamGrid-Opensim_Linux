using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>
/// Signing out of the guest pages is a POST (with the antiforgery token), so a link or an image cannot do it. It removes the
/// guest cookie only; a staff cookie in the same browser stays.
/// </summary>
public sealed class SignOutModel : PageModel
{
    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(AuthSchemes.Guest);
        return Redirect("/");
    }
}
