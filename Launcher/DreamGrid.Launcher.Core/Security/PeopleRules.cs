namespace DreamGrid.Launcher.Core.Security;

/// <summary>A task on the People list and a person's page.</summary>
public enum PersonTask { Edit, ResetLink, SignOut, Suspend, Unsuspend, Delete, Inventory }

/// <summary>
/// What the rules need to know about a person: the role they are judged by (the higher of their current role and the one they
/// get back when unsuspended), whether they are the owner named in setup or the one asking, whether they are suspended or a
/// sign-up waiting for staff or email, and whether a reset link can reach them.
/// </summary>
public sealed record PersonFacts(StaffRole Judged, bool IsOwner, bool IsActor, bool Suspended, bool Waiting, bool HasEmail, bool EmailReady);

/// <summary>
/// Who may do which task to whom on the People pages. Suspend and unsuspend are <see cref="StaffChanges"/>'s rules. Everything
/// else is for the owner and admins, on residents only: staff are changed on the Staff page first, nobody uses these tasks on
/// themself, and the owner can't be changed. Delete needs the person suspended first. The pages draw a disabled icon with the
/// sentence as its reason, and ask again on every post.
/// </summary>
public static class PeopleRules
{
    public const string Waiting = "Approve or deny them on the Sign-ups page.";
    public const string Self = "Use your own account page.";
    public const string SupervisorOnly = "Supervisors can only suspend or unsuspend.";
    public const string StaffFirst = "Change staff on the Staff page first.";
    public const string OwnerChangesStaff = "Only the owner can change staff.";
    public const string EmailOff = "Email isn't set up.";
    public const string NoEmail = "No email address.";
    public const string IsSuspended = "Suspended.";
    public const string SuspendFirst = "Suspend them first.";

    /// <summary>Null when <paramref name="actor"/> may do <paramref name="task"/> to <paramref name="target"/>, else the sentence to show.</summary>
    public static string? Refusal(StaffRole actor, PersonFacts target, PersonTask task)
    {
        if (target.IsOwner) return StaffChanges.OwnerRefusal;
        if (target.Waiting) return Waiting;
        if (task is PersonTask.Suspend or PersonTask.Unsuspend)
            return StaffChanges.Refusal(actor, target.Judged, target.IsOwner,
                task == PersonTask.Suspend ? StaffChange.Suspend : StaffChange.Unsuspend, target.IsActor);
        if (target.IsActor) return Self;
        if (actor is not (StaffRole.Admin or StaffRole.Owner)) return SupervisorOnly;
        if (target.Judged != StaffRole.None) return actor == StaffRole.Owner ? StaffFirst : OwnerChangesStaff;
        return task switch
        {
            PersonTask.ResetLink when !target.EmailReady => EmailOff,
            PersonTask.ResetLink when !target.HasEmail => NoEmail,
            PersonTask.ResetLink when target.Suspended => IsSuspended,
            PersonTask.Delete when !target.Suspended => SuspendFirst,
            _ => null,
        };
    }
}
