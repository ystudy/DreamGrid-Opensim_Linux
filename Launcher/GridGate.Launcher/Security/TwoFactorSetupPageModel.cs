using System.Security.Cryptography;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Security;

/// <summary>What the person is told at the setup steps; each page has its own wording (the residents' comes from the translations).</summary>
public sealed record TwoFactorSetupTexts(string WrongCode, string TooManyAttempts, string SaveFailed, string StartedAgain, string StartedElsewhere);

/// <summary>
/// The steps the staff page (<c>/Profile/TwoFactor</c>) and the resident page (<c>/Guest/TwoFactor</c>) share: showing a new secret
/// (QR code and text, protected in the form), confirming it, new backup codes, turning off, and the wrong-code throttle. Each page
/// says who is signed in, which store, which data-protection purpose, door, audit role, log area and wording to use. Secrets and
/// codes are never logged.
/// </summary>
public abstract class TwoFactorSetupPageModel(Func<LauncherSettings> settings, AuditLog audit, IDataProtectionProvider dp,
    LoginThrottle throttle, ILogger log) : PageModel
{
    [BindProperty] public string? Code { get; set; }

    /// <summary>The secret this form showed, protected for this person.</summary>
    [BindProperty] public string? Shown { get; set; }

    public TwoFactorState State { get; protected set; }
    public string? QrSvg { get; private set; }
    public string? SecretText { get; private set; }
    /// <summary>Shown once, just after turning on or asking for new ones.</summary>
    public IReadOnlyList<string>? BackupCodes { get; private set; }
    public string? Error { get; protected set; }
    public string? Notice { get; private set; }

    protected abstract TwoFactorStore Store { get; }
    protected abstract string ShownPurpose { get; }
    protected abstract SignInDoor Door { get; }
    protected abstract string AuditRole { get; }
    protected abstract string LogArea { get; }
    protected abstract TwoFactorSetupTexts Texts { get; }

    /// <summary>After the codes are made and audited, before the page shows them.</summary>
    protected virtual Task TurnedOnAsync(Guid principal) => Task.CompletedTask;

    /// <summary>The pages read settings through this: passing the constructor parameter to the base and using it again is CS9124.</summary>
    protected LauncherSettings Settings() => settings();

    /// <summary>Starts setup with a new secret and shows it.</summary>
    protected void StartEnrol(Guid principal)
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

    /// <summary>The "Turn on" form: confirms the secret the form carried with the code, then shows the backup codes once.</summary>
    protected async Task<IActionResult> TurnOnAsync(Guid principal)
    {
        if (Unprotect(principal, Shown) is not { } shown)
        {
            Notice = Texts.StartedAgain;
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
                Error = Texts.WrongCode;
                ShowSecret(principal, shown);
            }
            else
            {
                Notice = Texts.StartedElsewhere;
                StartEnrol(principal);
            }
            return Page();
        }

        audit.Write(Entry(principal, "two-factor-on"));
        await TurnedOnAsync(principal);
        State = TwoFactorState.On;
        BackupCodes = codes;
        return Page();
    }

    /// <summary>The "Turn off" form, from the throttle check on: a current code or a backup code; <paramref name="done"/> says where to go after.</summary>
    protected IActionResult TurnOff(Guid principal, Func<IActionResult> done)
    {
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
        return done();
    }

    /// <summary>The "New backup codes" form, from the throttle check on: a current code replaces all ten.</summary>
    protected IActionResult NewCodes(Guid principal)
    {
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

    private string ThrottleKey => AvatarName.Parse(User.Identity?.Name).Key;

    private bool Locked()
    {
        if (!throttle.IsLocked(Door, ThrottleKey, HttpContext.Connection.RemoteIpAddress)) return false;
        Error = Texts.TooManyAttempts;
        return true;
    }

    /// <summary>A wrong code: counted by the throttle and audited, as at the code step.</summary>
    private IActionResult Wrong(Guid principal)
    {
        throttle.RecordFailure(Door, ThrottleKey, HttpContext.Connection.RemoteIpAddress);
        audit.Write(Entry(principal, "sign-in-refused", "two-factor code", "refused"));
        Error = Texts.WrongCode;
        return Page();
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

    /// <summary>"First Last", as the authenticator app lists the account.</summary>
    private string AccountName() => LogText.Clean(User.Identity?.Name, 64);

    /// <summary>
    /// The exception filter for the store's own failures: a damaged file (the page then shows how the owner resets it) or a save
    /// that didn't happen (said so, and logged without any secret). False for anything else, which is left to propagate.
    /// </summary>
    private bool Failed(Exception e)
    {
        if (e is InvalidOperationException)
        {
            State = TwoFactorState.Unknown;
            return true;
        }
        if (e is not (IOException or UnauthorizedAccessException)) return false;
        log.LogWarning("{Area}: a change for first name {First} couldn't be saved: {Message}",
            LogArea, AvatarName.Parse(User.Identity?.Name).ForLog, e.Message);
        Error = Texts.SaveFailed;
        return true;
    }

    private AuditEntry Entry(Guid principal, string action, string? target = null, string result = "ok") =>
        new(LogText.Clean(User.Identity?.Name, 64), principal, AuditRole,
            HttpContext.Connection.RemoteIpAddress?.ToString(), action, target, result);
}
