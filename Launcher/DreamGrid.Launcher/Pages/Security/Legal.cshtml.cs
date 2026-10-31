using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Security;

/// <summary>
/// The owner's editor for the guest-facing pages (OwnerPolicy; <c>/security/legal</c>; 404 where there is no guest side): one text
/// for each of Terms, Privacy, Cookies and Contact in the default language, written in Markdown. DreamGrid supplies no wording.
/// <list type="bullet">
/// <item>Save: each page whose text changed gets the next version number and the time; an unchanged page keeps its version; a page
/// with only whitespace is cleared (it stops existing). Audited as <c>legal-text-saved</c> (the page id and its new version, or
/// "cleared").</item>
/// <item>Preview shows all four as visitors would see them and saves nothing.</item>
/// <item>The tick that the owner wrote the texts and is responsible for them is its own small form, audited as
/// <c>legal-responsibility</c> (on or off).</item>
/// </list>
/// Sign-up opens only when the three required pages are written and the tick is on (<see cref="LegalPages.SignUpGateOpen"/>).
/// </summary>
public sealed class LegalModel(IGridController grid, Func<LauncherSettings> settings, AuditLog audit, TimeProvider time) : PageModel
{
    /// <summary>The texts as shown or posted, by page id.</summary>
    [BindProperty] public Dictionary<string, string?> Texts { get; set; } = [];

    /// <summary>The tick as posted (an unchecked box posts nothing, which reads as off).</summary>
    [BindProperty] public bool Responsible { get; set; }

    /// <summary>The previews of the texts written, in page order; empty unless Preview was pressed.</summary>
    public IReadOnlyList<(string Title, string Html)> Previews { get; private set; } = [];

    /// <summary>The gate sentence: sign-ups can be opened, or what is still missing.</summary>
    public string GateText { get; private set; } = "";

    [TempData] public string? Notice { get; set; }

    public void OnGet() => Fill();

    public IActionResult OnPostSave()
    {
        var changes = new List<(string Target, string Result)>();
        var now = time.GetUtcNow();
        grid.UpdateSettings(s =>
        {
            changes.Clear(); // the update may run again against a newer copy
            var all = s.Legal.Texts?.ToDictionary(l => l.Key, l => l.Value) ?? [];
            var pages = all.TryGetValue(LegalPages.DefaultLanguage, out var current)
                ? new Dictionary<string, LegalText>(current)
                : [];
            foreach (var id in LegalPages.All)
            {
                if (!Texts.TryGetValue(id, out var typed)) continue; // not on the form: left alone
                var markdown = Normalize(typed);
                pages.TryGetValue(id, out var before);
                if (string.IsNullOrWhiteSpace(markdown))
                {
                    if (before is not null && pages.Remove(id))
                        changes.Add(($"{id} cleared", "ok"));
                }
                else if (before is null || Normalize(before.Markdown) != markdown)
                {
                    var version = (before?.Version ?? 0) + 1;
                    pages[id] = new LegalText(markdown, version, now);
                    changes.Add(($"{id} v{version}", "ok"));
                }
            }
            if (changes.Count == 0) return s;
            if (pages.Count == 0) all.Remove(LegalPages.DefaultLanguage);
            else all[LegalPages.DefaultLanguage] = pages;
            return s with
            {
                Legal = s.Legal with { Texts = all.Count == 0 ? null : all },
            };
        }, writeConfig: false);

        foreach (var (target, result) in changes)
            Write("legal-text-saved", target, result);
        Notice = changes.Count == 0 ? "Nothing changed." : "Saved. The pages are live now.";
        return Redirect("/security/legal");
    }

    public IActionResult OnPostPreview()
    {
        Previews = [.. LegalPages.All
            .Where(id => Texts.TryGetValue(id, out var t) && !string.IsNullOrWhiteSpace(t))
            .Select(id => (LegalPages.Title(id), LegalMarkdown.ToHtml(Texts[id])))];
        SetGate();
        return Page();
    }

    public IActionResult OnPostResponsibility()
    {
        var now = time.GetUtcNow();
        var changed = false;
        grid.UpdateSettings(s =>
        {
            changed = s.Legal.OperatorResponsible != Responsible;
            return changed
                ? s with { Legal = s.Legal with { OperatorResponsible = Responsible, ResponsibleSince = Responsible ? now : null } }
                : s;
        }, writeConfig: false);
        if (changed)
            Write("legal-responsibility", Responsible ? "on" : "off", "ok");
        Notice = Responsible ? "Saved. You are marked as responsible for these pages." : "Saved. You are no longer marked as responsible for these pages.";
        return Redirect("/security/legal");
    }

    private void Fill()
    {
        var s = settings();
        Texts = LegalPages.All.ToDictionary(id => id, id => (string?)(LegalPages.Get(s, id)?.Markdown ?? ""));
        Responsible = s.Legal.OperatorResponsible;
        SetGate();
    }

    private void SetGate()
    {
        var s = settings();
        Responsible = s.Legal.OperatorResponsible;
        if (LegalPages.SignUpGateOpen(s))
        {
            GateText = "Sign-ups can be opened";
            return;
        }
        var missing = LegalPages.Missing(s).Select(LegalPages.Title).ToList();
        if (!s.Legal.OperatorResponsible) missing.Add("the box saying you are responsible");
        GateText = "Before sign-ups can be opened: " + string.Join(", ", missing);
    }

    /// <summary>Browsers post a textarea with CRLF line ends; the text is kept with LF.</summary>
    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    private void Write(string action, string target, string result)
    {
        var (name, id, role) = AuditActor.Of(User);
        audit.Write(new AuditEntry(name, id, role, HttpContext.Connection.RemoteIpAddress?.ToString(), action, target, result));
    }
}
