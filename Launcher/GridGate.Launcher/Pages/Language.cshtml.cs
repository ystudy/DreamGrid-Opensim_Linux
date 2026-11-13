using System.Security.Claims;
using GridGate.Launcher.Core;
using GridGate.Launcher.Guests;
using GridGate.Launcher.Resident;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages;

/// <summary>
/// The language picker's target: remembers the language a visitor chose (in the <c>dg-lang</c> cookie, and for a signed-in guest
/// also in <see cref="ResidentLanguages"/>) and goes back to the page they were on. A POST only (the antiforgery token is
/// required); anyone may use it. The way back is a path on this site, never another address.
/// </summary>
/// <remarks>
/// The picker sits in the guest layout of every resident page, and its antiforgery token is the one that page made, for the
/// page's own <c>HttpContext.User</c>: the guest on /guest/* (the Guest policy), the guest or nobody on /home (which sets it
/// itself), and the default (staff) scheme's user elsewhere. This page has no policy, so the automatic check would compare the
/// token with the staff scheme's user only and refuse a signed-in guest. Instead it checks the token itself against each user
/// this browser's own cookies give (staff scheme, guest scheme, nobody); the cookie and form token pair is still required.
/// </remarks>
[IgnoreAntiforgeryToken]
public sealed class LanguageModel(OfferedLanguages offered, ResidentLanguages saved, IAntiforgery antiforgery) : PageModel
{
    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync(string? code, string? returnUrl)
    {
        var guest = (await HttpContext.AuthenticateAsync(AuthSchemes.Guest)).Principal;
        if (!await TokenIsValidAsync(guest))
            return BadRequest();

        if (Core.Languages.Match(code, offered.Current()) is not { } chosen)
            return BadRequest();

        LanguagePick.SetCookie(HttpContext, chosen);
        if (guest is not null && GuestSignIn.Read(guest)?.Principal is { } id)
            saved.Set(id, chosen);

        Response.Headers.Location = Safe(returnUrl);
        return StatusCode(StatusCodes.Status303SeeOther);
    }

    private async Task<bool> TokenIsValidAsync(ClaimsPrincipal? guest)
    {
        var own = HttpContext.User;
        try
        {
            foreach (var who in new[] { own, guest, new ClaimsPrincipal(new ClaimsIdentity()) })
            {
                if (who is null) continue;
                HttpContext.User = who;
                if (await antiforgery.IsRequestValidAsync(HttpContext)) return true;
            }
            return false;
        }
        finally
        {
            HttpContext.User = own;
        }
    }

    /// <summary>
    /// <paramref name="url"/> when it is a path on this site in printable ASCII (Kestrel refuses a Location header with control
    /// or non-ASCII characters, which <see cref="IUrlHelper.IsLocalUrl"/> lets through), else the front page.
    /// </summary>
    private string Safe(string? url) =>
        url is { Length: > 0 } && url.All(ch => ch is >= ' ' and <= '~') && Url.IsLocalUrl(url)
            && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";
}
