using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Splash;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Splash;

/// <summary>
/// The splash page as it will look once published (plan 12c): the draft, or the published design, or the built-in one, drawn
/// exactly as /welcome draws it, at the browser window's size. Owner and admins only (the /Splash folder), never kept.
/// </summary>
public sealed class PreviewModel(SplashSnapshot snapshot, Func<LauncherSettings> settings) : PageModel
{
    public SplashView View { get; private set; } = null!;

    public LauncherSettings Settings { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        View = await snapshot.WorkingAsync(ct);
        Settings = settings();
        Response.Headers.CacheControl = "no-store";
    }
}
