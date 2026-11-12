using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Iar;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Endpoints;
using DreamGrid.Launcher.Iar;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.People;

/// <summary>
/// An avatar's inventory archives for the owner and admins (<c>/people/{id}/iar</c>; 403 for anyone else; spec
/// 2026-11-11-iar-backups-design §7): save the whole inventory, load any archive (an upload, or a kept staff save) into a named
/// folder, download or delete a kept save, and every record in full. Nothing runs without a reason; a load whose archive matches
/// taken-down content shows the matches and needs a confirm.
/// </summary>
public sealed class IarModel(PeopleActions people, IarStaffRecords records, IarStaffJobs jobs, TakedownStore takedowns, IInventoryStore inventory,
    LauncherPaths paths) : PageModel
{
    public PersonView? Person { get; private set; }
    public IReadOnlyList<IarStaffRecord> Records { get; private set; } = [];
    public IReadOnlyList<IarStaffRecord> Kept { get; private set; } = [];
    public IReadOnlyList<IarStaffRecord> KeptAnyone { get; private set; } = [];
    public string? Pending { get; private set; }
    public IReadOnlyList<string> Banned { get; private set; } = [];
    public bool BannedUnknown { get; private set; }
    public string? Error { get; private set; }
    [TempData] public string? Notice { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, string? pending, string? problem, CancellationToken ct)
    {
        if (!TakedownAccess.MayUse(User)) return StatusCode(StatusCodes.Status403Forbidden);
        if (problem is not null) Error = ProblemWords(problem);
        if (!await LoadAsync(id, ct)) return NotFound();
        if (pending is not null && PendingPath(pending) is { } path)
        {
            Pending = pending;
            await FindBannedAsync(path, ct);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid id, string? reason, string? caseNumber, CancellationToken ct)
    {
        if (!TakedownAccess.MayUse(User)) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await LoadAsync(id, ct)) return NotFound();
        if (IarStaffJobs.Problem(reason, caseNumber) is { } p) { Error = p; return Page(); }
        var a = Person!.Row.Account;
        jobs.Start(Actor(), id, a.FirstName, a.LastName, IarJobKind.Save, reason!, caseNumber);
        Notice = $"Saving {Person.FullName}'s whole inventory. The record below shows when it is done.";
        return Redirect($"/people/{id}/iar");
    }

    public async Task<IActionResult> OnPostLoadAsync(Guid id, string? pending, string? keptFile, string? reason, string? caseNumber, string? folder,
        bool confirm, CancellationToken ct)
    {
        if (!TakedownAccess.MayUse(User)) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await LoadAsync(id, ct)) return NotFound();
        Pending = pending;
        string? source = pending is null ? null : PendingPath(pending);
        if (source is null && keptFile is not null && KeptAnyone.FirstOrDefault(k => k.File == keptFile) is { } kept)
        {
            // A kept save is copied, so the evidence itself is never loaded from or removed.
            source = Path.Combine(paths.IarsIncoming, Guid.NewGuid().ToString("N") + ".iar");
            Directory.CreateDirectory(paths.IarsIncoming);
            System.IO.File.Copy(Path.Combine(paths.IarsStaff, kept.File!), source);
            Pending = Path.GetFileNameWithoutExtension(source);
        }
        if (source is null) { Error = "Upload an inventory archive, or choose a kept save, first."; return Page(); }
        if (IarStaffJobs.Problem(reason, caseNumber) is { } p) { Error = p; await FindBannedAsync(source, ct); return Page(); }
        if (!string.IsNullOrWhiteSpace(folder) && !IarFolderNames.IsSafe(folder.Trim()))
        {
            Error = "Name the folder with letters, digits, spaces and . _ ( ) - only, up to 64 characters.";
            await FindBannedAsync(source, ct);
            return Page();
        }
        await FindBannedAsync(source, ct);
        if ((Banned.Count > 0 || BannedUnknown) && !confirm)
        {
            Error = "This archive holds taken-down content. Tick the box to load it anyway; the record will list it.";
            return Page();
        }
        var a = Person!.Row.Account;
        jobs.Start(Actor(), id, a.FirstName, a.LastName, IarJobKind.Load, reason!, caseNumber, source, folder, Banned);
        Notice = $"Loading the archive into {Person.FullName}'s inventory. The record below shows when it is done.";
        return Redirect($"/people/{id}/iar");
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, string? file, string? reason, CancellationToken ct)
    {
        if (!TakedownAccess.MayUse(User)) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await LoadAsync(id, ct)) return NotFound();
        if (Kept.FirstOrDefault(k => k.File == file) is not { } kept) { Error = "That file isn't kept any more."; return Page(); }
        if (!jobs.Delete(Actor(), kept, reason ?? "")) { Error = IarStaffJobs.Problem(reason, null); return Page(); }
        Notice = "Deleted the kept file. The record stays.";
        return Redirect($"/people/{id}/iar");
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken ct)
    {
        var (person, problem) = await people.LookAsync(User, id, ct);
        Person = person;
        Error ??= problem;
        if (person is null) return false;
        Records = records.For(id);
        Kept = records.KeptFiles(id);
        KeptAnyone = records.KeptFiles();
        return true;
    }

    private async Task FindBannedAsync(string path, CancellationToken ct)
    {
        if (takedowns.Unreadable) { BannedUnknown = true; return; }
        Banned = [.. IarBannedFilter.Find(path, await takedowns.BannedAssetsAsync(inventory, ct), false).Select(m => m.ItemName)];
    }

    private string? PendingPath(string pending)
    {
        if (!IarStaffApi.FileName().IsMatch(pending)) return null;
        var path = Path.Combine(paths.IarsIncoming, pending + ".iar");
        return System.IO.File.Exists(path) ? path : null;
    }

    private IarStaffActor Actor()
    {
        var (name, id, role) = AuditActor.Of(User);
        return new IarStaffActor(name, id ?? Guid.Empty, role, HttpContext.Connection.RemoteIpAddress?.ToString());
    }

    private static string ProblemWords(string problem) => problem switch
    {
        nameof(IarUploadProblem.NotAnIar) => "That file isn't an inventory archive (IAR).",
        nameof(IarUploadProblem.NoFile) => "Choose an inventory archive to upload.",
        nameof(IarUploadProblem.NoSpace) => "There isn't enough disk space for that file.",
        _ => "The upload stopped before it finished. Try again.",
    };
}
