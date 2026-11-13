using System.Text.Json;
using GridGate.Launcher.Core;

namespace GridGate.Launcher.Iar;

/// <summary>
/// One step of an owner's or admin's inventory archive action, in full (spec 2026-11-11-iar-backups-design §7.3): who, from where
/// and when; whose inventory; save or load; the typed reason and the case; the file with its size and SHA-256; the counts; the
/// folder loaded into; the taken-down matches a load carried; and the result. <see cref="Action"/> is <c>asked</c>,
/// <c>started</c>, <c>finished</c>, <c>failed</c>, <c>fetched</c> or <c>deleted</c>; <see cref="Kind"/> is <c>save</c> or <c>load</c>.
/// </summary>
public sealed record IarStaffRecord(string Id, string Action, string Kind, string Actor, Guid ActorId, string Role, string? Ip, DateTimeOffset At,
    Guid Subject, string SubjectName, string Reason, string? CaseNumber, string? File, long? Bytes, string? Sha256, int? Items, string? Folder,
    IReadOnlyList<string> BannedMatches, string Result);

/// <summary>
/// The full records of the owner's and admins' inventory archive actions: one JSON line each in
/// <c>&lt;data&gt;/iars/staff/records.jsonl</c>, appended and never rewritten or deleted. Safe to call from any thread.
/// </summary>
public sealed class IarStaffRecords(LauncherPaths paths)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private string FilePath => Path.Combine(paths.IarsStaff, "records.jsonl");

    public void Add(IarStaffRecord record)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(paths.IarsStaff);
            File.AppendAllText(FilePath, JsonSerializer.Serialize(record, Options) + "\n");
        }
    }

    /// <summary>Every record about <paramref name="subject"/>, newest first.</summary>
    public IReadOnlyList<IarStaffRecord> For(Guid subject) => [.. All().Where(r => r.Subject == subject).OrderByDescending(r => r.At)];

    /// <summary>The finished staff saves whose file is still kept, newest first (of anyone when <paramref name="subject"/> is null).</summary>
    public IReadOnlyList<IarStaffRecord> KeptFiles(Guid? subject = null)
    {
        var all = All();
        var deleted = all.Where(r => r.Action == "deleted" && r.File is not null).Select(r => r.File!).ToHashSet(StringComparer.Ordinal);
        return [.. all.Where(r => r.Action == "finished" && r.Kind == "save" && r.File is { } f && !deleted.Contains(f)
                                  && (subject is null || r.Subject == subject) && File.Exists(Path.Combine(paths.IarsStaff, f)))
            .OrderByDescending(r => r.At)];
    }

    /// <summary>The kept staff save of <paramref name="subject"/> that names a case, if any: it holds the account against delete.</summary>
    public IarStaffRecord? KeptEvidence(Guid subject) => KeptFiles(subject).FirstOrDefault(r => r.CaseNumber is not null);

    private List<IarStaffRecord> All()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath)) return [];
            var list = new List<IarStaffRecord>();
            foreach (var line in File.ReadAllLines(FilePath))
            {
                if (line.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<IarStaffRecord>(line, Options) is { } r) list.Add(r);
                }
                catch (JsonException) { }
            }
            return list;
        }
    }
}
