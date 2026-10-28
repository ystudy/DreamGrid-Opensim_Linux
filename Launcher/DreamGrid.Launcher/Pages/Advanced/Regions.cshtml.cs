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
        [("ubODE", "ubODE"), ("BulletSim", "BulletSim"), ("BulletSimThreaded", "BulletSim in its own thread")];

    [BindProperty] public string? DefaultPhysics { get; set; }

    [BindProperty] public string? DefaultScripts { get; set; }

    /// <summary>Set after a save: the line telling which regions the change reaches.</summary>
    public string? SavedMessage { get; private set; }

    public void OnGet() => Fill(settings());

    public IActionResult OnPost()
    {
        if (DefaultPhysics is null || !RegionSettingsRules.PhysicsEngines.Contains(DefaultPhysics)
            || DefaultScripts is not ("true" or "false"))
            return BadRequest();
        var physics = DefaultPhysics;
        var scripts = DefaultScripts == "true";

        var before = settings();
        var saved = grid.UpdateSettings(s => s with { DefaultPhysics = physics, DefaultScripts = scripts }, writeConfig: true);

        var physicsChanged = before.DefaultPhysics != physics;
        var scriptsChanged = before.DefaultScripts != scripts;
        var names = RegionModel.Live(saved)
            .Where(r => RegionModel.ProgramOf(saved, r) is { } p
                && ((physicsChanged && p.Physics is null) || (scriptsChanged && p.Options?.Scripts is null)))
            .Select(r => r.Name)
            .ToList();
        SavedMessage = names.Count == 0
            ? "Saved. No region uses the grid default right now."
            : $"Saved. It applies when {RegionAdvice.Join(names)} {(names.Count == 1 ? "restarts" : "restart")}.";
        Fill(saved);
        return Page();
    }

    private void Fill(LauncherSettings s)
    {
        DefaultPhysics = s.DefaultPhysics;
        DefaultScripts = s.DefaultScripts ? "true" : "false";
    }
}
