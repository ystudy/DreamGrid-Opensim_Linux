using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Iar;
using DreamGrid.Launcher.Core.Logs;

namespace DreamGrid.Launcher.Iar;

/// <summary>Who did a staff step: their name, id and role, and the address they used.</summary>
public sealed record IarStaffActor(string Name, Guid Id, string Role, string? Ip);

/// <summary>
/// The owner's and admins' inventory archive actions on any avatar (spec 2026-11-11-iar-backups-design §7): a save of the whole
/// inventory with no permission filter, or a load of any archive into a named folder, with none of the residents' switches or
/// limits. The region program's own job slot still applies, so the grid isn't stalled. Every step is recorded in full
/// (<see cref="IarStaffRecords"/>) and audited in one line (<c>iar-staff-save</c>, <c>iar-staff-load</c>, <c>iar-staff-fetched</c>,
/// <c>iar-staff-deleted</c>, target <c>"Actor -> Subject"</c>). Nothing here runs without a reason.
/// </summary>
public sealed partial class IarStaffJobs(IGridController grid, IarStaffRecords records, IInventoryStore inventory, LauncherPaths paths,
    BackgroundWork background, AuditLog audit, TimeProvider time, ILogger<IarStaffJobs> log)
{
    public const int MinReason = 10;
    public const int MaxReason = 1000;

    /// <summary>Why the reason or case number can't be used, or null when both can.</summary>
    public static string? Problem(string? reason, string? caseNumber)
    {
        var r = reason?.Trim() ?? "";
        if (r.Length < MinReason) return $"Write why, in at least {MinReason} characters. It goes in the record.";
        if (r.Length > MaxReason) return $"Keep the reason to {MaxReason} characters.";
        if (!string.IsNullOrWhiteSpace(caseNumber) && !CasePattern().IsMatch(caseNumber.Trim()))
            return "A case number looks like TD-2026-001. Leave it empty if there is none.";
        return null;
    }

    /// <summary>
    /// Records the ask, then runs the save (<paramref name="file"/> null) or the load of <paramref name="file"/> (in
    /// <see cref="LauncherPaths.IarsIncoming"/>, deleted afterwards) into a new folder named after <paramref name="folder"/>, in
    /// the background. <paramref name="banned"/> is what the load's archive matched, for the record. Returns the job's id.
    /// </summary>
    public string Start(IarStaffActor actor, Guid subject, string first, string last, IarJobKind kind, string reason, string? caseNumber,
        string? file = null, string? folder = null, IReadOnlyList<string>? banned = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var subjectName = $"{first} {last}";
        var c = string.IsNullOrWhiteSpace(caseNumber) ? null : caseNumber.Trim();
        var k = kind == IarJobKind.Save ? "save" : "load";
        IarStaffRecord Make(string action, string result, string? f = null, long? bytes = null, string? sha = null, int? items = null, string? into = null) =>
            new(id, action, k, actor.Name, actor.Id, actor.Role, actor.Ip, time.GetUtcNow(), subject, subjectName, reason.Trim(), c, f, bytes, sha,
                items, into, banned ?? [], result);

        Write(actor, subjectName, "iar-staff-" + k, "ok");
        records.Add(Make("asked", "ok"));
        background.Run("Staff inventory " + k, async ct =>
        {
            var target = kind == IarJobKind.Save ? Path.Combine(paths.IarsStaff, Guid.NewGuid().ToString("N") + ".iar") : file!;
            string? into = null;
            try
            {
                records.Add(Make("started", "ok"));
                Write(actor, subjectName, "iar-staff-" + k, "started");
                if (kind == IarJobKind.Load)
                {
                    var taken = await TakenNamesAsync(subject, ct);
                    into = IarFolderNames.Free(string.IsNullOrWhiteSpace(folder) ? IarFolderNames.Imported(time.GetUtcNow()) : folder.Trim(), taken);
                    await inventory.CreateFolderAsync(subject, into, ct);
                }
                var n = await grid.RunIarJobAsync(new IarJobRequest(subject, first, last, kind, target, into), ct);
                var bytes = new FileInfo(target).Length;
                var sha = await ShaAsync(target, ct);
                records.Add(Make("finished", "ok", kind == IarJobKind.Save ? Path.GetFileName(target) : null, bytes, sha, n, into));
                Write(actor, subjectName, "iar-staff-" + k, "ok");
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Staff inventory {Kind} for {Name} failed: {Message}", k, subjectName, e.Message);
                records.Add(Make("failed", e is OperationCanceledException ? "stopped" : e.Message, into: into));
                Write(actor, subjectName, "iar-staff-" + k, "failed");
                if (kind == IarJobKind.Save) TryDelete(target);
            }
            finally
            {
                if (kind == IarJobKind.Load) TryDelete(target);
            }
        });
        return id;
    }

    /// <summary>Records that a kept staff file was downloaded.</summary>
    public void Fetched(IarStaffActor actor, IarStaffRecord kept)
    {
        records.Add(kept with { Id = Guid.NewGuid().ToString("N"), Action = "fetched", Actor = actor.Name, ActorId = actor.Id, Role = actor.Role,
            Ip = actor.Ip, At = time.GetUtcNow(), Result = "ok" });
        Write(actor, kept.SubjectName, "iar-staff-fetched", "ok");
    }

    /// <summary>Deletes a kept staff file with its own reason, and records it; false (nothing changes) without a usable reason.</summary>
    public bool Delete(IarStaffActor actor, IarStaffRecord kept, string reason)
    {
        if (Problem(reason, null) is not null) return false;
        TryDelete(Path.Combine(paths.IarsStaff, kept.File!));
        records.Add(kept with { Id = Guid.NewGuid().ToString("N"), Action = "deleted", Actor = actor.Name, ActorId = actor.Id, Role = actor.Role,
            Ip = actor.Ip, At = time.GetUtcNow(), Reason = reason.Trim(), Result = "ok" });
        Write(actor, kept.SubjectName, "iar-staff-deleted", "ok");
        return true;
    }

    private async Task<IEnumerable<string>> TakenNamesAsync(Guid subject, CancellationToken ct)
    {
        var folders = await inventory.FoldersAsync(subject, ct);
        var root = folders.FirstOrDefault(f => f.Type == 8);
        return root is null ? [] : folders.Where(f => f.Parent == root.Id).Select(f => f.Name);
    }

    private static async Task<string> ShaAsync(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
    }

    private void Write(IarStaffActor actor, string subject, string action, string result) =>
        audit.Write(new AuditEntry(actor.Name, actor.Id, actor.Role, actor.Ip, action, LogText.Clean($"{actor.Name} -> {subject}", 140), result));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"^TD-\d{4}-\d{3,}$")]
    private static partial Regex CasePattern();
}
