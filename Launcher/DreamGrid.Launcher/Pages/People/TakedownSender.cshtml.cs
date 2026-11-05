using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.People;

/// <summary>
/// The page for the notice's sender (<c>/people/takedowns/TD-2026-001/sender</c>): staff print it or save it as a PDF. It shows
/// <see cref="TakedownRules.SenderView"/> only, never the holders, their folders or staff notes. Still behind the staff sign-in.
/// </summary>
public sealed class TakedownSenderModel(TakedownStore store, Func<LauncherSettings> settings) : PageModel
{
    public TakedownCase? Case { get; private set; }
    public IReadOnlyList<SenderItem> Items { get; private set; } = [];
    public string GridName { get; private set; } = "";

    public IActionResult OnGet(string number)
    {
        if (!TakedownAccess.MayUse(User)) return StatusCode(StatusCodes.Status403Forbidden);
        Case = store.Get(number);
        if (Case is null) return NotFound();
        Items = TakedownRules.SenderView(Case);
        GridName = settings().GridName;
        return Page();
    }
}
