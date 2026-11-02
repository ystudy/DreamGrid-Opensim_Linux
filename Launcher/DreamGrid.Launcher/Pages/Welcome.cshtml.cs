using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Splash;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

/// <summary>
/// The viewer splash page Robust's <c>welcome=</c> points at (spec 2026-11-01-viewer-splash-design §4.7). Open to anyone, from
/// any address (<c>AdminNetworkFilter</c> lets /welcome through), read-only, with no script; the layout is in the design's own
/// stylesheet. Viewers and proxies may keep it for a minute, as the server does.
/// </summary>
public sealed class WelcomeModel(SplashSnapshot snapshot, Func<LauncherSettings> settings) : PageModel
{
    public SplashView View { get; private set; } = null!;

    public LauncherSettings Settings { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        View = await snapshot.CurrentAsync(ct);
        Settings = settings();
        Response.Headers.CacheControl = "public, max-age=60";
    }
}
