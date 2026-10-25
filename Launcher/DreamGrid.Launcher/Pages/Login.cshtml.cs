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
        var name = (Name ?? "").Trim();
        var password = Password ?? "";
        // First name up to the first space; the rest is the last name. The throttle key is the normalised
        // "First Last", so extra spaces cannot give one account several lock states.
        var space = name.IndexOf(' ');
        var first = space < 0 ? "" : name[..space];
        var last = space < 0 ? "" : name[(space + 1)..].Trim();
        var key = first.Length == 0 || last.Length == 0 ? name : $"{first} {last}";
        if (throttle.IsLocked(key))
        {
            Error = "Too many attempts. Try again in a few minutes.";
            return Page();
        }

        if (first.Length == 0 || last.Length == 0)
            return Fail(key);

        bool ok;
        RobustAccount? account;
        try
        {
            account = await robust.GetAccountAsync(first, last, ct);
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

        if (!ok) return Fail(key);

        throttle.RecordSuccess(key);
        await AdminSignIn.SignInAsync(HttpContext, account!.FirstName, account.LastName, account.PrincipalId);
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }

    private IActionResult Fail(string name)
    {
        throttle.RecordFailure(name);
        log.LogInformation("Sign-in: a sign-in for {Name} was refused", name);
        Error = Generic;
        return Page();
    }
}
