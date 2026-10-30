using System.Security.Cryptography;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Profile;

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
    LoginThrottle throttle, ILogger<TwoFactorModel> log) : PageModel
{
    public const string WrongCode = "That code isn't right. Try again.";
    public const string RequiredText = "The grid owner requires two-factor sign-in.";
    private const string ShownPurpose = "DreamGrid.TwoFactorSetup.v1";

    [BindProperty] public string? Code { get; set; }

    /// <summary>The secret this form showed, protected for this person.</summary>
    [BindProperty] public string? Shown { get; set; }

    public TwoFactorState State { get; private set; }
    /// <summary>Has to set up two-factor before anything else (<see cref="TwoFactorGate.MustEnrol"/>).</summary>
    public bool MustEnrol { get; private set; }
    /// <summary>Signed in with a password only, to set up two-factor: the cookie is replaced once it is on.</summary>
    private bool HasEnrolClaim { get; set; }
    /// <summary>The owner's switch is on and applies to this person.</summary>
    public bool Required { get; private set; }
    public string? QrSvg { get; private set; }
    public string? SecretText { get; private set; }
    /// <summary>Shown once, just after turning on or asking for new ones.</summary>
    public IReadOnlyList<string>? BackupCodes { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public string Home { get; private set; } = "/";

    public IActionResult OnGet()
    {
        if (StaffSignIn.Read(User) is not { } claims) return Challenge(AuthSchemes.Admin);
        Load(claims);
        if (State == TwoFactorState.Off) StartEnrol(claims);
        return Page();
    }

    public async Task<IActionResult> OnPostTurnOnAsync()
    {
        if (StaffSignIn.Read(User) is not { } claims) return Challenge(AuthSchemes.Admin);
        Load(claims);
        if (State != TwoFactorState.Off) return Page();

        if (Unprotect(claims.Principal, Shown) is not { } shown)
        {
            Notice = "Setup started again. Scan the new code below.";
            StartEnrol(claims);
            return Page();
        }

        IReadOnlyList<string>? codes;
        try
        {
            codes = store.ConfirmEnrol(claims.Principal, Code ?? "", shown);
        }
        catch (Exception e) when (Failed(e, claims))
        {
            if (State == TwoFactorState.Off) ShowSecret(claims, shown);
            return Page();
        }

        if (codes is null)
        {
            if (store.IsPending(claims.Principal, shown))
            {
                Error = WrongCode;
                ShowSecret(claims, shown);
            }
            else
            {
                Notice = "Setup was started again somewhere else. Scan the new code below.";
                StartEnrol(claims);
            }
            return Page();
        }

        audit.Write(Entry(claims, "two-factor-on"));
        if (HasEnrolClaim)
        {
            // The cookie that could only reach this page is replaced by an ordinary one (a new session; the old one ends).
            var name = AvatarName.Parse(User.Identity?.Name);
            // The level isn't part of the cookie. This page is then rendered for the new cookie, so the layout shows the ordinary
            // links (TwoFactorGate.MustEnrol is worked out again for it).
            HttpContext.User = await StaffSignIn.SignInAsync(HttpContext,
                new RobustAccount(claims.Principal, name.First, name.Last, 0, ""), claims.Role);
            MustEnrol = false;
        }
        State = TwoFactorState.On;
        TwoFactorGate.Forget(HttpContext); // the layout works it out again: two-factor is on now
        BackupCodes = codes;
        return Page();
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

        if (Locked()) return Page();
        try
        {
            if (!store.VerifyCode(claims.Principal, Code ?? "", out var usedBackup)) return Wrong(claims);
            if (usedBackup) audit.Write(Entry(claims, "backup-code-used"));
            store.Disable(claims.Principal);
        }
        catch (Exception e) when (Failed(e, claims))
        {
            return Page();
        }
        audit.Write(Entry(claims, "two-factor-off"));
        return RedirectToPage();
    }

    public IActionResult OnPostNewCodes()
    {
        if (StaffSignIn.Read(User) is not { } claims) return Challenge(AuthSchemes.Admin);
        Load(claims);
        if (State != TwoFactorState.On) return RedirectToPage();

        if (Locked()) return Page();
        IReadOnlyList<string>? codes;
        try
        {
            codes = store.RegenerateBackupCodes(claims.Principal, Code ?? "");
        }
        catch (Exception e) when (Failed(e, claims))
        {
            return Page();
        }
        if (codes is null) return Wrong(claims);
        audit.Write(Entry(claims, "backup-codes-new"));
        BackupCodes = codes;
        return Page();
    }

    private string ThrottleKey => AvatarName.Parse(User.Identity?.Name).Key;

    private bool Locked()
    {
        if (!throttle.IsLocked(ThrottleKey, HttpContext.Connection.RemoteIpAddress)) return false;
        Error = "Too many attempts. Try again in a few minutes.";
        return true;
    }

    /// <summary>A wrong code: counted by the throttle and audited, as at the code step.</summary>
    private IActionResult Wrong(StaffClaims claims)
    {
        throttle.RecordFailure(ThrottleKey, HttpContext.Connection.RemoteIpAddress);
        audit.Write(Entry(claims, "sign-in-refused", "two-factor code", "refused"));
        Error = WrongCode;
        return Page();
    }

    private void Load(StaffClaims claims)
    {
        State = store.StateOf(claims.Principal);
        HasEnrolClaim = User.HasClaim(c => c.Type == StaffSignIn.EnrolClaim);
        MustEnrol = TwoFactorGate.MustEnrol(HttpContext);
        Required = settings().Security.RequireTwoFactorForStaff && claims.Role is StaffRole.Admin or StaffRole.Supervisor;
        Home = StaffSignIn.Home(claims.Role);
    }

    /// <summary>Starts setup with a new secret and shows it.</summary>
    private void StartEnrol(StaffClaims claims)
    {
        try
        {
            var (secret, _) = store.BeginEnrol(claims.Principal, AccountName(), settings().GridName);
            ShowSecret(claims, secret);
        }
        catch (Exception e) when (Failed(e, claims))
        {
        }
    }

    private void ShowSecret(StaffClaims claims, byte[] secret)
    {
        QrSvg = TwoFactorQr.Svg(Totp.OtpAuthUri(settings().GridName, AccountName(), secret));
        SecretText = Totp.Base32(secret);
        Shown = Convert.ToBase64String(Protector(claims.Principal).Protect(secret));
    }

    private byte[]? Unprotect(Guid principal, string? shown)
    {
        if (string.IsNullOrEmpty(shown)) return null;
        try
        {
            return Protector(principal).Unprotect(Convert.FromBase64String(shown));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private IDataProtector Protector(Guid principal) => dp.CreateProtector(ShownPurpose, principal.ToString("N"));

    /// <summary>"First Last", as the authenticator app lists the account.</summary>
    private string AccountName() => LogText.Clean(User.Identity?.Name, 64);

    /// <summary>
    /// The exception filter for the store's own failures: a damaged file (the page then shows how the owner resets it) or a save
    /// that didn't happen (said so, and logged without any secret). False for anything else, which is left to propagate.
    /// </summary>
    private bool Failed(Exception e, StaffClaims claims)
    {
        if (e is InvalidOperationException)
        {
            State = TwoFactorState.Unknown;
            return true;
        }
        if (e is not (IOException or UnauthorizedAccessException)) return false;
        log.LogWarning("Two-factor: a change for first name {First} couldn't be saved: {Message}",
            AvatarName.Parse(User.Identity?.Name).ForLog, e.Message);
        Error = TwoFactorStore.SaveFailed;
        return true;
    }

    private AuditEntry Entry(StaffClaims claims, string action, string? target = null, string result = "ok") =>
        new(LogText.Clean(User.Identity?.Name, 64), claims.Principal, claims.Role.AuditName(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), action, target, result);
}
