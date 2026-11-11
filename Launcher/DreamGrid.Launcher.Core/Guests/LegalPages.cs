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
    /// The language whose texts are the source: the grid's default language (<see cref="LanguageSettings.Default"/>). Every other
    /// language's text of a page is a translation, shown only while it carries the source's <see cref="LegalText.Version"/>.
    /// </summary>
    public static string Source(LauncherSettings s) => s.Languages.Default;

    /// <summary>
    /// The text of <paramref name="page"/> for a visitor reading <paramref name="language"/>: that language's text while it is at the
    /// source's version, else the source's; null when there is none (or only whitespace). See <see cref="Find"/>.
    /// </summary>
    public static LegalText? Get(LauncherSettings s, string page, string language) => Find(s, page, language)?.Text;

    /// <summary>
    /// Like <see cref="Get"/>, and says which language the text is in, so a page can tell the reader when it is not theirs. The
    /// source is the default language's text; when the default has none (a default set by hand before its pages were written),
    /// English stands in as the source. A translation is shown only while its version equals the source's, so a source saved
    /// since hides it until it is translated again; a translation of a page with no source is never shown.
    /// </summary>
    public static (LegalText Text, string Language)? Find(LauncherSettings s, string page, string language)
    {
        var code = Source(s);
        var source = Exact(s, page, code);
        if (source is null && Exact(s, page, DefaultLanguage) is { } english)
            (code, source) = (DefaultLanguage, english);
        if (source is null) return null;
        if (!string.Equals(language, code, StringComparison.OrdinalIgnoreCase)
            && Exact(s, page, language) is { } translation && translation.Version == source.Version)
            return (translation, language);
        return (source, code);
    }

    /// <summary>The text of <paramref name="page"/> written in exactly <paramref name="language"/>; null when missing or only whitespace.</summary>
    public static LegalText? Exact(LauncherSettings s, string page, string language) =>
        s.Legal.Texts is { } texts
        && texts.TryGetValue(language, out var pages)
        && pages.TryGetValue(page, out var text)
        && text is not null
        && !string.IsNullOrWhiteSpace(text.Markdown)
            ? text
            : null;

    /// <summary>Whether sign-up may open: Terms, Privacy and Cookies have text in the default language and the operator has ticked that they are responsible.</summary>
    public static bool SignUpGateOpen(LauncherSettings s) => s.Legal.OperatorResponsible && Missing(s).Count == 0;

    /// <summary>Which of Terms, Privacy and Cookies still need text in the default language (the source), in that order.</summary>
    public static IReadOnlyList<string> Missing(LauncherSettings s) => Required.Where(p => Exact(s, p, Source(s)) is null).ToList();

    /// <summary>
    /// Which of Terms, Privacy and Cookies in <paramref name="language"/> are missing or at a version other than the source's, in that
    /// order: what must be written before <paramref name="language"/> can become the default. Empty for the source itself when it is
    /// complete.
    /// </summary>
    public static IReadOnlyList<string> UpToDate(LauncherSettings s, string language) =>
        Required.Where(p => Exact(s, p, language) is not { } text || (Exact(s, p, Source(s)) is { } source && source.Version != text.Version)).ToList();
}
