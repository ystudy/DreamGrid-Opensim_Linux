using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Guests;

/// <summary>
/// The operator's legal pages as the site reads them. DreamGrid ships no wording: a page exists only once the operator has
/// written it, and sign-up opens only when Terms, Privacy and Cookies are written and the operator has ticked that they are
/// responsible for them.
/// </summary>
public static class LegalPages
{
    /// <summary>English: the stand-in source when the default language has no text of a page.</summary>
    public const string DefaultLanguage = "en";

    public const string Terms = "terms";
    public const string Privacy = "privacy";
    public const string Cookies = "cookies";
    public const string Contact = "contact";

    /// <summary>Every page id, in the order the links are shown.</summary>
    public static readonly IReadOnlyList<string> All = [Terms, Privacy, Cookies, Contact];

    /// <summary>The three pages sign-up needs, in the order <see cref="Missing"/> lists them. Contact is optional.</summary>
    private static readonly string[] Required = [Terms, Privacy, Cookies];

    /// <summary>The page's page title.</summary>
    public static string Title(string page) => page switch
    {
        Terms => "Terms",
        Privacy => "Privacy",
        Cookies => "Cookies",
        Contact => "Contact",
        _ => page,
    };

    /// <summary>
    /// The language whose texts are the source: the grid's default language (<see cref="LanguageSettings.Default"/>), as saved. Every
    /// other language's text of a page is a translation, shown only while it carries the source's <see cref="LegalText.Version"/>.
    /// </summary>
    public static string Source(LauncherSettings s) => s.Languages.Default;

    /// <summary>
    /// The source of <paramref name="page"/>, the one choice every rule here makes: the default language's record of the page, or,
    /// only when the default has no record of it at all (a default set by hand before its pages were written), English standing in.
    /// A cleared record in the default (written, then emptied) is "no text": English never takes over then. <c>Text</c> is null
    /// when the chosen record has no text.
    /// </summary>
    public static (string Language, LegalText? Text) SourceOf(LauncherSettings s, string page)
    {
        var code = Source(s);
        if (Record(s, page, code) is null && !string.Equals(code, DefaultLanguage, StringComparison.OrdinalIgnoreCase)
            && Record(s, page, DefaultLanguage) is not null)
            code = DefaultLanguage;
        return (code, Exact(s, page, code));
    }

    /// <summary>
    /// The version the source's next text of <paramref name="page"/> gets: one more than <see cref="Highest"/>, so a number is never
    /// used twice and never goes backwards.
    /// </summary>
    public static int NextVersion(LauncherSettings s, string page) => 1 + Highest(s.Legal, page);

    /// <summary>
    /// The highest version <paramref name="page"/> has had: its high-water mark (<see cref="LegalSettings.HighestVersion"/>) or any
    /// record's version in any language, cleared records included, whichever is higher; 0 when there is none.
    /// </summary>
    public static int Highest(LegalSettings legal, string page)
    {
        var mark = legal.HighestVersion is { } marks && marks.TryGetValue(page, out var m) ? m : 0;
        var records = legal.Texts?.Values.Select(l => l is not null && l.TryGetValue(page, out var t) && t is not null ? t.Version : 0)
            .DefaultIfEmpty(0).Max() ?? 0;
        return Math.Max(mark, records);
    }

    /// <summary>
    /// <paramref name="legal"/> with every page's high-water mark raised to <see cref="Highest"/>: done on every save, and before records
    /// are removed, so the marks outlive them. A file from before plan 16b gets its marks here, from its records.
    /// </summary>
    public static LegalSettings WithMarks(LegalSettings legal)
    {
        var pages = (legal.Texts?.Values.Where(l => l is not null).SelectMany(l => l.Keys) ?? []).Concat(legal.HighestVersion?.Keys ?? []).Distinct();
        var marks = pages.ToDictionary(p => p, p => Highest(legal, p));
        return marks.Count == 0 ? legal : legal with { HighestVersion = marks };
    }

    /// <summary>
    /// The text of <paramref name="page"/> for a visitor reading <paramref name="language"/>: that language's text while it is at the
    /// source's version, else the source's; null when there is none (or only whitespace). See <see cref="Find"/>.
    /// </summary>
    public static LegalText? Get(LauncherSettings s, string page, string language) => Find(s, page, language)?.Text;

