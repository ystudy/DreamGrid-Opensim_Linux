using System.Globalization;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.Extensions.Localization;

namespace DreamGrid.Launcher.Resident;

/// <summary>
/// The resident pages' text, in layers for the current UI language: the owner's tone text (<see cref="ToneText.Keys"/> only, from
/// <see cref="LauncherSettings.Wording"/>), then the owner-added language's page text, then the built-in resources (which fall back
/// to English). A blank value in a layer is skipped. Replaces the framework's <c>IStringLocalizer&lt;ResidentText&gt;</c>.
/// </summary>
public sealed class ResidentTextLocalizer(IStringLocalizerFactory factory, Func<LauncherSettings> settings, OwnerLanguages added) : IStringLocalizer<ResidentText>
{
    private readonly IStringLocalizer _inner = factory.Create(typeof(ResidentText));

    public LocalizedString this[string name] => Get(name, []);

    public LocalizedString this[string name, params object[] arguments] => Get(name, arguments);

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
    {
        var all = new Dictionary<string, LocalizedString>(StringComparer.Ordinal);
        foreach (var s in _inner.GetAllStrings(includeParentCultures))
            all[s.Name] = s;
        var lang = Current();
        if (lang is not null && added.Get(lang)?.Page is { } page)
            foreach (var (key, text) in page)
                if (!string.IsNullOrWhiteSpace(text))
                    all[key] = new LocalizedString(key, text, resourceNotFound: false);
        foreach (var key in ToneText.Keys)
            if (Override(lang, key) is { } owner)
                all[key] = Made(key, ToneText.ToFormat(owner), [settings().GridName], escaped: true);
        return all.Values;
    }

    private LocalizedString Get(string name, object[] arguments)
    {
        var lang = Current();
        if (Override(lang, name) is { } owner)
            // {grid} is the grid's name even where the page passes no arguments.
            return Made(name, ToneText.ToFormat(owner), arguments.Length == 0 ? [settings().GridName] : arguments, escaped: true);
        if (AddedPage(lang)?.GetValueOrDefault(name) is { } page && !string.IsNullOrWhiteSpace(page))
            return Made(name, page, arguments, escaped: false);
        return arguments.Length == 0 ? _inner[name] : _inner[name, arguments];
    }

    // Tone text is always a format string (see ToneText.ToFormat); an added language's text is one only when the page passes arguments, as .resx text is.
    private static LocalizedString Made(string name, string format, object[] arguments, bool escaped)
    {
        if (!escaped && arguments.Length == 0)
            return new LocalizedString(name, format, resourceNotFound: false);
        try
        {
            return new LocalizedString(name, string.Format(CultureInfo.CurrentCulture, format, arguments), resourceNotFound: false);
        }
        catch (FormatException)
        {
            // An added language's file may hold stray braces; show it as written.
            return new LocalizedString(name, format, resourceNotFound: false);
        }
    }

    /// <summary>The owner's tone text for <paramref name="key"/> in <paramref name="lang"/>, or null when none, blank, or the key is not a tone key.</summary>
    private string? Override(string? lang, string key)
    {
        if (lang is null || !ToneText.Keys.Contains(key, StringComparer.Ordinal))
            return null;
        var wording = settings().Wording;
        foreach (var (code, texts) in wording)
            if (string.Equals(code, lang, StringComparison.OrdinalIgnoreCase) && texts.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text))
                return text;
        return null;
    }

    /// <summary>The page text of the owner-added language <paramref name="lang"/>; null for a built-in one, which never asks the store.</summary>
    private IReadOnlyDictionary<string, string>? AddedPage(string? lang) =>
        lang is null || Languages.BuiltIn.Contains(lang, StringComparer.Ordinal) ? null : added.Get(lang)?.Page;

    /// <summary>
    /// The built-in or added language the current UI culture belongs to ("de-AT" is "de", an added "pt-PT" is itself), or null. A plain
    /// built-in name ("de") is answered without the store; only a regional name can be an added code and asks it.
    /// </summary>
    private string? Current()
    {
        var name = CultureInfo.CurrentUICulture.Name;
        if (Languages.BuiltIn.Contains(name, StringComparer.Ordinal))
            return name;
        return Languages.Match(name, [.. Languages.BuiltIn, .. added.Codes()]);
    }
}
