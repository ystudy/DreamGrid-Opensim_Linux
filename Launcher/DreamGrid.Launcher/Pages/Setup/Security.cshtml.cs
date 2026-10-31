using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Email;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Setup;

/// <summary>
/// The setup step "What kind of grid is this?" (plans with a guest side only): the answer fills in the three password-recovery
/// rules and the "staff must use two-factor" switch (<see cref="Recovery.Apply"/>). Home lab is preselected. Without a working
/// mail account it says to set up email afterwards; the answer is saved either way.
/// </summary>
public sealed class SecurityModel(IGridController grid, Func<LauncherSettings> settings, EmailSender email) : PageModel
{
    public const string Path = "/setup/security";

    [BindProperty] public GridKind Kind { get; set; } = GridKind.HomeLab;

    public bool EmailReady { get; private set; }

    public IActionResult OnGet()
    {
        if (Closed() is { } closed) return closed;
        EmailReady = email.IsReady;
        return Page();
    }

    public IActionResult OnPost()
    {
        if (Closed() is { } closed) return closed;
        if (!Enum.IsDefined(Kind))
        {
            EmailReady = email.IsReady;
            Kind = GridKind.HomeLab;
            return Page();
        }
        grid.UpdateSettings(s => Recovery.Apply(s, Kind), writeConfig: false);
        return Redirect("/setup");
    }

    private IActionResult? Closed() => SetupSteps.CanOpen(settings(), Path) ? null : Redirect("/setup");
}
