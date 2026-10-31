using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>
/// The guest door: signs in with the avatar name and password. Any account at level 0 or more gets in (a staff member too, but
/// only to the guest pages: this door opens nothing on the staff side, so two-factor, which guards that side, is not asked).
/// Every failure (wrong name or password, a suspended account) shows the same message. It uses the staff door's
/// <see cref="LoginThrottle"/> with its own name count (<see cref="SignInDoor.Guest"/>): guesses here can't lock a name out of
/// the staff door, and a right password here can't clear the staff count of wrong two-factor codes; the address count is shared,
/// so an address gets 20 failures in all, whichever door. The password is never logged; only the cleaned first name is.
/// </summary>
public sealed class SignInModel(IRobustClient robust, LoginThrottle throttle, AuditLog audit, ILogger<SignInModel> log) : PageModel
{
    private const string Generic = "Name or password is wrong.";

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Password { get; set; }

    public string? Error { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
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
            allowed = account is not null && await robust.AuthenticateAsync(account.PrincipalId, Password ?? "", ct) && account.UserLevel >= 0;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
            or InvalidDataException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Guest sign-in: the account service did not answer: {Message}", e.Message);
            Error = "The grid's sign-in service isn't running right now. Try again later.";
            return Page();
        }
        if (!allowed) return Fail(name);

        throttle.RecordSuccess(SignInDoor.Guest, name.Key);
        await GuestSignIn.SignInAsync(HttpContext, account!);
        audit.Write(new AuditEntry($"{account!.FirstName} {account.LastName}", account.PrincipalId, AuthSchemes.GuestRole,
            address?.ToString(), "sign-in", null, "ok"));
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
