using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>
/// The guest door's second step for a resident who set up two-factor (<see cref="ResidentTwoFactor"/>): the password step left a
/// pending cookie (<see cref="GuestTwoFactorPending"/>, five minutes); a right authenticator code or an unused backup code signs
/// them in (audited <c>sign-in</c>, plus <c>backup-code-used</c>). A wrong code counts in the throttle for the name at the guest
/// door and the address; while locked, no code is checked. Without a pending cookie, or once two-factor was turned off for them
/// meanwhile, this sends back to the password. Codes are never logged.
/// </summary>
public sealed class SignInCodeModel(ResidentTwoFactor residents, LoginThrottle throttle, AuditLog audit, ILogger<SignInCodeModel> log) : PageModel
{
    public const string WrongCode = "That code isn't right. Try again.";

    [BindProperty] public string? Code { get; set; }

    public string? Error { get; private set; }

    /// <summary>The two-factor file can't be read: the page says to contact the grid owner.</summary>
    public bool TwoFactorUnknown { get; private set; }

    public IActionResult OnGet() => GuestTwoFactorPending.Read(HttpContext) is null ? BackToPassword() : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (GuestTwoFactorPending.Read(HttpContext) is not { } pending) return BackToPassword();
        var key = AvatarName.Parse(pending.Name).Key;
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (throttle.IsLocked(SignInDoor.Guest, key, remote))
        {
            Error = "Too many attempts. Try again in a few minutes.";
            return Page();
        }

        var store = residents.Store;
        switch (store.StateOf(pending.Principal))
        {
            case TwoFactorState.Off:
                return BackToPassword();
            case TwoFactorState.Unknown:
                TwoFactorUnknown = true;
                return Page();
        }

        bool right, usedBackup;
        try
        {
            right = store.VerifyCode(pending.Principal, Code ?? "", out usedBackup);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Guest sign-in: a two-factor code for first name {First} couldn't be marked used: {Message}",
                LogText.Clean(pending.First, 32), e.Message);
            Error = TwoFactorStore.SaveFailed;
            return Page();
        }

        var ip = remote?.ToString();
        if (!right)
        {
            throttle.RecordFailure(SignInDoor.Guest, key, remote);
            log.LogInformation("Guest sign-in: a two-factor code for first name {First} was refused", LogText.Clean(pending.First, 32));
            audit.Write(new AuditEntry(pending.Name, pending.Principal, AuthSchemes.GuestRole, ip, "sign-in-refused", "two-factor code", "refused"));
            Error = WrongCode;
            return Page();
        }

        throttle.RecordSuccess(SignInDoor.Guest, key);
        GuestTwoFactorPending.Delete(HttpContext);
        await GuestSignIn.SignInAsync(HttpContext, pending.Account);
        audit.Write(new AuditEntry(pending.Name, pending.Principal, AuthSchemes.GuestRole, ip, "sign-in", null, "ok"));
        if (usedBackup)
            audit.Write(new AuditEntry(pending.Name, pending.Principal, AuthSchemes.GuestRole, ip, "backup-code-used", null, "ok"));
        return LocalRedirect("/guest");
    }

    private IActionResult BackToPassword()
    {
        GuestTwoFactorPending.Delete(HttpContext);
        return LocalRedirect("/guest/sign-in");
    }
}
