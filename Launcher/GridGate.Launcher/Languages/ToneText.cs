namespace GridGate.Launcher.Resident;

/// <summary>
/// The resident-page wording the owner may change per language (plan 16b): the headings and introductions that set the grid's tone.
/// The owner's text is plain text with <c>{grid}</c> as its only placeholder; it is always HTML-encoded when shown, and a key not
/// listed here never takes an owner's override. Every key's English has no placeholder, or only the grid's name as <c>{0}</c>.
/// </summary>
public static class ToneText
{
    private static readonly (string Key, string Label)[] Entries =
    [
        ("Home.Title", "Front page: page title"),
        ("Home.Intro", "Front page: introduction"),
        ("SignIn.Intro", "Sign in: introduction"),
        ("SignUp.Intro", "Create account: introduction"),
        ("ForgotPassword.Intro", "Forgot password: introduction"),
        ("Guest.Title", "Your account: page title"),
        ("Support.Title", "Ask for help: heading"),
        ("Support.Intro", "Ask for help: introduction"),
        ("Connect.Help", "How to connect: help heading"),
    ];

    /// <summary>The keys the owner may reword.</summary>
    public static IReadOnlyList<string> Keys { get; } = [.. Entries.Select(e => e.Key)];

    /// <summary>A short English label for the editor.</summary>
    public static string Title(string key) => Entries.FirstOrDefault(e => e.Key == key).Label ?? key;

    /// <summary>
    /// The owner's text as a format string for <c>T["Key", gridName]</c>: every brace is escaped, then <c>{grid}</c> becomes <c>{0}</c>,
    /// so no text can throw a <see cref="FormatException"/> or reach another argument.
    /// </summary>
    public static string ToFormat(string ownerText) =>
        ownerText.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal)
            .Replace("{{grid}}", "{0}", StringComparison.Ordinal);
}
