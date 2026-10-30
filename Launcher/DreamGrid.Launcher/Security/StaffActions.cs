using System.Security.Claims;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Security;

/// <summary>What a change came to: <paramref name="Done"/> when the level was changed, and the sentence to show either way.</summary>
public sealed record ChangeOutcome(bool Done, string Message);

/// <summary>
/// A person's role now, the role they get back when unsuspended (the same unless they are suspended), and whether they are the
/// owner named in setup.
/// </summary>
public readonly record struct RoleView(StaffRole Current, StaffRole Restored, bool IsOwner)
{
    /// <summary>The role that decides who may suspend or unsuspend them: the higher of the two.</summary>
    public StaffRole Judged => Restored > Current ? Restored : Current;
}

/// <summary>
/// The one place that changes someone's level for the Staff and People pages. For each change it asks <see cref="StaffChanges"/>
/// (on the post, not only when the buttons were drawn), sets the level through the grid controller (Robust's console), ends the
/// person's sessions when they lose rights (suspended, made a guest, or an admin made a supervisor), remembers the level a
/// suspended person had (<see cref="SuspendedStore"/>) and audits the result: <c>level-changed</c>, <c>suspended</c> or
/// <c>unsuspended</c>; a refused change is audited with the result <c>refused</c>, and one that could not be made (Robust not
/// running, or the suspended list can't be read) is not audited. A change that was sent but that Robust didn't confirm may have
/// been made: the remembered level is kept, the person is signed out if the change takes rights away, and it is audited with
/// the result <c>unconfirmed</c>.
/// </summary>
public sealed class StaffActions(IGridController grid, Func<LauncherSettings> settings, SuspendedStore suspended, StaffSessions sessions,
    AuditLog audit, ILogger<StaffActions> log)
{
    public const string RobustDown = "Your world's sign-in service isn't running yet. Start your world, then try again.";

    /// <summary>What a change says when Robust took the command but didn't show the new level in time.</summary>
    public const string Unconfirmed = "The change was sent but your world didn't confirm it. Check the person's level, then try again if needed.";

    /// <summary>What a change says when the remembered levels can't be read: nothing is changed, so nobody is given level 0 for want of theirs.</summary>
    public const string SuspendedUnreadable = "The list of suspended people couldn't be read, so nothing was changed.";

    /// <summary>
    /// The roles to judge a change to <paramref name="account"/> by. A suspended person's level is -1, which reads as a guest, so
    /// they are also judged by the level they get back: a supervisor can't hand an admin their rights again. For a suspended person
    /// this reads the suspended list, so it throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when
    /// that can't be read.
    /// </summary>
    public RoleView RoleOf(RobustAccount account)
    {
        var s = settings();
        var current = AdminRule.RoleOf(s, account);
        var restored = account.UserLevel < 0
            ? AdminRule.RoleOf(s, account with { UserLevel = Math.Max(0, suspended.PreviousLevel(account.PrincipalId) ?? 0) })
            : current;
        return new RoleView(current, restored, s.Setup.Owner is { } owner && owner.PrincipalId == account.PrincipalId);
    }

    /// <summary>Why <paramref name="actor"/> may not make <paramref name="change"/> to <paramref name="target"/>, or null when they may.</summary>
    public string? Refusal(ClaimsPrincipal actor, RobustAccount target, StaffChange change)
    {
        var view = RoleOf(target);
        var isActor = StaffSignIn.Read(actor)?.Principal == target.PrincipalId;
        return StaffChanges.Refusal(StaffSignIn.RoleOf(actor), view.Judged, view.IsOwner, change, isActor);
    }

    public async Task<ChangeOutcome> ApplyAsync(ClaimsPrincipal actor, string? ip, RobustAccount target, StaffChange change, CancellationToken ct)
    {
        var name = $"{target.FirstName} {target.LastName}";
        var shown = LogText.Clean(name, 64);
        var action = ActionOf(change);
        var s = settings();
        RoleView view;
        int to;
        try
        {
            if (Refusal(actor, target, change) is { } refusal)
            {
                Write(actor, ip, action, shown, "refused");
                return new ChangeOutcome(false, refusal);
            }
            view = RoleOf(target);
            to = change switch
            {
                StaffChange.MakeSupervisor => s.Security.SupervisorLevel,
                StaffChange.MakeAdmin => s.Security.AdminLevel,
                StaffChange.MakeGuest => 0,
                StaffChange.Suspend => -1,
                _ => Math.Max(0, suspended.PreviousLevel(target.PrincipalId) ?? 0),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Staff: the suspended list could not be read ({Error}); no level was changed.", e.GetType().Name);
            return new ChangeOutcome(false, SuspendedUnreadable);
        }
        var from = target.UserLevel;
        var detail = change is StaffChange.Suspend or StaffChange.Unsuspend ? shown : $"{shown}: {from} -> {to}";
        // Rights lost take effect at once, not at the next five-minute check.
        var losesRights = change is StaffChange.Suspend or StaffChange.MakeGuest
            || change == StaffChange.MakeSupervisor && view.Current == StaffRole.Admin;
        var gainsRights = change == StaffChange.MakeAdmin || change == StaffChange.MakeSupervisor && view.Current == StaffRole.None;
        if (change == StaffChange.Suspend && from < 0) return new ChangeOutcome(false, $"{name} is already suspended.");
        if (change == StaffChange.Unsuspend && from >= 0) return new ChangeOutcome(false, $"{name} is not suspended.");
        if (change is StaffChange.MakeSupervisor or StaffChange.MakeAdmin or StaffChange.MakeGuest && from == to)
            return new ChangeOutcome(false, $"{name} is already {Noun(change)}.");

        var remembered = false;
        try
        {
            if (change == StaffChange.Suspend)
            {
                suspended.Remember(target.PrincipalId, from);
                remembered = true;
            }
            await grid.SetUserLevelAsync(target.FirstName, target.LastName, to, ct);
        }
        catch (LevelNotConfirmedException e)
        {
            // Sent, so it may have been made: the remembered level stays (forgetting it could lose it for good), rights that may
            // be gone are taken at once, and the audit says so.
            log.LogWarning("Staff: a level change was sent but not confirmed: {Message}", LogText.Clean(e.Message, 300));
            if (losesRights) sessions.EndAll(target.PrincipalId);
            Write(actor, ip, action, detail, "unconfirmed");
            return new ChangeOutcome(false, Unconfirmed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Staff: the suspended list could not be saved ({Error}); no level was changed.", e.GetType().Name);
            return new ChangeOutcome(false, "The list of suspended people could not be saved, so nothing was changed.");
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TimeoutException or ConsoleAuthException
                                      or TaskCanceledException && !ct.IsCancellationRequested)
        {
            if (remembered) Forget(target.PrincipalId);
            log.LogWarning("Staff: a level could not be set: {Message}", LogText.Clean(e.Message, 300));
            return new ChangeOutcome(false, RobustDown);
        }
        catch (ArgumentException)
        {
            if (remembered) Forget(target.PrincipalId);
            return new ChangeOutcome(false, $"{shown}'s level can't be changed here.");
        }

        if (losesRights) sessions.EndAll(target.PrincipalId);
        if (change == StaffChange.Unsuspend) Forget(target.PrincipalId);

        Write(actor, ip, action, detail, "ok");
        // The recheck while signed in never raises the role a person signed in with: new rights come with their next sign-in.
        return new ChangeOutcome(true, Done(change, name) + (gainsRights ? " They get the new rights the next time they sign in." : ""));
    }

    private void Forget(Guid principal)
    {
        try { suspended.Forget(principal); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Staff: the suspended list could not be saved ({Error}).", e.GetType().Name);
        }
    }

    private static string ActionOf(StaffChange change) => change switch
    {
        StaffChange.Suspend => "suspended",
        StaffChange.Unsuspend => "unsuspended",
        _ => "level-changed",
    };

    private static string Done(StaffChange change, string name) => change switch
    {
        StaffChange.MakeSupervisor => $"{name} is now a supervisor.",
        StaffChange.MakeAdmin => $"{name} is now an admin.",
        StaffChange.MakeGuest => $"{name} is now a guest.",
        StaffChange.Suspend => $"{name} is suspended.",
        _ => $"{name} is not suspended any more.",
    };

    private static string Noun(StaffChange change) => change switch
    {
        StaffChange.MakeSupervisor => "a supervisor",
        StaffChange.MakeAdmin => "an admin",
        _ => "a guest",
    };

    private void Write(ClaimsPrincipal actor, string? ip, string action, string target, string result)
    {
        var (who, id, role) = AuditActor.Of(actor);
        audit.Write(new AuditEntry(who, id, role, ip, action, target, result));
    }
}
