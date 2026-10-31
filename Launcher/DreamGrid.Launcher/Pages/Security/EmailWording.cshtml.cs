using DreamGrid.Launcher.Core.Email;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Security;
using MimeKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Security;

/// <summary>
/// The owner's page for the wording of the grid's emails (OwnerPolicy; <c>/security/email/wording</c>; 404 where there is no
/// guest side). One form per template: subject, body (Markdown, raw HTML off) and its fill-in words. Save refuses wording that
/// leaves out a needed word; saving the default wording drops the stored copy. Preview and Send test use what was typed, with
/// sample values, and save nothing. Saving and resetting are audited (<c>email-template-saved</c>, <c>email-template-reset</c>).
/// </summary>
public sealed class EmailWordingModel(IGridController grid, Func<LauncherSettings> settings, EmailAccounts accounts, EmailSender sender,
    EmailTestLimiter limiter, IRobustClient robust, AuditLog audit, TimeProvider time) : PageModel
{
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
        if (!EmailTemplates.All.Contains(id)) return NotFound();
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
        var d = EmailTemplates.Default(id);
        var isDefault = subject == d.Subject && body == d.Body;
        var now = time.GetUtcNow();
        grid.UpdateSettings(s => Store(s, id, isDefault ? null : new EmailTemplateText(subject, body, now)), writeConfig: false);
        Write("email-template-saved", id);
        Notice = $"Saved \"{EmailTemplates.Title(id)}\".";
        return Redirect("/security/email/wording");
    }

    public IActionResult OnPostReset(string id)
    {
        if (!EmailTemplates.All.Contains(id)) return NotFound();
        grid.UpdateSettings(s => Store(s, id, null), writeConfig: false);
        Write("email-template-reset", id);
        Notice = $"\"{EmailTemplates.Title(id)}\" uses the default wording again.";
        return Redirect("/security/email/wording");
    }

    public IActionResult OnPostPreview(string id)
    {
        if (!EmailTemplates.All.Contains(id)) return NotFound();
        Fill(id, null, null, Render(id));
        return Page();
    }

    public async Task<IActionResult> OnPostTest(string id, CancellationToken ct)
    {
        if (!EmailTemplates.All.Contains(id)) return NotFound();
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
        return EmailTemplates.Render(id, new EmailTemplateText(subject, body, time.GetUtcNow()), EmailTemplates.Sample(id, settings().GridName));
    }

    /// <summary>The typed subject on one line and the body with LF line ends, both trimmed.</summary>
    private (string Subject, string Body) Typed() =>
        ((Subject ?? "").Replace("\r", " ").Replace("\n", " ").Trim(), (Body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim());

    private void Fill(string? postedId, string? error, string? outcome, RenderedEmail? preview)
    {
        var s = settings();
        Sections = [.. EmailTemplates.All.Select(id =>
        {
            var current = EmailTemplates.Current(s, id);
            var posted = id == postedId;
            var (subject, body) = posted ? Typed() : (current.Subject, current.Body);
            var d = EmailTemplates.Default(id);
            return new Section(id, EmailTemplates.Title(id), EmailTemplates.Words(id), EmailTemplates.Required(id), subject, body,
                current.Subject == d.Subject && current.Body == d.Body, posted ? error : null, posted ? outcome : null, posted ? preview : null);
        })];
    }

    /// <summary>The settings with <paramref name="text"/> stored for the template in the default language, or removed when null.</summary>
    private static LauncherSettings Store(LauncherSettings s, string id, EmailTemplateText? text)
    {
        var all = s.Email.Templates?.ToDictionary(l => l.Key, l => l.Value) ?? [];
        var texts = all.TryGetValue(EmailTemplates.DefaultLanguage, out var current) ? new Dictionary<string, EmailTemplateText>(current) : [];
        if (text is null) texts.Remove(id);
        else texts[id] = text;
        if (texts.Count == 0) all.Remove(EmailTemplates.DefaultLanguage);
        else all[EmailTemplates.DefaultLanguage] = texts;
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
