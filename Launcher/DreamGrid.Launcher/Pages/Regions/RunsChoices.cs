using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Pages.Regions;

/// <summary>One "runs with" choice: the regions that already run together. <see cref="Value"/> is the first region's id (never a folder name).</summary>
public sealed record GroupChoice(string Value, string Instance, IReadOnlyList<string> Names, bool Running)
{
    public string Label => "Runs with " + RegionAdvice.GroupLabel(Names);

    /// <summary>"Home", "Home and Beach", "Home, Beach and Forest".</summary>
    public string NamesText => RegionAdvice.Join(Names);

    /// <summary>Every group of live regions, in settings order; <see cref="Running"/> when its program is up.</summary>
    public static IReadOnlyList<GroupChoice> All(LauncherSettings s, GridStatus status)
    {
        var programs = status.Programs.ToDictionary(p => p.Instance, p => p.Process.State);
        return s.Programs
            .Select(p => (Program: p, Regions: RegionModel.In(s, p.Instance).ToList()))
            .Where(g => g.Regions.Count > 0)
            .Select(g => new GroupChoice(g.Regions[0].Uuid.ToString(), g.Program.Instance, g.Regions.Select(r => r.Name).ToList(),
                programs.TryGetValue(g.Program.Instance, out var state) && RegionPowerPlan.IsActive(state)))
            .ToList();
    }
}

/// <summary>
/// The "How it runs" choices the add form and the change page share (<c>_RunsChoices.cshtml</c>): "Runs on its own" when
/// <see cref="Own"/> is set, then each group. A choice whose <see cref="Restarts"/> names regions gets an "I understand ...
/// will restart" box (posted as <c>Confirm</c> with the choice's value); <see cref="Note"/> adds one line each under a group.
/// </summary>
public sealed record RunsChoices(
    string? Chosen,
    OwnAdvice? Own,
    IReadOnlyList<GroupChoice> Groups,
    Func<GroupChoice, string> ShareText,
    Func<string, IReadOnlyList<string>> Restarts,
    IReadOnlyCollection<string> Confirmed,
    Func<GroupChoice, IReadOnlyList<string>>? Note = null)
{
    /// <summary>The posted value of "Runs on its own".</summary>
    public const string OwnValue = "own";
}
