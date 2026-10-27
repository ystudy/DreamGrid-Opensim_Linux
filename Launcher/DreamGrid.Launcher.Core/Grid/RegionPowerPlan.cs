using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>
/// What a region power action does: the settings and held set afterwards, the programs to restart, stop and start, and
/// the other regions that go offline meanwhile (named to the admin and logged).
/// </summary>
public sealed record PowerSteps(LauncherSettings Settings, IReadOnlySet<Guid> Held, IReadOnlyList<string> Restart, IReadOnlyList<string> Stop,
    IReadOnlyList<string> Start, IReadOnlyList<string> Affected);

/// <summary>
/// The rules of a region's power menu (spec §6.8). Stop holds the region until the next world start; Park is saved and
/// lasts until Unpark. A program restarts to load or drop a region, and stops when it has none left to load.
/// </summary>
public static class RegionPowerPlan
{
    public static PowerSteps For(LauncherSettings s, IReadOnlySet<Guid> held, IReadOnlySet<string> runningPrograms, Guid region, RegionPower action)
    {
        var r = RegionModel.Find(s, region) ?? throw new InvalidOperationException("There is no such region.");
        if (r.DeletedAt is not null) throw new InvalidOperationException("That region has been deleted.");

        var nextHeld = new HashSet<Guid>(held);
        var next = s;
        var load = false; // whether the region should be loaded afterwards
        switch (action)
        {
            case RegionPower.Start or RegionPower.UnparkAndStart:
                next = WithParked(s, region, false);
                nextHeld.Remove(region);
                load = true;
                break;
            case RegionPower.Unpark:
                return new PowerSteps(WithParked(s, region, false), held, [], [], [], []);
            case RegionPower.Stop:
                nextHeld.Add(region);
                break;
            case RegionPower.Park:
                next = WithParked(s, region, true);
                nextHeld.Remove(region);
                break;
            case RegionPower.Restart:
                // A parked or stopped region isn't running: there is nothing to restart.
                if (r.Parked || held.Contains(region)) return new PowerSteps(s, held, [], [], [], []);
                load = true;
                break;
        }

        var program = r.Instance;
        var running = runningPrograms.Contains(program);
        var remaining = RegionModel.Loadable(next, program, nextHeld).ToList();
        var others = running
            ? RegionModel.Loadable(s, program, held).Where(o => o.Uuid != region).Select(o => o.Name).ToList()
            : [];

        if (!running)
            return new PowerSteps(next, nextHeld, [], [], load && remaining.Count > 0 ? [program] : [], []);
        if (remaining.Count == 0)
            return new PowerSteps(next, nextHeld, [], [program], [], others);
        return new PowerSteps(next, nextHeld, [program], [], [], others);
    }

    /// <summary>Running programs that don't load every region they should (from <paramref name="loaded"/>; null = unknown).</summary>
    public static IReadOnlyList<string> MissingRegions(LauncherSettings s, IReadOnlySet<Guid> held, IReadOnlySet<string> runningPrograms,
        IReadOnlyDictionary<string, IReadOnlyCollection<Guid>?> loaded) =>
        s.Programs.Select(p => p.Instance)
            .Where(runningPrograms.Contains)
            .Where(p => loaded.GetValueOrDefault(p) is { } ids && RegionModel.Loadable(s, p, held).Any(r => !ids.Contains(r.Uuid)))
            .ToList();

    /// <summary>Every region unparked ("Start everything").</summary>
    public static LauncherSettings Unparked(LauncherSettings s) =>
        s with { Regions = s.Regions.Select(r => r.Parked ? r with { Parked = false } : r).ToList() };

    private static LauncherSettings WithParked(LauncherSettings s, Guid region, bool parked) =>
        s with { Regions = s.Regions.Select(r => r.Uuid == region ? r with { Parked = parked } : r).ToList() };
}
