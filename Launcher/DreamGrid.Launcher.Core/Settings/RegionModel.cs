namespace DreamGrid.Launcher.Core.Settings;

/// <summary>Lookups over the settings' programs and regions.</summary>
public static class RegionModel
{
    /// <summary>Every region that is not deleted.</summary>
    public static IEnumerable<RegionSettings> Live(LauncherSettings s) => s.Regions.Where(r => r.DeletedAt is null);

    /// <summary>The live regions of one program.</summary>
    public static IEnumerable<RegionSettings> In(LauncherSettings s, string instance) =>
        Live(s).Where(r => r.Instance.Equals(instance, StringComparison.Ordinal));

    /// <summary>The regions a program loads: live, not parked and not held (stopped until the next world start).</summary>
    public static IEnumerable<RegionSettings> Loadable(LauncherSettings s, string instance, IReadOnlySet<Guid> held) =>
        In(s, instance).Where(r => !r.Parked && !held.Contains(r.Uuid));

    public static ProgramSettings? ProgramOf(LauncherSettings s, RegionSettings r) =>
        s.Programs.FirstOrDefault(p => p.Instance.Equals(r.Instance, StringComparison.Ordinal));

    /// <summary>The first live region: what single-region code paths act on.</summary>
    public static RegionSettings? First(LauncherSettings s) => Live(s).FirstOrDefault();

    public static RegionSettings? Find(LauncherSettings s, Guid id) => s.Regions.FirstOrDefault(r => r.Uuid == id);

    /// <summary>Ignores case and includes deleted regions: a name stays taken until the region is erased.</summary>
    public static RegionSettings? FindByName(LauncherSettings s, string name) =>
        s.Regions.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
