using System.Text.RegularExpressions;

namespace DreamGrid.Launcher.Core.Splash;

/// <summary>
/// What a splash design may hold (spec 2026-11-01-viewer-splash-design §5). Every save and every load of the published copy
/// goes through <see cref="Check"/>, and the public page is drawn only from a design that passed it.
/// </summary>
public static partial class SplashRules
{
    public const int MinWidth = 640, MaxWidth = 3840, MinHeight = 400, MaxHeight = 2160;
    public const int MaxBlocks = 100, MaxLayer = 999, MinBlockSize = 16;
    public const int MaxSeeThrough = 100, MaxDarken = 80, MaxPadding = 64, MinFontSize = 8, MaxFontSize = 200;
    public const int MaxText = 4000, MaxCaption = 200, MaxHeading = 120, MaxNewsText = 2000, MaxPlaceLine = 200, MaxRegionName = 64;
    public const int MaxNews = 20, MaxPlaces = 8;
    public static readonly IReadOnlyList<int> SnapSizes = [8, 16, 32, 64];
    /// <summary>The languages a text may be in (English only until 1.5).</summary>
    public static readonly IReadOnlyList<string> Languages = ["en"];

    // \z, not $: $ also matches before a final newline.
    [GeneratedRegex("^#[0-9a-fA-F]{6}\\z")]
    private static partial Regex ColourPattern();

    [GeneratedRegex("^[0-9a-f]{32}\\.(png|jpg|webp)\\z")]
    private static partial Regex PicturePattern();

    /// <summary>The problems with <paramref name="d"/> as plain sentences naming the block; empty when it may be saved.</summary>
    public static IReadOnlyList<string> Check(SplashDesign? d, Func<string, bool> pictureExists)
    {
        // A file or body can hold a null anywhere (System.Text.Json doesn't enforce the annotations): a problem, never a crash.
        if (d is null) return ["The page could not be read."];
        var problems = new List<string>();
        if (d.Width is < MinWidth or > MaxWidth || d.Height is < MinHeight or > MaxHeight)
            problems.Add($"The page size must be {MinWidth}–{MaxWidth} wide and {MinHeight}–{MaxHeight} high.");
        if (!SnapSizes.Contains(d.SnapSize))
            problems.Add("The grid size must be 8, 16, 32 or 64.");
        CheckBackground(d.Background, "The page", problems, pictureExists);
        if (d.Blocks is null)
        {
            problems.Add("The page has no block list.");
            return problems;
        }
        if (d.Blocks.Count > MaxBlocks)
            problems.Add($"A page can have at most {MaxBlocks} blocks.");

        var seen = new HashSet<int>();
        foreach (var b in d.Blocks)
        {
            if (b is null)
            {
                problems.Add("A block is empty.");
                continue;
            }
            var who = $"Block {b.Id}";
            if (b.Id <= 0) problems.Add($"{who}: the id must be positive.");
            else if (!seen.Add(b.Id)) problems.Add($"{who}: two blocks have this id.");
            if (b.X < 0 || b.Y < 0 || b.W < MinBlockSize || b.H < MinBlockSize || (long)b.X + b.W > d.Width || (long)b.Y + b.H > d.Height)
                problems.Add($"{who}: it must be at least {MinBlockSize} px and inside the page.");
            if (b.Layer is < 0 or > MaxLayer) problems.Add($"{who}: the layer must be 0–{MaxLayer}.");
            if (!IsColour(b.Colour)) problems.Add($"{who}: the colour must look like #1a2b3c.");
            if (b.SeeThrough is < 0 or > MaxSeeThrough) problems.Add($"{who}: see-through must be 0–{MaxSeeThrough} %.");
            if (b.Padding is < 0 or > MaxPadding) problems.Add($"{who}: padding must be 0–{MaxPadding}.");
            if (b.Background is { } bg) CheckPicture(bg, who, problems, pictureExists);
            if (b.Font is null) problems.Add($"{who}: it has no font.");
            else
            {
                if (b.Font.Size is < MinFontSize or > MaxFontSize) problems.Add($"{who}: the font size must be {MinFontSize}–{MaxFontSize}.");
                if (!IsColour(b.Font.Colour)) problems.Add($"{who}: the text colour must look like #1a2b3c.");
                if (!Enum.IsDefined(b.Font.Align) || !Enum.IsDefined(b.Font.Family)) problems.Add($"{who}: unknown alignment or font.");
            }
            if (!Enum.IsDefined(b.Kind)) problems.Add($"{who}: unknown kind of block.");
            else if (b.Content is null) problems.Add($"{who}: it has no content.");
            else CheckContent(b.Kind, b.Content, who, problems, pictureExists);
        }
        return problems;
    }

