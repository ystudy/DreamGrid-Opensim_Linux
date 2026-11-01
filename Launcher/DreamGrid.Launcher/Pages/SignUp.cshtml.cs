using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

/// <summary>
/// Where "Create an account" leads (spec 2026-10-31-sign-up-design §4). Open to anyone and, like the other guest pages, not there
/// on a Region Server or a Connected Server (<c>GuestSideOnly</c>). While sign-up is closed it only says so. The form goes to
/// <see cref="SignUps.SubmitAsync"/> after the owner's bot protection (<see cref="BotGuard"/>, form <c>signup</c>): without email
/// the new resident is signed in and sent to How to connect; with email the page says to check it and offers to send the link
/// again (<see cref="SignUps.ResendAsync"/>), which the guest sign-in's "Confirm your email first" posts to as well.
/// </summary>
public sealed class SignUpModel(Func<LauncherSettings> settings, SignUps signUps, BotGuard bots) : PageModel
{
    public const string BotFailed = "The form couldn't check that you're a person. Try again.";
    public const string Busy = "Sign-up isn't working right now. Try again in a few minutes.";

    [BindProperty] public string? First { get; set; }
    [BindProperty] public string? Last { get; set; }
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? Again { get; set; }
    [BindProperty] public bool AcceptTerms { get; set; }

    public bool Closed { get; private set; }
    public bool CheckEmail { get; private set; }
    public string? Error { get; private set; }
    public string GridName { get; private set; } = "";

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Load();
        if (Closed) return Page();
        if (!bots.Check(HttpContext, "signup").Passed)
        {
            Error = BotFailed;
            return Page();
        }
        var r = await signUps.SubmitAsync(new SignUpForm(First, Last, Email, Password, Again, AcceptTerms), HttpContext.Connection.RemoteIpAddress, ct);
        switch (r.Outcome)
        {
            case SignUpOutcome.Closed:
                Closed = true;
                break;
            case SignUpOutcome.SignedUp:
                await GuestSignIn.SignInAsync(HttpContext, r.Account!);
                return LocalRedirect("/guest/connect");
            case SignUpOutcome.CheckEmail:
                CheckEmail = true;
                break;
            case SignUpOutcome.Busy:
                Error = Busy;
                break;
            default:
                Error = r.Problem;
                break;
        }
        return Page();
    }

    /// <summary>"Send it again", from this page or from the guest sign-in: the same reply whatever the name.</summary>
    public async Task<IActionResult> OnPostResendAsync(CancellationToken ct)
    {
        Load();
        if (await signUps.ResendAsync(First, Last, HttpContext.Connection.RemoteIpAddress, ct) == SignUpOutcome.Busy)
            Error = Busy;
        else
            CheckEmail = true;
        Closed = false;
        return Page();
    }

    private void Load()
    {
        var s = settings();
        Closed = !SignUpRules.IsOpen(s);
        GridName = s.GridName;
    }
}
