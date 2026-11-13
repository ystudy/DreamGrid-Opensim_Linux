using System.Collections;
using System.Globalization;
using System.Resources;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Email;

namespace GridGate.Launcher.Resident;

/// <summary>
/// The texts a language has to translate, and what a language the owner added is still missing. The texts are every English
/// resident-page key (<c>ResidentText</c>) and every English resident-email key (<see cref="EmailTemplates.ResidentTextKeys"/>),
/// read once from the embedded neutral resources. A built-in language is never missing anything; an added one misses each text
/// its file has no non-blank value for, and a key in the file that GridGate doesn't know is ignored.
/// </summary>
public sealed class LanguageCatalog(OwnerLanguages added)
{
    private static readonly ResourceManager PageText = new("GridGate.Launcher.Resources.ResidentText", typeof(ResidentText).Assembly);

    private static readonly Lazy<IReadOnlyList<string>> Page = new(() =>
    {
        List<string> keys = [];
        if (PageText.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false) is { } set)
            foreach (DictionaryEntry e in set)
                if (e.Key is string k && e.Value is string)
                    keys.Add(k);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    });

    /// <summary>Every English page key, in order.</summary>
    public IReadOnlyList<string> PageKeys() => Page.Value;

    /// <summary>Every English email key of the resident emails, plus <c>ExpiresIn.*</c> and <c>Reason.*</c>, in order.</summary>
    public IReadOnlyList<string> EmailKeys() => EmailTemplates.ResidentTextKeys();

    /// <summary>The English page text for <paramref name="key"/>.</summary>
    public string English(string key) =>
        PageText.GetString(key, CultureInfo.InvariantCulture) ?? throw new ArgumentException($"No page text \"{key}\".", nameof(key));

    /// <summary>The English email text for <paramref name="key"/>.</summary>
    public string EnglishEmail(string key) => EmailTemplates.EnglishText(key);

    /// <summary>How many texts the added language <paramref name="code"/> has no value for; 0 for a built-in language.</summary>
    public int Missing(string code) => MissingKeys(code).Count;

    /// <summary>The page keys then the email keys the added language <paramref name="code"/> has no non-blank value for; empty for a built-in language.</summary>
    public IReadOnlyList<string> MissingKeys(string code)
    {
        if (Languages.BuiltIn.Contains(code, StringComparer.Ordinal))
            return [];
        var file = added.Get(code);
        bool Has(IReadOnlyDictionary<string, string>? words, string key) =>
            words is not null && words.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v);
        return [.. PageKeys().Where(k => !Has(file?.Page, k)), .. EmailKeys().Where(k => !Has(file?.Email, k))];
    }
}
