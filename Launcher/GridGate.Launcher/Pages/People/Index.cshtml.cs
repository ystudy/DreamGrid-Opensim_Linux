using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Guests;
using System.Globalization;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Guests;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.People;

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
    SignUpBoard pending, PeopleActions people, IPeopleDirectory directory, TimeProvider time, ILogger<IndexModel> log) : PageModel
{
    /// <summary>The list's search text (part of a name), filter and page (from 1; read as <c>page</c>, a word Razor Pages keeps for itself).</summary>
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    [BindProperty(SupportsGet = true)] public PeopleFilter Filter { get; set; }

    public int PageNumber { get; private set; } = 1;

    /// <summary>The list's rows with what the signed-in person may do to each, or null when <see cref="Name"/> asked for one person.</summary>
    public IReadOnlyList<PersonView>? Rows { get; private set; }

    public int Total { get; private set; }

    public TaskIcons IconsFor(PersonView p) => TaskIcons.For(people, User, p, "/people?handler=Task", Back);

    /// <summary>The list's search, filter and page, sent back with a task so the answer shows the same list.</summary>
    public IReadOnlyDictionary<string, string> Back => new Dictionary<string, string>
    {
        ["q"] = Q ?? "", ["filter"] = Filter.ToString(), ["page"] = PageNumber.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>"1–25 of 132", or "No one found".</summary>
    public string CountText => Total == 0 ? "No one found"
        : $"{(PageNumber - 1) * PeopleDirectory.PageSize + 1}–{Math.Min(Total, PageNumber * PeopleDirectory.PageSize)} of {Total}";

    public bool HasNext => PageNumber * PeopleDirectory.PageSize < Total;

    public string PageLink(int page) =>
        $"/people?q={Uri.EscapeDataString(Q ?? "")}&filter={Filter}&page={page.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>"Online now", "Today", "Yesterday", the date, or "Never".</summary>
    public string LastSeen(PersonRow row)
    {
        if (row.OnlineRegion is not null) return "Online now";
        var seen = new[] { row.LastLogin, row.LastLogout }.Max();
        if (seen is not { } at) return "Never";
        var today = time.GetUtcNow().UtcDateTime.Date;
        var day = at.UtcDateTime.Date;
        return day == today ? "Today" : day == today.AddDays(-1) ? "Yesterday" : day.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
    }

    public static string RoleWord(PersonView p) => p.Role.Current switch
    {
        StaffRole.Owner => "Owner",
        StaffRole.Admin => "Admin",
        StaffRole.Supervisor => "Supervisor",
        _ => "Resident",
    };

    /// <summary>The name to look up, as typed (a GET, so a person can be linked to).</summary>
    [BindProperty(SupportsGet = true)] public string? Name { get; set; }

    /// <summary>The person found, with what the signed-in person may do to them.</summary>
    public Person? Found { get; private set; }

    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    /// <summary><c>Pending</c>: a sign-up waiting for its email link or for staff (level −1 like a suspension, but not one); <c>WaitingForStaff</c> says which.</summary>
    public sealed record Person(RobustAccount Account, StaffRole Role, bool Suspended, bool CanSuspend, bool CanUnsuspend,
        IReadOnlyList<string> Regions, bool Pending = false, bool WaitingForStaff = false)
    {
        public string FullName => Account.FullName();

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
        else await ListAsync(Request.Query["page"], ct);
    }

    /// <summary>An icon's task (reset link, sign out, suspend, unsuspend) for one person, then the same list again.</summary>
    public async Task<IActionResult> OnPostTaskAsync(Guid id, PersonTask task, CancellationToken ct)
    {
        var outcome = await people.RunAsync(User, HttpContext.Connection.RemoteIpAddress?.ToString(), id, task, ct);
        if (outcome.Done) Notice = outcome.Message;
        else Error = outcome.Message;
        await ListAsync(Request.Form["page"], ct);
        return Page();
    }

    private async Task ListAsync(string? page, CancellationToken ct)
    {
        PageNumber = int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 and < 100000 ? n : 1;
        try
        {
            var found = await directory.SearchAsync(Q, Filter, pending.Listed(), settings().Security.SupervisorLevel, PageNumber, ct);
            Rows = people.Views(User, found);
            Total = found.Total;
        }
        catch (PeopleDirectoryUnavailableException e)
        {
            log.LogWarning("People: the list could not be read: {Message}", e.InnerException?.Message ?? e.Message);
            Rows = [];
            Error ??= PeopleActions.DatabaseDown;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("People: the suspended or sign-up list could not be read ({Error}).", e.GetType().Name);
            Rows = [];
            Error ??= "The list of suspended people couldn't be read. Try again.";
        }
    }

    public Task<IActionResult> OnPostSuspendAsync(CancellationToken ct) => ChangeAsync(StaffChange.Suspend, ct);

    public Task<IActionResult> OnPostUnsuspendAsync(CancellationToken ct) => ChangeAsync(StaffChange.Unsuspend, ct);

    private async Task<IActionResult> ChangeAsync(StaffChange change, CancellationToken ct)
    {
        var account = await FindAsync(ct);
        if (account is not null && IsPending(account))
        {
            // A sign-up is let in by its email link or on the Sign-ups page: "unsuspend" would skip both.
            Error = "This person is a new sign-up. Approve or deny them on the Sign-ups page.";
            Show(account);
        }
        else if (account is not null)
        {
            // Through PeopleActions, so a suspension also logs the avatar out in-world.
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var outcome = change == StaffChange.Suspend
                ? await people.SuspendAsync(User, ip, account.PrincipalId, ct)
                : await people.UnsuspendAsync(User, ip, account.PrincipalId, ct);
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
        catch (Exception e) when (RobustFailure.IsUnreachable(e, ct))
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

    private SignUpState? StateOf(RobustAccount account)
    {
        try { return pending.Get(account.PrincipalId)?.State; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
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
        var forStaff = waiting && StateOf(account) == SignUpState.WaitingForStaff;
        try
        {
            var view = actions.RoleOf(account);
            Found = new Person(account, view.Current, suspended,
                CanSuspend: !suspended && !waiting && actions.Refusal(User, account, StaffChange.Suspend) is null,
                CanUnsuspend: suspended && actions.Refusal(User, account, StaffChange.Unsuspend) is null,
                RegionsOf(account), waiting, forStaff);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Who may unsuspend depends on the level they get back, which can't be read: shown, but no buttons.
            log.LogWarning("People: the suspended list could not be read ({Error}).", e.GetType().Name);
            Found = new Person(account, StaffRole.None, suspended, CanSuspend: false, CanUnsuspend: false, RegionsOf(account), waiting, forStaff);
            Error ??= "The list of suspended people couldn't be read, so this person can't be changed here now.";
        }
        Name = Found.FullName;
    }
}
