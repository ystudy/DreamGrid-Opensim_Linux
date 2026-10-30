using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Setup;

/// <summary>Saving the plan picked on the cards page or on a plan's own page (setup's first step).</summary>
public static class PlanChoice
{
    /// <summary>
    /// True once a plan is chosen and the owner avatar exists: from then on setup no longer offers the plans (changing
    /// plan is the dashboard's job). A v7 install that made its owner before plans existed still chooses one here.
    /// </summary>
    public static bool Locked(LauncherSettings s) => s.Plan is not null && s.Setup.OwnerCreated;

    /// <summary>
    /// Saves <paramref name="plan"/> (an available one) unless a part it needs is missing; returns that part's owner
    /// text, or null when saved or <see cref="Locked"/>. A different plan than before asks the network step again.
    /// </summary>
    public static string? Save(IGridController grid, LauncherSettings current, MariaDbBinaries mariaDb, PlanInfo plan)
    {
        if (Locked(current)) return null;
        if (PlanParts.Missing(plan.Plan, mariaDb) is { } missing) return missing;
        grid.UpdateSettings(s => s with
        {
            Plan = plan.Plan,
            Setup = s.Plan == plan.Plan ? s.Setup : s.Setup with { NetworkSet = false },
        }, writeConfig: true);
        return null;
    }
}
