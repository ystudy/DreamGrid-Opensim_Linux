using System.Globalization;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Guests;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Resident;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Security;

/// <summary>
/// The owner's Languages page (OwnerPolicy; <c>/security/languages</c>; 404 where there is no guest side): every built-in and added
/// language with whether it is offered and the default, what an added one still misses (linking to its translate page) and how many
/// residents chose it. The owner adds a language from the cultures the system knows (an empty file, then its translate page) and
/// deletes an added one that is not offered (with its page wording), after a confirmation that shows how many residents use it; their saved choice is left
/// and falls back to the default. A language code only ever comes from <see cref="OwnerLanguages.CodeFor"/>. Audited as
/// <c>language-added</c> and <c>language-deleted</c> with the target <c>code</c>.
/// The Offered and Default columns are one form (<see cref="OnPostChoose"/>), checked in this order: every code is on the list (else
/// 400); the default is offered; an added language being turned on has nothing left to translate (one already on stays on after an
/// update adds texts); a new default has its legal pages at the source's versions (<see cref="LegalPages.UpToDate"/>), so nothing is
/// renumbered and Terms acceptances stand; turning off a language residents chose asks first, with how many. Audited as
/// <c>languages-offered-changed</c> (the code list) and <c>language-default-changed</c> (from → to). Rules 2 to 5 are checked inside the
/// settings update, against the copy being saved.
/// </summary>
public sealed class LanguagesModel(IGridController grid, OwnerLanguages added, LanguageCatalog catalog,
    OfferedLanguages offeredLanguages, ResidentLanguages residents, AuditLog audit) : PageModel
{
    /// <summary>One language's row of the table.</summary>
    public sealed record Row(string Code, string Name, bool BuiltIn, bool Offered, bool Default, int Missing, int Residents);

    /// <summary>A language that can be added: its code and "native name (English name)".</summary>
    public sealed record Choice(string Code, string Label);

    [TempData] public string? Notice { get; set; }

    public string? Error { get; private set; }

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public IReadOnlyList<Choice> Choices { get; private set; } = [];

    /// <summary>Added languages that are on and miss texts, also said on the dashboard.</summary>
    public IReadOnlyList<NewTexts> NewTexts { get; private set; } = [];

    /// <summary>The language of the legal pages the owner must write first, for the link beside <see cref="Error"/>; null otherwise.</summary>
    public string? LegalLanguage { get; private set; }

    /// <summary>A choice of languages waiting for the owner to confirm that residents lose theirs: what was posted, and one line per language.</summary>
    public sealed record PendingChoice(IReadOnlyList<string> Offered, string Default, IReadOnlyList<string> Lines);

    public PendingChoice? ConfirmChoose { get; private set; }

    /// <summary>The added language waiting for the owner to confirm its deletion, or null.</summary>
    public Row? ConfirmDelete { get; private set; }

    public void OnGet() => Fill();

    public IActionResult OnPostAdd(string? code)
    {
        // Only the exact code the list offers: "EN-us", a path or a built-in language is refused, and nothing is written.
        if (OwnerLanguages.CodeFor(code) is not { } name || !string.Equals(name, code, StringComparison.Ordinal))
            return Refuse("Pick a language from the list.");
        if (added.Get(name) is not null)
            return Refuse($"{Languages.NativeName(name)} is already added.");
        added.Save(new OwnerLanguage(name, new Dictionary<string, string>(), new Dictionary<string, string>(), default));
        Write("language-added", name);
        Notice = $"Added {Languages.NativeName(name)}. Translate its texts here, or download the file to translate it elsewhere.";
        return Redirect($"/security/languages/{Uri.EscapeDataString(name)}");
    }

    public IActionResult OnPostDelete(string? code, bool confirm)
    {
        if (OwnerLanguages.CodeFor(code) is not { } name || added.Get(name) is null)
            return NotFound();
        if (offeredLanguages.Current().Contains(name, StringComparer.OrdinalIgnoreCase))
            return Refuse($"{Languages.NativeName(name)} is offered to residents. Turn it off first, then delete it.");
        if (!confirm)
        {
            Fill();
            ConfirmDelete = Rows.Single(r => r.Code == name);
            return Page();
        }
        if (!added.Delete(name))
            return Refuse($"{Languages.NativeName(name)} could not be deleted. Try again in a moment.");
        // All its translations go, the owner's page wording, email wording and legal texts in it too, so adding it again starts empty.
        bool Its(string key) => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);
        grid.UpdateSettings(s =>
        {
            if (s.Wording.Keys.Any(Its))
                s = s with { Wording = s.Wording.Where(l => !Its(l.Key)).ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal) };
            if (s.Email.Templates is { } templates && templates.Keys.Any(Its))
            {
                var kept = templates.Where(l => !Its(l.Key)).ToDictionary(l => l.Key, l => l.Value);
                s = s with { Email = s.Email with { Templates = kept.Count == 0 ? null : kept } };
            }
            if (s.Legal.Texts is { } texts && texts.Keys.Any(Its))
                // The high-water marks are raised first, so a number its records held is never handed out again.
                s = s with { Legal = LegalPages.WithMarks(s.Legal) with { Texts = texts.Where(l => !Its(l.Key)).ToDictionary(l => l.Key, l => l.Value) } };
            return s;
        }, writeConfig: false);
        Write("language-deleted", name);
        Notice = $"Deleted {Languages.NativeName(name)}.";
        return Redirect("/security/languages");
    }

    public IActionResult OnPostChoose(string[]? offered, string? @default, bool confirm)
    {
        // 1. Only codes on the list, in its order; anything else is not a form this page made.
        var all = offeredLanguages.All();
        string? Known(string? code) => all.FirstOrDefault(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
        var wanted = new List<string>();
        foreach (var code in offered ?? [])
        {
            if (Known(code) is not { } known) return BadRequest();
            wanted.Add(known);
        }
        if (Known(@default) is not { } chosen) return BadRequest();
        List<string> picked = [.. all.Where(wanted.Contains)];

        // Rules 2 to 5 are checked against the copy being saved, so nothing saved breaks one.
        string? error = null;
        string? legal = null;
        IReadOnlyList<string> lines = [];
        IReadOnlyList<string> before = [];
        var oldDefault = "";
        var saved = false;
        grid.UpdateSettings(x =>
        {
            (error, legal, lines, saved) = (null, null, [], false);
            before = offeredLanguages.Current(x);
            oldDefault = LegalPages.Source(x); // the one default the legal rules and the audit read
            error = Check(x, picked, chosen, before, oldDefault, out legal, out var off);
            if (error is not null) return x;
            if (off.Count > 0 && !confirm)
            {
                lines = off;
                return x;
            }
            saved = true;
            return x with { Languages = x.Languages with { Offered = picked, Default = chosen } };
        }, writeConfig: false);

        if (error is not null)
        {
            var refused = Refuse(error);
            LegalLanguage = legal;
            return refused;
        }
        if (!saved)
        {
            Fill();
            ConfirmChoose = new PendingChoice(picked, chosen, lines);
            return Page();
        }
        if (!before.SequenceEqual(picked, StringComparer.OrdinalIgnoreCase))
            Write("languages-offered-changed", string.Join(",", picked));
        if (!string.Equals(chosen, oldDefault, StringComparison.OrdinalIgnoreCase))
            Write("language-default-changed", $"{oldDefault} \u2192 {chosen}");
        Notice = "Saved.";
        return Redirect("/security/languages");
    }

    /// <summary>
    /// Rules 2 to 4 of <see cref="OnPostChoose"/> against <paramref name="s"/>: the refusal, or null. <paramref name="legal"/> is the
    /// language whose legal pages must be written first; <paramref name="off"/> the confirmation lines for languages residents use
    /// that are being turned off (rule 5).
    /// </summary>
    private string? Check(LauncherSettings s, IReadOnlyList<string> picked, string chosen, IReadOnlyList<string> before, string oldDefault,
        out string? legal, out IReadOnlyList<string> off)
    {
        legal = null;
        off = [];
        // 2. The default is one of the offered.
        if (!picked.Contains(chosen))
            return $"Turn on {Languages.NativeName(chosen)} before making it the default.";

        // 3. An added language is turned on only once it is fully translated; one already on stays on with new texts.
        foreach (var code in picked.Where(c => !offeredLanguages.IsBuiltIn(c) && !before.Contains(c, StringComparer.OrdinalIgnoreCase)))
            if (catalog.Missing(code) is > 0 and var n)
                return $"{Languages.NativeName(code)} still has {n} texts to translate.";

        // 4. A new default must keep each legal page's source at its version: the numbers carry over, so acceptances stand.
        if (!string.Equals(chosen, oldDefault, StringComparison.OrdinalIgnoreCase) && LegalPages.DefaultBlockers(s, chosen) is { Count: > 0 } pages)
        {
            legal = chosen;
            return $"Write the {LegalPages.Titles(pages)} {(pages.Count == 1 ? "page" : "pages")} in {Languages.NativeName(chosen)} first";
        }

        // 5. Turning off a language residents chose asks first; their saved choice stays and reads as the default.
        off = [.. before.Where(c => !picked.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Select(c => (Code: c, Count: residents.CountUsing(c))).Where(c => c.Count > 0)
            .Select(c => $"{(c.Count == 1 ? "1 resident uses" : $"{c.Count} residents use")} {Languages.NativeName(c.Code)}. They will see {Languages.NativeName(chosen)} instead.")];
        return null;
    }

    private IActionResult Refuse(string error)
    {
        Fill();
        Error = error;
        return Page();
    }

    private void Fill()
    {
        var current = offeredLanguages.Current();
        var standard = offeredLanguages.Default();
        Rows = [.. offeredLanguages.All().Select(code => new Row(code, Languages.NativeName(code), offeredLanguages.IsBuiltIn(code),
            current.Contains(code, StringComparer.OrdinalIgnoreCase), string.Equals(code, standard, StringComparison.OrdinalIgnoreCase),
            offeredLanguages.IsBuiltIn(code) ? 0 : catalog.Missing(code), residents.CountUsing(code)))];
        NewTexts = Resident.NewTexts.Of(offeredLanguages, catalog);
        var have = added.Codes();
        Choices = [.. AddableCultures.Value.Where(c => !have.Contains(c.Code, StringComparer.Ordinal))];
    }

    /// <summary>Every culture the system knows that <see cref="OwnerLanguages.CodeFor"/> accepts as it is, by its native name.</summary>
    private static readonly Lazy<IReadOnlyList<Choice>> AddableCultures = new(() =>
        [.. CultureInfo.GetCultures(CultureTypes.AllCultures)
            .Where(c => c.Name.Length > 0 && OwnerLanguages.CodeFor(c.Name) == c.Name)
            .Select(c => new Choice(c.Name, $"{Languages.NativeName(c.Name)} ({c.EnglishName})"))
            .DistinctBy(c => c.Code, StringComparer.Ordinal)
            .OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)]);

    private void Write(string action, string target) =>
        audit.Write(User, HttpContext.Connection.RemoteIpAddress?.ToString(), action, target);
}
