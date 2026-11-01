using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
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
/// <item>A save ticked as a small fix keeps a written page's version (only the text and time change), so nobody is asked to accept
/// the Terms again; audited with "small fix". A new page still gets its first number.</item>
/// <item>Preview shows all four as visitors would see them and saves nothing.</item>
/// <item>The tick that the owner wrote the texts and is responsible for them is its own small form, audited as
/// <c>legal-responsibility</c> (on or off).</item>
/// </list>
/// Sign-up opens only when the three required pages are written and the tick is on (<see cref="LegalPages.SignUpGateOpen"/>).
/// </summary>
public sealed class LegalModel(IGridController grid, Func<LauncherSettings> settings, AuditLog audit, TimeProvider time, EmailSender email) : PageModel
{
    /// <summary>The owner's sign-up switch as shown or posted (an unchecked box posts nothing, which reads as off).</summary>
    [BindProperty] public bool SignUpOpen { get; set; }

    /// <summary>Whether the legal pages let sign-up open (the switch can't be turned on otherwise).</summary>
    public bool GateOpen { get; private set; }

    public bool EmailReady { get; private set; }

    /// <summary>Every bot-protection layer is off.</summary>
    public bool BotsAllOff { get; private set; }

    /// <summary>The texts as shown or posted, by page id.</summary>
    [BindProperty] public Dictionary<string, string?> Texts { get; set; } = [];

    /// <summary>The tick as posted (an unchecked box posts nothing, which reads as off).</summary>
    [BindProperty] public bool Responsible { get; set; }

    /// <summary>The save is a small fix: written pages keep their version. Never shown ticked, so a real change can't inherit it.</summary>
    [BindProperty] public bool SmallFix { get; set; }

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
                    // A cleared page keeps its version as an empty record, so the next text gets a number nobody has accepted yet.
                    if (before is not null && !string.IsNullOrWhiteSpace(before.Markdown))
                    {
                        pages[id] = new LegalText("", before.Version, now);
                        changes.Add(($"{id} cleared", "ok"));
                    }
                }
                else if (before is null || Normalize(before.Markdown) != markdown)
                {
                    // A small fix to a written page keeps its number: what people accepted still stands.
                    var keep = SmallFix && before is not null && !string.IsNullOrWhiteSpace(before.Markdown);
                    var version = keep ? before!.Version : (before?.Version ?? 0) + 1;
                    pages[id] = new LegalText(markdown, version, now);
                    changes.Add(($"{id} v{version}" + (keep ? " small fix" : ""), "ok"));
                }
            }
            if (changes.Count == 0) return s;
            all[LegalPages.DefaultLanguage] = pages;
            return s with { Legal = s.Legal with { Texts = all } };
        }, writeConfig: false);

        foreach (var (target, result) in changes)
            Write("legal-text-saved", target, result);
        Notice = changes.Count == 0 ? "Nothing changed."
            : changes.Any(c => c.Target.EndsWith(" small fix", StringComparison.Ordinal))
                ? "Saved as a small fix. The pages are live now, and nobody is asked to accept them again."
                : "Saved. The pages are live now.";
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

    /// <summary>
    /// The sign-up switch, audited as <c>signup-switch</c> (on or off). It can be turned on only while the legal pages are written
    /// and ticked (<see cref="LegalPages.SignUpGateOpen"/>); turning it off always works.
    /// </summary>
    public IActionResult OnPostSignUp()
    {
        var changed = false;
        var refused = false;
        grid.UpdateSettings(s =>
        {
            refused = SignUpOpen && !LegalPages.SignUpGateOpen(s);
            var wanted = SignUpOpen && !refused;
            changed = (s.SignUp?.Open == true) != wanted;
            return changed ? s with { SignUp = new SignUpSettings(wanted) } : s;
        }, writeConfig: false);
        if (changed)
            Write("signup-switch", SignUpOpen && !refused ? "on" : "off", "ok");
        Notice = refused ? "Sign-up can't be opened yet. Write the pages and tick the box first."
            : SignUpOpen ? "Saved. People can sign up now." : "Saved. Sign-up is closed.";
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
        GateOpen = LegalPages.SignUpGateOpen(s);
        SignUpOpen = SignUpRules.IsOpen(s);
        EmailReady = email.IsReady;
        BotsAllOff = s.Bots is { Honeypot: false, RateLimit: false, ProofOfWork: false, ImageCaptcha: false };
        if (GateOpen)
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
