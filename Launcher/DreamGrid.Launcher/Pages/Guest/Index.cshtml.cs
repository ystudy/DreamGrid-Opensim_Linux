using DreamGrid.Launcher.Core.Config;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>A signed-in guest's home (<c>AuthSchemes.GuestPolicy</c>, by the /Guest folder convention): who they are and how to connect with Firestorm.</summary>
public sealed class IndexModel(Func<LauncherSettings> settings) : PageModel
{
    public string DisplayName { get; private set; } = "";
    public string GridName { get; private set; } = "";

    /// <summary>The login address to paste into the viewer's grid manager.</summary>
    public string LoginUri { get; private set; } = "";

    public void OnGet()
    {
        var s = settings();
        DisplayName = User.Identity?.Name ?? "";
        GridName = s.GridName;
        LoginUri = GridEndpoints.LoginUri(s, HostAddress.Effective(s.Hostname));
    }
}
