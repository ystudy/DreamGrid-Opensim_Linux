using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Resident;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Security;

/// <summary>
/// The owner's editor for the guest-facing pages (OwnerPolicy; <c>/security/legal</c>; 404 where there is no guest side): one text
/// for each of Terms, Privacy, Cookies and Contact, written in Markdown, per language (<c>?lang=</c>, any built-in or added language;
/// the grid's default when missing or unknown). DreamGrid supplies no wording. The default language's texts are the source
/// (<see cref="LegalPages.Source"/>); every other language's are translations.
/// <list type="bullet">
/// <item>Save: each page whose text changed gets the next version number and the time; an unchanged page keeps its version; a page
/// with only whitespace is cleared (it stops existing). Audited as <c>legal-text-saved</c> (the page id and its new version, or
/// "cleared").</item>
/// <item>Saving a translation stamps each changed page with the source's current version (audited as "de terms v3"), so it shows
/// until the source is saved again; a page the source hasn't written can't be translated. A translation behind the source is marked
/// out of date.</item>
/// <item>A save of the source ticked as a small fix keeps a written page's version (only the text and time change), so nobody is asked to accept
/// the Terms again; audited with "small fix". A new page still gets its first number.</item>
/// <item>Preview shows all four as visitors would see them and saves nothing.</item>
/// <item>The tick that the owner wrote the texts and is responsible for them is its own small form, audited as
/// <c>legal-responsibility</c> (on or off).</item>
/// </list>
/// Sign-up opens only when the three required pages are written and the tick is on (<see cref="LegalPages.SignUpGateOpen"/>).
/// </summary>
public sealed class LegalModel(IGridController grid, Func<LauncherSettings> settings, AuditLog audit, TimeProvider time, EmailSender email,
    OfferedLanguages offeredLanguages) : PageModel
{
    /// <summary>The language asked for (<c>?lang=</c>, or the form's); see <see cref="Language"/> for the one edited.</summary>
    [BindProperty(SupportsGet = true, Name = "lang")] public string? Lang { get; set; }

    /// <summary>The language edited: <see cref="Lang"/> when it is built in or added, else the source.</summary>
    public string Language { get; private set; } = Core.Languages.English;

    /// <summary>The source language: the grid's default.</summary>
    public string Source { get; private set; } = Core.Languages.English;

    public bool IsSource => string.Equals(Language, Source, StringComparison.OrdinalIgnoreCase);

    /// <summary>One language of the selector: its code, its name and whether any of its pages is behind the source.</summary>
    public sealed record LanguageChoice(string Code, string Name, bool OutOfDate);

    public IReadOnlyList<LanguageChoice> Choices { get; private set; } = [];

    /// <summary>The pages of a translation whose version is not the source's.</summary>
    public IReadOnlySet<string> OutOfDate { get; private set; } = new HashSet<string>();

    /// <summary>
    /// The pages of a translation the owner ticked as still matching the current version: unchanged, they take the source's version
    /// anyway. Shown only on pages that are out of date; never shown ticked.
    /// </summary>
    [BindProperty] public Dictionary<string, bool> Matches { get; set; } = [];

    /// <summary>Why a save was refused, or null.</summary>
    public string? Error { get; private set; }

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
        Resolve();
        return IsSource ? SaveSource() : SaveTranslation();
    }

    private IActionResult SaveSource()
    {
        var changes = new List<(string Target, string Result)>();
        var now = time.GetUtcNow();
        var language = Language;
        grid.UpdateSettings(s =>
        {
            changes.Clear(); // the update may run again against a newer copy
            var all = s.Legal.Texts?.ToDictionary(l => l.Key, l => l.Value) ?? [];
            var pages = all.TryGetValue(language, out var current)
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
                    // Otherwise one more than the page's highest number in any language: never reused, never backwards.
                    var version = keep ? before!.Version : LegalPages.NextVersion(s, id);
                    pages[id] = new LegalText(markdown, version, now);
                    changes.Add(($"{id} v{version}" + (keep ? " small fix" : ""), "ok"));
                }
            }
            if (changes.Count == 0) return s;
            all[language] = pages;
            return s with { Legal = s.Legal with { Texts = all } };
        }, writeConfig: false);

        foreach (var (target, result) in changes)
            Write("legal-text-saved", target, result);
        Notice = changes.Count == 0 ? "Nothing changed."
            : changes.Any(c => c.Target.EndsWith(" small fix", StringComparison.Ordinal))
                ? "Saved as a small fix. The pages are live now, and nobody is asked to accept them again."
                : "Saved. The pages are live now.";
        return Redirect(Here);
    }

    /// <summary>
    /// Saves a translation. A page whose text changed takes the source's current version (<see cref="LegalPages.SourceOf"/>); an
    /// unchanged page keeps its version unless the owner ticked that it still matches the current version (<see cref="Matches"/>).
    /// A page the source hasn't written is refused, and then nothing is saved. The checks run against the copy being saved.
    /// </summary>
    private IActionResult SaveTranslation()
    {
        var language = Language;
        var changes = new List<string>();
        var unwritten = new List<string>();
        var now = time.GetUtcNow();
        grid.UpdateSettings(s =>
        {
            changes.Clear(); // the update may run again against a newer copy
            unwritten.Clear();
            var all = s.Legal.Texts?.ToDictionary(l => l.Key, l => l.Value) ?? [];
            var pages = all.TryGetValue(language, out var current) ? new Dictionary<string, LegalText>(current) : [];
            foreach (var id in LegalPages.All)
            {
                if (!Texts.TryGetValue(id, out var typed)) continue; // not on the form: left alone
                var markdown = Normalize(typed);
                pages.TryGetValue(id, out var before);
                var written = before is not null && !string.IsNullOrWhiteSpace(before.Markdown);
                var changed = !written || Normalize(before!.Markdown) != markdown;
                if (string.IsNullOrWhiteSpace(markdown))
                {
                    if (written)
                    {
                        pages[id] = new LegalText("", before!.Version, now);
                        changes.Add($"{language} {id} cleared");
                    }
                    continue;
                }
                var (code, original) = LegalPages.SourceOf(s, id);
                if (original is null)
                {
                    unwritten.Add(id);
                    continue;
                }
                // English standing in for a default with no record of the page: it is the source of that page, numbered as one.
                var version = string.Equals(code, language, StringComparison.OrdinalIgnoreCase)
                    ? (changed ? LegalPages.NextVersion(s, id) : before!.Version)
                    : original.Version;
                if (changed || (Matches.GetValueOrDefault(id) && before!.Version != version))
                {
                    pages[id] = new LegalText(markdown, version, now);
                    changes.Add($"{language} {id} v{version}");
                }
            }
            if (unwritten.Count > 0 || changes.Count == 0) return s;
            all[language] = pages;
            return s with { Legal = s.Legal with { Texts = all } };
        }, writeConfig: false);

        if (unwritten.Count > 0)
        {
            Error = string.Join(" ", unwritten.Select(id => $"Write {LegalPages.Title(id)} in {Core.Languages.NativeName(Source)} first, then translate it."));
            SetChoices(settings());
            SetGate();
            return Page();
        }
        foreach (var target in changes)
            Write("legal-text-saved", target, "ok");
        Notice = changes.Count == 0 ? "Nothing changed." : "Saved. The pages are live now.";
        return Redirect(Here);
    }

    /// <summary>This editor in the language edited: <c>/security/legal</c> for the source, else with <c>?lang=</c>.</summary>
    private string Here => IsSource ? "/security/legal" : "/security/legal?lang=" + Uri.EscapeDataString(Language);

    public IActionResult OnPostPreview()
    {
        Resolve();
        SetChoices(settings());
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
        Resolve();
        var s = settings();
        Texts = LegalPages.All.ToDictionary(id => id, id => (string?)(LegalPages.Exact(s, id, Language)?.Markdown ?? ""));
        Responsible = s.Legal.OperatorResponsible;
        SetChoices(s);
        SetGate();
    }

    /// <summary>Sets <see cref="Source"/> and <see cref="Language"/> from the settings and <see cref="Lang"/>.</summary>
    private void Resolve()
    {
        var all = offeredLanguages.All();
        var source = LegalPages.Source(settings());
        Source = all.FirstOrDefault(c => string.Equals(c, source, StringComparison.OrdinalIgnoreCase)) ?? source;
        Language = all.FirstOrDefault(c => string.Equals(c, Lang, StringComparison.OrdinalIgnoreCase)) ?? Source;
    }

    /// <summary>The selector's languages, and the pages of the one edited that are behind the source.</summary>
    private void SetChoices(LauncherSettings s)
    {
        IEnumerable<string> Behind(string code) => LegalPages.All.Where(id => LegalPages.SourceOf(s, id) is var (from, src)
            && !string.Equals(code, from, StringComparison.OrdinalIgnoreCase)
            && LegalPages.Exact(s, id, code) is { } t && src is not null && t.Version != src.Version);
        Choices = [.. offeredLanguages.All().Select(c => new LanguageChoice(c, Core.Languages.NativeName(c), Behind(c).Any()))];
        OutOfDate = Behind(Language).ToHashSet();
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
