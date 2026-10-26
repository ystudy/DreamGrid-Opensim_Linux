using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;
using DreamGrid.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Setup;

/// <summary>
/// Step 2: creates the owner avatar, spends the setup token and signs the person in, so step 3 onward runs signed in.
/// Skipped once the owner exists. The password is never logged or shown.
/// </summary>
public sealed partial class AvatarModel(
    IGridController grid,
    Func<LauncherSettings> settings,
    SetupRunner runner,
    IHostApplicationLifetime lifetime,
    ILogger<AvatarModel> log) : PageModel
{
    [BindProperty] public string? FirstName { get; set; }
    [BindProperty] public string? LastName { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? Confirm { get; set; }
    [BindProperty] public string? Email { get; set; }

    public string? Error { get; private set; }
    public string? Details { get; private set; }
    /// <summary>True when another submit is already creating the avatar.</summary>
    public bool Creating { get; private set; }

    public IActionResult OnGet()
    {
        if (Closed() is { } closed) return closed;
        Creating = runner.CreatingOwner;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Closed() is { } closed) return closed;
        Validate();
        if (!ModelState.IsValid) return Page();

        var (first, last, password) = (FirstName!, LastName!, Password!);
        var email = string.IsNullOrWhiteSpace(Email) ? null : Email.Trim();
        RobustAccount? account;
        try
        {
            // Not the request's token: an abandoned page must not cut account creation off halfway.
            account = await runner.TryCreateOwnerAsync(first, last, password, email, lifetime.ApplicationStopping);
        }
        // Only the launcher stopping is a cancellation; an HttpClient timeout (TaskCanceledException) is a failure.
        catch (Exception e) when (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            // Console transcripts can end up in the message: keep the password out of the log and the page.
            var message = e.Message.Replace(password, "********", StringComparison.Ordinal);
            log.LogError("Setup: the owner avatar {First} {Last} could not be created: {Message}", first, last, message);
            Error = runner.PortConflictText(e)
                ?? "Your avatar could not be created. The grid may still be starting: wait a minute, then try again.";
            Details = message;
            return Page();
        }
        if (account is null)
        {
            // A second submit while the first is still creating the avatar: that one finishes the step.
            Creating = true;
            return Page();
        }

        grid.UpdateSettings(s => s with { SetupToken = null }, writeConfig: false);
        Response.Cookies.Delete(SetupTokenCookie.Name, new CookieOptions { Path = "/" });
        await AdminSignIn.SignInAsync(HttpContext, account.FirstName, account.LastName, account.PrincipalId);
        return Redirect("/setup");
    }

    private IActionResult? Closed()
    {
        var s = settings().Setup;
        return s.OwnerCreated || !SetupSteps.CanOpen(s, 2) ? Redirect("/setup") : null;
    }

    private void Validate()
    {
        foreach (var (field, value) in new[] { (nameof(FirstName), FirstName), (nameof(LastName), LastName) })
            if (value is null || !NamePartPattern().IsMatch(value))
                ModelState.AddModelError(field, "Use 2 to 31 letters or digits, with no spaces.");

        if (Password is null || Password.Length < 8)
            ModelState.AddModelError(nameof(Password), "Use at least 8 characters.");
        else if (Password.Any(char.IsWhiteSpace))
            ModelState.AddModelError(nameof(Password), "The password must not contain spaces.");
        else if (Confirm != Password)
            ModelState.AddModelError(nameof(Confirm), "The two passwords are not the same.");

        if (!string.IsNullOrWhiteSpace(Email) && !new EmailAddressAttribute().IsValid(Email.Trim()))
            ModelState.AddModelError(nameof(Email), "This does not look like an e-mail address.");

        if (!ModelState.IsValid) return;
        // The grid's own rules for console arguments (e.g. no double quotes) catch anything left.
        try
        {
            GridConsoleCommands.ValidateOwner(FirstName!, LastName!, Password!, string.IsNullOrWhiteSpace(Email) ? null : Email.Trim());
        }
        catch (ArgumentException e)
        {
            ModelState.AddModelError(e.ParamName == "email" ? nameof(Email) : nameof(Password),
                e.ParamName == "email" ? "This e-mail address cannot be used." : "The password must not contain double quotes or start with \"--\".");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9]{2,31}$")]
    private static partial Regex NamePartPattern();
}
