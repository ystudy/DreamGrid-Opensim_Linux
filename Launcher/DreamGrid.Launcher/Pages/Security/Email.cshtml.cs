using DreamGrid.Launcher.Core.Email;
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
/// The owner's Email page (OwnerPolicy; <c>/security/email</c>; 404 where there is no guest side): a card for each provider and
/// one for each saved account. A card opens its pop-up (a <c>dialog</c>; <c>?edit=&lt;key&gt;</c> renders it open without
/// script), where the owner types the account's details. The password field is never filled in from the store: left blank, the
/// stored password stays. A named provider's server settings come from <see cref="EmailProviders"/> whatever is posted; only
/// Other server takes them from the form. Saving, removing, moving and pausing are audited (<c>email-account-*</c>), as is a
/// test send (<c>email-test-sent</c>); no audit line carries a password.
/// </summary>
public sealed class EmailModel(EmailAccounts accounts, EmailSender sender, EmailTestLimiter limiter, IRobustClient robust,
    Func<LauncherSettings> settings, AuditLog audit) : PageModel
{
    public const string NotReadyText = "Email isn't set up yet. Sign-up and password reset stay hidden until it is.";
    public const string NoneWarning = "Mail and password go over the network unencrypted. Use only for a mail server on your own network.";
    public const string TooManyTests = "You can send 5 test emails a minute. Wait a moment and try again.";

    [BindProperty] public Guid? Id { get; set; }
    [BindProperty] public string? Provider { get; set; }
    [BindProperty] public string? UserName { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? FromName { get; set; }
    [BindProperty] public string? FromAddress { get; set; }
    [BindProperty] public string? ReplyTo { get; set; }
    [BindProperty] public string? PerDay { get; set; }
    [BindProperty] public string? PerHour { get; set; }
    [BindProperty] public string? Host { get; set; }
    [BindProperty] public string? Port { get; set; }
    [BindProperty] public string? Security { get; set; }
    [BindProperty] public string? TestTo { get; set; }

    [TempData] public string? Notice { get; set; }

    /// <summary>One card and its pop-up, with the values the pop-up shows.</summary>
    public sealed record Card(
        string Key,
        EmailProvider Provider,
        EmailAccount? Account,
        AccountStatus? Status,
        string UserName,
        string FromName,
        string FromAddress,
        string ReplyTo,
        string PerDay,
        string PerHour,
        string Host,
        string Port,
        MailSecurity Security,
        bool Open,
        string? Error,
        string? Outcome);

    public IReadOnlyList<Card> Cards { get; private set; } = [];

    public EmailStatus Status { get; private set; } = null!;

    public bool Ready { get; private set; }

    public void OnGet(string? edit) => Fill(edit, null, null);

    public IActionResult OnPostSave()
    {
        var (account, error) = Build(requirePassword: !(Id is { } id && accounts.Find(id)?.ProtectedPassword is not null) && string.IsNullOrEmpty(Password));
        if (account is null) return Reopen(error, null);
        if (!accounts.Save(account, Password))
            return Reopen("The email settings couldn't be saved. Try again.", null);
        Write("email-account-saved", Target(account), "ok");
        Notice = "Saved.";
        return Redirect("/security/email");
    }

    public async Task<IActionResult> OnPostTest(CancellationToken ct)
    {
        var stored = Id is { } id ? accounts.Find(id) : null;
        var (account, error) = Build(requirePassword: stored?.ProtectedPassword is null && string.IsNullOrEmpty(Password));
        if (account is null) return Reopen(error, null);
        var password = string.IsNullOrEmpty(Password) && stored is not null ? accounts.PasswordOf(stored) : Password;
        if (string.IsNullOrEmpty(password)) return Reopen("Type the password.", null);

        var to = string.IsNullOrWhiteSpace(TestTo) ? await OwnerEmailAsync(ct) : TestTo.Trim();
        if (string.IsNullOrEmpty(to) || !MailboxAddress.TryParse(to, out _))
            return Reopen("Type an address to send the test to.", null);
        if (!limiter.TryTake(User.Identity?.Name ?? "owner"))
            return Reopen(null, TooManyTests);

        var s = settings();
        var email = EmailTemplates.Render(EmailTemplates.Test, EmailTemplates.Current(s, EmailTemplates.Test), EmailTemplates.Sample(EmailTemplates.Test, s.GridName));
        var outcome = await sender.SendTestAsync(account, password, to, email, ct);
        Write("email-test-sent", Target(account), outcome.Result == SendResult.Sent ? "ok" : outcome.Result.ToString());
        return Reopen(null, outcome.Result == SendResult.Sent ? $"{outcome.Plain}. Look for it at {to}." : outcome.Plain);
    }

    public IActionResult OnPostRemove(Guid id)
    {
        if (accounts.Find(id) is { } a && accounts.Remove(id))
        {
            Write("email-account-removed", Target(a), "ok");
            Notice = "Removed. Its password is deleted.";
        }
        return Redirect("/security/email");
    }

    public IActionResult OnPostMove(Guid id, int delta)
    {
        if (accounts.Find(id) is { } a && accounts.Move(id, Math.Sign(delta)))
            Write("email-account-moved", Target(a), delta < 0 ? "up" : "down");
        return Redirect("/security/email");
    }

    public IActionResult OnPostPause(Guid id, bool paused)
    {
        if (accounts.Find(id) is { } a && accounts.SetPaused(id, paused))
            Write(paused ? "email-account-paused" : "email-account-resumed", Target(a), "ok");
        return Redirect("/security/email");
    }

    /// <summary>"1st", "2nd", "3rd", "4th"… for a card's place in the order.</summary>
    public static string Ordinal(int n) => n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    /// <summary>The state line on a saved card.</summary>
    public static string StateOf(AccountStatus s) =>
        s.Account.Paused ? "Paused"
        : s.Account.Problem is { } p ? "Needs attention: " + p
        : s.FullUntil is { } until ? "Full until " + until.ToLocalTime().ToString("HH:mm")
        : s.RestingUntil is { } rest ? "Trying again at " + rest.ToLocalTime().ToString("HH:mm")
        : "In use";

    private IActionResult Reopen(string? error, string? outcome)
    {
        var key = Id is { } id && accounts.Find(id) is not null ? id.ToString("N") : (EmailProviders.Find(Provider)?.Key ?? EmailProviders.Custom);
        Fill(key, error, outcome, posted: true);
        return Page();
    }

    /// <summary>The account as posted, checked; or null and the sentence saying what is wrong.</summary>
    private (EmailAccount? Account, string? Error) Build(bool requirePassword)
    {
        var stored = Id is { } id ? accounts.Find(id) : null;
        var provider = EmailProviders.Find(stored?.Provider ?? Provider);
        if (provider is null) return (null, "Choose a card first.");
        var custom = provider.Key == EmailProviders.Custom;

        if (string.IsNullOrWhiteSpace(UserName)) return (null, "Type the username.");
        if (requirePassword) return (null, "Type the password.");
        if (!IsAddress(FromAddress)) return (null, "The \"From\" address isn't a valid email address.");
        if (!string.IsNullOrWhiteSpace(ReplyTo) && !IsAddress(ReplyTo)) return (null, "The \"Reply-to\" address isn't a valid email address.");
        if (!Limit(PerDay, out var perDay) || !Limit(PerHour, out var perHour)) return (null, "Limits are blank or a number from 1 to 100000.");

        string host = provider.Host;
        int port = provider.Port;
        var security = provider.Security;
        if (custom)
        {
            host = (Host ?? "").Trim();
            if (host.Length == 0 || host.Any(char.IsWhiteSpace)) return (null, "Type the server name.");
            if (!int.TryParse(Port, out port) || port is < 1 or > 65535) return (null, "The port is a number from 1 to 65535.");
            if (!Enum.TryParse(Security, out security) || !Enum.IsDefined(security)) return (null, "Choose how the connection is secured.");
        }

        var fromName = string.IsNullOrWhiteSpace(FromName) ? settings().GridName : FromName.Trim();
        return (new EmailAccount(stored?.Id ?? Guid.NewGuid(), provider.Key, host, port, security, UserName.Trim(), null, fromName,
            FromAddress!.Trim(), string.IsNullOrWhiteSpace(ReplyTo) ? null : ReplyTo.Trim(), perDay, perHour, stored?.Paused ?? false, 0,
            null, null), null);
    }

    private void Fill(string? openKey, string? error, string? outcome, bool posted = false)
    {
        Status = sender.Status();
        Ready = sender.IsReady;
        var saved = Status.Accounts.Select(s => FromAccount(s, openKey, posted, error, outcome));
        // One card per named provider that has no account yet; Other server can always add another.
        var used = Status.Accounts.Select(s => s.Account.Provider).ToHashSet();
        var fresh = EmailProviders.All.Where(p => p.Key == EmailProviders.Custom || !used.Contains(p.Key)).Select(p => FromProvider(p, openKey, posted, error, outcome));
        Cards = [.. saved, .. fresh];
    }

    private Card FromAccount(AccountStatus s, string? openKey, bool posted, string? error, string? outcome)
    {
        var key = s.Account.Id.ToString("N");
        var open = key == openKey;
        var p = EmailProviders.Find(s.Account.Provider) ?? EmailProviders.Find(EmailProviders.Custom)!;
        var a = s.Account;
        return open && posted
            ? Posted(key, p, a, s, error, outcome)
            : new Card(key, p, a, s, a.UserName, a.FromName, a.FromAddress, a.ReplyTo ?? "", a.PerDay?.ToString() ?? "", a.PerHour?.ToString() ?? "",
                a.Host, a.Port.ToString(), a.Security, open, null, null);
    }

    private Card FromProvider(EmailProvider p, string? openKey, bool posted, string? error, string? outcome)
    {
        var open = p.Key == openKey;
        return open && posted
            ? Posted(p.Key, p, null, null, error, outcome)
            : new Card(p.Key, p, null, null, "", settings().GridName, "", "", p.PerDay?.ToString() ?? "", p.PerHour?.ToString() ?? "",
                p.Host, p.Port.ToString(), p.Security, open, null, null);
    }

    /// <summary>The pop-up again with what was typed (never the password), after an error or a test.</summary>
    private Card Posted(string key, EmailProvider p, EmailAccount? a, AccountStatus? s, string? error, string? outcome)
    {
        var custom = p.Key == EmailProviders.Custom;
        var security = custom && Enum.TryParse<MailSecurity>(Security, out var sec) ? sec : (a?.Security ?? p.Security);
        return new Card(key, p, a, s, UserName ?? "", FromName ?? "", FromAddress ?? "", ReplyTo ?? "", PerDay ?? "", PerHour ?? "",
            custom ? Host ?? "" : p.Host, custom ? Port ?? "" : p.Port.ToString(), security, true, error, outcome);
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

    private static bool IsAddress(string? text) =>
        !string.IsNullOrWhiteSpace(text) && MailboxAddress.TryParse(text.Trim(), out var m) && m.Address.Contains('@') && !m.Address.EndsWith('@');

    private static bool Limit(string? text, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!int.TryParse(text, out var n) || n is < 1 or > 100_000) return false;
        value = n;
        return true;
    }

    private static string Target(EmailAccount a) => $"{a.Provider} {a.Id:N}";

    private void Write(string action, string target, string result)
    {
        var (name, id, role) = AuditActor.Of(User);
        audit.Write(new AuditEntry(name, id, role, HttpContext.Connection.RemoteIpAddress?.ToString(), action, target, result));
    }
}
