using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Setup;

/// <summary>Step 1: the grid name. Saving it also warms the grid up in the background.</summary>
public sealed class WorldModel(IGridController grid, Func<LauncherSettings> settings, SetupRunner runner) : PageModel
{
    [BindProperty] public string? GridName { get; set; }

    public void OnGet() => GridName = settings().GridName;

    public IActionResult OnPost()
    {
        var name = GridName?.Trim() ?? "";
        if (name == "")
            ModelState.AddModelError(nameof(GridName), "Give your world a name.");
        else if (SettingsValidator.GridNameError(name) is { } nameError)
            ModelState.AddModelError(nameof(GridName), nameError);
        if (!ModelState.IsValid) return Page();

        // The name goes into the generated INIs ([Const] GridName, [GridInfoService] gridname, the estate name).
        grid.UpdateSettings(s => s with { GridName = name, Setup = s.Setup with { WorldNamed = true } }, writeConfig: true);
        runner.WarmUp();
        return Redirect("/setup");
    }
}
