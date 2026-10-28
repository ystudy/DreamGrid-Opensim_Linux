using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Core.Setup;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Setup;

/// <summary>
/// Step 3: the region's name and content. Posting starts the region in the background (one at a time) and shows its
/// progress at /setup/region?working=1; a failure shows the message and a log excerpt, and the form again to retry.
/// </summary>
public sealed class RegionModel(Func<LauncherSettings> settings, LauncherPaths paths, SetupProgress progress, SetupRunner runner) : PageModel
{
    private const string WorkingUrl = "/setup/region?working=1";

    [BindProperty] public string? RegionName { get; set; }
    [BindProperty(Name = "Content")] public string? RegionContent { get; set; }

    public bool StarterAvailable { get; private set; }
    public SetupProgressSnapshot Progress { get; private set; } = new("", 0, false, null, null);
    /// <summary>True to show the progress view (running, failed or ready) instead of only the form.</summary>
    public bool ShowProgress { get; private set; }
    /// <summary>True when the region exists (also after a restart, when the progress is empty) or this run just made it.</summary>
    public bool Ready => !Progress.Running && (RegionCreated || (ShowProgress && Progress.Error is null && Progress.Stage == SetupProgress.Ready));
    private bool RegionCreated { get; set; }
    public bool ShowForm => !Progress.Running && !Ready;

    public IActionResult OnGet(string? working)
    {
        if (Closed() is { } closed) return closed;
        Load();
        ShowProgress = working is not null || Progress.Running;
        RegionName = Core.Settings.RegionModel.First(settings())?.Name ?? "Welcome";
        RegionContent = StarterAvailable ? "starter" : "island";
        return Page();
    }

    public IActionResult OnPost()
    {
        if (Closed() is { } closed) return closed;
        if (progress.Running) return Redirect(WorkingUrl);
        Load();
        if (RegionCreated) return Redirect("/setup/region"); // shows "ready"; a second region is not set up here

        if (string.IsNullOrEmpty(RegionName))
            ModelState.AddModelError(nameof(RegionName), "Give your region a name.");
        else if (!GridController.IsValidRegionName(RegionName)) // the controller's own rule
            ModelState.AddModelError(nameof(RegionName),
                "Use 2 to 32 letters, digits, spaces, hyphens (-), underscores (_) or apostrophes ('), with no space at the start or end.");
        if (!(RegionContent == "island" || (RegionContent == "starter" && StarterAvailable)))
            ModelState.AddModelError(nameof(RegionContent), "Pick what your region should look like.");
        if (!ModelState.IsValid) return Page();

        runner.TryStartRegion(RegionName!, RegionContent!); // false: one is already running, whose progress is shown
        return Redirect(WorkingUrl);
    }

    private IActionResult? Closed() => SetupSteps.CanOpen(settings().Setup, 3) ? null : Redirect("/setup");

    private void Load()
    {
        StarterAvailable = StarterContent.StarterOar(paths) is not null;
        Progress = progress.Snapshot();
        RegionCreated = settings().Setup.RegionCreated;
    }
}
