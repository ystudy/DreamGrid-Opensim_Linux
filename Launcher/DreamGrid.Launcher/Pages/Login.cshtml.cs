using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

/// <summary>
/// Signs in with the avatar name and password. Only staff get in (<see cref="AdminRule.RoleOf"/>: the supervisor level or more
/// (100 unless changed), or the owner named in setup; on a Region Server only the admin level and the owner; on a Connected
/// Server only the owner), and every failure shows the same message so the page does not reveal which names exist. A
/// supervisor lands on their own page; everyone else on the return URL or the dashboard. The password is never logged.
/// </summary>
public sealed class LoginModel(IRobustClient robust, Func<LauncherSettings> settings, LoginThrottle throttle, AuditLog audit, ILogger<LoginModel> log) : PageModel
{
    private const string Generic = "Name or password is wrong, or this avatar is not an administrator.";

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var password = Password ?? "";
        // The throttle key is the normalised "First Last" (shared with the port-8001 API), so extra spaces cannot
        // give one account several lock states.
        var name = AvatarName.Parse(Name);
        if (throttle.IsLocked(name.Key, HttpContext.Connection.RemoteIpAddress))
        {
            Error = "Too many attempts. Try again in a few minutes.";
            return Page();
        }

        if (!name.IsComplete)
            return Fail(name);

        var role = StaffRole.None;
        RobustAccount? account;
        try
        {
            // The strict lookup: an unusable reply is the service failing, never a wrong password counted by the throttle.
            account = await robust.FindAccountAsync(name.First, name.Last, ct);
            if (account is not null && await robust.AuthenticateAsync(account.PrincipalId, password, ct))
                role = AdminRule.RoleOf(settings(), account);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
            or InvalidDataException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Sign-in: the account service did not answer: {Message}", e.Message);
            Error = "Your world's sign-in service isn't running yet. Start your world, then try again.";
            return Page();
        }

        if (role == StaffRole.None) return Fail(name);

        throttle.RecordSuccess(name.Key);
        await StaffSignIn.SignInAsync(HttpContext, account!, role);
        audit.Write(new AuditEntry($"{account!.FirstName} {account.LastName}", account.PrincipalId, role.AuditName(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), "sign-in", null, "ok"));
        if (role == StaffRole.Supervisor) return LocalRedirect(StaffSignIn.Home(role));
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : StaffSignIn.Home(role));
    }

    private IActionResult Fail(AvatarName name)
    {
        throttle.RecordFailure(name.Key, HttpContext.Connection.RemoteIpAddress);
        // Only the cleaned first name: the rest could be a password typed into the wrong field.
        log.LogInformation("Sign-in: a sign-in for first name {First} was refused", name.ForLog);
        audit.Write(new AuditEntry(name.ForLog, null, StaffRole.None.AuditName(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), "sign-in-refused", null, "refused"));
        Error = Generic;
        return Page();
    }
}
