using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Guests;

/// <summary>
/// The new email addresses waiting for their link, one per person, with when each was asked (spec
/// 2026-10-31-resident-account-design §6). A new request replaces the old one. An address is kept only until its link is used or
/// runs out: <see cref="Set"/> drops entries older than the age it is given. Kept in <c>file</c>
/// (<c>&lt;data&gt;/guests/email-changes.json</c>, written atomically, in the owner-only data folder). Only a missing file holds
/// nothing. A file that can't be read throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> from every call.
/// A file that isn't valid JSON reads as empty, and the next write first moves it aside as
/// <c>email-changes.json.damaged-&lt;utc yyyyMMddHHmmss&gt;</c>, with a warning. Safe to call from any thread.
/// </summary>
public sealed class PendingEmailChanges(string file, TimeProvider time, ILogger<PendingEmailChanges>? log = null)
{
    /// <summary>How long a waiting address is kept when <see cref="Set"/> tidies the file: the change-email link's lifetime.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromHours(24);

    private sealed record Entry(string Email, DateTimeOffset Asked);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();

    /// <summary>Records <paramref name="normalEmail"/> as the address <paramref name="principal"/> asked for now, replacing an earlier one.</summary>
    public void Set(Guid principal, string normalEmail)
    {
        lock (_gate)
        {
            var (waiting, damaged) = Read();
            var cutoff = time.GetUtcNow() - KeepFor;
            foreach (var old in waiting.Where(p => p.Value.Asked < cutoff).Select(p => p.Key).ToList()) waiting.Remove(old);
            waiting[principal] = new Entry(normalEmail, time.GetUtcNow());
            Write(waiting, damaged);
        }
    }

    /// <summary>The address <paramref name="principal"/> is waiting for, or null when none or it was asked more than <paramref name="maxAge"/> ago.</summary>
    public string? Get(Guid principal, TimeSpan maxAge)
    {
        lock (_gate)
            return Read().Waiting.TryGetValue(principal, out var e) && e.Asked >= time.GetUtcNow() - maxAge ? e.Email : null;
    }

    public void Remove(Guid principal)
    {
        lock (_gate)
        {
            var (waiting, damaged) = Read();
            if (waiting.Remove(principal)) Write(waiting, damaged);
        }
    }

    private (Dictionary<Guid, Entry> Waiting, bool Damaged) Read()
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
            return (JsonSerializer.Deserialize<Dictionary<Guid, Entry>>(json, Options) ?? [], false);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return ([], true);
        }
    }

    private void Write(Dictionary<Guid, Entry> waiting, bool damaged)
    {
        if (damaged)
        {
            var aside = file + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(file, aside, overwrite: true);
            log?.LogWarning("The waiting email changes {File} could not be read and were set aside as {Aside}; those links no longer work.",
                file, Path.GetFileName(aside));
        }
        if (Path.GetDirectoryName(Path.GetFullPath(file)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(waiting, Options));
    }
}
