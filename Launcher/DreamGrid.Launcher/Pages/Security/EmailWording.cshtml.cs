using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Resident;
using DreamGrid.Launcher.Security;
using MimeKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Security;

/// <summary>
/// The owner's page for the wording of the grid's emails (OwnerPolicy; <c>/security/email/wording</c>; 404 where there is no
/// guest side), per language (<c>?lang=</c>, any built-in or added language; the grid's default when missing or unknown). One form
/// per template: subject, body (Markdown, raw HTML off) and its fill-in words, starting from the built-in wording in that language
/// (for an added language its file's, English per text where it has none). Emails to staff are English only, so they are edited
/// under English alone. Save refuses wording that leaves out a needed word; saving the built-in wording drops the stored copy.
/// Preview and Send test use what was typed, with sample values in the language, and save nothing. Saving and resetting are
/// audited (<c>email-template-saved</c>, <c>email-template-reset</c>, the target "&lt;language&gt; &lt;template&gt;").
/// </summary>
public sealed class EmailWordingModel(IGridController grid, Func<LauncherSettings> settings, EmailAccounts accounts, EmailSender sender,
    EmailTestLimiter limiter, IRobustClient robust, AuditLog audit, TimeProvider time, OfferedLanguages offeredLanguages, OwnerLanguages added) : PageModel
{
    /// <summary>The language asked for (<c>?lang=</c>); see <see cref="Language"/> for the one edited.</summary>
    [BindProperty(SupportsGet = true, Name = "lang")] public string? Lang { get; set; }

    /// <summary>The language edited: <see cref="Lang"/> when it is built in or added, else the grid's default.</summary>
    public string Language { get; private set; } = Languages.English;

    /// <summary>Every built-in and added language, for the selector.</summary>
    public IReadOnlyList<string> Choices { get; private set; } = [];

    /// <summary>Whether the language edited is English, the only one the emails to staff are written in.</summary>
    public bool IsEnglish => Language == Languages.English;

    [BindProperty] public string? Subject { get; set; }
    [BindProperty] public string? Body { get; set; }
    [BindProperty] public string? TestTo { get; set; }

    [TempData] public string? Notice { get; set; }

    /// <summary>One template's form: what it shows, and after a post its error, test outcome or preview.</summary>
    public sealed record Section(string Id, string Title, IReadOnlyList<string> Words, IReadOnlyList<string> Required, string Subject, string Body,
        bool IsDefault, string? Error, string? Outcome, RenderedEmail? Preview);

    public IReadOnlyList<Section> Sections { get; private set; } = [];

    public void OnGet() => Fill(null, null, null, null);

    public IActionResult OnPostSave(string id)
    {
        if (!Shown(id)) return NotFound();
        var (subject, body) = Typed();
        var missing = EmailTemplates.MissingWords(id, subject, body);
        if (missing.Count > 0)
        {
            Fill(id, $"This email needs {string.Join(" and ", missing)}.", null, null);
            return Page();
        }
        if (subject.Length == 0 || body.Length == 0)
        {
            Fill(id, "Type a subject and a message.", null, null);
            return Page();
        }
        var d = EmailTemplates.Default(id, Language, added);
        // The built-in text may carry CRLF line ends (a resource file checked out on Windows); what was typed has LF.
        var isDefault = subject == d.Subject.Trim() && body == d.Body.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var now = time.GetUtcNow();
        var language = Language;
        grid.UpdateSettings(s => Store(s, language, id, isDefault ? null : new EmailTemplateText(subject, body, now)), writeConfig: false);
        Write("email-template-saved", $"{language} {id}");
        Notice = $"Saved \"{EmailTemplates.Title(id)}\" in {Languages.NativeName(language)}.";
        return Redirect(Here);
    }

    public IActionResult OnPostReset(string id)
    {
        if (!Shown(id)) return NotFound();
        var language = Language;
        grid.UpdateSettings(s => Store(s, language, id, null), writeConfig: false);
        Write("email-template-reset", $"{language} {id}");
        Notice = $"\"{EmailTemplates.Title(id)}\" uses the default wording in {Languages.NativeName(language)} again.";
        return Redirect(Here);
    }

    public IActionResult OnPostPreview(string id)
    {
        if (!Shown(id)) return NotFound();
        Fill(id, null, null, Render(id));
        return Page();
    }

    public async Task<IActionResult> OnPostTest(string id, CancellationToken ct)
    {
        if (!Shown(id)) return NotFound();
        var account = accounts.All().FirstOrDefault(a => !a.Paused && a.Problem is null && accounts.PasswordOf(a) is not null);
        if (account is null)
        {
            Fill(id, null, "Set up an email account first.", null);
            return Page();
        }
        var to = string.IsNullOrWhiteSpace(TestTo) ? await OwnerEmailAsync(ct) : TestTo.Trim();
        if (string.IsNullOrEmpty(to) || !MailboxAddress.TryParse(to, out _))
        {
            Fill(id, null, "Type an address to send the test to.", null);
            return Page();
        }
        if (!limiter.TryTake(User.Identity?.Name ?? "owner"))
        {
            Fill(id, null, EmailModel.TooManyTests, null);
            return Page();
        }
        var outcome = await sender.SendTestAsync(account, accounts.PasswordOf(account)!, to, Render(id), ct);
        Write("email-test-sent", $"{account.Provider} {account.Id:N} {id}", outcome.Result == SendResult.Sent ? "ok" : outcome.Result.ToString());
        Fill(id, null, outcome.Result == SendResult.Sent ? $"{outcome.Plain}. Look for it at {to}." : outcome.Plain, null);
        return Page();
    }

    private RenderedEmail Render(string id)
    {
        var (subject, body) = Typed();
        // The sample's "30 minutes" and reason are in the language edited, as a resident would read them.
        var values = new Dictionary<string, string>(EmailTemplates.Sample(id, settings().GridName));
        if (values.ContainsKey("ExpiresIn")) values["ExpiresIn"] = EmailTemplates.Words("ExpiresIn.30m", Language, added);
        if (values.ContainsKey("Reason")) values["Reason"] = EmailTemplates.Words("Reason.Copyright", Language, added);
        return EmailTemplates.Render(id, new EmailTemplateText(subject, body, time.GetUtcNow()), values);
    }

    /// <summary>Sets <see cref="Language"/> and <see cref="Choices"/>; done before every handler runs.</summary>
    public override void OnPageHandlerExecuting(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext context)
    {
        Choices = offeredLanguages.All();
        var asked = Choices.FirstOrDefault(c => string.Equals(c, Lang, StringComparison.OrdinalIgnoreCase));
        Language = asked ?? offeredLanguages.Default();
        // A post for a language deleted in another tab must not land on the default language's wording.
        if (asked is null && !string.IsNullOrEmpty(Lang) && HttpMethods.IsPost(Request.Method))
            context.Result = NotFound();
    }

    /// <summary>The templates on the page: every one in English, the emails to residents in any other language.</summary>
    private IEnumerable<string> Templates => EmailTemplates.All.Where(id => IsEnglish || !EmailTemplates.IsStaffEmail(id));

    private bool Shown(string id) => Templates.Contains(id);

    private string Here => "/security/email/wording?lang=" + Uri.EscapeDataString(Language);

    /// <summary>The typed subject on one line and the body with LF line ends, both trimmed.</summary>
    private (string Subject, string Body) Typed() =>
        ((Subject ?? "").Replace("\r", " ").Replace("\n", " ").Trim(), (Body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim());

    private void Fill(string? postedId, string? error, string? outcome, RenderedEmail? preview)
    {
        var s = settings();
        Sections = [.. Templates.Select(id =>
        {
            var current = EmailTemplates.Current(s, id, Language, added);
            var posted = id == postedId;
            var (subject, body) = posted ? Typed() : (current.Subject, current.Body);
            var d = EmailTemplates.Default(id, Language, added);
            return new Section(id, EmailTemplates.Title(id), EmailTemplates.Words(id), EmailTemplates.Required(id), subject, body,
                current.Subject == d.Subject && current.Body == d.Body, posted ? error : null, posted ? outcome : null, posted ? preview : null);
        })];
    }

    /// <summary>The settings with <paramref name="text"/> stored for the template in <paramref name="language"/>, or removed when null.</summary>
    private static LauncherSettings Store(LauncherSettings s, string language, string id, EmailTemplateText? text)
    {
        var all = s.Email.Templates?.ToDictionary(l => l.Key, l => l.Value) ?? [];
        var texts = all.TryGetValue(language, out var current) ? new Dictionary<string, EmailTemplateText>(current) : [];
        if (text is null) texts.Remove(id);
        else texts[id] = text;
        if (texts.Count == 0) all.Remove(language);
        else all[language] = texts;
        return s with { Email = s.Email with { Templates = all.Count == 0 ? null : all } };
    }

    private async Task<string?> OwnerEmailAsync(CancellationToken ct)
    {
        var owner = settings().Setup.Owner;
        if (owner is null) return null;
        try
        {
            var account = await robust.FindAccountAsync(owner.FirstName, owner.LastName, ct);
            return string.IsNullOrWhiteSpace(account?.Email) ? null : account.Email;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Write(string action, string target, string result = "ok")
    {
        var (name, actorId, role) = AuditActor.Of(User);
        audit.Write(new AuditEntry(name, actorId, role, HttpContext.Connection.RemoteIpAddress?.ToString(), action, target, result));
    }
}
