using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

/// <summary>
/// The public front page: the grid's name and the ways in. Anyone not signed in as staff gets it at "/" (SetupGate hands the
/// bare root to this page); it is open to every address (<c>AdminNetworkFilter</c>) and, with the other guest pages, does not
/// exist on a Region Server or a Connected Server (<c>GuestSideOnly</c>).
/// </summary>
public sealed class HomeModel(Func<LauncherSettings> settings) : PageModel
{
    public string GridName { get; private set; } = "";

    public void OnGet() => GridName = settings().GridName;
}
