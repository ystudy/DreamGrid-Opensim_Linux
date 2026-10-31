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
        ("Sign-in times", "audit.log, and the staff sessions."),
        ("IP addresses", "audit.log, security/throttle.json and the staff sessions."),
        ("Terms acceptances, with the IP address", "guests/terms.json."),
        ("Region rights", "guests/region-rights.json."),
    ];
}
