using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

/// <summary>
/// Signs in with the avatar name and password. Only an avatar of level 200 or more gets in, and every
/// failure shows the same message so the page does not reveal which names exist. The password is never logged.
/// </summary>
public sealed class LoginModel(IRobustClient robust, LoginThrottle throttle, ILogger<LoginModel> log) : PageModel
{
    public const int AdminLevel = 200;
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
        if (throttle.IsLocked(name.Key))
        {
            Error = "Too many attempts. Try again in a few minutes.";
            return Page();
        }

        if (!name.IsComplete)
            return Fail(name);

        bool ok;
        RobustAccount? account;
        try
        {
            account = await robust.GetAccountAsync(name.First, name.Last, ct);
            ok = account is not null && await robust.AuthenticateAsync(account.PrincipalId, password, ct)
                && account.UserLevel >= AdminLevel;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
            && !ct.IsCancellationRequested)
        {
            log.LogWarning("Sign-in: the account service did not answer: {Message}", e.Message);
            Error = "Your world's sign-in service isn't running yet. Start your world, then try again.";
            return Page();
        }

        if (!ok) return Fail(name);

        throttle.RecordSuccess(name.Key);
        await AdminSignIn.SignInAsync(HttpContext, account!.FirstName, account.LastName, account.PrincipalId);
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }

    private IActionResult Fail(AvatarName name)
    {
        throttle.RecordFailure(name.Key);
        // Only the cleaned first name: the rest could be a password typed into the wrong field.
        log.LogInformation("Sign-in: a sign-in for first name {First} was refused", name.ForLog);
        Error = Generic;
        return Page();
    }
}
