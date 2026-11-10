using System.Globalization;

namespace DreamGrid.Launcher.Core;

/// <summary>The languages the resident web pages come in, and how a browser's language tag is matched to one of them.</summary>
public static class Languages
{
    public const string English = "en";

    /// <summary>The languages that ship with GridGate, English first.</summary>
    public static IReadOnlyList<string> BuiltIn { get; } = ["en", "de", "es", "fr", "pt", "ru"];

    private const int LongestTag = 35;

    /// <summary>
    /// The offered language that fits a language tag such as "de-AT" or "pt-BR", or null if none does. A tag with anything other than
    /// letters, digits and "-" in it, or longer than 35 characters, never matches.
    /// </summary>
    public static string? Match(string? tag, IReadOnlyCollection<string> offered)
    {
        tag = tag?.Trim();
        if (string.IsNullOrEmpty(tag) || tag.Length > LongestTag || !tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return null;
        tag = tag.ToLowerInvariant();
        if (offered.Contains(tag))
            return tag;
        var dash = tag.IndexOf('-');
        var primary = dash > 0 ? tag[..dash] : null;
        return primary is not null && offered.Contains(primary) ? primary : null;
    }

    /// <summary>The culture used to write dates and numbers in a language; Portuguese is Brazilian.</summary>
    public static CultureInfo Culture(string code) =>
        CultureInfo.GetCultureInfo(code == "pt" ? "pt-BR" : code);

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
