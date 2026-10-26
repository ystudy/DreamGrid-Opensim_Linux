using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Advanced;

/// <summary>
/// The launcher's private MariaDB: read-only in version 1. The passwords reach the page only after a "Show" POST,
/// and "Test connection" opens a connection as robustuser.
/// </summary>
public sealed class DatabaseModel(Func<LauncherSettings> settings) : PageModel
{
    public const string Host = "127.0.0.1";
    public const string Masked = "••••";

    public LauncherSettings Settings { get; private set; } = null!;
    public bool Revealed { get; private set; }

    /// <summary>Set after "Test connection": null message means connected.</summary>
    public (bool Tested, string? Error) Test { get; private set; }

    public void OnGet() => Settings = settings();

    public void OnPostShow()
    {
        Settings = settings();
        Revealed = true;
    }

    public async Task OnPostTestAsync()
    {
        Settings = settings();
        Test = (true, await DatabaseCheck.ConnectAsRobustUserAsync(Settings, HttpContext.RequestAborted));
    }
}
