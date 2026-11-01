using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DreamGrid.Launcher.Core.Guests;

/// <summary>Where a sign-up stands (spec 2026-11-01-sign-up-approval-design §3).</summary>
public enum SignUpState { WaitingForEmail, WaitingForStaff, Unlisted, SelfApproved, Approved, LetIn, Denied }

/// <summary>
/// One sign-up on the board. <see cref="Made"/> never changes; <see cref="LastLink"/> is when the newest confirm link went out;
/// <see cref="Changed"/> and <see cref="By"/> say when, and which staff member, last moved it. <see cref="Greeted"/> is set once the
/// person has seen "You're approved" after a staff approval.
/// </summary>
public sealed record SignUpEntry(Guid Principal, string Name, DateTimeOffset Made, DateTimeOffset LastLink, SignUpState State,
    DateTimeOffset Changed, string? By, bool Greeted = false)
{
    public bool Waiting => State is SignUpState.WaitingForEmail or SignUpState.WaitingForStaff;
}

/// <summary>
/// The sign-ups board (spec 2026-11-01-sign-up-approval-design §3, growing the pending list of 2026-10-31-sign-up-design §5). This
/// list, not the level, tells "waiting" from "suspended": both sit at level −1 in Robust. <see cref="SignUpState.Unlisted"/> entries
/// (a sign-up with an address already on the grid, or one carried over from the old list) are on the list at −1 but never shown and
/// never approved. Only listed avatars are ever deleted by the clean-up. Kept in <c>file</c>
/// (<c>&lt;data&gt;/guests/pending-signups.json</c>, written atomically), which holds PrincipalIDs, avatar names, states and times only:
/// never an email, password or token. The old format (a PrincipalID-to-time map) reads as unlisted entries. Only a missing file lists
/// nothing. A file that can't be read throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> from every call. A
/// file that isn't valid JSON reads as empty, and the next write first moves it aside as
/// <c>pending-signups.json.damaged-&lt;utc yyyyMMddHHmmss&gt;</c>, with a warning. Safe to call from any thread.
/// </summary>
public sealed class SignUpBoard(string file, TimeProvider time, ILogger<SignUpBoard>? log = null)
{
    /// <summary>A waiting sign-up is deleted this long after it was made.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromDays(30);
    /// <summary>A finished row leaves the board this long after its state changed.</summary>
    public static readonly TimeSpan ShowFinishedFor = TimeSpan.FromDays(14);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();

    /// <summary>Lists <paramref name="principal"/> as made now in <paramref name="state"/> (replacing an earlier entry).</summary>
    public void Add(Guid principal, string name, SignUpState state)
    {
        var now = time.GetUtcNow();
        Change(all => all[principal] = new SignUpEntry(principal, name, now, now, state, now, null));
    }

    /// <summary>A new confirm link went out now.</summary>
    public void Linked(Guid principal) => Update(principal, e => e with { LastLink = time.GetUtcNow() });

    /// <summary>On the list at −1: waiting, or unlisted.</summary>
    public bool Contains(Guid principal) =>
        Get(principal) is { } e && (e.Waiting || e.State == SignUpState.Unlisted);

    public SignUpEntry? Get(Guid principal)
    {
        lock (_gate) return Read().All.GetValueOrDefault(principal);
    }

    /// <summary>
    /// Moves a waiting entry (or an unlisted one, to <see cref="SignUpState.SelfApproved"/> only) to <paramref name="to"/>, by
    /// <paramref name="by"/>. False, and nothing changes, when it isn't there or was already moved: only one mover ever wins.
    /// </summary>
    public bool TryFinish(Guid principal, SignUpState to, string? by)
    {
        lock (_gate)
        {
            var (all, damaged) = Read();
            if (!all.TryGetValue(principal, out var e)) return false;
            if (!(e.Waiting || e.State == SignUpState.Unlisted && to == SignUpState.SelfApproved)) return false;
            all[principal] = e with { State = to, Changed = time.GetUtcNow(), By = by };
            Write(all, damaged);
            return true;
        }
    }

    public void SetName(Guid principal, string name) => Update(principal, e => e with { Name = name });

    public void MarkGreeted(Guid principal) => Update(principal, e => e with { Greeted = true });

    public void Remove(Guid principal)
    {
        lock (_gate)
        {
            var (all, damaged) = Read();
            if (all.Remove(principal)) Write(all, damaged);
        }
    }

    /// <summary>Every entry the board shows: all but the unlisted.</summary>
    public IReadOnlyList<SignUpEntry> Shown()
    {
        lock (_gate) return Read().All.Values.Where(e => e.State != SignUpState.Unlisted).ToList();
    }

    public int WaitingCount()
    {
        lock (_gate) return Read().All.Values.Count(e => e.Waiting);
    }

    /// <summary>
    /// Entries the clean-up deletes: waiting ones made more than <see cref="WaitLimit"/> ago, and unlisted ones whose last link went
    /// out more than <paramref name="linkLifetime"/> ago.
    /// </summary>
    public IReadOnlyList<SignUpEntry> ToDelete(TimeSpan linkLifetime)
    {
        var now = time.GetUtcNow();
        lock (_gate)
            return Read().All.Values.Where(e =>
                e.Waiting && e.Made < now - WaitLimit || e.State == SignUpState.Unlisted && e.LastLink < now - linkLifetime).ToList();
    }

    /// <summary>Drops finished entries whose state changed more than <see cref="ShowFinishedFor"/> ago. The accounts are untouched.</summary>
    public void PruneFinished()
    {
        var cutoff = time.GetUtcNow() - ShowFinishedFor;
        Change(all =>
        {
            foreach (var e in all.Values.Where(e => !e.Waiting && e.State != SignUpState.Unlisted && e.Changed < cutoff).ToList())
                all.Remove(e.Principal);
        });
    }

    private void Update(Guid principal, Func<SignUpEntry, SignUpEntry> change) => Change(all =>
    {
        if (all.TryGetValue(principal, out var e)) all[principal] = change(e);
    });

    private void Change(Action<Dictionary<Guid, SignUpEntry>> change)
    {
        lock (_gate)
        {
            var (all, damaged) = Read();
            var before = JsonSerializer.Serialize(all.Values, Options);
            change(all);
            if (damaged || JsonSerializer.Serialize(all.Values, Options) != before) Write(all, damaged);
        }
    }

    private (Dictionary<Guid, SignUpEntry> All, bool Damaged) Read()
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
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // The old list: PrincipalID to when its last link went out. It can't tell a decoy from a real sign-up, so all are unlisted.
                var old = doc.RootElement.Deserialize<Dictionary<Guid, DateTimeOffset>>(Options) ?? [];
                return (old.ToDictionary(p => p.Key, p => new SignUpEntry(p.Key, "", p.Value, p.Value, SignUpState.Unlisted, p.Value, null)), false);
            }
            var entries = doc.RootElement.Deserialize<List<SignUpEntry>>(Options) ?? [];
            return (entries.ToDictionary(e => e.Principal), false);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException)
        {
            return ([], true);
        }
    }

    private void Write(Dictionary<Guid, SignUpEntry> all, bool damaged)
    {
        if (damaged && File.Exists(file))
        {
            var aside = file + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(file, aside, overwrite: true);
            log?.LogWarning("The sign-ups board {File} could not be read and was set aside as {Aside}; those sign-ups are no longer cleaned up.",
                file, Path.GetFileName(aside));
        }
        if (Path.GetDirectoryName(Path.GetFullPath(file)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(all.Values.OrderBy(e => e.Made).ToList(), Options));
    }
}
