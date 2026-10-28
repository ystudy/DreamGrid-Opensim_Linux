using System.Text;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>One OAR in the library.</summary>
public sealed record OarEntry(string Name, long Bytes, DateTimeOffset Modified);

/// <summary>
/// The OAR library: the <c>*.oar</c> files directly in <see cref="LauncherPaths.Oars"/>. Names come from the browser,
/// so every name is checked before it becomes a path.
/// </summary>
public sealed class OarLibrary(LauncherPaths paths)
{
    private const int MaxNameLength = 120;

    /// <summary>The library's files, newest first.</summary>
    public IReadOnlyList<OarEntry> List()
    {
        if (!Directory.Exists(paths.Oars)) return [];
        return new DirectoryInfo(paths.Oars).EnumerateFiles("*.oar")
            .Where(f => IsSafeName(f.Name))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new OarEntry(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))
            .ToList();
    }

    /// <summary>The full path of a library file, or null for an unsafe or missing name.</summary>
    public string? Resolve(string name)
    {
        if (!IsSafeName(name)) return null;
        var full = Path.GetFullPath(Path.Combine(paths.Oars, name));
        var dir = Path.GetDirectoryName(full);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (dir is null || !string.Equals(dir, Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Oars)), comparison))
            return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>True for a plain file name: no <c>/</c>, <c>\</c> or <c>"</c>, not starting with a dot, ending in <c>.oar</c> in any case, at most 120 characters.</summary>
    public static bool IsSafeName(string name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength
        && name.IndexOfAny(['/', '\\', '"']) < 0
        && name[0] != '.'
        && name.EndsWith(".oar", StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns a browser's file name into a safe one: last path segment, odd characters become <c>_</c>, at most 120 characters, ending in <c>.oar</c>.</summary>
    public static string CleanName(string uploaded)
    {
        var name = uploaded[(uploaded.LastIndexOfAny(['/', '\\']) + 1)..];
        var clean = new StringBuilder(name.Length);
        foreach (var c in name)
            clean.Append(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '\'' or '(' or ')' ? c : '_');
        var text = clean.ToString().TrimStart('.');
        var hasExtension = text.EndsWith(".oar", StringComparison.OrdinalIgnoreCase);
        var stem = hasExtension ? text[..^4] : text;
        var extension = hasExtension ? text[^4..] : ".oar";
        if (stem.Length > MaxNameLength - 4) stem = stem[..(MaxNameLength - 4)];
        if (stem.Length == 0) stem = "region";
        return stem + extension;
    }

    /// <summary><paramref name="name"/> if no such file exists, else <c>name-2.oar</c>, <c>name-3.oar</c>, and so on.</summary>
    public string FreeName(string name)
    {
        if (!File.Exists(Path.Combine(paths.Oars, name))) return name;
        var stem = name.EndsWith(".oar", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem}-{n}.oar";
            if (!File.Exists(Path.Combine(paths.Oars, candidate))) return candidate;
        }
    }

    /// <summary>Deletes a library file; an unsafe or missing name does nothing.</summary>
    public void Delete(string name)
    {
        if (Resolve(name) is { } path) File.Delete(path);
    }

    /// <summary>Empties the staging folders (<c>.incoming</c> and <c>.saving</c>), leaving the library alone.</summary>
    public void ClearStaging()
    {
        foreach (var dir in new[] { paths.OarsIncoming, paths.OarsSaving })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                try
                {
                    if (entry is DirectoryInfo d) d.Delete(true); else entry.Delete();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
