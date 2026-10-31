namespace DreamGrid.Launcher.Security;

/// <summary>
/// The staff cookie scheme, its policies and its role claims. An owner carries all three roles, an admin
/// <see cref="AdminRole"/> and <see cref="StaffRole"/>, a supervisor <see cref="StaffRole"/> only.
/// </summary>
public static class AuthSchemes
{
    public const string Admin = "dg";
    /// <summary>The guest cookie scheme: the guest door's own cookie, never read by the staff pages.</summary>
    public const string Guest = "dg-guest";

    /// <summary>The owner named in setup.</summary>
    public const string OwnerPolicy = "owner";
    /// <summary>Admins and the owner; also the fallback for everything without a decision of its own.</summary>
    public const string AdminPolicy = "admin";
    /// <summary>Supervisors, admins and the owner.</summary>
    public const string StaffPolicy = "staff";
    /// <summary>Anyone signed in at the guest door (scheme <see cref="Guest"/> only).</summary>
    public const string GuestPolicy = "guest";

    public const string OwnerRole = "owner";
    public const string AdminRole = "admin";
    public const string StaffRole = "staff";
    public const string GuestRole = "guest";
}
