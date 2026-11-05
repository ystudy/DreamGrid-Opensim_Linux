using DreamGrid.Launcher.Core.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.People;

/// <summary>Every takedown case (<c>/people/takedowns</c>), open ones first, the owner and admins only.</summary>
public sealed class TakedownsModel(TakedownStore store) : PageModel
{
    public IReadOnlyList<TakedownCase> Cases { get; private set; } = [];
    public bool Unreadable { get; private set; }

    public IActionResult OnGet()
    {
        if (!TakedownAccess.MayUse(User)) return StatusCode(StatusCodes.Status403Forbidden);
        Cases = store.All();
        Unreadable = store.Unreadable;
        return Page();
    }
}
