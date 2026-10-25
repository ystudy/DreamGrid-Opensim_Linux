using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Setup;

/// <summary>The setup link lands here: it always continues at the first unfinished step, so a refresh or restart resumes in place.</summary>
public sealed class IndexModel(Func<LauncherSettings> settings) : PageModel
{
    public IActionResult OnGet() => Redirect(SetupSteps.FirstUnfinished(settings().Setup));
}
