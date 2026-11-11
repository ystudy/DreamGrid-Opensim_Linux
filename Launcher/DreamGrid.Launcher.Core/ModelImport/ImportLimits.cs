using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.ModelImport;

/// <summary>Whether a model can be uploaded here, and the limits it is checked against.</summary>
public static class ImportLimitsRules
{
    /// <summary>
    /// What every region gets when its own options don't set them: GridConfigWriter starts each region's OpenSim.ini from
    /// bin/config-include/Proto/Robust/OpenSim.ini, whose [Startup] sets NonPhysicalPrimMax=1024, PhysicalPrimMax=128 and
    /// LinksetPrims = 1024 (a test pins these to that file). NonPhysicalPrimMin is left at OpenSim's 0.001.
    /// </summary>
    public const int DefaultMaxPartSize = 1024;
    public const int DefaultMaxPhysicalPartSize = 128;
    public const int DefaultLinksetParts = 1024;
    public const double MinPartSize = 0.001;

    /// <summary>This computer runs Robust (not a Region Server or Connected Server) and has a region that isn't deleted.</summary>
    public static bool UploadAvailable(LauncherSettings s) => PlanCatalog.RunsRobust(s) && Live(s).Any();

    /// <summary>
    /// The smallest part sizes over the live regions, so a model fits everywhere. A region's options can set the part sizes but not
    /// LinksetPrims, so every region has the prototype's linkset limit.
    /// </summary>
    public static ImportLimits For(LauncherSettings s)
    {
        var regions = Live(s).ToList();
        var maxPart = regions.Select(r => r.Options?.MaxNonPhysicalSize ?? DefaultMaxPartSize).DefaultIfEmpty(DefaultMaxPartSize).Min();
        var maxPhysical = regions.Select(r => r.Options?.MaxPhysicalSize ?? DefaultMaxPhysicalPartSize).DefaultIfEmpty(DefaultMaxPhysicalPartSize).Min();
        return new ImportLimits(maxPart, MinPartSize, maxPhysical, DefaultLinksetParts,
            MaxUploadBytes: 64L * 1024 * 1024, MaxImageSide: 8192, MaxTextureSide: 1024,
            MaxTriangles: 2_000_000, MaxNodes: 512, HeavyTriangles: 200_000, MaxAssetBytes: 64L * 1024 * 1024);
    }

    private static IEnumerable<RegionSettings> Live(LauncherSettings s) => s.Regions.Where(r => r.DeletedAt is null);
}