    /// <summary>
    /// Like <see cref="Get"/>, and says which language the text is in, so a page can tell the reader when it is not theirs. The
    /// source is <see cref="SourceOf"/>. A translation is shown only while its version equals the source's, so a source saved since
    /// hides it until it is translated again; a translation of a page with no source text is never shown.
    /// </summary>
    public static (LegalText Text, string Language)? Find(LauncherSettings s, string page, string language)
    {
        var (code, source) = SourceOf(s, page);
        if (source is null) return null;
        if (!string.Equals(language, code, StringComparison.OrdinalIgnoreCase)
            && Exact(s, page, language) is { } translation && translation.Version == source.Version)
            return (translation, language);
        return (source, code);
    }

    /// <summary>The text of <paramref name="page"/> written in exactly <paramref name="language"/>; null when missing or only whitespace.</summary>
    public static LegalText? Exact(LauncherSettings s, string page, string language) =>
        Record(s, page, language) is { } text && !string.IsNullOrWhiteSpace(text.Markdown) ? text : null;

    /// <summary>The stored record of <paramref name="page"/> in <paramref name="language"/>, cleared or not; null when there is none.</summary>
    private static LegalText? Record(LauncherSettings s, string page, string language) =>
        s.Legal.Texts is { } texts && texts.TryGetValue(language, out var pages) && pages is not null && pages.TryGetValue(page, out var text)
            ? text
            : null;

    /// <summary>Whether sign-up may open: Terms, Privacy and Cookies have source text and the operator has ticked that they are responsible.</summary>
    public static bool SignUpGateOpen(LauncherSettings s) => s.Legal.OperatorResponsible && Missing(s).Count == 0;

    /// <summary>
    /// Which of Terms, Privacy and Cookies have no text in the default language itself, in that order. English standing in
    /// (<see cref="SourceOf"/>) is shown and accepted, but never opens sign-up: the owner writes the default's own pages first.
    /// </summary>
    public static IReadOnlyList<string> Missing(LauncherSettings s) =>
        Required.Where(p => SourceOf(s, p) is not (var code, { }) || !string.Equals(code, Source(s), StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Which of Terms, Privacy and Cookies in <paramref name="language"/> are missing or at a version other than the source's
    /// (<see cref="SourceOf"/>), in that order. Empty for the source itself when it is complete.
    /// </summary>
    public static IReadOnlyList<string> UpToDate(LauncherSettings s, string language) =>
        Required.Where(p => Exact(s, p, language) is not { } text || (SourceOf(s, p).Text is { } source && source.Version != text.Version)).ToList();

    /// <summary>
    /// The required pages that stop <paramref name="language"/> from becoming the default, in order: while sign-up can open, each one
    /// <see cref="UpToDate"/> lists; at any time, each page whose source text would change version or vanish with the switch, and each
    /// whose source text after the switch would be below the page's highest version (<see cref="Highest"/>; a cleared source leaves a
    /// stale translation behind). The numbers carry over, so Terms acceptances stand and never go backwards.
    /// </summary>
    public static IReadOnlyList<string> DefaultBlockers(LauncherSettings s, string language)
    {
        var after = s with { Languages = s.Languages with { Default = language } };
        var gate = SignUpGateOpen(s);
        var behind = gate ? UpToDate(s, language) : [];
        return Required.Where(p => behind.Contains(p)
            || (SourceOf(s, p).Text is { } now && (SourceOf(after, p).Text is not { } next || next.Version != now.Version))
            || (SourceOf(after, p).Text is { } then && then.Version < Highest(s.Legal, p))).ToList();
    }

    /// <summary>"Terms", "Terms and Cookies" or "Terms, Privacy and Cookies": page titles joined for a sentence.</summary>
    public static string Titles(IReadOnlyList<string> pages)
    {
        var titles = pages.Select(Title).ToList();
        return titles.Count <= 1 ? string.Concat(titles) : string.Join(", ", titles.Take(titles.Count - 1)) + " and " + titles[^1];
    }
}
