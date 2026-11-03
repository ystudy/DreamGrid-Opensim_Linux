using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Groups;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Groups;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Groups;

/// <summary>
/// One group (<c>/groups/group?id=…</c>): its name and charter, members, adding one by avatar name (the strict lookup the Staff
/// page uses), removing one (a group's last owner only after a second press), deleting it (a ticked box), and the default group
/// with "Add everyone now". Every change goes through <see cref="GroupsAdmin"/>, which checks the rules and audits it.
/// </summary>
public sealed class GroupModel(IGridGroups groups, GroupJoins joins, GroupsAdmin admin, IRobustClient robust, ILogger<GroupModel> log) : PageModel
{
    public GroupDetail? Group { get; private set; }
    public bool IsDefault { get; private set; }
    public bool Unavailable { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    /// <summary>What was typed, kept when a change was refused.</summary>
    public string? TypedName { get; private set; }
    public string? TypedCharter { get; private set; }
    public string? TypedAvatar { get; private set; }

    /// <summary>The member whose removal needs a second press (the group's last owner).</summary>
    public string? ConfirmRemove { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => await ShowAsync(id, ct);

    public async Task<IActionResult> OnPostSaveAsync(Guid id, string? name, string? charter, CancellationToken ct)
    {
        var outcome = await admin.UpdateAsync(User, Ip, id, name, charter, ct);
        if (!outcome.Done) (TypedName, TypedCharter) = (name, charter);
        return await ShowAsync(id, ct, outcome);
    }

    public async Task<IActionResult> OnPostAddAsync(Guid id, string? avatar, CancellationToken ct)
    {
        GroupOutcome outcome;
        if (AvatarName.Parse(avatar) is not { IsComplete: true } who)
            outcome = new GroupOutcome(false, "Type the avatar's first and last name.");
        else
        {
            RobustAccount? account = null;
            outcome = new GroupOutcome(false, $"No avatar called {LogText.Clean(who.Key, 64)}.");
            try
            {
                // The strict lookup: an unusable reply is the service failing, never "no such avatar".
                account = await robust.FindAccountAsync(who.First, who.Last, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
                or InvalidDataException && !ct.IsCancellationRequested)
            {
                log.LogWarning("Groups: the account service did not answer: {Message}", LogText.Clean(e.Message, 300));
                outcome = new GroupOutcome(false, StaffActions.RobustDown);
            }
            if (account is not null) outcome = await admin.AddMemberAsync(User, Ip, id, account, ct);
        }
        if (!outcome.Done) TypedAvatar = avatar;
        return await ShowAsync(id, ct, outcome);
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid id, string? member, bool confirm, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(member)) return await ShowAsync(id, ct, new GroupOutcome(false, "That person isn't in this group any more."));
        var outcome = await admin.RemoveMemberAsync(User, Ip, id, member, confirm, ct);
        // Only the last-owner question offers a second press.
        if (!outcome.Done && !confirm && outcome.Message.Contains("Press Remove again", StringComparison.Ordinal)) ConfirmRemove = member;
        return await ShowAsync(id, ct, outcome);
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, bool confirm, CancellationToken ct)
    {
        if (!confirm) return await ShowAsync(id, ct, new GroupOutcome(false, "Tick the box to say you want to delete this group."));
        var outcome = await admin.DeleteAsync(User, Ip, id, ct);
        if (!outcome.Done) return await ShowAsync(id, ct, outcome);
        TempData["GroupsNotice"] = outcome.Message;
        return Redirect("/groups");
    }

    public async Task<IActionResult> OnPostDefaultAsync(Guid id, CancellationToken ct) =>
        await ShowAsync(id, ct, await admin.MakeDefaultAsync(User, Ip, id, ct));

    public async Task<IActionResult> OnPostNoDefaultAsync(Guid id, CancellationToken ct) =>
        await ShowAsync(id, ct, await admin.ClearDefaultAsync(User, Ip, ct));

    public async Task<IActionResult> OnPostEveryoneAsync(Guid id, CancellationToken ct)
    {
        var outcome = await admin.AddEveryoneAsync(User, Ip, ct);
        return await ShowAsync(id, ct, new GroupOutcome(outcome.Done, outcome.Message));
    }

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task<IActionResult> ShowAsync(Guid id, CancellationToken ct, GroupOutcome? outcome = null)
    {
        if (outcome is not null)
        {
            if (outcome.Done) Notice = outcome.Message;
            else Error = outcome.Message;
        }
        try
        {
            Group = await groups.GetAsync(id, ct);
        }
        catch (GroupsUnavailableException)
        {
            Unavailable = true;
            return Page();
        }
        if (Group is null) return NotFound();
        IsDefault = joins.Default() == id;
        return Page();
    }
}
