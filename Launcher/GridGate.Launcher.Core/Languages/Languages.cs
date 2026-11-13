using System.Globalization;

namespace GridGate.Launcher.Core;

/// <summary>The languages the resident web pages come in, and how a browser's language tag is matched to one of them.</summary>
public static class Languages
{
    public const string English = "en";

    /// <summary>The languages that ship with GridGate, English first.</summary>
    public static IReadOnlyList<string> BuiltIn { get; } = ["en", "de", "es", "fr", "pt", "ru"];

    private const int LongestTag = 35;

    /// <summary>
    /// The offered language that fits a language tag such as "de-AT" or "pt-BR" (the case of the tag doesn't matter), or null if none does. A tag with anything other than
    /// letters, digits and "-" in it, or longer than 35 characters, never matches.
    /// </summary>
    public static string? Match(string? tag, IReadOnlyCollection<string> offered)
    {
        tag = tag?.Trim();
        if (string.IsNullOrEmpty(tag) || tag.Length > LongestTag || !tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return null;
        // The offered code is returned as it is written there ("pt-PT"), so an owner-added regional code can match.
        string? Find(string code) => offered.FirstOrDefault(o => string.Equals(o, code, StringComparison.OrdinalIgnoreCase));
        if (Find(tag) is { } exact)
            return exact;
        var dash = tag.IndexOf('-');
        return dash > 0 ? Find(tag[..dash]) : null;
    }

    private static string CultureName(string code) => code == "pt" ? "pt-BR" : code;

    /// <summary>The culture used to write dates and numbers in a language; Portuguese is Brazilian.</summary>
    public static CultureInfo Culture(string code) => CultureInfo.GetCultureInfo(CultureName(code));

    /// <summary>Whether the system has a predefined culture for the language code (an unknown one such as "xx" has none).</summary>
    public static bool HasCulture(string code)
    {
        try
        {
            CultureInfo.GetCultureInfo(CultureName(code), predefinedOnly: true);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Whether a language is written right to left.</summary>
    public static bool IsRightToLeft(string code) => Culture(code).TextInfo.IsRightToLeft;

    /// <summary>The language's own name with a capital letter, such as "Deutsch" or "Русский".</summary>
    public static string NativeName(string code)
    {
        var culture = Culture(code);
        var name = culture.NativeName;
        var paren = name.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0)
            name = name[..paren];
        return culture.TextInfo.ToTitleCase(name);
    }
}
