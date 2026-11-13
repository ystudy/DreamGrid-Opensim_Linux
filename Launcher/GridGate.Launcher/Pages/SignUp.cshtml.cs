using GridGate.Launcher.Email;
using GridGate.Launcher.Core.Guests;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Guests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace GridGate.Launcher.Pages;

/// <summary>
/// Where "Create an account" leads (spec 2026-10-31-sign-up-design §4). Open to anyone and, like the other guest pages, not there
/// on a Region Server or a Connected Server (<c>GuestSideOnly</c>). While sign-up is closed it only says so. The form goes to
/// <see cref="SignUps.SubmitAsync"/> after the owner's bot protection (<see cref="BotGuard"/>, form <c>signup</c>): without email
/// the new resident is signed in and sent to How to connect; with email the page says to check it and offers to send the link
/// again (<see cref="SignUps.ResendAsync"/>), which the guest sign-in's "Confirm your email first" posts to as well.
/// </summary>
public sealed class SignUpModel(Func<LauncherSettings> settings, SignUps signUps, BotGuard bots,
    EmailLanguage emailLanguage, IStringLocalizer<ResidentText> t, Core.Starter.StarterAvatarList starters) : PageModel
{
    [BindProperty] public string? First { get; set; }
    [BindProperty] public string? Last { get; set; }
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? Again { get; set; }
    [BindProperty] public bool AcceptTerms { get; set; }
    /// <summary>The starter avatar picked on step 2 (spec 2026-11-11-starter-avatars-design §5.3).</summary>
    [BindProperty] public Guid? Avatar { get; set; }

    /// <summary>The starter avatars sign-up offers, in the owner's order; none means there is no step 2.</summary>
    public IReadOnlyList<Core.Starter.StarterAvatar> Starters { get; private set; } = [];
    /// <summary>The one checked on step 2: the posted pick when it is still offered, else the default.</summary>
    public Guid? Selected { get; private set; }

    public bool Closed { get; private set; }
    public bool CheckEmail { get; private set; }
    /// <summary>The sign-up waits for staff approval (no email, the owner's switch on).</summary>
    public bool WaitingForStaff { get; private set; }
    public string? Error { get; private set; }
    public string GridName { get; private set; } = "";

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Load();
        if (Closed) return Page();
        if (!bots.Check(HttpContext, "signup").Passed)
        {
            Error = t["Common.BotFailed"];
            return Page();
        }
        var r = await signUps.SubmitAsync(new SignUpForm(First, Last, Email, Password, Again, AcceptTerms, Avatar), HttpContext.Connection.RemoteIpAddress, ct,
            emailLanguage.ForRequest(HttpContext));
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
            case SignUpOutcome.WaitingForStaff:
                WaitingForStaff = true;
                break;
            case SignUpOutcome.Busy:
                Error = t["Common.SignUpBusy"];
                break;
            default:
                Error = r.Message!.In(t);
                break;
        }
        return Page();
    }

    /// <summary>"Send it again", from this page or from the guest sign-in: the same reply whatever the name.</summary>
    public async Task<IActionResult> OnPostResendAsync(CancellationToken ct)
    {
        Load();
        if (await signUps.ResendAsync(First, Last, HttpContext.Connection.RemoteIpAddress, ct, emailLanguage.ForRequest(HttpContext)) == SignUpOutcome.Busy)
            Error = t["Common.SignUpBusy"];
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
        Starters = starters.Ready();
        Selected = Starters.Any(a => a.Id == Avatar) ? Avatar : starters.Default()?.Id;
    }
}
