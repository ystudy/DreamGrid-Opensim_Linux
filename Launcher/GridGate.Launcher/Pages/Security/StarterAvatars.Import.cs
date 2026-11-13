using System.Text.RegularExpressions;
using GridGate.Launcher.Core.Starter;
using GridGate.Launcher.Iar;
using GridGate.Launcher.Security;
using GridGate.Launcher.Starter;
using Microsoft.AspNetCore.Mvc;

namespace GridGate.Launcher.Pages.Security;

/// <summary>One avatar offered from an uploaded archive: its folder, the name it will have, and whether the owner ticked it.</summary>
public sealed class ArchivePick
{
    public string Folder { get; set; } = "";
    public string Name { get; set; } = "";
    public bool On { get; set; }
}

/// <summary>The page's loading part (spec §6.2): the avatars in an uploaded archive, the import's progress, and adding an existing account.</summary>
public partial class StarterAvatarsModel
{
    /// <summary>The uploaded archive waiting for the owner's picks (its random name in <c>IarsIncoming</c>).</summary>
    [BindProperty(SupportsGet = true)] public string? Pending { get; set; }
    [BindProperty(SupportsGet = true)] public string? Job { get; set; }
    [BindProperty(SupportsGet = true, Name = "problem")] public string? UploadProblem { get; set; }
    [BindProperty] public List<ArchivePick> Picks { get; set; } = [];

    public IReadOnlyList<StarterCandidate> Candidates { get; private set; } = [];
    public StarterImportProgress? Progress { get; private set; }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex PendingName();

    private string? PendingPath() =>
        Pending is { } p && PendingName().IsMatch(p) && Path.Combine(paths.IarsIncoming, p + ".iar") is var path && System.IO.File.Exists(path) ? path : null;

    /// <summary>Fills the archive's avatars and the job's progress for the page.</summary>
    private void LoadImport()
    {
        if (UploadProblem is not null && Error is null) Error = "That upload didn't arrive. Try again.";
        if (Job is { } j) Progress = import.For(j);
        if (Pending is null) return;
        if (PendingPath() is not { } path)
        {
            Error ??= "That upload isn't here any more. Upload the archive again.";
            Pending = null;
            return;
        }
        try
        {
            Candidates = StarterArchive.Candidates(path);
            if (Candidates.Count == 0) Error ??= "No avatars were found in that archive: an avatar is a folder, two levels down, with body parts in it.";
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            log.LogWarning("A starter avatar archive couldn't be read: {Message}", e.Message);
            Error ??= "That file isn't an inventory archive GridGate can read.";
            TryDelete(path);
            Pending = null;
        }
    }

    public IActionResult OnPostImport()
    {
        if (PendingPath() is not { } path) return Problem("That upload isn't here any more. Upload the archive again.");
        var known = StarterArchive.Candidates(path).Select(c => c.FolderPath).ToHashSet(StringComparer.Ordinal);
        var picks = Picks.Where(p => p.On && known.Contains(p.Folder)).ToList();
        if (picks.Count == 0) return Problem("Tick at least one avatar to add.");
        if (picks.FirstOrDefault(p => p.Name.Trim().Length is 0 or > MaxName) is { } bad)
            return Problem($"A name is 1 to {MaxName} characters ({bad.Folder}).");
        var (name, id, role) = AuditActor.Of(User);
        var job = import.Start(new IarStaffActor(name, id ?? Guid.Empty, role, HttpContext.Connection.RemoteIpAddress?.ToString()), path,
            [.. picks.Select(p => (p.Folder, p.Name.Trim()))]);
        return Redirect($"/security/starter-avatars?job={job}");
    }

    public async Task<IActionResult> OnPostAddExistingAsync(string? first, string? last, string? name, CancellationToken ct)
    {
        var display = string.IsNullOrWhiteSpace(name) ? $"{first} {last}".Trim() : name.Trim();
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last)) return Problem("Give the account's first and last name.");
        if (display.Length > MaxName) return Problem($"A name is 1 to {MaxName} characters.");
        var (actor, id, role) = AuditActor.Of(User);
        StarterAvatar? added;
        try
        {
            added = await import.AddExistingAsync(new IarStaffActor(actor, id ?? Guid.Empty, role, HttpContext.Connection.RemoteIpAddress?.ToString()),
                first, last, display, ct);
        }
        catch (HttpRequestException)
        {
            return Problem("The grid service isn't answering. Start the grid and try again.");
        }
        return added is null
            ? Problem($"{first} {last} can't be added: it must be on the grid, at level 0, with no email, and not already a starter avatar. A model's password is reset, so it can't be anyone's own avatar.")
            : Done($"{added.Name} is added. Upload its snapshot, then press Check it.");
    }
}
