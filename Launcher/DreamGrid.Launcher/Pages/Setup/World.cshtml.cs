using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Endpoints;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Setup;

/// <summary>Step 1: the grid name. Saving it also warms the grid up in the background.</summary>
public sealed class WorldModel(IGridController grid, Func<LauncherSettings> settings, SetupRunner runner) : PageModel
{
    [BindProperty] public string? GridName { get; set; }

    /// <summary>The last failed start that is not a port conflict (that one gets the port form), shown like on the avatar step.</summary>
    public string? Error { get; private set; }
    public string? Details { get; private set; }

    public IActionResult OnGet()
    {
        if (Closed() is { } closed) return closed;
        GridName = settings().GridName;
        ShowStartFailure();
        return Page();
    }

    private IActionResult? Closed() => SetupSteps.CanOpen(settings(), "/setup/world") ? null : Redirect("/setup");

    private void ShowStartFailure()
    {
        var status = grid.GetStatus();
        if (!StatusApi.ShowsStartFailure(status) || status.StartFailure!.ConflictPort is not null) return;
        Error = StatusApi.CouldNotStart;
        Details = status.StartFailure.Message;
    }

    public IActionResult OnPost()
    {
        if (Closed() is { } closed) return closed;
        var name = GridName?.Trim() ?? "";
        if (name == "")
            ModelState.AddModelError(nameof(GridName), "Give your world a name.");
        else if (SettingsValidator.GridNameError(name) is { } nameError)
            ModelState.AddModelError(nameof(GridName), nameError);
        if (!ModelState.IsValid)
        {
            ShowStartFailure();
            return Page();
        }

        // The name goes into the generated INIs ([Const] GridName, [GridInfoService] gridname, the estate name).
        grid.UpdateSettings(s => s with { GridName = name, Setup = s.Setup with { WorldNamed = true } }, writeConfig: true);
        runner.WarmUp();
        return Redirect("/setup");
    }
}
