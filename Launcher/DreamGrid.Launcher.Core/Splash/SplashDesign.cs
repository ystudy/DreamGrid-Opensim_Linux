using System.Text.Json.Serialization;

namespace DreamGrid.Launcher.Core.Splash;

// The viewer splash page the owner builds from blocks (spec 2026-11-01-viewer-splash-design). Positions and sizes are in
// design pixels; the public page turns them into proportions (SplashCss). Text is keyed by language ("en" until 1.5).
// Enums are stored by name, so the files read well and the editor's JSON says "Text" rather than 0.

/// <summary>How a picture fills its box: crop the edges, show it all, stretch, repeat it, or centre it at actual size.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SplashFit>))]
public enum SplashFit { Fill, Fit, Stretch, Tile, Centre }

[JsonConverter(typeof(JsonStringEnumConverter<SplashBlockKind>))]
public enum SplashBlockKind { Text, Picture, GridName, Status, News, Places, Links }

[JsonConverter(typeof(JsonStringEnumConverter<SplashAlign>))]
public enum SplashAlign { Left, Centre, Right }

[JsonConverter(typeof(JsonStringEnumConverter<SplashFontFamily>))]
public enum SplashFontFamily { Sans, Serif, Monospace }

public sealed record SplashFont(int Size, string Colour, bool Bold, SplashAlign Align, SplashFontFamily Family);

/// <param name="Name">A picture-store name (32 hex characters and an extension, plan 12b).</param>
public sealed record SplashPicture(string Name, SplashFit Fit);

public sealed record SplashNewsItem(DateOnly Date, IReadOnlyDictionary<string, string> Text, bool Hidden);

public sealed record SplashPlace(Guid RegionId, string RegionName, IReadOnlyDictionary<string, string> Line, string? Picture);

/// <summary>Which lines a status block shows: grid online, people online, regions running.</summary>
public sealed record SplashStatusLines(bool Online, bool People, bool Regions);

public sealed record SplashLinks(bool SignUp, bool ForgotPassword, bool Terms, bool Privacy, bool FrontPage);

/// <summary>
/// One record for every kind of block: a kind uses only its own fields and leaves the rest null (<see cref="SplashRules"/>
/// refuses a field of another kind). <see cref="ShowName"/> and <see cref="ShowLogo"/> are the grid-name block's.
/// </summary>
public sealed record SplashContent(
    IReadOnlyDictionary<string, string>? Text = null,
    IReadOnlyDictionary<string, string>? Caption = null,
    SplashPicture? Picture = null,
    bool ShowName = true,
    bool ShowLogo = false,
    SplashStatusLines? Status = null,
    IReadOnlyDictionary<string, string>? Heading = null,
    IReadOnlyList<SplashNewsItem>? News = null,
    IReadOnlyList<SplashPlace>? Places = null,
    SplashLinks? Links = null);

/// <param name="SeeThrough">0 (solid) to 100 (invisible) for <paramref name="Colour"/>.</param>
/// <param name="Layer">Higher is drawn on top.</param>
public sealed record SplashBlock(int Id, SplashBlockKind Kind, int X, int Y, int W, int H, int Layer,
    string Colour, int SeeThrough, SplashPicture? Background, bool Rounded, int Padding, SplashFont Font, SplashContent Content);

/// <param name="FocusX">The part of the picture (0–100 % across) that stays in view when it is cropped.</param>
/// <param name="Darken">0–80 %: a dark layer over the picture so text reads over it.</param>
public sealed record SplashBackground(string Colour, SplashPicture? Picture, int FocusX, int FocusY, int Darken);

public sealed record SplashDesign(int Width, int Height, SplashBackground Background, bool Snap, int SnapSize,
    IReadOnlyList<SplashBlock> Blocks);
