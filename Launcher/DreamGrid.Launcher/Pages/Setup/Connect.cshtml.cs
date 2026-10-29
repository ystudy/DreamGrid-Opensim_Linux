using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Setup;

/// <summary>Step 4: how to reach the world from Firestorm; "I'm in!" finishes setup.</summary>
public sealed class ConnectModel(IGridController grid, Func<LauncherSettings> settings) : PageModel
{
    public string LoginUri { get; private set; } = "";
    public string GridName { get; private set; } = "";
    public string OwnerName { get; private set; } = "";

    public IActionResult OnGet()
    {
        if (Closed() is { } closed) return closed;
        var s = settings();
        LoginUri = grid.GetStatus().LoginUri;
        GridName = s.GridName;
        OwnerName = s.Setup.Owner is { } o ? $"{o.FirstName} {o.LastName}" : "your avatar";
        return Page();
    }

    public IActionResult OnPost()
    {
        if (Closed() is { } closed) return closed;
        grid.UpdateSettings(s => s with { Setup = s.Setup with { Finished = true } }, writeConfig: false);
        return Redirect("/");
    }

    private IActionResult? Closed() => SetupSteps.CanOpen(settings(), "/setup/connect") ? null : Redirect("/setup");
}
