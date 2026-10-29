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
/// Skipped once the owner exists. The password is never logged or shown. On a Region Server the avatar already exists
/// on the Grid Asset Server: the owner types its name only, and it becomes the owner of this computer's regions.
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

    /// <summary>True on a Region Server: the avatar is named, not created, and no password is asked for.</summary>
    public bool ByName { get; private set; }

    /// <summary>The grid the avatar lives on: the joined grid's name on a Region Server.</summary>
    public string GridName { get; private set; } = "";

    public IActionResult OnGet()
    {
        if (Closed() is { } closed) return closed;
        Load();
        Creating = runner.CreatingOwner;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Closed() is { } closed) return closed;
        Load();
        if (ByName) return await PostByNameAsync();
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

        return await SignInAsync(account);
    }

    /// <summary>
    /// A Region Server's post: the name of an avatar on the Grid Asset Server. A missing avatar or a Grid Asset Server that
    /// doesn't answer is said in the controller's own words.
    /// </summary>
    private async Task<IActionResult> PostByNameAsync()
    {
        var first = FirstName?.Trim() ?? "";
        var last = LastName?.Trim() ?? "";
        if (first.Length == 0) ModelState.AddModelError(nameof(FirstName), "Type your avatar's first name.");
        if (last.Length == 0) ModelState.AddModelError(nameof(LastName), "Type your avatar's last name.");
        if (!ModelState.IsValid) return Page();

        RobustAccount? account;
        try
        {
            account = await runner.TrySetEstateOwnerAsync(first, last, lifetime.ApplicationStopping);
        }
        catch (Exception e) when (e is ArgumentException or GridAssetUnreachableException)
        {
            log.LogWarning("Setup: {First} {Last} could not be made the owner: {Message}", first, last, e.Message);
            Error = e.Message;
            return Page();
        }
        // Only the launcher stopping is a cancellation; an HttpClient timeout (TaskCanceledException) is a failure.
        catch (Exception e) when (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            log.LogError("Setup: {First} {Last} could not be made the owner: {Message}", first, last, e.Message);
            Error = runner.PortConflictText(e)
                ?? "Your avatar could not be looked up. The grid may still be starting: wait a minute, then try again.";
            Details = e.Message;
            return Page();
        }
        if (account is null)
        {
            // A second submit while the first is still looking the avatar up: that one finishes the step.
            Creating = true;
            return Page();
        }
        return await SignInAsync(account);
    }

    /// <summary>Spends the setup token and signs the owner in, so the rest of setup runs signed in.</summary>
    private async Task<IActionResult> SignInAsync(RobustAccount account)
    {
        grid.UpdateSettings(s => s with { SetupToken = null }, writeConfig: false);
        Response.Cookies.Delete(SetupTokenCookie.Name, new CookieOptions { Path = "/" });
        await AdminSignIn.SignInAsync(HttpContext, account.FirstName, account.LastName, account.PrincipalId);
        return Redirect("/setup");
    }

    private void Load()
    {
        var s = settings();
        ByName = s.Plan == OwnerPlan.RegionServer;
        GridName = (ByName ? s.Join?.GridName : null) ?? s.GridName;
    }

    private IActionResult? Closed()
    {
        var s = settings();
        return s.Setup.OwnerCreated || !SetupSteps.CanOpen(s, "/setup/avatar") ? Redirect("/setup") : null;
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
