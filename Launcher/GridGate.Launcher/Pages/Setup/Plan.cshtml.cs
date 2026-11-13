using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Setup;

/// <summary>Setup's first page: the five plan cards; picking one and pressing Continue saves it.</summary>
public sealed class PlanModel(IGridController grid, Func<LauncherSettings> settings, MariaDbBinaries mariaDb) : PageModel
{
    /// <summary>The picked plan's id.</summary>
    [BindProperty] public string? Id { get; set; }

    public string? Error { get; private set; }

    public IActionResult OnGet()
    {
        var s = settings();
        if (PlanChoice.Locked(s)) return Redirect("/setup");
        Id = s.Plan is { } plan ? PlanCatalog.Get(plan).Id : null;
        return Page();
    }

    public IActionResult OnPost()
    {
        if (string.IsNullOrEmpty(Id))
        {
            Error = "Pick a plan, then press Continue.";
            return Page();
        }
        if (PlanCatalog.ById(Id) is not { Available: true } plan) return BadRequest();
        Error = PlanChoice.Save(grid, settings(), mariaDb, plan);
        return Error is null ? Redirect("/setup") : Page();
    }
}
