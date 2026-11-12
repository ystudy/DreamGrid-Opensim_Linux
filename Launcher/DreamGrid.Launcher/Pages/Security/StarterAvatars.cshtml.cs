using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Starter;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Security;

/// <summary>
/// The owner's Starter avatars page (OwnerPolicy, <c>/security/starter-avatars</c>, 404 where there is no guest side; spec
/// 2026-11-11-starter-avatars-design §6): each avatar with its checklist, its snapshot, and arranging (order, default, name,
/// hide and show, remove). Every change is in the audit log as <c>starter-avatar-*</c>.
/// </summary>
public partial class StarterAvatarsModel(StarterAvatarList starters, LauncherPaths paths, IAccountRemover remover, AuditLog audit,
    ILogger<StarterAvatarsModel> log) : PageModel
{
    public const int MaxName = 64;

    [TempData] public string? Notice { get; set; }
    public string? Error { get; protected set; }

    public IReadOnlyList<StarterAvatar> Avatars { get; private set; } = [];
    public Guid? DefaultId { get; private set; }
    /// <summary>The avatar sign-up checks when the page opens: the owner's default if ready, else the first ready one.</summary>
    public Guid? EffectiveDefault { get; private set; }

    public virtual void OnGet() => Load();

    /// <summary>Any avatar's snapshot (or suggestion), for this page: the public path only serves ready ones.</summary>
    public IActionResult OnGetPicture(Guid id, bool suggested = false)
    {
        if (starters.All().FirstOrDefault(a => a.Id == id) is null) return NotFound();
        var path = Path.Combine(paths.StarterAvatars, id + (suggested ? ".suggested.webp" : ".webp"));
        return System.IO.File.Exists(path) ? PhysicalFile(path, "image/webp") : NotFound();
    }

    public IActionResult OnPostSnapshot(Guid id, IFormFile? file)
    {
        if (Find(id) is not { } a) return Gone();
        if (file is null || file.Length == 0) return Problem("Choose a picture to upload.");
        if (file.Length > StarterSnapshots.MaxBytes) return Problem(StarterSnapshots.TooBig);
        string? name;
        string? problem;
        using (var s = file.OpenReadStream()) name = StarterSnapshots.Save(s, paths.StarterAvatars, a.Id, out problem);
        if (name is null) return Problem(problem!);
        starters.Update(a.Id, x => x with { Snapshot = name });
        Write("starter-avatar-snapshot", a.Name, "ok");
        return Done($"The snapshot for {a.Name} is saved.");
    }

    public IActionResult OnPostMove(Guid id, int delta)
    {
        if (Find(id) is not { } a) return Gone();
        if (starters.Move(id, delta)) Write("starter-avatar-order", a.Name, delta < 0 ? "up" : "down");
        return Done(null);
    }

    public IActionResult OnPostDefault(Guid id)
    {
        if (Find(id) is not { } a) return Gone();
        starters.SetDefault(id);
        Write("starter-avatar-default", a.Name, "ok");
        return Done($"{a.Name} is the default.");
    }

    public IActionResult OnPostRename(Guid id, string? name)
    {
        if (Find(id) is not { } a) return Gone();
        var wanted = name?.Trim() ?? "";
        if (wanted.Length is 0 or > MaxName || wanted.Any(char.IsControl)) return Problem($"A name is 1 to {MaxName} characters.");
        starters.Update(id, x => x with { Name = wanted });
        Write("starter-avatar-renamed", $"{LogText.Clean(a.Name, MaxName)} -> {LogText.Clean(wanted, MaxName)}", "ok");
        return Done($"Renamed to {wanted}.");
    }

    public IActionResult OnPostHide(Guid id) => SetHidden(id, true);

    public IActionResult OnPostShow(Guid id) => SetHidden(id, false);

    public async Task<IActionResult> OnPostRemoveAsync(Guid id, bool deleteModel, CancellationToken ct)
    {
        if (Find(id) is not { } a) return Gone();
        starters.Remove(id);
        foreach (var f in new[] { ".webp", ".suggested.webp" })
            TryDelete(Path.Combine(paths.StarterAvatars, a.Id + f));
        var result = "ok";
        if (deleteModel)
        {
            try
            {
                await remover.DeleteAsync(a.Model, ct);
                result = "model deleted";
            }
            catch (StaffDirectoryUnavailableException e)
            {
                log.LogWarning("The model account {Model} couldn't be deleted: {Message}", a.ModelName, e.Message);
                result = "model not deleted";
            }
        }
        Write("starter-avatar-removed", a.Name, result);
        return Done(result == "model not deleted"
            ? $"{a.Name} is off the list, but its model account {a.ModelName} couldn't be deleted. Delete it from People."
            : $"{a.Name} is off the list. Residents who picked it keep their copies.");
    }

    private IActionResult SetHidden(Guid id, bool hidden)
    {
        if (Find(id) is not { } a) return Gone();
        starters.Update(id, x => x with { Hidden = hidden });
        Write(hidden ? "starter-avatar-hidden" : "starter-avatar-shown", a.Name, "ok");
        return Done(hidden ? $"{a.Name} is hidden from sign-up." : $"{a.Name} is shown at sign-up when it's ready.");
    }

    protected StarterAvatar? Find(Guid id) => starters.All().FirstOrDefault(a => a.Id == id);

    protected void Load()
    {
        Avatars = starters.All();
        DefaultId = starters.DefaultId;
        EffectiveDefault = starters.Default()?.Id;
    }

    protected IActionResult Done(string? notice)
    {
        Notice = notice;
        return Redirect("/security/starter-avatars");
    }

    protected IActionResult Problem(string message)
    {
        Error = message;
        Load();
        return Page();
    }

    private IActionResult Gone() => Problem("That starter avatar isn't on the list any more.");

    protected void Write(string action, string target, string result)
    {
        var (name, id, role) = AuditActor.Of(User);
        audit.Write(new AuditEntry(name, id, role, HttpContext.Connection.RemoteIpAddress?.ToString(), action, LogText.Clean(target, 140), result));
    }

    private static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The checklist words for one avatar's row.</summary>
    public static string Status(StarterAvatar a) =>
        a.IsReady ? "On the sign-up page"
        : a.Hidden ? "Hidden from sign-up"
        : "Not on the sign-up page";
}
