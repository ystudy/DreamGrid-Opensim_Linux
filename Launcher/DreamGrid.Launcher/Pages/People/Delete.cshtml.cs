using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.People;

/// <summary>
/// "Are you sure?" before a suspended resident is deleted for good (<c>/people/{id}/delete</c>), the owner and admins only (a
/// supervisor gets 403). It lists what goes and the regions that move back to the grid; the post must carry <c>confirm=yes</c>,
/// the page's one Delete button, and <see cref="PeopleActions.DeleteAsync"/> checks every rule again.
/// </summary>
public sealed class DeleteModel(PeopleActions people) : PageModel
{
    public PeopleActions.DeletePreview? Preview { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        if (!MayDelete()) return StatusCode(StatusCodes.Status403Forbidden);
        Preview = await people.PreviewDeleteAsync(User, id, ct);
        return Preview is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, string? confirm, CancellationToken ct)
    {
        if (!MayDelete()) return StatusCode(StatusCodes.Status403Forbidden);
        if (confirm != "yes")
        {
            Error = "Press the Delete button on this page to confirm.";
        }
        else
        {
            var outcome = await people.DeleteAsync(User, HttpContext.Connection.RemoteIpAddress?.ToString(), id, ct);
            if (outcome.Done)
            {
                Notice = outcome.Message;
                return Page();
            }
            Error = outcome.Message;
        }
        Preview = await people.PreviewDeleteAsync(User, id, ct);
        return Page();
    }

    private bool MayDelete() => StaffSignIn.RoleOf(User) is StaffRole.Admin or StaffRole.Owner;
}
