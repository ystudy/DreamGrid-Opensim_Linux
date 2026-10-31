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

    /// <summary>The text of <paramref name="page"/> in the default language; null when it is missing or only whitespace.</summary>
    public static LegalText? Get(LauncherSettings s, string page) =>
        s.Legal.Texts is { } texts
        && texts.TryGetValue(DefaultLanguage, out var pages)
        && pages.TryGetValue(page, out var text)
        && text is not null
        && !string.IsNullOrWhiteSpace(text.Markdown)
            ? text
            : null;

    /// <summary>Whether sign-up may open: Terms, Privacy and Cookies have text and the operator has ticked that they are responsible.</summary>
    public static bool SignUpGateOpen(LauncherSettings s) => s.Legal.OperatorResponsible && Missing(s).Count == 0;

    /// <summary>Which of Terms, Privacy and Cookies still need text, in that order.</summary>
    public static IReadOnlyList<string> Missing(LauncherSettings s) => Required.Where(p => Get(s, p) is null).ToList();
}
