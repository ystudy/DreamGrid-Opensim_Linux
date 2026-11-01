using DreamGrid.Launcher.Core.Guests;
using System.Globalization;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
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
/// pages bring it). The regions the person has rights over are listed under the facts.
/// </summary>
public sealed class IndexModel(IRobustClient robust, StaffActions actions, RegionRights rights, Func<LauncherSettings> settings,
    SignUpBoard pending, ILogger<IndexModel> log) : PageModel
{
    /// <summary>The name to look up, as typed (a GET, so a person can be linked to).</summary>
    [BindProperty(SupportsGet = true)] public string? Name { get; set; }

    /// <summary>The person found, with what the signed-in person may do to them.</summary>
    public Person? Found { get; private set; }

    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    /// <summary><c>Pending</c>: a sign-up waiting for its email link (level −1 like a suspension, but not one).</summary>
    public sealed record Person(RobustAccount Account, StaffRole Role, bool Suspended, bool CanSuspend, bool CanUnsuspend,
        IReadOnlyList<string> Regions, bool Pending = false)
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

        /// <summary>"Region rights: " and the region names, or "none".</summary>
        public string RegionsText => "Region rights: " + (Regions.Count == 0 ? "none" : string.Join(", ", Regions));

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
        if (account is not null && IsPending(account))
        {
            // Confirming a sign-up always takes its email link: "unsuspend" would skip it.
            Error = "This person hasn't confirmed their email yet. Their account opens when they click the link.";
            Show(account);
        }
        else if (account is not null)
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

    /// <summary>The names of the regions the person has rights over (regions in Recently deleted don't count).</summary>
    private IReadOnlyList<string> RegionsOf(RobustAccount account)
    {
        var owned = rights.OwnedBy(account.PrincipalId).Select(r => r.Region).ToHashSet();
        return settings().Regions.Where(r => r.DeletedAt is null && owned.Contains(r.Uuid)).Select(r => r.Name)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool IsPending(RobustAccount account)
    {
        try { return account.UserLevel < 0 && pending.Contains(account.PrincipalId); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("People: the pending sign-ups list could not be read ({Error}).", e.GetType().Name);
            // Unknown: treated as pending, so no button can skip someone's email link.
            return account.UserLevel < 0;
        }
    }

    private void Show(RobustAccount account)
    {
        var waiting = IsPending(account);
        var suspended = account.UserLevel < 0 && !waiting;
        try
        {
            var view = actions.RoleOf(account);
            Found = new Person(account, view.Current, suspended,
                CanSuspend: !suspended && !waiting && actions.Refusal(User, account, StaffChange.Suspend) is null,
                CanUnsuspend: suspended && actions.Refusal(User, account, StaffChange.Unsuspend) is null,
                RegionsOf(account), waiting);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Who may unsuspend depends on the level they get back, which can't be read: shown, but no buttons.
            log.LogWarning("People: the suspended list could not be read ({Error}).", e.GetType().Name);
            Found = new Person(account, StaffRole.None, suspended, CanSuspend: false, CanUnsuspend: false, RegionsOf(account), waiting);
            Error ??= "The list of suspended people couldn't be read, so this person can't be changed here now.";
        }
        Name = Found.FullName;
    }
}
