using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>
/// The guest door: signs in with the avatar name and password. Any account at level 0 or more gets in (a staff member too, but
/// only to the guest pages: this door opens nothing on the staff side, so staff two-factor, which guards that side, is not asked).
/// Someone who set up two-factor as a resident (<see cref="ResidentTwoFactor"/>) goes on to <c>/guest/sign-in/code</c>, and so does
/// anyone while the residents' two-factor file can't be read (that page says to contact the owner).
/// Every failure (wrong name or password, a suspended account) shows the same message. It uses the staff door's
/// <see cref="LoginThrottle"/> with its own name count (<see cref="SignInDoor.Guest"/>): guesses here can't lock a name out of
/// the staff door, and a right password here can't clear the staff count of wrong two-factor codes; the address count is shared,
/// so an address gets 20 failures in all, whichever door. The password is never logged; only the cleaned first name is.
/// </summary>
public sealed class SignInModel(IRobustClient robust, LoginThrottle throttle, AuditLog audit, ResidentTwoFactor residents,
    SignUpBoard pending, Func<LauncherSettings> settings, ILogger<SignInModel> log) : PageModel
{
    private const string Generic = "Name or password is wrong.";
    public const string ConfirmFirst = "Confirm your email first. Or wait for staff to approve you.";
    public const string WaitForStaff = "Your account is waiting for approval by the grid's staff.";

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Password { get; set; }

    public string? Error { get; private set; }

    /// <summary>The guest pages exist only with a guest side, so the link always is.</summary>
    public bool ShowForgot => true;

    /// <summary>"Create an account" is shown only while sign-up is open.</summary>
    public bool ShowSignUp { get; private set; }

    /// <summary>The right password for a sign-up still waiting for its email link: the page offers to send it again.</summary>
    public RobustAccount? Pending { get; private set; }

    public void OnGet() => ShowSignUp = SignUpRules.IsOpen(settings());

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        ShowSignUp = SignUpRules.IsOpen(settings());
        var name = AvatarName.Parse(Name);
        var address = HttpContext.Connection.RemoteIpAddress;
        if (throttle.IsLocked(SignInDoor.Guest, name.Key, address))
        {
            Error = "Too many attempts. Try again in a few minutes.";
            return Page();
        }
        if (!name.IsComplete) return Fail(name);

        RobustAccount? account;
        var allowed = false;
        try
        {
            // The strict lookup: an unusable reply is the service failing, never a wrong password counted by the throttle.
            account = await robust.FindAccountAsync(name.First, name.Last, ct);
            var rightPassword = account is not null && await robust.AuthenticateAsync(account.PrincipalId, Password ?? "", ct);
            allowed = rightPassword && account!.UserLevel >= 0;
            // Only someone who knows the password learns that the account waits for its email or for staff (a suspended one doesn't).
            if (rightPassword && !allowed && pending.Contains(account!.PrincipalId))
            {
                if (pending.Get(account.PrincipalId)?.State == SignUpState.WaitingForStaff)
                {
                    Error = WaitForStaff;
                    return Page();
                }
                Pending = account;
                Error = ConfirmFirst;
                return Page();
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
            or InvalidDataException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Guest sign-in: the account service did not answer: {Message}", e.Message);
            Error = "The grid's sign-in service isn't running right now. Try again later.";
            return Page();
        }
        if (!allowed) return Fail(name);

        // The code next, for a resident with two-factor on; the name's failures are kept until the code is right.
        if (residents.Store.StateOf(account!.PrincipalId) != TwoFactorState.Off)
        {
            GuestTwoFactorPending.Issue(HttpContext, account);
            return LocalRedirect("/guest/sign-in/code");
        }

        throttle.RecordSuccess(SignInDoor.Guest, name.Key);
        await GuestSignIn.SignInAsync(HttpContext, account!);
        audit.Write(new AuditEntry($"{account!.FirstName} {account.LastName}", account.PrincipalId, AuthSchemes.GuestRole,
            address?.ToString(), "sign-in", null, "ok"));
        // The first sign-in after a staff approval says so, once, with the name to log in as.
        if (pending.Get(account.PrincipalId) is { State: SignUpState.Approved, Greeted: false })
        {
            pending.MarkGreeted(account.PrincipalId);
            return LocalRedirect("/guest/connect?approved=1");
        }
        return LocalRedirect("/guest");
    }

    private IActionResult Fail(AvatarName name)
    {
        throttle.RecordFailure(SignInDoor.Guest, name.Key, HttpContext.Connection.RemoteIpAddress);
        // Only the cleaned first name: the rest could be a password typed into the wrong field.
        log.LogInformation("Guest sign-in: a sign-in for first name {First} was refused", name.ForLog);
        audit.Write(new AuditEntry(name.ForLog, null, StaffRole.None.AuditName(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), "sign-in-refused", null, "refused"));
        Error = Generic;
        return Page();
    }
}
