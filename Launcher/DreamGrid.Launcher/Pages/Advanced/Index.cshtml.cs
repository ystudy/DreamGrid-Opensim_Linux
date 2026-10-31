using DreamGrid.Launcher.Core.Modules;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Advanced;

/// <summary>
/// The Advanced Settings menu (spec 6.5): Network, Database, one entry per module page, then Logs. The owner also sees Password
/// reset where there is a guest side.
/// </summary>
public sealed class IndexModel(IEnumerable<ISettingsPage> modulePages, Func<LauncherSettings> settings) : PageModel
{
    public IReadOnlyList<ISettingsPage> ModulePages { get; } = modulePages.ToList();

    public bool ShowPasswordReset { get; private set; }

    public void OnGet() => ShowPasswordReset = User.IsInRole(AuthSchemes.OwnerRole) && AdminRule.HasStaffPages(settings());
}
