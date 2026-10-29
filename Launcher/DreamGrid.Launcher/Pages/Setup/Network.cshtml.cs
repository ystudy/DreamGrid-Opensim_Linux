using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Setup;

/// <summary>
/// The network step. For now only Continue, which marks the step done and keeps the address and hypergrid as they
/// are; the real step (address, router advice, hypergrid switch) replaces this page.
/// </summary>
public sealed class NetworkModel(IGridController grid, Func<LauncherSettings> settings) : PageModel
{
    public IActionResult OnGet() => Closed() ?? Page();

    public IActionResult OnPost()
    {
        if (Closed() is { } closed) return closed;
        grid.UpdateSettings(s => s with { Setup = s.Setup with { NetworkSet = true } }, writeConfig: true);
        return Redirect("/setup");
    }

    private IActionResult? Closed() => SetupSteps.CanOpen(settings(), "/setup/network") ? null : Redirect("/setup");
}
