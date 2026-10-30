using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Plans;

/// <summary>Whether the programs a plan needs are on this computer (spec section 11, "a missing part says why").</summary>
public static class PlanParts
{
    /// <summary>
    /// Null when every part <paramref name="plan"/> needs is present; otherwise the owner's text naming the plan, the
    /// part, where DreamGrid looked and what to do. In 6a the only part checked is the database (every plan uses it).
    /// </summary>
    public static string? Missing(OwnerPlan plan, MariaDbBinaries maria)
    {
        if (maria.NotFoundMessage is not { } message) return null;
        var looked = message.Split('\n')[0].TrimEnd('\r');
        return $"The {PlanCatalog.Get(plan).Title} plan needs the database, where avatars and inventory are kept, "
            + $"but its program is missing from this computer. DreamGrid looked here: {looked}. "
            + "This website can't install programs, because it runs without administrator rights. "
            + "Run the DreamGrid installer again: it puts the missing part back and keeps your settings and world.";
    }
}
