using System.Globalization;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Resident;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace GridGate.Launcher.Pages.Security;

/// <summary>
/// The owner's page for the wording of the guest pages (OwnerPolicy; <c>/security/wording</c>; 404 where there is no guest side).
/// One language at a time (<c>?lang=</c>, from <see cref="OfferedLanguages.All"/>): a row per <see cref="ToneText.Keys"/> entry with the
/// built-in text beside a box for the owner's own. Saving text equal to the built-in drops the override, and saving an empty box
/// puts the original back (audited as a reset). Stored keys that are no longer
/// tone keys are listed and can be deleted. Saving and resetting are audited (<c>page-wording-saved</c>, <c>page-wording-reset</c>,
/// <c>page-wording-deleted</c>) with the target <c>lang/key</c> and never the text.
/// </summary>
public sealed class WordingModel(IGridController grid, Func<LauncherSettings> settings, IStringLocalizerFactory factory,
    OwnerLanguages added, LanguageCatalog catalog, OfferedLanguages offered, AuditLog audit) : PageModel
{
    /// <summary>The most characters one text may have.</summary>
    public const int MaxLength = 2000;

    [BindProperty] public string? Text { get; set; }

    [TempData] public string? Notice { get; set; }

    /// <summary>One tone key's row: its built-in text, the owner's text (or what they typed) and any error.</summary>
    public sealed record Row(string Key, string Title, string BuiltIn, string Mine, bool Changed, string? Error);

    public string Lang { get; private set; } = "";

    public IReadOnlyList<string> Languages { get; private set; } = [];

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    /// <summary>Stored keys that are not tone keys any more, with their text.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Unused { get; private set; } = [];

    public void OnGet(string? lang) => Fill(Pick(lang), null, null);

    public IActionResult OnPostSave(string lang, string key)
    {
        if (Match(lang) is not { } code || !ToneText.Keys.Contains(key, StringComparer.Ordinal)) return NotFound();
        var text = (Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Length > MaxLength)
        {
            Fill(code, key, $"That is too long. Keep it to {MaxLength:N0} characters or fewer.");
            return Page();
        }
        // An empty box means the original wording, as the page says: the same as "Put the original back".
        if (text.Length == 0)
            return OnPostReset(code, key);
        var builtIn = BuiltIn(code, key);
        string? stored = text == builtIn ? null : text;
        grid.UpdateSettings(s => s with { Wording = Store(s.Wording, code, key, stored) }, writeConfig: false);
        Write("page-wording-saved", $"{code}/{key}");
        Notice = $"Saved \"{ToneText.Title(key)}\".";
        return Redirect($"/security/wording?lang={Uri.EscapeDataString(code)}");
    }

    public IActionResult OnPostReset(string lang, string key)
    {
        if (Match(lang) is not { } code || !ToneText.Keys.Contains(key, StringComparer.Ordinal)) return NotFound();
        grid.UpdateSettings(s => s with { Wording = Store(s.Wording, code, key, null) }, writeConfig: false);
        Write("page-wording-reset", $"{code}/{key}");
        Notice = $"\"{ToneText.Title(key)}\" uses the original wording again.";
        return Redirect($"/security/wording?lang={Uri.EscapeDataString(code)}");
    }

    public IActionResult OnPostDelete(string lang, string key)
    {
        if (Match(lang) is not { } code || ToneText.Keys.Contains(key, StringComparer.Ordinal) || !Stored(settings().Wording, code).ContainsKey(key))
            return NotFound();
        grid.UpdateSettings(s => s with { Wording = Store(s.Wording, code, key, null) }, writeConfig: false);
        Write("page-wording-deleted", $"{code}/{key}");
        Notice = "Deleted the wording that is no longer used.";
        return Redirect($"/security/wording?lang={Uri.EscapeDataString(code)}");
    }

    private string? Match(string? lang) => GridGate.Launcher.Core.Languages.Match(lang, offered.All());

    private string Pick(string? lang) => Match(lang) ?? offered.Default();

    private void Fill(string code, string? errorKey, string? error)
    {
        Lang = code;
        Languages = offered.All();
        var mine = Stored(settings().Wording, code);
        Rows = [.. ToneText.Keys.Select(key =>
        {
            var has = mine.TryGetValue(key, out var own);
            var posted = key == errorKey;
            return new Row(key, ToneText.Title(key), BuiltIn(code, key), posted ? (Text ?? "") : has ? own! : "", has, posted ? error : null);
        })];
        Unused = [.. mine.Where(t => !ToneText.Keys.Contains(t.Key, StringComparer.Ordinal)).OrderBy(t => t.Key, StringComparer.Ordinal)];
    }

    /// <summary>The text the page has without the owner's wording, with <c>{grid}</c> where the grid's name goes. Never read through <see cref="ResidentTextLocalizer"/>, which would give the override.</summary>
    private string BuiltIn(string code, string key)
    {
        string raw;
        if (!offered.IsBuiltIn(code))
            raw = added.Get(code)?.Page.GetValueOrDefault(key) is { } own && !string.IsNullOrWhiteSpace(own) ? own : catalog.English(key);
        else
        {
            var previous = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = GridGate.Launcher.Core.Languages.Culture(code);
                raw = factory.Create(typeof(ResidentText))[key].Value;
            }
            finally
            {
                CultureInfo.CurrentUICulture = previous;
            }
        }
        return raw.Replace("{0}", "{grid}", StringComparison.Ordinal).Trim();
    }

    private static IReadOnlyDictionary<string, string> Stored(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> wording, string code) =>
        wording.FirstOrDefault(l => string.Equals(l.Key, code, StringComparison.OrdinalIgnoreCase)).Value ?? new Dictionary<string, string>();

    /// <summary>The wording with <paramref name="text"/> stored for the key in the language, or the key removed when null. Pure, so the update may run again.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Store(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> wording,
        string code, string key, string? text)
    {
        var all = wording.Where(l => !string.Equals(l.Key, code, StringComparison.OrdinalIgnoreCase)).ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(Stored(wording, code), StringComparer.Ordinal);
        if (text is null) texts.Remove(key);
        else texts[key] = text;
        if (texts.Count > 0) all[code] = texts;
        return all;
    }

    private void Write(string action, string target) =>
        audit.Write(User, HttpContext.Connection.RemoteIpAddress?.ToString(), action, target);
}
