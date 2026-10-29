using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Setup;

/// <summary>A plan's ⓘ page (spec 6.2): what it is, its layout, what it needs, and "Choose this plan".</summary>
public sealed class PlanInfoModel(IGridController grid, Func<LauncherSettings> settings, MariaDbBinaries mariaDb) : PageModel
{
    public PlanInfo Plan { get; private set; } = PlanCatalog.All[0];

    /// <summary>Why the plan can't be chosen yet (a missing part), after a refused choice.</summary>
    public string? Error { get; private set; }

    public IActionResult OnGet(string id)
    {
        if (PlanChoice.Locked(settings())) return Redirect("/setup");
        if (PlanCatalog.ById(id) is not { } plan) return NotFound();
        Plan = plan;
        return Page();
    }

    /// <summary>POST /setup/plan/{id}?handler=Choose. An unknown or not yet available plan is a bad request; nothing is saved.</summary>
    public IActionResult OnPostChoose(string id)
    {
        if (PlanCatalog.ById(id) is not { Available: true } plan) return BadRequest();
        Plan = plan;
        Error = PlanChoice.Save(grid, settings(), mariaDb, plan);
        return Error is null ? Redirect("/setup") : Page();
    }
}
