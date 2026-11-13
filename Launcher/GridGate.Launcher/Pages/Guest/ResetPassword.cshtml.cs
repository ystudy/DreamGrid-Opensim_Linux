using GridGate.Launcher.Email;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace GridGate.Launcher.Pages.Guest;

/// <summary>
/// Where the reset email's link leads (anonymous, guest side, open from outside Admin networks): one form with the token, the
/// two-factor code when the owner's rule asks for it and the person has it on, and the new password twice
/// (<see cref="PasswordResets.ResetAsync"/>). A used, expired or unknown link, or one whose rule is now off, says so and offers a
/// new one. The page only changes a password; it never signs anyone in. Typed passwords are never put back into the page.
/// </summary>
public sealed class ResetPasswordModel(PasswordResets resets, EmailLanguage emailLanguage, IStringLocalizer<ResidentText> t) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Token { get; set; }
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? Again { get; set; }

    public bool Expired { get; private set; }
    public bool NeedsCode { get; private set; }
    public string? Name { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Set once the password is changed: where to sign in now.</summary>
    public string? SignInAt { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await resets.OpenAsync(Token, ct) is not { } link)
        {
            Expired = true;
            return Page();
        }
        Show(link);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var (result, link, problem) = await resets.ResetAsync(Token, Code, Password, Again, HttpContext.Connection.RemoteIpAddress, ct, emailLanguage.ForRequest(HttpContext));
        if (link is not null) Show(link);
        switch (result)
        {
            case ResetResult.Done:
                SignInAt = link!.Role == StaffRole.None ? "/guest/sign-in" : "/login";
                break;
            case ResetResult.Expired:
                Expired = true;
                break;
            case ResetResult.WrongCode:
                Error = t["ResetPassword.WrongCode"];
                break;
            case ResetResult.Locked:
                Error = t["Common.TooManyAttempts"];
                break;
            case ResetResult.CodeUnknown:
                Error = t["SignIn.CodeUnknown"];
                break;
            case ResetResult.BadPassword:
                Error = PasswordFaultText.For(t, problem!.Value);
                break;
            case ResetResult.WriteFailed:
                Error = t["Common.PasswordWriteFailed"];
                break;
        }
        return Page();
    }

    private void Show(ResetLink link)
    {
        NeedsCode = link.NeedsCode;
        Name = link.Account.FullName();
    }
}
