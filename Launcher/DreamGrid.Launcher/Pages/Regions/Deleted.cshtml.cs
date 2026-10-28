using System.Data.Common;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Regions;

/// <summary>One row of Recently deleted: the region, when it was deleted and the space it holds in words.</summary>
public sealed record DeletedRow(Guid Id, string Name, int X, int Y, int Size, string DeletedText, string SpaceText);

/// <summary>
/// Recently deleted (spec 5.6): every deleted region with the space it still holds, to restore (back, stopped) or to
/// delete forever. Restore and Delete forever run in the background; Delete forever asks first and needs the database.
/// </summary>
public sealed class DeletedModel(IGridController grid, RegionActions actions, ILogger<DeletedModel> log) : PageModel
{
    public const string SpaceUnknown = "Unknown while the database is stopped";
    /// <summary>The space of a region whose query failed although the database runs (the failure is in the launcher log).</summary>
    public const string SpaceFailed = "Unknown";
    public const string DatabaseStopped = "Start the database first.";

    public IReadOnlyList<DeletedRow> Rows { get; private set; } = [];

    /// <summary>Set while the page asks whether to delete a region forever: its id and name.</summary>
    public Guid? Asking { get; private set; }
    public string AskingText { get; private set; } = "";

    [TempData] public string? Notice { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    /// <summary>Brings the region back, stopped; nothing restarts.</summary>
    public IActionResult OnPostRestore(Guid id)
    {
        if (!actions.TryRestore(id)) return NotFound();
        Notice = $"{Name(id)} is back, stopped. Start it when you're ready.";
        return Redirect("/regions/deleted");
    }

    /// <summary>Delete forever. Without a yes the question shows; a stopped database is refused in plain words.</summary>
    public async Task<IActionResult> OnPostEraseAsync(Guid id, bool confirmed)
    {
        if (RegionModel.Find(grid.CurrentSettings(), id) is not { DeletedAt: not null } region) return NotFound();
        if (await SpaceAsync() is (null, false))
        {
            Notice = DatabaseStopped;
            return Redirect("/regions/deleted");
        }
        if (!confirmed)
        {
            await LoadAsync();
            (Asking, AskingText) = (id, $"Delete {region.Name} forever? Its land and objects are erased from the database. This can't be undone.");
            return Page();
        }
        if (!actions.TryErase(id)) return NotFound();
        Notice = $"Deleting {region.Name} forever…";
        return Redirect("/regions/deleted");
    }

    private string Name(Guid id) => RegionModel.Find(grid.CurrentSettings(), id)?.Name ?? "";

    private async Task LoadAsync()
    {
        var (space, failed) = await SpaceAsync();
        Rows = grid.CurrentSettings().Regions.Where(r => r.DeletedAt is not null)
            .OrderByDescending(r => r.DeletedAt)
            .Select(r => new DeletedRow(r.Uuid, r.Name, r.X, r.Y, r.Size,
                r.DeletedAt!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                failed ? SpaceFailed
                    : space is null ? SpaceUnknown
                    : space.TryGetValue(r.Uuid, out var bytes) ? "about " + RegionAdvice.FileSize(bytes) : "Nothing"))
            .ToList();
    }

    /// <summary>
    /// The space each deleted region holds; null while the database is stopped. A query that fails although the database
    /// runs is logged and gives <c>Failed</c>, so the page still shows (space unknown) and Restore still works.
    /// </summary>
    private async Task<(IReadOnlyDictionary<Guid, long>? Space, bool Failed)> SpaceAsync()
    {
        try { return (await grid.DeletedSpaceAsync(HttpContext.RequestAborted), false); }
        catch (DbException e)
        {
            log.LogWarning(e, "The space of the deleted regions could not be read from the database.");
            return (null, true);
        }
    }
}
