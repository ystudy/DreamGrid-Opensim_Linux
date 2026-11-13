using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Login;

/// <summary>
/// The second sign-in step for someone with two-factor on: the password step left a pending cookie (<see cref="TwoFactorPending"/>,
/// five minutes); a right authenticator code or an unused backup code signs them in (audited as <c>sign-in</c>, plus
/// <c>backup-code-used</c>). A wrong code counts as a failure in the throttle for the account name and the address, so five of
/// them lock the name as five wrong passwords do; while locked, no code is checked. Without a pending cookie (or once it has
/// expired, or two-factor was turned off for them meanwhile) this sends back to the password. A right code that can't be marked
/// used (the save failed) signs nobody in and isn't counted as wrong. Codes are never logged.
/// </summary>
public sealed class CodeModel(TwoFactorStore twoFactor, LoginThrottle throttle, AuditLog audit, ILogger<CodeModel> log) : PageModel
{
    public const string WrongCode = "That code isn't right. Try again.";

    [BindProperty] public string? Code { get; set; }

    public string? Error { get; private set; }

    /// <summary>The two-factor file can't be read: the page says how the owner resets it.</summary>
    public bool TwoFactorUnknown { get; private set; }

    public IActionResult OnGet()
    {
        if (TwoFactorPending.Read(HttpContext) is null) return BackToPassword();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (TwoFactorPending.Read(HttpContext) is not { } pending) return BackToPassword();
        var who = new CodeStepCaller(pending.Principal, pending.Name, pending.First, pending.Role.AuditName());
        var result = await TwoFactorCodeStep.VerifyAsync(twoFactor, throttle, audit, log, "Sign-in", SignInDoor.Staff, who, Code,
            HttpContext.Connection.RemoteIpAddress, async () =>
            {
                TwoFactorPending.Delete(HttpContext);
                await StaffSignIn.SignInAsync(HttpContext, pending.Account, pending.Role);
            });
        switch (result)
        {
            case CodeStepResult.TwoFactorOff:
                return BackToPassword(); // reset meanwhile: the password step decides again
            case CodeStepResult.TwoFactorUnknown:
                TwoFactorUnknown = true; // not a wrong code: nothing is counted
                return Page();
            case CodeStepResult.SignedIn:
                return LocalRedirect(LoginModel.Landing(pending.Role, pending.ReturnUrl, Url));
            default:
                Error = result switch
                {
                    CodeStepResult.Locked => "Too many attempts. Try again in a few minutes.",
                    CodeStepResult.SaveFailed => TwoFactorStore.SaveFailed,
                    _ => WrongCode,
                };
                return Page();
        }
    }

    private IActionResult BackToPassword()
    {
        TwoFactorPending.Delete(HttpContext);
        return LocalRedirect("/login");
    }
}
