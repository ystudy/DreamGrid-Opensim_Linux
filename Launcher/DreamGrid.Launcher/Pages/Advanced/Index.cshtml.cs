using DreamGrid.Launcher.Core.Modules;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Advanced;

/// <summary>The Advanced Settings menu (spec 6.5): Network, Database, one entry per module page, then Logs.</summary>
public sealed class IndexModel(IEnumerable<ISettingsPage> modulePages) : PageModel
{
    public IReadOnlyList<ISettingsPage> ModulePages { get; } = modulePages.ToList();

    public void OnGet() { }
}
