using System.Security.Cryptography;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>
/// A signed-in resident's own two-factor sign-in (GuestPolicy, by the /Guest folder convention), against the residents' store
/// (<see cref="ResidentTwoFactor"/>). The page exists (else 404) only while the residents' recovery rule asks for a code or the
/// resident already has it on (<see cref="ResidentTwoFactor.PageShown"/>). It works as the staff page does: off shows a new
/// secret as a QR code and as text, with the protected secret in the form; "Turn on" shows the ten backup codes once (audited
/// <c>two-factor-on</c>); on offers "New backup codes" and "Turn off" (audited <c>two-factor-off</c>). A wrong code counts in the
/// throttle for the name at the guest door and the address; while locked, no code is checked. Secrets and codes are never logged.
/// </summary>
public sealed class TwoFactorModel(ResidentTwoFactor residents, Func<LauncherSettings> settings, AuditLog audit, IDataProtectionProvider dp,
    LoginThrottle throttle, ILogger<TwoFactorModel> log) : PageModel
{
    public const string WrongCode = "That code isn't right. Try again.";
    private const string ShownPurpose = "DreamGrid.GuestTwoFactorSetup.v1";

    [BindProperty] public string? Code { get; set; }

    /// <summary>The secret this form showed, protected for this person.</summary>
    [BindProperty] public string? Shown { get; set; }

    public TwoFactorState State { get; private set; }
    public string? QrSvg { get; private set; }
    public string? SecretText { get; private set; }
    /// <summary>Shown once, just after turning on or asking for new ones.</summary>
    public IReadOnlyList<string>? BackupCodes { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    private TwoFactorStore Store => residents.Store;

    public IActionResult OnGet()
    {
        if (Open() is not { } principal) return NotFound();
        if (State == TwoFactorState.Off) StartEnrol(principal);
        return Page();
    }

    public IActionResult OnPostTurnOn()
    {
        if (Open() is not { } principal) return NotFound();
        if (State != TwoFactorState.Off) return Page();
        if (Unprotect(principal, Shown) is not { } shown)
        {
            Notice = "Setup started again. Scan the new code below.";
            StartEnrol(principal);
            return Page();
        }

        IReadOnlyList<string>? codes;
        try
        {
            codes = Store.ConfirmEnrol(principal, Code ?? "", shown);
        }
        catch (Exception e) when (Failed(e))
        {
            if (State == TwoFactorState.Off) ShowSecret(principal, shown);
            return Page();
        }
        if (codes is null)
        {
            if (Store.IsPending(principal, shown))
            {
                Error = WrongCode;
                ShowSecret(principal, shown);
            }
            else
            {
                Notice = "Setup was started again somewhere else. Scan the new code below.";
                StartEnrol(principal);
            }
            return Page();
        }

        audit.Write(Entry(principal, "two-factor-on"));
        State = TwoFactorState.On;
        BackupCodes = codes;
        return Page();
    }

    public IActionResult OnPostTurnOff()
    {
        if (Open() is not { } principal) return NotFound();
        if (State != TwoFactorState.On) return RedirectToPage();
        if (Locked()) return Page();
        try
        {
            if (!Store.VerifyCode(principal, Code ?? "", out var usedBackup)) return Wrong(principal);
            if (usedBackup) audit.Write(Entry(principal, "backup-code-used"));
            Store.Disable(principal);
        }
        catch (Exception e) when (Failed(e))
        {
            return Page();
        }
        audit.Write(Entry(principal, "two-factor-off"));
        // Off and the rule doesn't ask for it: the page is gone, so go home.
        return ResidentTwoFactor.PageShown(settings(), TwoFactorState.Off) ? RedirectToPage() : LocalRedirect("/guest");
    }

    public IActionResult OnPostNewCodes()
    {
        if (Open() is not { } principal) return NotFound();
        if (State != TwoFactorState.On) return RedirectToPage();
        if (Locked()) return Page();
        IReadOnlyList<string>? codes;
        try
        {
            codes = Store.RegenerateBackupCodes(principal, Code ?? "");
        }
        catch (Exception e) when (Failed(e))
        {
            return Page();
        }
        if (codes is null) return Wrong(principal);
        audit.Write(Entry(principal, "backup-codes-new"));
        BackupCodes = codes;
        return Page();
    }

    /// <summary>The signed-in resident, with <see cref="State"/> loaded; null when the page doesn't exist for them.</summary>
    private Guid? Open()
    {
        if (GuestSignIn.Read(User) is not { } guest) return null;
        State = Store.StateOf(guest.Principal);
        return ResidentTwoFactor.PageShown(settings(), State) ? guest.Principal : null;
    }

    private string ThrottleKey => AvatarName.Parse(User.Identity?.Name).Key;

    private bool Locked()
    {
        if (!throttle.IsLocked(SignInDoor.Guest, ThrottleKey, HttpContext.Connection.RemoteIpAddress)) return false;
        Error = "Too many attempts. Try again in a few minutes.";
        return true;
    }

    private IActionResult Wrong(Guid principal)
    {
        throttle.RecordFailure(SignInDoor.Guest, ThrottleKey, HttpContext.Connection.RemoteIpAddress);
        audit.Write(Entry(principal, "sign-in-refused", "two-factor code", "refused"));
        Error = WrongCode;
        return Page();
    }

    private void StartEnrol(Guid principal)
    {
        try
        {
            var (secret, _) = Store.BeginEnrol(principal, AccountName(), settings().GridName);
            ShowSecret(principal, secret);
        }
        catch (Exception e) when (Failed(e))
        {
        }
    }

    private void ShowSecret(Guid principal, byte[] secret)
    {
        QrSvg = TwoFactorQr.Svg(Totp.OtpAuthUri(settings().GridName, AccountName(), secret));
        SecretText = Totp.Base32(secret);
        Shown = Convert.ToBase64String(Protector(principal).Protect(secret));
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

    private string AccountName() => LogText.Clean(User.Identity?.Name, 64);

    /// <summary>The store's own failures: a damaged file, or a save that didn't happen. False for anything else.</summary>
    private bool Failed(Exception e)
    {
        if (e is InvalidOperationException)
        {
            State = TwoFactorState.Unknown;
            return true;
        }
        if (e is not (IOException or UnauthorizedAccessException)) return false;
        log.LogWarning("Resident two-factor: a change for first name {First} couldn't be saved: {Message}",
            AvatarName.Parse(User.Identity?.Name).ForLog, e.Message);
        Error = TwoFactorStore.SaveFailed;
        return true;
    }

    private AuditEntry Entry(Guid principal, string action, string? target = null, string result = "ok") =>
        new(LogText.Clean(User.Identity?.Name, 64), principal, AuthSchemes.GuestRole,
            HttpContext.Connection.RemoteIpAddress?.ToString(), action, target, result);
}
