using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Security;

/// <summary>
/// Who may administer this computer's website and port-8001 commands. An avatar of level 200 or more may. On a Region
/// Server the accounts belong to the Grid Asset Server's grid, where the estate owner is a normal level-0 avatar, so the
/// owner named in setup may too. On a Connected Server the accounts belong to someone else's grid, whose levels say
/// nothing about this computer, so only the owner named in setup may.
/// </summary>
public static class AdminRule
{
    /// <summary>The account level that administers any plan.</summary>
    public const int AdminLevel = 200;

    /// <summary>Whether <paramref name="account"/> may administer under <paramref name="s"/>'s plan.</summary>
    public static bool MayAdminister(LauncherSettings s, RobustAccount account)
    {
        var isOwner = s.Setup.Owner is { } owner && account.PrincipalId == owner.PrincipalId;
        return s.Plan switch
        {
            OwnerPlan.Connected => isOwner,
            OwnerPlan.RegionServer => account.UserLevel >= AdminLevel || isOwner,
            _ => account.UserLevel >= AdminLevel,
        };
    }
}
