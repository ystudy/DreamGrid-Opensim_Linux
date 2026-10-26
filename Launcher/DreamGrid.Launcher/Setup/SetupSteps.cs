using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Setup;

/// <summary>The four checklist steps (spec 6.2); each one is done when its Setup flag is saved.</summary>
public static class SetupSteps
{
    public static readonly IReadOnlyList<(string Title, string Path)> All =
    [
        ("Name your world", "/setup/world"),
        ("Create your avatar", "/setup/avatar"),
        ("Choose your first region", "/setup/region"),
        ("Connect with Firestorm", "/setup/connect"),
    ];

    /// <summary><paramref name="step"/> is 1-4.</summary>
    public static bool IsDone(SetupState s, int step) => step switch
    {
        1 => s.WorldNamed,
        2 => s.OwnerCreated,
        3 => s.RegionCreated,
        4 => s.Finished,
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };

    /// <summary>True when every step before <paramref name="step"/> is done.</summary>
    public static bool CanOpen(SetupState s, int step) => Enumerable.Range(1, step - 1).All(i => IsDone(s, i));

    public static string FirstUnfinished(SetupState s)
    {
        for (var i = 1; i <= All.Count; i++)
            if (!IsDone(s, i)) return All[i - 1].Path;
        return All[^1].Path;
    }
}
