using System.Reflection;

namespace GridGate.Launcher.Core.Security;

/// <summary>
/// What a new password must be: typed the same twice, at least <see cref="MinLength"/> characters, not on the shipped list of
/// common passwords (<c>common-passwords.txt</c>, compared ignoring case) and not the avatar's first name, last name or full
/// name. The list ships with GridGate; nothing is looked up over the network.
/// </summary>
public static class PasswordRules
{
    /// <summary>What is wrong with a new password, so a page can say it in the reader's language (<see cref="Problem"/> says it in English).</summary>
    public enum Fault { Mismatch, TooShort, TooCommon, IsName }

    public const int MinLength = 8;

    private static readonly Lazy<HashSet<string>> Common = new(LoadCommon);

    /// <summary>Null when the password is fine, otherwise one plain sentence (English) saying what to change.</summary>
    public static string? Problem(string? password, string? again, string first, string last) =>
        Check(password, again, first, last) switch
        {
            Fault.Mismatch => "Type the same password twice.",
            Fault.TooShort => "Use at least 8 characters.",
            Fault.TooCommon => "That password is too common. Choose another.",
            Fault.IsName => "Don't use your avatar's name as the password.",
            _ => null,
        };

    /// <summary>Null when the password is fine, otherwise the first thing wrong with it.</summary>
    public static Fault? Check(string? password, string? again, string first, string last)
    {
        password ??= "";
        if (password != (again ?? "")) return Fault.Mismatch;
        if (password.Length < MinLength) return Fault.TooShort;
        if (Common.Value.Contains(password)) return Fault.TooCommon;
        var p = password.Trim();
        if (p.Equals(first, StringComparison.OrdinalIgnoreCase) || p.Equals(last, StringComparison.OrdinalIgnoreCase)
            || p.Equals($"{first} {last}", StringComparison.OrdinalIgnoreCase))
            return Fault.IsName;
        return null;
    }

    private static HashSet<string> LoadCommon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GridGate.Launcher.Core.Security.common-passwords.txt")
            ?? throw new InvalidOperationException("The common-passwords list is missing from the build.");
        using var reader = new StreamReader(stream);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length > 0 && !line.StartsWith('#')) set.Add(line);
        }
        return set;
    }
}
