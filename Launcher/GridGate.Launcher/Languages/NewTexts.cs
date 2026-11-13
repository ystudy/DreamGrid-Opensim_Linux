using GridGate.Launcher.Core;

namespace GridGate.Launcher.Resident;

/// <summary>An added language that residents can pick but that misses <see cref="Count"/> texts, which show in English meanwhile.</summary>
public sealed record NewTexts(string Code, string Name, int Count)
{
    /// <summary>"Italiano: 3 new texts to translate".</summary>
    public string Line => $"{Name}: {Count} new {(Count == 1 ? "text" : "texts")} to translate";

    /// <summary>
    /// Every added language that is offered now and misses texts, for instance after a GridGate update added some (plan 16b,
    /// Task 7). A language that is off, or built in, never needs this.
    /// </summary>
    public static IReadOnlyList<NewTexts> Of(OfferedLanguages offered, LanguageCatalog catalog) =>
        [.. offered.Current()
            .Where(c => !offered.IsBuiltIn(c))
            .Select(c => new NewTexts(c, Languages.NativeName(c), catalog.Missing(c)))
            .Where(n => n.Count > 0)];
}
