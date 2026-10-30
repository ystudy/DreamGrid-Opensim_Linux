using System.Globalization;
using System.Text.Json;
using DreamGrid.Launcher.Core;

namespace DreamGrid.Launcher.Security;

/// <summary>
/// The level each suspended person had, so unsuspending gives it back. Suspending sets the level to −1 in Robust, which keeps
/// nothing of the old one. Kept in <c>file</c> (<c>&lt;data&gt;/security/suspended.json</c>, written atomically). Only a missing
/// file remembers nothing. A file that can't be read (access denied, something in the way) throws <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/> from every call, so nothing is restored to 0 and no write replaces it. A file that
/// isn't valid JSON reads as empty (an unsuspended person then gets level 0), and the next write first moves it aside as
/// <c>suspended.json.damaged-&lt;utc yyyyMMddHHmmss&gt;</c>, with a warning. Safe to call from any thread.
/// </summary>
public sealed class SuspendedStore(string file, ILogger<SuspendedStore>? log = null)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();

    /// <summary>Remembers <paramref name="previousLevel"/> for <paramref name="principal"/> (replacing an earlier one). Throws <see cref="IOException"/> when it can't be read or saved.</summary>
    public void Remember(Guid principal, int previousLevel)
    {
        lock (_gate)
        {
            var (levels, damaged) = Read();
            levels[principal] = previousLevel;
            Write(levels, damaged);
        }
    }

    /// <summary>The level remembered for <paramref name="principal"/>, or null. Throws <see cref="IOException"/> when the file can't be read.</summary>
    public int? PreviousLevel(Guid principal)
    {
        lock (_gate) return Read().Levels.TryGetValue(principal, out var level) ? level : null;
    }

    /// <summary>Drops what is remembered for <paramref name="principal"/>. Throws <see cref="IOException"/> when it can't be read or saved.</summary>
    public void Forget(Guid principal)
    {
        lock (_gate)
        {
            var (levels, damaged) = Read();
            if (levels.Remove(principal)) Write(levels, damaged);
        }
    }

    /// <summary>The remembered levels; <c>Damaged</c> when the file is there but isn't valid JSON (then none).</summary>
    private (Dictionary<Guid, int> Levels, bool Damaged) Read()
    {
        string json;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            json = reader.ReadToEnd();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return ([], false);
        }

        try
        {
            return (JsonSerializer.Deserialize<Dictionary<Guid, int>>(json, Options) ?? [], false);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return ([], true);
        }
    }

    private void Write(Dictionary<Guid, int> levels, bool damaged)
    {
        if (damaged)
        {
            var aside = file + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(file, aside, overwrite: true);
            log?.LogWarning("The suspended list {File} could not be read and was set aside as {Aside}; the levels it remembered are lost.",
                file, Path.GetFileName(aside));
        }
        // The host makes the folder owner-only at start; this only matters where it is missing.
        if (Path.GetDirectoryName(Path.GetFullPath(file)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(levels, Options));
    }
}
