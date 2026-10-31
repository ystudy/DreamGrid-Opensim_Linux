namespace DreamGrid.Launcher.Core.Email;

/// <summary>How the connection to a mail server is secured. Never "auto": a named provider can't be talked down to plain text.</summary>
public enum MailSecurity
{
    /// <summary>Plain connection upgraded with STARTTLS (usually port 587); refused if the server doesn't offer it.</summary>
    StartTls,

    /// <summary>TLS from the first byte (usually port 465).</summary>
    SslOnConnect,

    /// <summary>No encryption: only for a mail server on the owner's own network (Other server only).</summary>
    None,
}

/// <summary>
/// One card on the Email page: a free mail service the owner can send through, with its server settings, its free limits (a
/// guide, since several providers don't publish them) and the steps to get a password for the grid.
/// </summary>
public sealed record EmailProvider(
    string Key,
    string Name,
    string Kind,
    string LimitText,
    string Needs,
    string Host,
    int Port,
    MailSecurity Security,
    int? PerDay,
    int? PerHour,
    string UserLabel,
    string PasswordLabel,
    string FromHint,
    IReadOnlyList<string> Steps,
    string? HelpUrl);

/// <summary>
/// The provider cards, in the order the Email page shows them: the free services with roughly 200 or more emails a day (researched
/// 2026-10-31, design §3), then Other server for anything else. Limits change; this list is the one place to fix them.
/// </summary>
public static class EmailProviders
{
    /// <summary>The key of the Other server card, the only one whose server settings the owner types.</summary>
    public const string Custom = "custom";

    private const string Mailbox = "Personal mailbox";
    private const string Service = "Sending service";

    public static IReadOnlyList<EmailProvider> All { get; } =
    [
        new("gmail", "Gmail", Mailbox, "about 500 a day", "2-Step Verification and an app password",
            "smtp.gmail.com", 587, MailSecurity.StartTls, 500, null, "Gmail address", "App password",
            "Must be your Gmail address, or an alias set up in Gmail.",
            ["Turn on 2-Step Verification in your Google account.", "Make an app password for the grid.", "Paste the app password below."],
            "https://myaccount.google.com/apppasswords"),
        new("yahoo", "Yahoo Mail", Mailbox, "about 500 a day", "An app password",
            "smtp.mail.yahoo.com", 465, MailSecurity.SslOnConnect, 500, null, "Yahoo address", "App password",
            "Must be your Yahoo address.",
            ["Open Account Info, then Account security.", "Generate an app password for the grid.", "Paste the app password below."],
            "https://login.yahoo.com/account/security"),
        new("yandex", "Yandex", Mailbox, "about 500 a day", "An app password (it starts working after 2 to 3 hours)",
            "smtp.yandex.com", 465, MailSecurity.SslOnConnect, 500, null, "Yandex address", "App password",
            "Must be your Yandex address.",
            ["Open Yandex ID, then Security, then App passwords.", "Make one for \"Email client\".", "Paste it below. Wait 2 to 3 hours before you test."],
            "https://id.yandex.com/security/app-passwords"),
        new("icloud", "iCloud Mail", Mailbox, "about 200 a day for new accounts, up to about 1,000", "Two-step sign-in and an app-specific password",
            "smtp.mail.me.com", 587, MailSecurity.StartTls, 200, null, "iCloud address", "App-specific password",
            "Must be your iCloud address or one of its aliases.",
            ["Sign in to your Apple Account.", "Open Sign-In and Security, then App-Specific Passwords.", "Make one for the grid and paste it below."],
            "https://account.apple.com"),
        new("sendpulse", "SendPulse", Service, "400 a day, 50 an hour, 12,000 a month", "A free account and a sending address confirmed there",
            "smtp-pulse.com", 465, MailSecurity.SslOnConnect, 400, 50, "SMTP login", "SMTP password",
            "Must be an address you confirmed in SendPulse.",
            ["Make a free SendPulse account.", "Turn on SMTP and confirm your sending address.", "Copy the SMTP login and password from the SMTP settings page."],
            "https://sendpulse.com/features/smtp"),
        new("brevo", "Brevo", Service, "300 a day", "A free account, a sender confirmed there and an SMTP key",
            "smtp-relay.brevo.com", 587, MailSecurity.StartTls, 300, null, "SMTP login", "SMTP key",
            "Must be a sender you confirmed in Brevo.",
            ["Make a free Brevo account.", "Add and confirm your sending address.", "Open SMTP & API, make an SMTP key and paste it below."],
            "https://app.brevo.com/settings/keys/smtp"),
        new("mailjet", "Mailjet", Service, "200 a day, 6,000 a month", "A free account and a sender confirmed there",
            "in-v3.mailjet.com", 587, MailSecurity.StartTls, 200, null, "API key", "Secret key",
            "Must be a sender you confirmed in Mailjet.",
            ["Make a free Mailjet account.", "Confirm your sending address.", "Copy the API key (username) and the secret key (password)."],
            "https://app.mailjet.com/account/apikeys"),
        new(Custom, "Other server", "Custom", "You set the limits", "Server, port, username and password from your provider or web host",
            "", 587, MailSecurity.StartTls, null, null, "Username", "Password",
            "Must be an address your provider lets you send from.",
            ["Ask your provider or web host for its SMTP settings.", "Fill in everything below, including the server details."],
            null),
    ];

    public static EmailProvider? Find(string? key) => All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));
}
