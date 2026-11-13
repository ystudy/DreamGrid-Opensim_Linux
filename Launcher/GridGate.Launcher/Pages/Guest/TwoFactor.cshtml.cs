using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Guests;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace GridGate.Launcher.Pages.Guest;

/// <summary>
/// A signed-in resident's own two-factor sign-in (GuestPolicy, by the /Guest folder convention), against the residents' store
/// (<see cref="ResidentTwoFactor"/>). The page exists (else 404) only while the residents' recovery rule asks for a code or the
/// resident already has it on (<see cref="ResidentTwoFactor.PageShown"/>). It works as the staff page does: off shows a new
/// secret as a QR code and as text, with the protected secret in the form; "Turn on" shows the ten backup codes once (audited
/// <c>two-factor-on</c>); on offers "New backup codes" and "Turn off" (audited <c>two-factor-off</c>). A wrong code counts in the
/// throttle for the name at the guest door and the address; while locked, no code is checked. Secrets and codes are never logged.
/// </summary>
public sealed class TwoFactorModel(ResidentTwoFactor residents, Func<LauncherSettings> settings, AuditLog audit, IDataProtectionProvider dp,
    LoginThrottle throttle, ILogger<TwoFactorModel> log, IStringLocalizer<ResidentText> t)
    : TwoFactorSetupPageModel(settings, audit, dp, throttle, log)
{
    protected override TwoFactorStore Store => residents.Store;
    /// <summary>Seals the setup form on screen; changing it breaks forms already open.</summary>
    public const string SetupPurpose = "GridGate.GuestTwoFactorSetup.v1";
    protected override string ShownPurpose => SetupPurpose;
    protected override SignInDoor Door => SignInDoor.Guest;
    protected override string AuditRole => AuthSchemes.GuestRole;
    protected override string LogArea => "Resident two-factor";
    protected override TwoFactorSetupTexts Texts => new(t["Common.WrongCode"], t["Common.TooManyAttempts"], t["SignIn.SaveFailed"],
        t["TwoFactor.StartedAgain"], t["TwoFactor.StartedElsewhere"]);

    public IActionResult OnGet()
    {
        if (Open() is not { } principal) return NotFound();
        if (State == TwoFactorState.Off) StartEnrol(principal);
        return Page();
    }

    public async Task<IActionResult> OnPostTurnOnAsync()
    {
        if (Open() is not { } principal) return NotFound();
        if (State != TwoFactorState.Off) return Page();
        return await TurnOnAsync(principal);
    }

    public IActionResult OnPostTurnOff()
    {
        if (Open() is not { } principal) return NotFound();
        if (State != TwoFactorState.On) return RedirectToPage();
        // Off and the rule doesn't ask for it: the page is gone, so go home.
        return TurnOff(principal, () => ResidentTwoFactor.PageShown(Settings(), TwoFactorState.Off) ? RedirectToPage() : LocalRedirect("/guest"));
    }

    public IActionResult OnPostNewCodes()
    {
        if (Open() is not { } principal) return NotFound();
        if (State != TwoFactorState.On) return RedirectToPage();
        return NewCodes(principal);
    }

    /// <summary>The signed-in resident, with <see cref="TwoFactorSetupPageModel.State"/> loaded; null when the page doesn't exist for them.</summary>
    private Guid? Open()
    {
        if (GuestSignIn.Read(User) is not { } guest) return null;
        State = Store.StateOf(guest.Principal);
        return ResidentTwoFactor.PageShown(Settings(), State) ? guest.Principal : null;
    }
}