    private static void CheckBackground(SplashBackground? bg, string who, List<string> problems, Func<string, bool> pictureExists)
    {
        if (bg is null)
        {
            problems.Add($"{who} has no background.");
            return;
        }
        if (!IsColour(bg.Colour)) problems.Add($"{who}: the background colour must look like #1a2b3c.");
        if (bg.FocusX is < 0 or > 100 || bg.FocusY is < 0 or > 100) problems.Add($"{who}: the focus point must be 0–100 %.");
        if (bg.Darken is < 0 or > MaxDarken) problems.Add($"{who}: darkening must be 0–{MaxDarken} %.");
        if (bg.Picture is { } p) CheckPicture(p, who, problems, pictureExists);
    }

    private static void CheckContent(SplashBlockKind kind, SplashContent c, string who, List<string> problems, Func<string, bool> pictureExists)
    {
        // Which fields this kind uses; any other set field is refused, so a stored design never carries hidden content.
        var uses = new HashSet<string>(kind switch
        {
            SplashBlockKind.Text => ["Text"],
            SplashBlockKind.Picture => ["Picture", "Caption"],
            SplashBlockKind.GridName => [],
            SplashBlockKind.Status => ["Status"],
            SplashBlockKind.News => ["Heading", "News"],
            SplashBlockKind.Places => ["Places"],
            SplashBlockKind.Links => ["Links"],
            _ => Array.Empty<string>(),
        });
        void Field(string name, object? value)
        {
            if (value is not null && !uses.Contains(name)) problems.Add($"{who}: a {kind} block can't have {name}.");
        }
        Field("Text", c.Text);
        Field("Caption", c.Caption);
        Field("Picture", c.Picture);
        Field("Status", c.Status);
        Field("Heading", c.Heading);
        Field("News", c.News);
        Field("Places", c.Places);
        Field("Links", c.Links);

        switch (kind)
        {
            case SplashBlockKind.Text:
                if (c.Text is null) problems.Add($"{who}: a text block needs its text.");
                else CheckText(c.Text, MaxText, who, problems);
                break;
            case SplashBlockKind.Picture:
                if (c.Picture is null) problems.Add($"{who}: a picture block needs its picture.");
                else CheckPicture(c.Picture, who, problems, pictureExists);
                if (c.Caption is not null) CheckText(c.Caption, MaxCaption, who, problems);
                break;
            case SplashBlockKind.Status:
                if (c.Status is null) problems.Add($"{who}: a status block needs its lines.");
                break;
            case SplashBlockKind.News:
                if (c.News is null) problems.Add($"{who}: a news block needs its list.");
                else
                {
                    if (c.News.Count > MaxNews) problems.Add($"{who}: at most {MaxNews} news items.");
                    foreach (var n in c.News)
                        if (n?.Text is null) problems.Add($"{who}: a news item has no text.");
                        else CheckText(n.Text, MaxNewsText, who, problems);
                }
                if (c.Heading is not null) CheckText(c.Heading, MaxHeading, who, problems);
                break;
            case SplashBlockKind.Places:
                if (c.Places is null) problems.Add($"{who}: a places block needs its list.");
                else
                {
                    if (c.Places.Count > MaxPlaces) problems.Add($"{who}: at most {MaxPlaces} places.");
                    foreach (var p in c.Places)
                    {
                        if (p?.Line is null || p.RegionName is null) { problems.Add($"{who}: a place is incomplete."); continue; }
                        if (p.RegionName.Length is 0 or > MaxRegionName) problems.Add($"{who}: a region name must be 1–{MaxRegionName} characters.");
                        CheckText(p.Line, MaxPlaceLine, who, problems);
                        if (p.Picture is { } name && !IsPicture(name, pictureExists)) problems.Add($"{who}: a place's picture isn't in the picture list.");
                    }
                }
                break;
            case SplashBlockKind.Links:
                if (c.Links is null) problems.Add($"{who}: a links block needs its links.");
                break;
        }
    }

    private static void CheckText(IReadOnlyDictionary<string, string> text, int max, string who, List<string> problems)
    {
        foreach (var (lang, value) in text)
        {
            if (!Languages.Contains(lang)) problems.Add($"{who}: text in \"{lang}\" isn't supported yet.");
            if (value is null || value.Length > max) problems.Add($"{who}: a text can be at most {max} characters.");
        }
    }

    private static void CheckPicture(SplashPicture p, string who, List<string> problems, Func<string, bool> pictureExists)
    {
        if (!IsPicture(p.Name, pictureExists)) problems.Add($"{who}: the picture isn't in the picture list.");
        if (!Enum.IsDefined(p.Fit)) problems.Add($"{who}: unknown picture fit.");
    }

    private static bool IsPicture(string? name, Func<string, bool> pictureExists) =>
        name is not null && PicturePattern().IsMatch(name) && pictureExists(name);

    private static bool IsColour(string? c) => c is not null && ColourPattern().IsMatch(c);
}
