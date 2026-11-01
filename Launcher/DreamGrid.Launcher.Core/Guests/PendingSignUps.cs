using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Guests;

/// <summary>
/// The avatars sign-up made that are still waiting for their email link, with when each was made (spec 2026-10-31-sign-up-design
/// §5). This list, not the level, is what tells "waiting for email" from "suspended": both sit at level −1 in Robust. Only listed
/// avatars are ever deleted by the clean-up. Kept in <c>file</c> (<c>&lt;data&gt;/guests/pending-signups.json</c>, written
/// atomically), which holds PrincipalIDs and times only. Only a missing file lists nothing. A file that can't be read throws
/// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> from every call. A file that isn't valid JSON reads as
/// empty, and the next write first moves it aside as <c>pending-signups.json.damaged-&lt;utc yyyyMMddHHmmss&gt;</c>, with a warning.
/// Safe to call from any thread.
/// </summary>
public sealed class PendingSignUps(string file, TimeProvider time, ILogger<PendingSignUps>? log = null)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();

    /// <summary>Lists <paramref name="principal"/> as made now (replacing an earlier entry).</summary>
    public void Add(Guid principal)
    {
        lock (_gate)
        {
            var (made, damaged) = Read();
            made[principal] = time.GetUtcNow();
            Write(made, damaged);
        }
    }

    public bool Contains(Guid principal)
    {
        lock (_gate) return Read().Made.ContainsKey(principal);
    }

    public void Remove(Guid principal)
    {
        lock (_gate)
        {
            var (made, damaged) = Read();
            if (made.Remove(principal)) Write(made, damaged);
        }
    }

    /// <summary>The listed avatars made more than <paramref name="age"/> ago.</summary>
    public IReadOnlyList<Guid> OlderThan(TimeSpan age)
    {
        var cutoff = time.GetUtcNow() - age;
        lock (_gate) return Read().Made.Where(p => p.Value < cutoff).Select(p => p.Key).ToList();
    }

    private (Dictionary<Guid, DateTimeOffset> Made, bool Damaged) Read()
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
            return (JsonSerializer.Deserialize<Dictionary<Guid, DateTimeOffset>>(json, Options) ?? [], false);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return ([], true);
        }
    }

    private void Write(Dictionary<Guid, DateTimeOffset> made, bool damaged)
    {
        if (damaged)
        {
            var aside = file + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(file, aside, overwrite: true);
            log?.LogWarning("The pending sign-ups list {File} could not be read and was set aside as {Aside}; those sign-ups are no longer cleaned up.",
                file, Path.GetFileName(aside));
        }
        if (Path.GetDirectoryName(Path.GetFullPath(file)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(made, Options));
    }
}
