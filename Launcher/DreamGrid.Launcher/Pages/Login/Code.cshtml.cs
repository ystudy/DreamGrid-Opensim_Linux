using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Login;

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
        var key = AvatarName.Parse(pending.Name).Key;
        var remote = HttpContext.Connection.RemoteIpAddress;
        var ip = remote?.ToString();
        if (throttle.IsLocked(key, remote))
        {
            Error = "Too many attempts. Try again in a few minutes.";
            return Page();
        }

        switch (twoFactor.StateOf(pending.Principal))
        {
            case TwoFactorState.Off:
                return BackToPassword(); // reset meanwhile: the password step decides again
            case TwoFactorState.Unknown:
                TwoFactorUnknown = true; // not a wrong code: nothing is counted
                return Page();
        }

        bool right, usedBackup;
        try
        {
            right = twoFactor.VerifyCode(pending.Principal, Code ?? "", out usedBackup);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not a wrong code, so not counted; nothing was used up.
            log.LogWarning("Sign-in: a two-factor code for first name {First} couldn't be marked used: {Message}",
                LogText.Clean(pending.First, 32), e.Message);
            Error = TwoFactorStore.SaveFailed;
            return Page();
        }

        if (!right)
        {
            throttle.RecordFailure(key, remote);
            log.LogInformation("Sign-in: a two-factor code for first name {First} was refused", LogText.Clean(pending.First, 32));
            audit.Write(new AuditEntry(pending.Name, pending.Principal, pending.Role.AuditName(), ip, "sign-in-refused", "two-factor code", "refused"));
            Error = WrongCode;
            return Page();
        }

        throttle.RecordSuccess(key);
        TwoFactorPending.Delete(HttpContext);
        await StaffSignIn.SignInAsync(HttpContext, pending.Account, pending.Role);
        audit.Write(new AuditEntry(pending.Name, pending.Principal, pending.Role.AuditName(), ip, "sign-in", null, "ok"));
        if (usedBackup)
            audit.Write(new AuditEntry(pending.Name, pending.Principal, pending.Role.AuditName(), ip, "backup-code-used", null, "ok"));
        return LocalRedirect(LoginModel.Landing(pending.Role, pending.ReturnUrl, Url));
    }

    private IActionResult BackToPassword()
    {
        TwoFactorPending.Delete(HttpContext);
        return LocalRedirect("/login");
    }
}
