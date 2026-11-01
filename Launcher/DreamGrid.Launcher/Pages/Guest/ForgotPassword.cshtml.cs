using DreamGrid.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>
/// "Forgot password" for residents, staff and the owner (anonymous; on the guest side, so it is open from outside the owner's
/// Admin networks). The name and email go to <see cref="PasswordResets.AskAsync"/>, after the owner's bot protection
/// (<see cref="BotGuard"/>, form <c>forgot-password</c>). The page shows the one reply whatever happened, or, when nobody can
/// reset a password here, says so before asking anything.
/// </summary>
public sealed class ForgotPasswordModel(PasswordResets resets, BotGuard bots) : PageModel
{
    public const string BotFailed = "The form couldn't check that you're a person. Try again.";

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Email { get; set; }

    public bool Off { get; private set; }
    public bool Sent { get; private set; }
    public string? Error { get; private set; }

    public void OnGet() => Off = !resets.Available();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!resets.Available())
        {
            Off = true;
            return Page();
        }
        if (!bots.Check(HttpContext, "forgot-password").Passed)
        {
            Error = BotFailed;
            return Page();
        }
        Off = await resets.AskAsync(Name, Email, HttpContext.Connection.RemoteIpAddress, ct) == ResetAsk.GridOff;
        Sent = !Off;
        return Page();
    }
}
