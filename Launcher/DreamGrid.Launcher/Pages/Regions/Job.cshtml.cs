using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Regions;

/// <summary>
/// An OAR job's progress (spec 5.5): what it does, its stage, how long it has run and, while saving, the size so far.
/// Without JavaScript the page refreshes itself every 3 seconds while the job runs.
/// </summary>
public sealed class JobModel(IGridController grid, OarJobs jobs) : PageModel
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public OarJobSnapshot Job { get; private set; } = null!;

    /// <summary>True when the region has been deleted (a save with Delete as its follow-up): the back link goes to Recently deleted.</summary>
    public bool Deleted { get; private set; }

    [TempData] public string? Notice { get; set; }

    public IActionResult OnGet(Guid id)
    {
        if (RegionModel.Find(grid.CurrentSettings(), id) is not { } r) return NotFound();
        if (jobs.For(id) is not { } job) return NotFound();
        (Id, Name, Job, Deleted) = (id, r.Name, job, r.DeletedAt is not null);
        return Page();
    }
}
