using DreamGrid.Launcher.Core.Modules;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Advanced;

/// <summary>The Settings menu (spec 6.5): Plan, Network, Database, Region defaults, one entry per module page, then Logs.</summary>
public sealed class IndexModel(IEnumerable<ISettingsPage> modulePages) : PageModel
{
    public IReadOnlyList<ISettingsPage> ModulePages { get; } = modulePages.ToList();
}
