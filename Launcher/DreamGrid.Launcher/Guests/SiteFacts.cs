namespace DreamGrid.Launcher.Guests;

/// <summary>
/// Facts about this software for the operator, who writes the Cookies and Privacy pages: which cookies the site sets and what it
/// stores about people. It is not legal text. Every cookie the site sets is listed here, and a test proves it.
/// </summary>
public static class SiteFacts
{
    /// <summary>Each cookie the site sets, and what it is for.</summary>
    public static IReadOnlyList<(string Name, string Purpose)> Cookies { get; } =
    [
        ("dg-auth", "Keeps a staff member signed in to the staff pages."),
        ("dg-2fa", "Remembers that the password was right while the two-factor code is typed. It lasts 5 minutes."),
        ("dg-setup", "Carries the setup link's key from page to page until the owner exists."),
        ("dg-guest", "Keeps a guest signed in to the guest pages."),
        ("dg-af", "Protects forms from being sent by other websites."),
        ("dg-notice", "Carries a one-time message to the next page after a form was sent."),
    ];

    /// <summary>What the software stores about people, and where.</summary>
    public static IReadOnlyList<(string What, string Where)> Stored { get; } =
    [
        ("Avatar name and email", "The grid's account service."),
        ("Sign-in times", "audit.log, and the staff sessions (security/sessions.json)."),
        ("IP addresses", "audit.log, security/throttle.json and the staff sessions (security/sessions.json)."),
        ("The browser each staff session was started from, as the browser names itself", "security/sessions.json."),
        ("Two-factor sign-in: the avatar name, the authenticator secret (encrypted) and the backup codes (only as hashes)",
            "security/twofactor.json."),
        ("The level a suspended account had before, by account id", "security/suspended.json."),
        ("Guests' account ids, with a value that signs them out everywhere when it changes", "guests/stamps.json."),
        ("Account ids, with the email links sent to them (only as hashes) and when they expire", "guests/email-tokens.json."),
        ("Terms acceptances, with the IP address", "guests/terms.json."),
        ("Region rights", "guests/region-rights.json."),
        ("Mail account settings, with their passwords (encrypted)", "email/accounts.json."),
        ("Emails waiting to be sent: the address and the message (encrypted), until sent or expired", "email/outbox.json."),
        ("Emails that couldn't be sent: when, which email, the avatar name and why, for 7 days", "email/problems.json."),
    ];
}
