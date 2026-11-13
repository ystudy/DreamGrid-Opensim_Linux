using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace GridGate.Launcher.Pages.Profile;

/// <summary>
/// The signed-in staff member's own two-factor sign-in (StaffPolicy, by the /Profile folder convention; the one page
/// <see cref="TwoFactorGate"/> leaves open to someone who must set it up first).
/// <para>Off: a new secret each time the page is shown (<see cref="TwoFactorStore.BeginEnrol"/>), as a QR code (issuer the grid
/// name, account "First Last") and as Base32 text, and a code field. The form carries the secret it showed, protected (purpose
/// bound to the person), so a wrong code shows the same secret again, and a secret shown in another browser (setup started
/// again there) can never be the one confirmed here. "Turn on" shows the ten backup codes once (audited <c>two-factor-on</c>);
/// someone who signed in only to set it up gets a new cookie without that limit (a new session; the old one ends).</para>
/// <para>On: "New backup codes" (a current authenticator code; replaces all ten) and "Turn off" (a current code or a backup
/// code; audited <c>two-factor-off</c>). An admin or supervisor can't turn it off while the owner requires two-factor for staff;
/// the switch never applies to the owner. A wrong code there counts in the sign-in throttle for the person's name and the
/// address, as at sign-in, so a stolen session can't guess its way to switching two-factor off; while locked, no code is checked.</para>
/// <para>A damaged two-factor file shows how the owner resets it; a failed save says so. Secrets and codes are never logged.</para>
/// </summary>
public sealed class TwoFactorModel(TwoFactorStore store, Func<LauncherSettings> settings, AuditLog audit, IDataProtectionProvider dp,
    LoginThrottle throttle, ILogger<TwoFactorModel> log) : TwoFactorSetupPageModel(settings, audit, dp, throttle, log)
{
    public const string WrongCode = "That code isn't right. Try again.";
    public const string RequiredText = "The grid owner requires two-factor sign-in.";

    private static readonly TwoFactorSetupTexts SetupTexts = new(WrongCode, "Too many attempts. Try again in a few minutes.",
        TwoFactorStore.SaveFailed, "Setup started again. Scan the new code below.",
        "Setup was started again somewhere else. Scan the new code below.");

    /// <summary>Has to set up two-factor before anything else (<see cref="TwoFactorGate.MustEnrol"/>).</summary>
    public bool MustEnrol { get; private set; }
    /// <summary>Signed in with a password only, to set up two-factor: the cookie is replaced once it is on.</summary>
    private bool HasEnrolClaim { get; set; }
    /// <summary>The owner's switch is on and applies to this person.</summary>
    public bool Required { get; private set; }
    public string Home { get; private set; } = "/";

    /// <summary>The claims <see cref="Load"/> read; every step that needs the role runs after it.</summary>
    private StaffClaims? _claims;

    protected override TwoFactorStore Store => store;
    /// <summary>Seals the setup form on screen; changing it breaks forms already open.</summary>
    public const string SetupPurpose = "GridGate.TwoFactorSetup.v1";
    protected override string ShownPurpose => SetupPurpose;
    protected override SignInDoor Door => SignInDoor.Staff;
    protected override string AuditRole => _claims!.Role.AuditName();
    protected override string LogArea => "Two-factor";
    protected override TwoFactorSetupTexts Texts => SetupTexts;

    public IActionResult OnGet()
    {
        if (StaffSignIn.Read(User) is not { } claims) return Challenge(AuthSchemes.Admin);
        Load(claims);
        if (State == TwoFactorState.Off) StartEnrol(claims.Principal);
        return Page();
    }

    public async Task<IActionResult> OnPostTurnOnAsync()
    {
        if (StaffSignIn.Read(User) is not { } claims) return Challenge(AuthSchemes.Admin);
        Load(claims);
        if (State != TwoFactorState.Off) return Page();
        return await TurnOnAsync(claims.Principal);
    }

    public IActionResult OnPostTurnOff()
    {
        if (StaffSignIn.Read(User) is not { } claims) return Challenge(AuthSchemes.Admin);
        Load(claims);
        if (State != TwoFactorState.On) return RedirectToPage();
        if (Required)
        {
            Error = RequiredText;
            return Page();
        }

        return TurnOff(claims.Principal, RedirectToPage);
    }

    public IActionResult OnPostNewCodes()
    {
        if (StaffSignIn.Read(User) is not { } claims) return Challenge(AuthSchemes.Admin);
        Load(claims);
        if (State != TwoFactorState.On) return RedirectToPage();

        return NewCodes(claims.Principal);
    }

    protected override async Task TurnedOnAsync(Guid principal)
    {
        if (HasEnrolClaim)
        {
            // The cookie that could only reach this page is replaced by an ordinary one (a new session; the old one ends).
            var name = AvatarName.Parse(User.Identity?.Name);
            // The level isn't part of the cookie. This page is then rendered for the new cookie, so the layout shows the ordinary
            // links (TwoFactorGate.MustEnrol is worked out again for it).
            HttpContext.User = await StaffSignIn.SignInAsync(HttpContext,
                new RobustAccount(principal, name.First, name.Last, 0, ""), _claims!.Role);
            MustEnrol = false;
        }
        TwoFactorGate.Forget(HttpContext); // the layout works it out again: two-factor is on now
    }

    private void Load(StaffClaims claims)
    {
        _claims = claims;
        State = store.StateOf(claims.Principal);
        HasEnrolClaim = User.HasClaim(c => c.Type == StaffSignIn.EnrolClaim);
        MustEnrol = TwoFactorGate.MustEnrol(HttpContext);
        Required = Settings().Security.RequireTwoFactorForStaff && claims.Role is StaffRole.Admin or StaffRole.Supervisor;
        Home = StaffSignIn.Home(claims.Role);
    }
}
