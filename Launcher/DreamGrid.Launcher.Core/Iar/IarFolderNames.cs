using System.Globalization;
using System.Text.RegularExpressions;

namespace DreamGrid.Launcher.Core.Iar;

/// <summary>The folder an upload goes into, and the folder names that may go on a console line.</summary>
public static partial class IarFolderNames
{
    /// <summary><c>Imported yyyy-MM-dd</c>, by the UTC date.</summary>
    public static string Imported(DateTimeOffset now) => "Imported " + now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary><paramref name="baseName"/>, or <c>baseName (2)</c>, <c>(3)</c> and so on: the first not in <paramref name="taken"/>, ignoring case.</summary>
    public static string Free(string baseName, IEnumerable<string> taken)
    {
        var names = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (var n = 2; ; n++)
            if (!names.Contains($"{baseName} ({n})")) return $"{baseName} ({n})";
    }

    /// <summary>Letters, digits, spaces and <c>. _ ( ) -</c>, 1 to 64 characters, not starting with <c>-</c> (OpenSim would read an option).</summary>
    public static bool IsSafe(string name) => SafeName().IsMatch(name) && !name.StartsWith('-');

    [GeneratedRegex(@"^[\p{L}\p{N} ._()-]{1,64}$")]
    private static partial Regex SafeName();
}
