using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Security;

/// <summary>
/// Who may do what on this computer's website and port-8001 commands. An avatar at the supervisor level (100 unless changed) is a
/// supervisor; at the admin level (200 unless changed) an administrator; the owner named in setup is always the owner. On a Region
/// Server the accounts belong to the Grid Asset Server's grid, where the estate owner is a normal level-0 avatar, so the owner
/// named in setup is the owner, and only a level at the admin level counts for anyone else (no supervisors). On a Connected Server
/// the accounts belong to someone else's grid, whose levels say nothing about this computer, so only the owner named in setup has
/// any role.
/// </summary>
public static class AdminRule
{
    /// <summary>
    /// The level to use for a command check: at least the admin level when the account may administer, else its own level but
    /// never an administrator's (a level at the admin level or more on another grid, refused above, must not pass).
    /// </summary>
    public static int EffectiveLevel(LauncherSettings s, RobustAccount account) =>
        MayAdminister(s, account)
            ? Math.Max(account.UserLevel, s.Security.AdminLevel)
            : Math.Min(account.UserLevel, s.Security.AdminLevel - 1);

    /// <summary>Whether <paramref name="account"/> may administer under <paramref name="s"/>'s plan.</summary>
    public static bool MayAdminister(LauncherSettings s, RobustAccount account) => RoleOf(s, account) >= StaffRole.Admin;

    /// <summary>The role <paramref name="account"/> has under <paramref name="s"/>'s plan and level settings.</summary>
    public static StaffRole RoleOf(LauncherSettings s, RobustAccount account)
    {
        if (s.Setup.Owner is { } owner && account.PrincipalId == owner.PrincipalId) return StaffRole.Owner;
        return s.Plan switch
        {
            OwnerPlan.Connected => StaffRole.None,
            OwnerPlan.RegionServer => account.UserLevel >= s.Security.AdminLevel ? StaffRole.Admin : StaffRole.None,
            _ => account.UserLevel >= s.Security.AdminLevel ? StaffRole.Admin
                : account.UserLevel >= s.Security.SupervisorLevel ? StaffRole.Supervisor
                : StaffRole.None,
        };
    }

    /// <summary>Whether the Staff and People pages exist: not on a Region Server or a Connected Server, which have no supervisors.</summary>
    public static bool HasStaffPages(LauncherSettings s) => s.Plan is not (OwnerPlan.RegionServer or OwnerPlan.Connected);
}
