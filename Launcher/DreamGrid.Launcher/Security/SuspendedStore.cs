using System.Text.Json;
using DreamGrid.Launcher.Core;

namespace DreamGrid.Launcher.Security;

/// <summary>
/// The level each suspended person had, so unsuspending gives it back. Suspending sets the level to −1 in Robust, which keeps
/// nothing of the old one. Kept in <c>file</c> (<c>&lt;data&gt;/security/suspended.json</c>, written atomically); a missing file
/// remembers nothing, and a damaged one is read as empty and replaced by the next write (an unsuspended person then gets level 0).
/// Safe to call from any thread.
/// </summary>
public sealed class SuspendedStore(string file)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();

    /// <summary>Remembers <paramref name="previousLevel"/> for <paramref name="principal"/> (replacing an earlier one). Throws <see cref="IOException"/> when it can't be saved.</summary>
    public void Remember(Guid principal, int previousLevel)
    {
        lock (_gate)
        {
            var levels = Read();
            levels[principal] = previousLevel;
            Write(levels);
        }
    }

    /// <summary>The level remembered for <paramref name="principal"/>, or null.</summary>
    public int? PreviousLevel(Guid principal)
    {
        lock (_gate) return Read().TryGetValue(principal, out var level) ? level : null;
    }

    /// <summary>Drops what is remembered for <paramref name="principal"/>.</summary>
    public void Forget(Guid principal)
    {
        lock (_gate)
        {
            var levels = Read();
            if (levels.Remove(principal)) Write(levels);
        }
    }

    private Dictionary<Guid, int> Read()
    {
        try
        {
            if (!File.Exists(file)) return [];
            string json;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
                json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<Dictionary<Guid, int>>(json, Options) ?? [];
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Write(Dictionary<Guid, int> levels)
    {
        // The host makes the folder owner-only at start; this only matters where it is missing.
        if (Path.GetDirectoryName(Path.GetFullPath(file)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(levels, Options));
    }
}
