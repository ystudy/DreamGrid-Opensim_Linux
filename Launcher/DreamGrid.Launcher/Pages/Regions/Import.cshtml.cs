using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Regions;

/// <summary>
/// Importing an OAR from the library into a region (spec 5.5): choose a file, then a yes, because the region's land and
/// objects are replaced. "Save as OAR first" starts a save instead. Both start an OAR job and go to its page.
/// </summary>
public sealed class ImportModel(IGridController grid, OarLibrary library, OarJobs jobs) : PageModel
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";

    /// <summary>The library's files, newest first.</summary>
    public IReadOnlyList<string> Files { get; private set; } = [];

    /// <summary>The file chosen; with <see cref="Asking"/> it is the one the question is about.</summary>
    [BindProperty(Name = "file")] public string? Chosen { get; set; }

    /// <summary>True when the question "Import … ?" is shown instead of the list.</summary>
    public bool Asking { get; private set; }

    /// <summary>Why the page could not go on, in plain words; null when nothing is wrong.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether the region runs; an OAR needs it running.</summary>
    public bool Running { get; private set; }

    [TempData] public string? Notice { get; set; }

    public IActionResult OnGet(Guid id)
    {
        if (!Load(id)) return NotFound();
        return Page();
    }

    public IActionResult OnPost(Guid id, bool confirmed)
    {
        if (!Load(id)) return NotFound();
        if (!Running) return Page();
        if (string.IsNullOrWhiteSpace(Chosen))
        {
            Error = "Choose a file.";
            return Page();
        }
        if (library.Resolve(Chosen) is not { } path)
        {
            Error = "That file is not in the OAR library.";
            Chosen = null;
            return Page();
        }
        Chosen = Path.GetFileName(path);
        if (!confirmed)
        {
            Asking = true;
            return Page();
        }
        return Start(new OarJobRequest(id, OarJobKind.Import, Chosen));
    }

    /// <summary>"Save as OAR first": saves the region into the library instead.</summary>
    public IActionResult OnPostSave(Guid id)
    {
        if (!Load(id)) return NotFound();
        if (!Running) return Page();
        return Start(new OarJobRequest(id, OarJobKind.Save));
    }

    private IActionResult Start(OarJobRequest request)
    {
        if (!jobs.TryStart(request, OarJobs.Describe(request, Name)) && jobs.For(request.RegionId) is { } running)
            Notice = $"{running.Text} is still running. Try again when it finishes.";
        return Redirect($"/regions/region/{request.RegionId}/job");
    }

    private bool Load(Guid id)
    {
        if (RegionModel.Find(grid.CurrentSettings(), id) is not { DeletedAt: null } r) return false;
        (Id, Name) = (id, r.Name);
        Files = library.List().Select(f => f.Name).ToList();
        Running = grid.GetStatus().Regions.FirstOrDefault(x => x.Id == id)?.State == RegionState.Running;
        return true;
    }
}
