using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>
/// How to connect with a viewer (spec 2026-10-31-sign-up-design §6): the grid's login address and the Firestorm steps. Open to
/// anyone, since the address isn't a secret; a signed-in resident is told which name to log in as.
/// </summary>
public sealed class ConnectModel(Func<LauncherSettings> settings) : PageModel
{
    public string GridName { get; private set; } = "";
    public string LoginUri { get; private set; } = "";
    public string DisplayName { get; private set; } = "";

    public void OnGet()
    {
        var s = settings();
        GridName = s.GridName;
        LoginUri = GridEndpoints.LoginUri(s, HostAddress.Effective(s.Hostname));
        DisplayName = GuestSignIn.Read(User) is not null ? User.Identity?.Name ?? "" : "";
    }
}
