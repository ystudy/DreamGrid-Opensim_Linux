using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Advanced;

/// <summary>
/// The generic page for a module's <see cref="ISettingsPage"/> (spec 6.9). Every field posts back under its key;
/// the module validates, and only a valid form is applied and saved through IGridController.UpdateSettings.
/// </summary>
public sealed class ModuleModel(IEnumerable<ISettingsPage> pages, IGridController grid, Func<LauncherSettings> settings) : PageModel
{
    public ISettingsPage Module { get; private set; } = null!;
    public IReadOnlyList<SettingField> Fields { get; private set; } = [];
    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();
    public bool Saved { get; private set; }

    public IActionResult OnGet(string id)
    {
        if (Find(id) is not { } module) return NotFound();
        Module = module;
        Fields = module.Fields(settings());
        return Page();
    }

    public IActionResult OnPost(string id)
    {
        if (Find(id) is not { } module) return NotFound();
        Module = module;
        var current = module.Fields(settings());
        var values = new Dictionary<string, string>();
        foreach (var field in current)
        {
            var posted = Request.Form[field.Key].ToString();
            // A secret is never sent to the page, so an empty one means "unchanged".
            values[field.Key] = field.Kind == "secret" && posted == "" ? field.Value : posted;
        }

        var result = module.Validate(values);
        if (!result.Ok)
        {
            Errors = result.Errors;
            Fields = current.Select(f => f with { Value = values[f.Key] }).ToList();
            return Page();
        }

        var saved = grid.UpdateSettings(s => module.Apply(s, values), writeConfig: true);
        Fields = module.Fields(saved);
        Saved = true;
        return Page();
    }

    private ISettingsPage? Find(string id) => pages.FirstOrDefault(p => p.ModuleId == id);
}
