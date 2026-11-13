using GridGate.Launcher.Core.Pictures;
using Microsoft.Extensions.Logging;

namespace GridGate.Launcher.Core.Theme;

/// <summary>
/// The grid-wide look (spec 2026-11-01-viewer-splash-design §4.2): for now the grid's one logo, used by the splash page's
/// grid-name block and later by the other pages. <c>Data/theme/theme.json</c> names the current logo; the picture itself is
/// in the theme's <see cref="PictureStore"/>, checked and redrawn like any upload.
/// </summary>
public sealed class ThemeStore(LauncherPaths paths, PictureStore pictures, ILogger<ThemeStore>? log = null)
{
    private sealed record ThemeFile(string? Logo);

    private readonly JsonStateFile<ThemeFile> _file = new(Path.Combine(paths.Theme, "theme.json"), () => new(null), log);

    /// <summary>The current logo's picture name; null when none is set or its file is gone.</summary>
    public string? Logo() => _file.Load().Logo is { } name && pictures.Exists(name) ? name : null;

    /// <summary>Names the current logo (null for none). Deleting the old logo's picture is the caller's.</summary>
    public void SetLogo(string? name)
    {
        if (!_file.Save(new ThemeFile(name))) throw new IOException("The grid's logo could not be saved.");
    }
}
