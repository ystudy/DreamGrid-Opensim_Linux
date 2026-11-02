using System.Globalization;
using System.Text;

namespace DreamGrid.Launcher.Core.Splash;

/// <summary>
/// The stylesheet a splash design is drawn with (spec 2026-11-01-viewer-splash-design §6). Blocks sit at proportions of a
/// page box that keeps the design's shape; the box follows the window between <see cref="MinWidth"/> and
/// <see cref="MaxWidth"/>, so the page shrinks until its smallest text would be hard to read and then scrolls. Text sizes are
/// in container units (cqw) of that box, written after a px size that a browser without container units keeps instead.
/// Everything goes in a file rather than style attributes, so the site's <c>style-src 'self'</c> holds.
/// </summary>
public static class SplashCss
{
    /// <summary>The smallest on-screen text size the shrinking stops at.</summary>
    public const int ReadableTextPx = 12;

    /// <summary>The page box's narrowest width in CSS px: where the smallest text would be <see cref="ReadableTextPx"/>, never below half the design width, never above it.</summary>
    public static int MinWidth(SplashDesign d)
    {
        var smallest = d.Blocks.Where(HasText).Select(b => b.Font.Size).DefaultIfEmpty(int.MaxValue).Min();
        var readable = smallest == int.MaxValue ? 0 : (int)Math.Ceiling((double)ReadableTextPx / smallest * d.Width);
        return Math.Clamp(readable, d.Width / 2, d.Width);
    }

    /// <summary>The page box's widest width: one and a half times the design width.</summary>
    public static int MaxWidth(SplashDesign d) => d.Width * 3 / 2;

    public static string ClassOf(SplashBlock b) => "b-" + b.Id.ToString(CultureInfo.InvariantCulture);

    /// <param name="pictureUrl">Turns a picture-store name into its address on the page.</param>
    public static string Build(SplashDesign d, Func<string, string> pictureUrl)
    {
        var css = new StringBuilder();
        var bg = d.Background;
        var pageBackground = Background(bg.Colour, bg.Picture, bg.FocusX, bg.FocusY, bg.Darken, pictureUrl);
        css.Append("body.splash{").Append(pageBackground).Append("}\n");
        css.Append(".splash-page{width:clamp(").Append(MinWidth(d)).Append("px, 100vw, ").Append(MaxWidth(d)).Append("px);")
            .Append("aspect-ratio:").Append(d.Width).Append(" / ").Append(d.Height).Append(";container-type:inline-size;}\n");

        foreach (var b in d.Blocks)
        {
            css.Append('.').Append(ClassOf(b)).Append('{')
                .Append("left:").Append(Pct(b.X, d.Width)).Append(";top:").Append(Pct(b.Y, d.Height))
                .Append(";width:").Append(Pct(b.W, d.Width)).Append(";height:").Append(Pct(b.H, d.Height))
                .Append(";z-index:").Append(b.Layer).Append(';')
                .Append("background-color:").Append(Rgba(b.Colour, 1 - b.SeeThrough / 100.0)).Append(';');
            if (b.Background is { } pic)
                css.Append(Picture(pic, 50, 50, 0, pictureUrl));
            if (b.Rounded) css.Append("border-radius:12px;border-radius:").Append(Cqw(12, d.Width)).Append(';');
            css.Append("padding:").Append(b.Padding).Append("px;padding:").Append(Cqw(b.Padding, d.Width)).Append(';');
            css.Append("font-size:").Append(b.Font.Size).Append("px;font-size:").Append(Cqw(b.Font.Size, d.Width)).Append(';')
                .Append("color:").Append(b.Font.Colour).Append(';')
                .Append("font-weight:").Append(b.Font.Bold ? "bold" : "normal").Append(';')
                .Append("text-align:").Append(b.Font.Align switch { SplashAlign.Centre => "center", SplashAlign.Right => "right", _ => "left" }).Append(';')
                .Append("font-family:").Append(b.Font.Family switch
                {
                    SplashFontFamily.Serif => "Georgia, serif",
                    SplashFontFamily.Monospace => "ui-monospace, monospace",
                    _ => "system-ui, sans-serif",
                }).Append(";}\n");
        }
        return css.ToString();
    }

    private static bool HasText(SplashBlock b) => b.Kind != SplashBlockKind.Picture || b.Content.Caption is not null;

    private static string Background(string colour, SplashPicture? picture, int focusX, int focusY, int darken, Func<string, string> pictureUrl) =>
        "background-color:" + colour + ";" + (picture is null ? "" : Picture(picture, focusX, focusY, darken, pictureUrl));

    /// <summary>
    /// A picture as CSS background. The darkening is an inset shadow, which is drawn over the background and under the
    /// text, so it needs no extra background layer.
    /// </summary>
    private static string Picture(SplashPicture p, int focusX, int focusY, int darken, Func<string, string> pictureUrl)
    {
        var (size, repeat, position) = p.Fit switch
        {
            SplashFit.Fit => ("contain", "no-repeat", $"{focusX}% {focusY}%"),
            SplashFit.Stretch => ("100% 100%", "no-repeat", $"{focusX}% {focusY}%"),
            SplashFit.Tile => ("auto", "repeat", $"{focusX}% {focusY}%"),
            SplashFit.Centre => ("auto", "no-repeat", "center"),
            _ => ("cover", "no-repeat", $"{focusX}% {focusY}%"),
        };
        var css = $"background-image:url(\"{pictureUrl(p.Name)}\");background-size:{size};background-repeat:{repeat};background-position:{position};";
        return darken > 0 ? css + $"box-shadow:inset 0 0 0 100vmax rgba(0, 0, 0, {Number(darken / 100.0)});" : css;
    }

    private static string Pct(int part, int whole) => Number(Math.Round(part * 100.0 / whole, 4)) + "%";

    private static string Cqw(int px, int designWidth) => Number(Math.Round(px * 100.0 / designWidth, 4)) + "cqw";

    private static string Rgba(string hex, double alpha) =>
        $"rgba({Convert.ToInt32(hex[1..3], 16)}, {Convert.ToInt32(hex[3..5], 16)}, {Convert.ToInt32(hex[5..7], 16)}, {Number(Math.Round(alpha, 4))})";

    private static string Number(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
