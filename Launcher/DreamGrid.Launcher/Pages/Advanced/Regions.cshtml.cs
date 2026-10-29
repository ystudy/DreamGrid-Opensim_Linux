using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Advanced;

/// <summary>
/// The physics engine and script setting that regions without their own choice use. A save goes through
/// IGridController.UpdateSettings; a value the form does not offer is a 400 and nothing is saved.
/// </summary>
public sealed class RegionsModel(IGridController grid, Func<LauncherSettings> settings) : PageModel
{
    /// <summary>The engines offered, with the words the region Physics tab uses.</summary>
    public static readonly IReadOnlyList<(string Value, string Text)> PhysicsChoices =
        [.. RegionSettingsRules.PhysicsEngines.Select(e => (e, SettingLabels.Physics(e)))];

    [BindProperty] public string? DefaultPhysics { get; set; }

    [BindProperty] public string? DefaultScripts { get; set; }

    /// <summary>Set after a save: the line telling which regions the change reaches.</summary>
    public string? SavedMessage { get; private set; }

    /// <summary>True when the save reaches at least one region, so a restart is worth offering.</summary>
    public bool ShowRestart { get; private set; }

    public void OnGet() => Fill(settings());

    public IActionResult OnPost()
    {
        if (DefaultPhysics is null || !RegionSettingsRules.PhysicsEngines.Contains(DefaultPhysics)
            || DefaultScripts is not ("true" or "false"))
            return BadRequest();
        var physics = DefaultPhysics;
        var scripts = DefaultScripts == "true";

        // Read the old values inside the change, so the comparison cannot race another save.
        var physicsChanged = false;
        var scriptsChanged = false;
        var saved = grid.UpdateSettings(s =>
        {
            physicsChanged = s.DefaultPhysics != physics;
            scriptsChanged = s.DefaultScripts != scripts;
            return s with { DefaultPhysics = physics, DefaultScripts = scripts };
        }, writeConfig: true);

        var live = RegionModel.Live(saved)
            .Select(r => (r.Name, p: RegionModel.ProgramOf(saved, r)))
            .Where(x => x.p is not null)
            .ToList();
        var names = live
            .Where(x => (physicsChanged && x.p!.Physics is null) || (scriptsChanged && x.p!.Options?.Scripts is null))
            .Select(x => x.Name)
            .ToList();
        if (!physicsChanged && !scriptsChanged) SavedMessage = "Saved. Nothing changed.";
        else if (names.Count > 0)
        {
            SavedMessage = $"Saved. It applies when {RegionAdvice.Join(names)} {(names.Count == 1 ? "restarts" : "restart")}.";
            ShowRestart = true;
        }
        else if (live.Any(x => x.p!.Physics is null || x.p.Options?.Scripts is null))
            SavedMessage = "Saved. No region needs a restart.";
        else
            SavedMessage = "Saved. No region uses the grid default right now.";
        Fill(saved);
        return Page();
    }

    private void Fill(LauncherSettings s)
    {
        DefaultPhysics = s.DefaultPhysics;
        DefaultScripts = s.DefaultScripts ? "true" : "false";
    }
}
