using System.Reflection;

namespace DreamGrid.Launcher.Core.Security;

/// <summary>
/// What a new password must be: typed the same twice, at least <see cref="MinLength"/> characters, not on the shipped list of
/// common passwords (<c>common-passwords.txt</c>, compared ignoring case) and not the avatar's first name, last name or full
/// name. The list ships with DreamGrid; nothing is looked up over the network.
/// </summary>
public static class PasswordRules
{
    public const int MinLength = 8;

    private static readonly Lazy<HashSet<string>> Common = new(LoadCommon);

    /// <summary>Null when the password is fine, otherwise one plain sentence saying what to change.</summary>
    public static string? Problem(string? password, string? again, string first, string last)
    {
        password ??= "";
        if (password != (again ?? "")) return "Type the same password twice.";
        if (password.Length < MinLength) return "Use at least 8 characters.";
        if (Common.Value.Contains(password)) return "That password is too common. Choose another.";
        var p = password.Trim();
        if (p.Equals(first, StringComparison.OrdinalIgnoreCase) || p.Equals(last, StringComparison.OrdinalIgnoreCase)
            || p.Equals($"{first} {last}", StringComparison.OrdinalIgnoreCase))
            return "Don't use your avatar's name as the password.";
        return null;
    }

    private static HashSet<string> LoadCommon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DreamGrid.Launcher.Core.Security.common-passwords.txt")
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
