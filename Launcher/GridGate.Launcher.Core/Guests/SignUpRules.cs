using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Core.Guests;

/// <summary>
/// What the sign-up form accepts (spec 2026-10-31-sign-up-design §4): avatar name parts of 1 to <see cref="MaxNamePart"/> letters,
/// digits or hyphens that aren't the owner's or a reserved name, and an email address of a plain shape. Each check answers null when
/// the value is fine, or one plain sentence.
/// </summary>
public static class SignUpRules
{
    public const int MaxNamePart = 31;

    /// <summary>The longest address Robust keeps (<c>UserAccounts.Email</c> is <c>varchar(64)</c>).</summary>
    public const int MaxEmail = 64;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "Test User", "Grid Owner", "Admin Admin", "Grid Admin", "System Admin",
    };

    /// <summary>Whether people can sign up now: the owner's switch is on and the legal pages are written.</summary>
    public static bool IsOpen(LauncherSettings s) => s.SignUp?.Open == true && LegalPages.SignUpGateOpen(s);

    /// <summary>What is wrong with a name, so a page can say it in the reader's language (<see cref="NameProblem"/> says it in English).</summary>
    public enum NameFault { Missing, TooLong, BadCharacters, Taken }

    /// <summary>What is wrong with an email address (<see cref="EmailProblem"/> says it in English).</summary>
    public enum EmailFault { Missing, TooLong, BadShape }

    /// <summary>Null when the name can be used, else the sentence; a name <paramref name="held"/> says is held counts as taken.</summary>
    public static string? NameProblem(string? first, string? last, LauncherSettings s, Func<string, bool>? held = null) =>
        NameCheck(first, last, s, held) switch
        {
            NameFault.Missing => "Type a first name and a last name.",
            NameFault.TooLong => $"Each name can have at most {MaxNamePart} characters.",
            NameFault.BadCharacters => "Use only letters A to Z, digits and hyphens in the name.",
            NameFault.Taken => "That name is taken. Try another.",
            _ => null,
        };

    /// <summary>Null when the name can be used, else the first thing wrong with it.</summary>
    public static NameFault? NameCheck(string? first, string? last, LauncherSettings s, Func<string, bool>? held = null)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(last)) return NameFault.Missing;
        if (first.Length > MaxNamePart || last.Length > MaxNamePart) return NameFault.TooLong;
        if (!first.All(Allowed) || !last.All(Allowed)) return NameFault.BadCharacters;
        try { GridConsoleCommands.ValidateAvatarName(first, last); }
        catch (ArgumentException) { return NameFault.BadCharacters; }
        var full = first + " " + last;
        if (s.Setup.Owner is { } owner && string.Equals(full, owner.FirstName + " " + owner.LastName, StringComparison.OrdinalIgnoreCase)
            || Reserved.Contains(full) || held?.Invoke(full) == true || Starter.StarterNames.IsModelName(first, last))
            return NameFault.Taken;
        return null;
    }

    public static string? EmailProblem(string? email) =>
        EmailCheck(email) switch
        {
            EmailFault.Missing => "Type your email address.",
            EmailFault.TooLong => $"Use an email address of at most {MaxEmail} characters.",
            EmailFault.BadShape => "That doesn't look like an email address.",
            _ => null,
        };

    /// <summary>Null when the address is fine, else the first thing wrong with it.</summary>
    public static EmailFault? EmailCheck(string? email)
    {
        var e = email?.Trim();
        if (string.IsNullOrEmpty(e)) return EmailFault.Missing;
        if (e.Length > MaxEmail) return EmailFault.TooLong;
        var at = e.IndexOf('@');
        // The console line quotes the address: no quote, backslash or leading "--" (as GridConsoleCommands.ValidateOwner).
        if (at <= 0 || at != e.LastIndexOf('@') || at == e.Length - 1 || e.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\')
            || e.StartsWith("--", StringComparison.Ordinal))
            return EmailFault.BadShape;
        return null;
    }

    /// <summary>The address as it is compared and counted: trimmed and lower-case.</summary>
    public static string NormalEmail(string email) => email.Trim().ToLowerInvariant();

    private static bool Allowed(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-';
}
