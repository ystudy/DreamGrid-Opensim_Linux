using System.Text;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Logs;

/// <param name="Truncated">True when the file has earlier lines than the ones returned.</param>
public sealed record LogTail(string Source, IReadOnlyList<string> Lines, bool Truncated);

/// <summary>
/// Reads the end of the grid's log files. The source name is looked up in <see cref="Sources"/>, never used as a path.
/// </summary>
public sealed partial class LogReader(LauncherPaths paths, Func<LauncherSettings> settings)
{
    private const int FirstWindow = 64 * 1024;
    // Logs roll at 10 MB; a window beyond this means the whole file has been read.
    private const long MaxWindow = 16 * 1024 * 1024;

    private static readonly string[] Levels = ["DEBUG", "INFO", "WARN", "ERROR"];

    public IReadOnlyList<string> Sources()
    {
        var list = new List<string> { "launcher", "robust" };
        if (settings().Region is { } region) list.Add("region-" + region.Instance);
        list.Add("database");
        return list;
    }

    /// <summary>The last <paramref name="lines"/> lines at or above <paramref name="minLevel"/> (lines without a level are kept).</summary>
    /// <exception cref="ArgumentException">The source is not one of <see cref="Sources"/>, or the level is unknown.</exception>
    public LogTail Tail(string source, int lines = 200, string? minLevel = null)
    {
        if (!Sources().Contains(source, StringComparer.Ordinal))
            throw new ArgumentException("Unknown log source.", nameof(source));
        var min = 0;
        if (minLevel is not null && (min = Array.IndexOf(Levels, minLevel.ToUpperInvariant())) < 0)
            throw new ArgumentException("Unknown log level.", nameof(minLevel));
        lines = Math.Max(1, lines);

        var file = Path.Combine(paths.Logs, source == "database" ? "mariadb.err" : source + ".log");
        FileStream stream;
        try
        {
            // OpenSim and MariaDB keep writing while we read.
            stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new LogTail(source, [], false);
        }

        using (stream)
        {
            var length = stream.Length;
            var window = (long)FirstWindow;
            while (true)
            {
                window = Math.Min(window, length);
                var all = window == length;
                var kept = Parse(stream, length, window, all, min);
                if (kept.Count >= lines || all || window >= MaxWindow)
                {
                    var truncated = kept.Count > lines || !all;
                    return new LogTail(source, kept.Count > lines ? kept.GetRange(kept.Count - lines, lines) : kept, truncated);
                }
                window *= 4;
            }
        }
    }

    private static List<string> Parse(FileStream stream, long length, long window, bool all, int min)
    {
        var buffer = new byte[window];
        stream.Seek(length - window, SeekOrigin.Begin);
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var parts = text.Split('\n');
        var start = 0;
        // Unless the window starts at the file's start, its first line is a fragment.
        if (!all) start = 1;
        var end = parts.Length;
        if (end > start && parts[end - 1].Length == 0) end--; // the final newline

        var result = new List<string>(Math.Max(0, end - start));
        for (var i = start; i < end; i++)
        {
            var line = parts[i].TrimEnd('\r');
            if (min == 0 || LevelOf(line) is not { } level || level >= min)
                result.Add(line);
        }
        return result;
    }

    /// <summary>The index in <see cref="Levels"/> of the line's level, or null. Knows log4net tokens and the launcher's own.</summary>
    private static int? LevelOf(string line)
    {
        var m = LevelPattern().Match(line);
        if (!m.Success) return null;
        return m.Groups["l"].ValueSpan switch
        {
            "DEBUG" or "dbug" or "trce" => 0,
            "INFO" or "info" => 1,
            "WARN" or "warn" => 2,
            _ => 3, // ERROR, FATAL, fail, crit
        };
    }

    // log4net: "[LEVEL]" (usually " - [LEVEL]"); the launcher: "<date>Z <level> <category>: ".
    [GeneratedRegex(@"\[(?<l>DEBUG|INFO|WARN|ERROR|FATAL)\]|^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}Z (?<l>trce|dbug|info|warn|fail|crit) ")]
    private static partial Regex LevelPattern();
}
