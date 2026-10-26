namespace DreamGrid.Launcher.Core.Security;

/// <summary>
/// A typed "First Last" avatar name, split the same way by the sign-in page and the port-8001 command API, so both
/// share one <see cref="LoginThrottle"/> key per avatar.
/// </summary>
/// <param name="Key">The throttle key: "First Last" with single spacing, or the trimmed text when it has no last name.</param>
public readonly record struct AvatarName(string First, string Last, string Key)
{
    public bool IsComplete => First.Length > 0 && Last.Length > 0;

    /// <summary>
    /// The first name only, cleaned for a log line. Never the rest of what was typed: a password pasted into the
    /// name field must not reach the log.
    /// </summary>
    public string ForLog => IsComplete ? LogText.Clean(First, 32) : "(an incomplete name)";

    /// <summary>Trims, splits on the first whitespace character, and trims the last name.</summary>
    public static AvatarName Parse(string? typed)
    {
        var name = (typed ?? "").Trim();
        var at = -1;
        for (var i = 0; i < name.Length; i++)
        {
            if (!char.IsWhiteSpace(name[i])) continue;
            at = i;
            break;
        }
        var first = at < 0 ? "" : name[..at];
        var last = at < 0 ? "" : name[(at + 1)..].Trim();
        return new AvatarName(first, last, first.Length == 0 || last.Length == 0 ? name : $"{first} {last}");
    }
}
