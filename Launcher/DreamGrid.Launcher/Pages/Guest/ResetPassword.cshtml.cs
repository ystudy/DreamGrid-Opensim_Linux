using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>
/// Where the reset email's link leads (anonymous, guest side, open from outside Admin networks): one form with the token, the
/// two-factor code when the owner's rule asks for it and the person has it on, and the new password twice
/// (<see cref="PasswordResets.ResetAsync"/>). A used, expired or unknown link, or one whose rule is now off, says so and offers a
/// new one. The page only changes a password; it never signs anyone in. Typed passwords are never put back into the page.
/// </summary>
public sealed class ResetPasswordModel(PasswordResets resets) : PageModel
{
    public const string ExpiredText = "This link has expired or was already used. Ask for a new one.";

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
        var (result, link, problem) = await resets.ResetAsync(Token, Code, Password, Again, HttpContext.Connection.RemoteIpAddress, ct);
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
                Error = "That code isn't right. Try again, or use a backup code.";
                break;
            case ResetResult.Locked:
                Error = "Too many attempts. Try again in a few minutes.";
                break;
            case ResetResult.CodeUnknown:
                Error = "Two-factor sign-in can't be checked right now. Contact the grid owner.";
                break;
            case ResetResult.BadPassword:
                Error = problem;
                break;
            case ResetResult.WriteFailed:
                Error = "Couldn't change the password right now. Try again in a few minutes.";
                break;
        }
        return Page();
    }

    private void Show(ResetLink link)
    {
        NeedsCode = link.NeedsCode;
        Name = $"{link.Account.FirstName} {link.Account.LastName}";
    }
}
