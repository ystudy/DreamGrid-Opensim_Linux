using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Core.Splash;
using GridGate.Launcher.Splash;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Splash;

/// <summary>
/// The splash page editor (spec 2026-11-01-viewer-splash-design §4.1; owner and admins, by the /Splash folder; 404 where there
/// is no guest side). The page carries the working design's blocks as the public page draws them; <c>splash-editor.js</c>
/// does the rest through /api/splash, and fetches the blocks again from <see cref="OnGetBlocksAsync"/> after each save.
/// </summary>
public sealed class IndexModel(SplashSnapshot snapshot, Func<LauncherSettings> settings) : PageModel
{
    /// <summary>The block shelf, in the order of <see cref="SplashBlockKind"/>, with each block's icon in img/icons.svg.</summary>
    public static readonly IReadOnlyList<(SplashBlockKind Kind, string Label, string Icon)> Shelf =
    [
        (SplashBlockKind.Text, "Text", "text"),
        (SplashBlockKind.Picture, "Picture", "image"),
        (SplashBlockKind.GridName, "Grid name and logo", "id-card"),
        (SplashBlockKind.Status, "Live status", "activity"),
        (SplashBlockKind.News, "News", "news"),
        (SplashBlockKind.Places, "Places to go", "map-pin"),
        (SplashBlockKind.Links, "Links", "link"),
    ];

    public SplashView View { get; private set; } = null!;

    public LauncherSettings Settings { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        View = await snapshot.WorkingAsync(ct);
        Settings = settings();
        Response.Headers.CacheControl = "no-store";
    }

    /// <summary>The working design's blocks alone, for the canvas after a save.</summary>
    public async Task<IActionResult> OnGetBlocksAsync(CancellationToken ct)
    {
        await OnGetAsync(ct);
        return Partial("_SplashBlocks", new SplashBlocksModel(View, Settings));
    }
}
