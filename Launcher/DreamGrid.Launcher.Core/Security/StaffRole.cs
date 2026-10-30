namespace DreamGrid.Launcher.Core.Security;

/// <summary>What an avatar may do on this computer's website, lowest to highest. The owner named in setup is always <see cref="Owner"/>.</summary>
public enum StaffRole { None, Supervisor, Admin, Owner }

public static class StaffRoleNames
{
    /// <summary>The lower-case word the audit log uses for a role (owner, admin, supervisor, none).</summary>
    public static string AuditName(this StaffRole role) => role.ToString().ToLowerInvariant();
}
