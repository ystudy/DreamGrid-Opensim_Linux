using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Security;

/// <summary>
/// Who may administer this computer's website and port-8001 commands. An avatar of level 200 or more always may. On a
/// Region Server the accounts belong to the Grid Asset Server's grid, where the estate owner is a normal level-0
/// avatar, so the owner named in setup may too.
/// </summary>
public static class AdminRule
{
    /// <summary>The account level that administers any plan.</summary>
    public const int AdminLevel = 200;

    /// <summary>Whether <paramref name="account"/> may administer under <paramref name="s"/>'s plan.</summary>
    public static bool MayAdminister(LauncherSettings s, RobustAccount account) =>
        account.UserLevel >= AdminLevel
        || (s.Plan == OwnerPlan.RegionServer && s.Setup.Owner is { } owner && account.PrincipalId == owner.PrincipalId);
}
