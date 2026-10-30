using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Staff;

/// <summary>One line of the staff table: the person, the role their level gives and the level itself.</summary>
public sealed record StaffRow(string Name, StaffRole Role, int Level);

/// <summary>
/// The owner's Staff page (OwnerPolicy by the /Staff folder convention; <c>/staff</c>; 404 where there are no supervisors). It lists
/// everyone at or above the supervisor level with their role, and the owner can make a person a supervisor, an admin or a guest,
/// or add someone by avatar name (a strict lookup first: "No avatar called ..." when there is none). The owner's own line has
/// no buttons. Every change goes through <see cref="StaffActions"/>, which checks the rules again, ends the sessions of someone
/// who loses rights, and audits it. The list comes from <see cref="IStaffDirectory"/>; when the database isn't running the page
/// says to start the world.
/// </summary>
public sealed class IndexModel(IStaffDirectory directory, IRobustClient robust, StaffActions actions, Func<LauncherSettings> settings,
    ILogger<IndexModel> log) : PageModel
{
    public IReadOnlyList<StaffRow> Rows { get; private set; } = [];

    /// <summary>The database can't be asked, so there is no list.</summary>
    public bool Unavailable { get; private set; }

    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    /// <summary>What was typed in the Add box, kept when the change was refused.</summary>
    public string? Typed { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostChangeAsync(string? name, string? change, CancellationToken ct)
    {
        Typed = name;
        if (!Enum.TryParse<StaffChange>(change, out var wanted) || wanted is not (StaffChange.MakeSupervisor or StaffChange.MakeAdmin or StaffChange.MakeGuest))
            Error = "Choose what to make this person.";
        else if (AvatarName.Parse(name) is not { IsComplete: true } who)
            Error = "Type the avatar's first and last name.";
        else
        {
            RobustAccount? account = null;
            try
            {
                // The strict lookup: an unusable reply is the service failing, never "no such avatar".
                account = await robust.FindAccountAsync(who.First, who.Last, ct);
                if (account is null)
                    Error = $"No avatar called {LogText.Clean(who.Key, 64)}.";
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
                or InvalidDataException && !ct.IsCancellationRequested)
            {
                log.LogWarning("Staff: the account service did not answer: {Message}", LogText.Clean(e.Message, 300));
                Error = StaffActions.RobustDown;
            }

            if (account is not null)
            {
                var outcome = await actions.ApplyAsync(User, HttpContext.Connection.RemoteIpAddress?.ToString(), account, wanted, ct);
                if (outcome.Done) { Notice = outcome.Message; Typed = null; }
                else Error = outcome.Message;
            }
        }

        await LoadAsync(ct);
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var s = settings();
        try
        {
            var accounts = await directory.ListAtOrAboveAsync(s.Security.SupervisorLevel, ct);
            // Everyone at the supervisor level or more has a role (the owner named in setup shows as the owner).
            Rows = [.. accounts.Select(a => new StaffRow($"{a.FirstName} {a.LastName}", AdminRule.RoleOf(s, a), a.UserLevel))
                .Where(r => r.Role != StaffRole.None)];
            Unavailable = false;
        }
        catch (StaffDirectoryUnavailableException e)
        {
            log.LogDebug("Staff: the staff list could not be read: {Message}", e.Message);
            Rows = [];
            Unavailable = true;
        }
    }
}
