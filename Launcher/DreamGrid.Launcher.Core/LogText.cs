namespace DreamGrid.Launcher.Core;

/// <summary>Untrusted values made safe for a log line.</summary>
public static class LogText
{
    public const int MaxChars = 64;

    /// <summary>Control characters (CR, LF, tab…) become '?', and at most <paramref name="max"/> characters are kept.</summary>
    public static string Clean(string? value, int max = MaxChars)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var chars = value.Take(max).Select(c => char.IsControl(c) ? '?' : c).ToArray();
        return new string(chars) + (value.Length > max ? "..." : "");
    }
}
