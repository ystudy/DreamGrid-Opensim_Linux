using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Guests;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace GridGate.Launcher.Pages.Guest;

/// <summary>
/// The guest door's second step for a resident who set up two-factor (<see cref="ResidentTwoFactor"/>): the password step left a
/// pending cookie (<see cref="GuestTwoFactorPending"/>, five minutes); a right authenticator code or an unused backup code signs
/// them in (audited <c>sign-in</c>, plus <c>backup-code-used</c>). A wrong code counts in the throttle for the name at the guest
/// door and the address; while locked, no code is checked. Without a pending cookie, or once two-factor was turned off for them
/// meanwhile, this sends back to the password. Codes are never logged.
/// </summary>
public sealed class SignInCodeModel(ResidentTwoFactor residents, LoginThrottle throttle, AuditLog audit, ILogger<SignInCodeModel> log,
    IStringLocalizer<ResidentText> t) : PageModel
{
    [BindProperty] public string? Code { get; set; }

    public string? Error { get; private set; }

    /// <summary>The two-factor file can't be read: the page says to contact the grid owner.</summary>
    public bool TwoFactorUnknown { get; private set; }

    public IActionResult OnGet() => GuestTwoFactorPending.Read(HttpContext) is null ? BackToPassword() : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (GuestTwoFactorPending.Read(HttpContext) is not { } pending) return BackToPassword();
        var who = new CodeStepCaller(pending.Principal, pending.Name, pending.First, AuthSchemes.GuestRole);
        var result = await TwoFactorCodeStep.VerifyAsync(residents.Store, throttle, audit, log, "Guest sign-in", SignInDoor.Guest, who, Code,
            HttpContext.Connection.RemoteIpAddress, async () =>
            {
                GuestTwoFactorPending.Delete(HttpContext);
                await GuestSignIn.SignInAsync(HttpContext, pending.Account);
            });
        switch (result)
        {
            case CodeStepResult.TwoFactorOff:
                return BackToPassword();
            case CodeStepResult.TwoFactorUnknown:
                TwoFactorUnknown = true;
                return Page();
            case CodeStepResult.SignedIn:
                return LocalRedirect("/guest");
            default:
                Error = result switch
                {
                    CodeStepResult.Locked => t["Common.TooManyAttempts"],
                    CodeStepResult.SaveFailed => t["SignIn.SaveFailed"],
                    _ => t["Common.WrongCode"],
                };
                return Page();
        }
    }

    private IActionResult BackToPassword()
    {
        GuestTwoFactorPending.Delete(HttpContext);
        return LocalRedirect("/guest/sign-in");
    }
}
