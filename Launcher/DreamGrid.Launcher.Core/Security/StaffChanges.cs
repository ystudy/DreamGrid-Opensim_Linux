namespace DreamGrid.Launcher.Core.Security;

/// <summary>A change to someone's level that the Staff and People pages offer.</summary>
public enum StaffChange { MakeSupervisor, MakeAdmin, MakeGuest, Suspend, Unsuspend }

/// <summary>
/// Who may make which change to whom. The owner may do anything to anyone but the owner; an admin may suspend and unsuspend
/// supervisors and guests; a supervisor may suspend and unsuspend guests. Making someone a supervisor, an admin or a guest is
/// the owner's alone, and nobody changes the owner. The pages ask this on every post (not only when drawing the buttons).
/// </summary>
public static class StaffChanges
{
    public const string OwnerRefusal = "The grid owner can't be changed here.";

    /// <summary>
    /// Null when <paramref name="actor"/> may make <paramref name="change"/> to someone in role <paramref name="target"/>, else the
    /// message to show. For an unsuspension <paramref name="target"/> is the role the person gets back, not the guest level they
    /// are at while suspended.
    /// </summary>
    /// <param name="targetIsOwner">The person is the owner named in setup, whatever their level.</param>
    /// <param name="targetIsActor">The person is the one making the change.</param>
    public static string? Refusal(StaffRole actor, StaffRole target, bool targetIsOwner, StaffChange change, bool targetIsActor = false)
    {
        if (targetIsOwner) return OwnerRefusal;
        if (targetIsActor && change == StaffChange.Suspend) return "You can't suspend yourself.";
        if (actor == StaffRole.Owner) return null;

        var suspension = change is StaffChange.Suspend or StaffChange.Unsuspend;
        if (!suspension) return "Only the grid owner can change who is staff.";
        return actor switch
        {
            StaffRole.Admin when target is StaffRole.None or StaffRole.Supervisor => null,
            StaffRole.Admin => "Admins can only suspend or unsuspend supervisors and guests.",
            StaffRole.Supervisor when target == StaffRole.None => null,
            StaffRole.Supervisor => "Supervisors can only suspend or unsuspend guests.",
            _ => "You can't make this change.",
        };
    }
}
