using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Guests;

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

    public static string? NameProblem(string? first, string? last, LauncherSettings s)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(last)) return "Type a first name and a last name.";
        if (first.Length > MaxNamePart || last.Length > MaxNamePart) return $"Each name can have at most {MaxNamePart} characters.";
        if (!first.All(Allowed) || !last.All(Allowed)) return "Use only letters A to Z, digits and hyphens in the name.";
        try { GridConsoleCommands.ValidateAvatarName(first, last); }
        catch (ArgumentException) { return "Use only letters A to Z, digits and hyphens in the name."; }
        var full = first + " " + last;
        if (s.Setup.Owner is { } owner && string.Equals(full, owner.FirstName + " " + owner.LastName, StringComparison.OrdinalIgnoreCase)
            || Reserved.Contains(full))
            return "That name is taken. Try another.";
        return null;
    }

    public static string? EmailProblem(string? email)
    {
        var e = email?.Trim();
        if (string.IsNullOrEmpty(e)) return "Type your email address.";
        if (e.Length > MaxEmail) return $"Use an email address of at most {MaxEmail} characters.";
        var at = e.IndexOf('@');
        // The console line quotes the address: no quote, backslash or leading "--" (as GridConsoleCommands.ValidateOwner).
        if (at <= 0 || at != e.LastIndexOf('@') || at == e.Length - 1 || e.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\')
            || e.StartsWith("--", StringComparison.Ordinal))
            return "That doesn't look like an email address.";
        return null;
    }

    /// <summary>The address as it is compared and counted: trimmed and lower-case.</summary>
    public static string NormalEmail(string email) => email.Trim().ToLowerInvariant();

    private static bool Allowed(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-';
}
