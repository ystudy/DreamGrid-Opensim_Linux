using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>
/// Where people land when their home or last region can't take them (2.14), and the Robust lines that say so. Robust applies
/// the lines when a region registers, so a change applies when the world restarts.
/// </summary>
public static class Landing
{
    /// <summary>The slot in Robust.HG.ini.proto's [GridService] the lines go in.</summary>
    public const string Section = "GridService";
    public const string SlotStart = "; START  <= Leave this line";
    public const string SlotEnd = "; END  <= Leave this line";

    /// <summary>The default region: the chosen one while it is a live region, otherwise the first live region; null with no regions.</summary>
    public static RegionSettings? Default(LauncherSettings s) => Chosen(s) ?? RegionModel.First(s);

    /// <summary>Whether the owner chose the default (and it is still a live region), rather than it being automatic.</summary>
    public static bool DefaultChosen(LauncherSettings s) => Chosen(s) is not null;

    /// <summary>The live fallback regions, in the region list's order, without the default.</summary>
    public static IReadOnlyList<RegionSettings> Fallbacks(LauncherSettings s)
    {
        var ids = s.Landing?.Fallbacks ?? [];
        var defaultId = Default(s)?.Uuid;
        return [.. RegionModel.Live(s).Where(r => r.Uuid != defaultId && ids.Contains(r.Uuid))];
    }

    /// <summary>
    /// One <c>Region_&lt;uuid&gt;</c> line per live region. Robust adds a line's flags to the region's database row and never clears
    /// them on its own, so each region that is not a landing region gets them taken off ("-"): one dropped from Landing stops
    /// being one. A hypergrid visitor who names no region lands on the default too (DefaultHGRegion).
    /// </summary>
    public static IReadOnlyList<string> Lines(LauncherSettings s)
    {
        var defaultId = Default(s)?.Uuid;
        var fallbacks = Fallbacks(s).Select(r => r.Uuid).ToHashSet();
        return [.. RegionModel.Live(s).Select(r =>
        {
            var flags = r.Uuid == defaultId ? "DefaultRegion, DefaultHGRegion, -FallbackRegion"
                : fallbacks.Contains(r.Uuid) ? "FallbackRegion, -DefaultRegion, -DefaultHGRegion"
                : "-DefaultRegion, -DefaultHGRegion, -FallbackRegion";
            return $"Region_{r.Uuid} = \"{flags}\"";
        })];
    }

    private static RegionSettings? Chosen(LauncherSettings s) =>
        s.Landing?.Default is { } id && RegionModel.Live(s).FirstOrDefault(r => r.Uuid == id) is { } r ? r : null;
}
