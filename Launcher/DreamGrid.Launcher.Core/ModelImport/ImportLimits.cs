using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.ModelImport;

/// <summary>Whether a model can be uploaded here, and the limits it is checked against.</summary>
public static class ImportLimitsRules
{
    /// <summary>OpenSim's own defaults when a region doesn't set them (NonPhysicalPrimMax, PhysicalPrimMax, NonPhysicalPrimMin).</summary>
    public const int DefaultMaxPartSize = 256;
    public const int DefaultMaxPhysicalPartSize = 10;
    public const double MinPartSize = 0.001;

    /// <summary>This computer runs Robust (not a Region Server or Connected Server) and has a region that isn't deleted.</summary>
    public static bool UploadAvailable(LauncherSettings s) => PlanCatalog.RunsRobust(s) && Live(s).Any();

    /// <summary>
    /// The smallest part sizes over the live regions, so a model fits everywhere. MaxLinksetParts is 0 (no limit): the launcher
    /// writes no LinksetPrims; if it ever does, take the smallest non-zero value.
    /// </summary>
    public static ImportLimits For(LauncherSettings s)
    {
        var regions = Live(s).ToList();
        var maxPart = regions.Select(r => r.Options?.MaxNonPhysicalSize ?? DefaultMaxPartSize).DefaultIfEmpty(DefaultMaxPartSize).Min();
        var maxPhysical = regions.Select(r => r.Options?.MaxPhysicalSize ?? DefaultMaxPhysicalPartSize).DefaultIfEmpty(DefaultMaxPhysicalPartSize).Min();
        return new ImportLimits(maxPart, MinPartSize, maxPhysical, 0,
            MaxUploadBytes: 64L * 1024 * 1024, MaxImageSide: 8192, MaxTextureSide: 1024,
            MaxTriangles: 2_000_000, MaxNodes: 512, HeavyTriangles: 200_000);
    }

    private static IEnumerable<RegionSettings> Live(LauncherSettings s) => s.Regions.Where(r => r.DeletedAt is null);
}
