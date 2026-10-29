using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Setup;

/// <summary>
/// The checklist steps for the install's plan (spec section 11); each one is done when its setting is saved. Setup
/// starts with the plan, so before one is chosen the list is the one Local, Simple Public and Grid Asset share. A Region
/// Server joins a Grid Asset Server instead of naming a world (the grid's name comes with the join) and names an avatar
/// that already exists there.
/// </summary>
public static class SetupSteps
{
    public const string PlanPath = "/setup/plan";

    /// <summary>The path of the Region Server's join step.</summary>
    public const string JoinPath = "/setup/join";

    // Connected Server gets its own list when it becomes available.
    private static readonly IReadOnlyList<(string Title, string Path, Func<LauncherSettings, bool> Done)> Single =
    [
        ("Choose your plan", PlanPath, s => s.Plan is not null),
        ("Your network", "/setup/network", s => s.Setup.NetworkSet),
        ("Name your world", "/setup/world", s => s.Setup.WorldNamed),
        ("Create your avatar", "/setup/avatar", s => s.Setup.OwnerCreated),
        ("Choose your first region", "/setup/region", s => s.Setup.RegionCreated || s.Setup.RegionSkipped),
        ("Connect with Firestorm", "/setup/connect", s => s.Setup.Finished),
    ];

    private static readonly IReadOnlyList<(string Title, string Path, Func<LauncherSettings, bool> Done)> RegionServer =
    [
        ("Choose your plan", PlanPath, s => s.Plan is not null),
        ("Join the Grid Asset Server", JoinPath, s => s.Join is not null),
        ("Your network", "/setup/network", s => s.Setup.NetworkSet),
        ("Your avatar", "/setup/avatar", s => s.Setup.OwnerCreated),
        ("Choose your first region", "/setup/region", s => s.Setup.RegionCreated),
        ("Connect with Firestorm", "/setup/connect", s => s.Setup.Finished),
    ];

    /// <summary>The steps for <paramref name="s"/>'s plan, in order.</summary>
    public static IReadOnlyList<(string Title, string Path, Func<LauncherSettings, bool> Done)> For(LauncherSettings s) =>
        s.Plan == OwnerPlan.RegionServer ? RegionServer : Single;

    /// <summary>True when every step before the one at <paramref name="path"/> is done (false for a path not in the list).</summary>
    public static bool CanOpen(LauncherSettings s, string path)
    {
        var steps = For(s);
        var index = IndexOf(steps, path);
        return index >= 0 && steps.Take(index).All(step => step.Done(s));
    }

    public static string FirstUnfinished(LauncherSettings s)
    {
        var steps = For(s);
        foreach (var step in steps)
            if (!step.Done(s)) return step.Path;
        return steps[^1].Path;
    }

    /// <summary>The index of the step at <paramref name="path"/>, or of the step a sub-page such as /setup/plan/local belongs to; -1 if none.</summary>
    public static int IndexOf(IReadOnlyList<(string Title, string Path, Func<LauncherSettings, bool> Done)> steps, string path)
    {
        for (var i = 0; i < steps.Count; i++)
            if (path.Equals(steps[i].Path, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(steps[i].Path + "/", StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
