using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Guests;

/// <summary>
/// The operator's legal pages as the site reads them. DreamGrid ships no wording: a page exists only once the operator has
/// written it, and sign-up opens only when Terms, Privacy and Cookies are written and the operator has ticked that they are
/// responsible for them.
/// </summary>
public static class LegalPages
{
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
    /// The text of <paramref name="page"/> for a visitor reading <paramref name="language"/>: that language's text, else the grid's
    /// default language's, else English; null when none has text (or only whitespace).
    /// </summary>
    public static LegalText? Get(LauncherSettings s, string page, string language) => Find(s, page, language)?.Text;

    /// <summary>
    /// Like <see cref="Get"/>, and says which language the text is in, so a page can tell the reader when it is not theirs.
    /// </summary>
    public static (LegalText Text, string Language)? Find(LauncherSettings s, string page, string language)
    {
        foreach (var code in new[] { language, s.Languages.Default, DefaultLanguage })
            if (Exact(s, page, code) is { } text) return (text, code);
        return null;
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

    /// <summary>Whether sign-up may open: Terms, Privacy and Cookies have text and the operator has ticked that they are responsible.</summary>
    public static bool SignUpGateOpen(LauncherSettings s) => s.Legal.OperatorResponsible && Missing(s).Count == 0;

    /// <summary>Which of Terms, Privacy and Cookies still need text, in that order.</summary>
    public static IReadOnlyList<string> Missing(LauncherSettings s) => Required.Where(p => Exact(s, p, DefaultLanguage) is null).ToList();
}
