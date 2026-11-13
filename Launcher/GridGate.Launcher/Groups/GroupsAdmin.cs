using System.Security.Claims;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Groups;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Security;

namespace GridGate.Launcher.Groups;

/// <summary>What a group action did: done or not, and what to tell the admin.</summary>
public sealed record GroupOutcome(bool Done, string Message);

public sealed record AddEveryoneOutcome(bool Done, int Added, int Skipped, string Message);

/// <summary>
/// The admin's group actions behind the Groups pages, the default group, and new residents joining it (spec
/// 2026-11-02-groups-admin-design). Changes to the group tables go through <see cref="IGridGroups"/>; who has been in a
/// group is kept in <see cref="GroupJoins"/>, always written before the group so a half-finished add leaves a person out rather
/// than pulling back someone who left. Each action is audited. Adds and default-group changes run one at a time, so "Add
/// everyone now" and a sign-up never race. Callers check who may act (the pages are admin-only).
/// </summary>
public sealed class GroupsAdmin(IGridGroups groups, GroupJoins joins, AuditLog audit, ILogger<GroupsAdmin> log)
{
    public const string DatabaseDown = "The grid's database isn't answering. Start the world, then try again.";
    public const string NotSaved = "Couldn't save the change. Try again.";
    public const string NoGroup = "That group isn't there any more.";
    public const string Delay = "It can take about a minute to show in the viewer.";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<GroupOutcome> UpdateAsync(ClaimsPrincipal actor, string? ip, Guid group, string? name, string? charter, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (GroupRules.NameProblem(name) is { } bad) return new GroupOutcome(false, bad);
            if (GroupRules.CharterProblem(charter) is { } tooLong) return new GroupOutcome(false, tooLong);
            if (await groups.GetAsync(group, ct) is not { } before) return new GroupOutcome(false, NoGroup);
            var newName = name!.Trim();
            var newCharter = charter ?? "";
            switch (await groups.UpdateAsync(group, newName, newCharter, ct))
            {
                case RenameResult.NoGroup: return new GroupOutcome(false, NoGroup);
                case RenameResult.NameTaken: return new GroupOutcome(false, $"Another group is already called {LogText.Clean(newName, 64)}.");
            }
            if (before.Name != newName) Write(actor, ip, "group-renamed", $"{group} {newName}");
            if (before.Charter != newCharter) Write(actor, ip, "group-charter-changed", group.ToString());
            return new GroupOutcome(true, $"Saved. {Delay}");
        });

    public Task<GroupOutcome> AddMemberAsync(ClaimsPrincipal actor, string? ip, Guid group, RobustAccount who, CancellationToken ct) =>
        GuardAsync(() => OneAtATimeAsync(async () =>
        {
            var name = who.FullName();
            if (await groups.GetAsync(group, ct) is not { } g) return new GroupOutcome(false, NoGroup);
            if (g.Members.Any(m => m.Local && GroupRules.IsLocal(m.PrincipalId, out var id) && id == who.PrincipalId))
                return new GroupOutcome(false, $"{name} is already in {g.Name}.");
            if (!joins.Remember(group, [who.PrincipalId.ToString()])) return new GroupOutcome(false, NotSaved);
            switch (await groups.AddMemberAsync(group, who.PrincipalId, ct))
            {
                case AddResult.NoGroup: return new GroupOutcome(false, NoGroup);
                case AddResult.AlreadyMember: return new GroupOutcome(false, $"{name} is already in {g.Name}.");
            }
            Write(actor, ip, "group-member-added", $"{group} {name}");
            return new GroupOutcome(true, $"Added {name} to {g.Name}. {Delay}");
        }));

    public Task<GroupOutcome> RemoveMemberAsync(ClaimsPrincipal actor, string? ip, Guid group, string principalId, bool confirmed, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (await groups.GetAsync(group, ct) is not { } g) return new GroupOutcome(false, NoGroup);
            var member = g.Members.FirstOrDefault(m => string.Equals(m.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase));
            if (member is null) return new GroupOutcome(false, "That person isn't in this group any more.");
            if (member.Owner && !confirmed && g.Members.Count(m => m.Owner) == 1)
                return new GroupOutcome(false,
                    $"{member.Name} is the group's last owner. Removing them leaves the group with no owner. Press Remove again to confirm.");
            if (!await groups.RemoveMemberAsync(group, member.PrincipalId, ct)) return new GroupOutcome(false, "That person isn't in this group any more.");
            Write(actor, ip, "group-member-removed", $"{group} {member.Name}");
            return new GroupOutcome(true, $"Removed {member.Name} from {g.Name}. {Delay}");
        });

    public Task<GroupOutcome> DeleteAsync(ClaimsPrincipal actor, string? ip, Guid group, CancellationToken ct) =>
        GuardAsync(() => OneAtATimeAsync(async () =>
        {
            if (await groups.GetAsync(group, ct) is not { } g || !await groups.DeleteAsync(group, ct)) return new GroupOutcome(false, NoGroup);
            if (!joins.GroupDeleted(group))
                log.LogWarning("Groups: {Group} was deleted but group-joins.json couldn't be saved; the default may still name it.", group);
            Write(actor, ip, "group-deleted", $"{group} {g.Name}");
            return new GroupOutcome(true, $"Deleted {g.Name}. {Delay}");
        }));

    public Task<GroupOutcome> MakeDefaultAsync(ClaimsPrincipal actor, string? ip, Guid group, CancellationToken ct) =>
        GuardAsync(() => OneAtATimeAsync(async () =>
        {
            if (await groups.GetAsync(group, ct) is not { } g) return new GroupOutcome(false, NoGroup);
            if (!joins.SetDefault(group, g.Members.Select(m => m.PrincipalId))) return new GroupOutcome(false, NotSaved);
            Write(actor, ip, "group-default-set", $"{group} {g.Name}");
            return new GroupOutcome(true, $"{g.Name} is now the default group. New residents join it when their sign-up is done.");
        }));

    public Task<GroupOutcome> ClearDefaultAsync(ClaimsPrincipal actor, string? ip, CancellationToken ct) =>
        GuardAsync(() => OneAtATimeAsync(() =>
        {
            var was = joins.Default();
            if (!joins.ClearDefault()) return Task.FromResult(new GroupOutcome(false, NotSaved));
            Write(actor, ip, "group-default-cleared", was?.ToString());
            return Task.FromResult(new GroupOutcome(true, "There is no default group now. New residents don't join a group."));
        }));

    public async Task<AddEveryoneOutcome> AddEveryoneAsync(ClaimsPrincipal actor, string? ip, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var added = 0;
        try
        {
            if (joins.Default() is not { } group) return new(false, 0, 0, "There is no default group.");
            try
            {
                if (await groups.GetAsync(group, ct) is not { } g) return new(false, 0, 0, NoGroup);
                var members = await groups.MemberIdsAsync(group, ct);
                var inGroup = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);
                var residents = await groups.ActiveResidentsAsync(ct);
                var toAdd = residents.Where(r => !inGroup.Contains(r.ToString()) && !joins.Has(group, r.ToString())).ToList();
                var skipped = residents.Count - toAdd.Count;
                // The members now are remembered, so one who leaves from now on is never pulled back. Each new one is remembered just
                // before they are added: a stop half-way leaves out only the one whose add failed, and the next press reaches the rest.
                if (!joins.Remember(group, members)) return new(false, 0, 0, NotSaved);
                foreach (var resident in toAdd)
                {
                    if (!joins.Remember(group, [resident.ToString()]))
                        return new(false, added, skipped, added == 0 ? NotSaved : $"Added {added}, then the change couldn't be saved. Try again.");
                    if (await groups.AddMemberAsync(group, resident, ct) == AddResult.Added) added++;
                }
                Write(actor, ip, "group-add-everyone", $"{group} added {added} skipped {skipped}");
                return new(true, added, skipped,
                    $"Added {added} residents to {g.Name}; skipped {skipped} who are already in it or were in it before. {Delay}");
            }
            catch (GroupsUnavailableException e)
            {
                log.LogWarning("Groups: add everyone stopped after {Added}: {Message}", added, e.InnerException?.Message ?? e.Message);
                if (added > 0) Write(actor, ip, "group-add-everyone", $"{group} added {added} stopped");
                return added == 0
                    ? new(false, 0, 0, DatabaseDown)
                    : new(false, added, 0, $"Added {added} before the database stopped answering. Press the button again once the world is running.");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Puts a resident whose sign-up has just become active into the default group. Never throws: no default, a resident already
    /// remembered, a list that can't be saved or a database that's down only leave them out (logged).
    /// </summary>
    public async Task JoinNewResidentAsync(RobustAccount resident, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (joins.Default() is not { } group) return;
            var id = resident.PrincipalId.ToString();
            if (joins.Has(group, id)) return;
            if (!joins.Remember(group, [id]))
            {
                log.LogWarning("Groups: {Principal} wasn't added to the default group: group-joins.json couldn't be saved.", resident.PrincipalId);
                return;
            }
            switch (await groups.AddMemberAsync(group, resident.PrincipalId, ct))
            {
                case AddResult.NoGroup:
                    log.LogWarning("Groups: the default group {Group} is gone (deleted in the viewer?), so there is no default now.", group);
                    joins.ClearDefault();
                    return;
                case AddResult.Added:
                    audit.Write(new AuditEntry("GridGate", null, StaffRole.None.AuditName(), null, "group-auto-joined", $"{group} {resident.FullName()}", "ok"));
                    return;
            }
        }
        catch (Exception e) when (e is GroupsUnavailableException or OperationCanceledException)
        {
            log.LogWarning("Groups: {Principal} wasn't added to the default group: {Message}", resident.PrincipalId, e.InnerException?.Message ?? e.Message);
        }
        finally { _gate.Release(); }
    }

    private async Task<GroupOutcome> GuardAsync(Func<Task<GroupOutcome>> work)
    {
        try
        {
            return await work();
        }
        catch (GroupsUnavailableException e)
        {
            log.LogWarning("Groups: the database didn't answer: {Message}", e.InnerException?.Message ?? e.Message);
            return new GroupOutcome(false, DatabaseDown);
        }
    }

    private async Task<GroupOutcome> OneAtATimeAsync(Func<Task<GroupOutcome>> work)
    {
        await _gate.WaitAsync();
        try { return await work(); }
        finally { _gate.Release(); }
    }

    private void Write(ClaimsPrincipal actor, string? ip, string action, string? target) =>
        audit.Write(actor, ip, action, target);

}
