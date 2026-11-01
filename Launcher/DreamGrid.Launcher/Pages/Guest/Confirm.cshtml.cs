using DreamGrid.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>
/// Where the sign-up email's link leads (anonymous). A live link confirms the account (<see cref="SignUps.ConfirmAsync"/>), signs
/// the new resident in and opens How to connect; any other link says it has expired. When the grid doesn't answer, it says to try
/// again, and the link stays good.
/// </summary>
public sealed class ConfirmModel(SignUps signUps) : PageModel
{
    public bool Busy { get; private set; }

    /// <summary>Staff approved the account before the link was opened.</summary>
    public bool AlreadyApproved { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? token, CancellationToken ct)
    {
        try
        {
            var (result, account) = await signUps.ConfirmAsync(token, HttpContext.Connection.RemoteIpAddress, ct);
            AlreadyApproved = result == ConfirmResult.AlreadyApproved;
            if (result != ConfirmResult.Confirmed) return Page();
            await GuestSignIn.SignInAsync(HttpContext, account!);
            return LocalRedirect("/guest/connect");
        }
        catch (SignUpBusyException)
        {
            Busy = true;
            return Page();
        }
    }
}
