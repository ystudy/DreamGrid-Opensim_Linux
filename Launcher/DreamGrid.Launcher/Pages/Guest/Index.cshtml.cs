using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>A signed-in guest's home (<c>AuthSchemes.GuestPolicy</c>, by the /Guest folder convention): who they are and how to connect with Firestorm.</summary>
public sealed class IndexModel(Func<LauncherSettings> settings, RegionRights rights, ResidentTwoFactor residents) : PageModel
{
    /// <summary>Whether the page links to the resident's two-factor page (<see cref="ResidentTwoFactor.PageShown"/>).</summary>
    public bool TwoFactorLink { get; private set; }

    public string DisplayName { get; private set; } = "";
    public string GridName { get; private set; } = "";

    /// <summary>The login address to paste into the viewer's grid manager.</summary>
    public string LoginUri { get; private set; } = "";

    /// <summary>Whether the guest has rights over any region now (the page links to their regions).</summary>
    public bool OwnsRegions { get; private set; }

    public void OnGet()
    {
        OwnsRegions = GuestSignIn.Read(User) is { } guest && rights.OwnedBy(guest.Principal).Count > 0;
        var s = settings();
        TwoFactorLink = GuestSignIn.Read(User) is { } me && ResidentTwoFactor.PageShown(s, residents.Store.StateOf(me.Principal));
        DisplayName = User.Identity?.Name ?? "";
        GridName = s.GridName;
        LoginUri = GridEndpoints.LoginUri(s, HostAddress.Effective(s.Hostname));
    }
}
