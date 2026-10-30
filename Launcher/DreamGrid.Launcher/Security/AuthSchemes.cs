namespace DreamGrid.Launcher.Security;

/// <summary>
/// The staff cookie scheme, its policies and its role claims. An owner carries all three roles, an admin
/// <see cref="AdminRole"/> and <see cref="StaffRole"/>, a supervisor <see cref="StaffRole"/> only.
/// </summary>
public static class AuthSchemes
{
    public const string Admin = "dg";

    /// <summary>The owner named in setup.</summary>
    public const string OwnerPolicy = "owner";
    /// <summary>Admins and the owner; also the fallback for everything without a decision of its own.</summary>
    public const string AdminPolicy = "admin";
    /// <summary>Supervisors, admins and the owner.</summary>
    public const string StaffPolicy = "staff";

    public const string OwnerRole = "owner";
    public const string AdminRole = "admin";
    public const string StaffRole = "staff";
}
