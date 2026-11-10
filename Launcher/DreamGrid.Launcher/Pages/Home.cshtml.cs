using System.Security.Claims;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

/// <summary>
/// The public front page: the grid's name and the ways in. Anyone not signed in as staff gets it at "/" (SetupGate hands the
/// bare root to this page); it is open to every address (<c>AdminNetworkFilter</c>) and, with the other guest pages, does not
/// exist on a Region Server or a Connected Server (<c>GuestSideOnly</c>).
/// </summary>
public sealed class HomeModel(Func<LauncherSettings> settings) : PageModel
{
    public string GridName { get; private set; } = "";

    /// <summary>The legal pages the operator has written, in link order (Terms, Privacy, Cookies, Contact).</summary>
    public IReadOnlyList<string> LegalPages { get; private set; } = [];

    /// <summary>The icon for a legal page's button.</summary>
    public static string LegalIcon(string page) => page switch
    {
        Core.Guests.LegalPages.Terms => "document",
        Core.Guests.LegalPages.Privacy => "lock",
        Core.Guests.LegalPages.Cookies => "cookie",
        Core.Guests.LegalPages.Contact => "mail",
        _ => "link",
    };

    public async Task OnGetAsync()
    {
        var s = settings();
        GridName = s.GridName;
        LegalPages = [.. Core.Guests.LegalPages.All.Where(p => Core.Guests.LegalPages.Get(s, p) is not null)];
        // The sign-in card posts to /guest/sign-in, which sees the guest cookie's user (or nobody), never the staff one. Its
        // antiforgery token is made for HttpContext.User, so the page takes that same user first: otherwise a staff member
        // looking at the guest side (Guest view) gets a 400 from the card. The header asks the staff cookie itself.
        var guest = await HttpContext.AuthenticateAsync(AuthSchemes.Guest);
        HttpContext.User = guest.Succeeded ? guest.Principal! : new ClaimsPrincipal(new ClaimsIdentity());
    }
}
