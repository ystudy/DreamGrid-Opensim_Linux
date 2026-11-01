using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.People;

/// <summary>
/// The step before a deny (StaffPolicy; <c>/people/sign-ups/deny?id=</c>; 404 where there is no guest side): "Deny {name}?" with an
/// optional note for the email. The post denies through <see cref="SignUpApprovals"/>, which checks the right again.
/// </summary>
public sealed class DenyModel(SignUpBoard board, SignUpApprovals approvals) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty] public string? Note { get; set; }

    [TempData] public string? Notice { get; set; }

    public string Name { get; private set; } = "";

    public IActionResult OnGet()
    {
        if (board.Get(Id) is not { Waiting: true } entry) return LocalRedirect(SignUpRows.ListPath);
        Name = entry.Name;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var r = await approvals.DenyAsync(Id, User.Identity?.Name ?? "", StaffSignIn.RoleOf(User), Note, HttpContext.Connection.RemoteIpAddress, ct);
        Notice = SignUpsModel.Message(r, "Denied.");
        return LocalRedirect(SignUpRows.SafeReturn(ReturnUrl, Url));
    }
}
