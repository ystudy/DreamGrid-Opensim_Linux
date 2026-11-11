using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Resident;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

/// <summary>
/// The language picker's target: remembers the language a visitor chose (in the <c>dg-lang</c> cookie, and for a signed-in guest
/// also in <see cref="ResidentLanguages"/>) and goes back to the page they were on. A POST only (the antiforgery token is
/// required); anyone may use it. The way back is a path on this site, never another address.
/// </summary>
public sealed class LanguageModel(OfferedLanguages offered, ResidentLanguages saved) : PageModel
{
    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync(string? code, string? returnUrl)
    {
        if (Core.Languages.Match(code, offered.Current()) is not { } chosen)
            return BadRequest();

        LanguagePick.SetCookie(HttpContext, chosen);
        if ((await HttpContext.AuthenticateAsync(AuthSchemes.Guest)).Principal is { } who && GuestSignIn.Read(who)?.Principal is { } id)
            saved.Set(id, chosen);

        Response.Headers.Location = Safe(returnUrl);
        return StatusCode(StatusCodes.Status303SeeOther);
    }

    private string Safe(string? url) =>
        url is { Length: > 0 } && Url.IsLocalUrl(url) && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";
}
