using System.Globalization;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Resident;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Security;

/// <summary>
/// The owner's Languages page (OwnerPolicy; <c>/security/languages</c>; 404 where there is no guest side): every built-in and added
/// language with whether it is offered and the default, what an added one still misses (linking to its translate page) and how many
/// residents chose it. The owner adds a language from the cultures the system knows (an empty file, then its translate page) and
/// deletes an added one that is not offered, after a confirmation that shows how many residents use it; their saved choice is left
/// and falls back to the default. A language code only ever comes from <see cref="OwnerLanguages.CodeFor"/>. Audited as
/// <c>language-added</c> and <c>language-deleted</c> with the target <c>code</c>.
/// </summary>
public sealed class LanguagesModel(OwnerLanguages added, LanguageCatalog catalog, OfferedLanguages offered, ResidentLanguages residents,
    AuditLog audit) : PageModel
{
    /// <summary>One language's row of the table.</summary>
    public sealed record Row(string Code, string Name, bool BuiltIn, bool Offered, bool Default, int Missing, int Residents);

    /// <summary>A language that can be added: its code and "native name (English name)".</summary>
    public sealed record Choice(string Code, string Label);

    [TempData] public string? Notice { get; set; }

    public string? Error { get; private set; }

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public IReadOnlyList<Choice> Choices { get; private set; } = [];

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
        if (offered.Current().Contains(name, StringComparer.OrdinalIgnoreCase))
            return Refuse($"{Languages.NativeName(name)} is offered to residents. Turn it off first, then delete it.");
        if (!confirm)
        {
            Fill();
            ConfirmDelete = Rows.Single(r => r.Code == name);
            return Page();
        }
        if (!added.Delete(name))
            return Refuse($"{Languages.NativeName(name)} could not be deleted. Try again in a moment.");
        Write("language-deleted", name);
        Notice = $"Deleted {Languages.NativeName(name)}.";
        return Redirect("/security/languages");
    }

    private IActionResult Refuse(string error)
    {
        Fill();
        Error = error;
        return Page();
    }

    private void Fill()
    {
        var current = offered.Current();
        var standard = offered.Default();
        Rows = [.. offered.All().Select(code => new Row(code, Languages.NativeName(code), offered.IsBuiltIn(code),
            current.Contains(code, StringComparer.OrdinalIgnoreCase), string.Equals(code, standard, StringComparison.OrdinalIgnoreCase),
            offered.IsBuiltIn(code) ? 0 : catalog.Missing(code), residents.CountUsing(code)))];
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

    private void Write(string action, string target)
    {
        var (name, actorId, role) = AuditActor.Of(User);
        audit.Write(new AuditEntry(name, actorId, role, HttpContext.Connection.RemoteIpAddress?.ToString(), action, target, "ok"));
    }
}
