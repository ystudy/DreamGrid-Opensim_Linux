using System.Globalization;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.People;

/// <summary>
/// The People page (StaffPolicy by the /People folder convention; <c>/people</c>; 404 where there are no supervisors). A supervisor, an
/// admin or the owner finds an avatar by "First Last" and sees the name, level, role, e-mail and when the account was made, and can
/// suspend a person or lift a suspension when <see cref="StaffChanges"/> allows it for their own role: the owner for anyone but
/// the owner, an admin for supervisors and guests, a supervisor for guests. A level below 0 is a suspension. The rules are
/// checked again on every post (<see cref="StaffActions"/>), so a post for someone the buttons were not drawn for is refused and
/// audited. The spec's "last login" needs Robust's GridUser service, which the launcher doesn't call yet (the 2.5 user admin
/// pages bring it); region rights come with plan 7b.
/// </summary>
public sealed class IndexModel(IRobustClient robust, StaffActions actions, ILogger<IndexModel> log) : PageModel
{
    /// <summary>The name to look up, as typed (a GET, so a person can be linked to).</summary>
    [BindProperty(SupportsGet = true)] public string? Name { get; set; }

    /// <summary>The person found, with what the signed-in person may do to them.</summary>
    public Person? Found { get; private set; }

    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    public sealed record Person(RobustAccount Account, StaffRole Role, bool Suspended, bool CanSuspend, bool CanUnsuspend)
    {
        public string FullName => $"{Account.FirstName} {Account.LastName}";

        /// <summary>The role word shown: what the level gives, a guest when none.</summary>
        public string RoleName => Role switch
        {
            StaffRole.Owner => "Owner",
            StaffRole.Admin => "Admin",
            StaffRole.Supervisor => "Supervisor",
            _ => "Guest",
        };

        public string CreatedText => Account.Created is { } c ? c.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) : "Unknown";
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(Name)) await LookUpAsync(ct);
    }

    public Task<IActionResult> OnPostSuspendAsync(CancellationToken ct) => ChangeAsync(StaffChange.Suspend, ct);

    public Task<IActionResult> OnPostUnsuspendAsync(CancellationToken ct) => ChangeAsync(StaffChange.Unsuspend, ct);

    private async Task<IActionResult> ChangeAsync(StaffChange change, CancellationToken ct)
    {
        var account = await FindAsync(ct);
        if (account is not null)
        {
            var outcome = await actions.ApplyAsync(User, HttpContext.Connection.RemoteIpAddress?.ToString(), account, change, ct);
            if (outcome.Done) Notice = outcome.Message;
            else Error = outcome.Message;
            // Show the person as they are now.
            account = await FindAsync(ct) ?? account;
            Show(account);
        }
        return Page();
    }

    private async Task LookUpAsync(CancellationToken ct)
    {
        if (await FindAsync(ct) is { } account) Show(account);
    }

    /// <summary>The account called <see cref="Name"/>, or null with <see cref="Error"/> saying why not.</summary>
    private async Task<RobustAccount?> FindAsync(CancellationToken ct)
    {
        if (AvatarName.Parse(Name) is not { IsComplete: true } who)
        {
            Error ??= "Type the avatar's first and last name.";
            return null;
        }
        try
        {
            // The strict lookup: an unusable reply is the service failing, never "no such avatar".
            var account = await robust.FindAccountAsync(who.First, who.Last, ct);
            if (account is null) Error ??= $"No avatar called {LogText.Clean(who.Key, 64)}.";
            return account;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
            or InvalidDataException && !ct.IsCancellationRequested)
        {
            log.LogWarning("People: the account service did not answer: {Message}", LogText.Clean(e.Message, 300));
            Error ??= StaffActions.RobustDown;
            return null;
        }
    }

    private void Show(RobustAccount account)
    {
        var suspended = account.UserLevel < 0;
        try
        {
            var view = actions.RoleOf(account);
            Found = new Person(account, view.Current, suspended,
                CanSuspend: !suspended && actions.Refusal(User, account, StaffChange.Suspend) is null,
                CanUnsuspend: suspended && actions.Refusal(User, account, StaffChange.Unsuspend) is null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Who may unsuspend depends on the level they get back, which can't be read: shown, but no buttons.
            log.LogWarning("People: the suspended list could not be read ({Error}).", e.GetType().Name);
            Found = new Person(account, StaffRole.None, suspended, CanSuspend: false, CanUnsuspend: false);
            Error ??= "The list of suspended people couldn't be read, so this person can't be changed here now.";
        }
        Name = Found.FullName;
    }
}
