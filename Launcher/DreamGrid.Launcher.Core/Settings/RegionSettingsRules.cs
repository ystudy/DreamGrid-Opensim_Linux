using DreamGrid.Launcher.Core.Modules;

namespace DreamGrid.Launcher.Core.Settings;

/// <summary>
/// Checks region and program options before they are saved, and works out their effective values. Error keys are the
/// option property names.
/// </summary>
public static class RegionSettingsRules
{
    /// <summary>The physics engines a program may use.</summary>
    public static readonly IReadOnlyList<string> PhysicsEngines = ["ubODE", "BulletSim", "BulletSimThreaded"];

    internal const string PriorityNeedsRoot = "On Linux only the root user can raise the priority of these regions.";

    private static string Range(int low, int high) =>
        $"Use a number from {low:N0} to {high:N0}, or leave it empty for OpenSim's default.";

    public static ValidationResult Options(RegionOptions o)
    {
        var errors = new Dictionary<string, string>();
        Check(errors, nameof(RegionOptions.MaxAgents), o.MaxAgents, 1, 500);
        Check(errors, nameof(RegionOptions.MaxPrims), o.MaxPrims, 1, 1_000_000);
        Check(errors, nameof(RegionOptions.MaxNonPhysicalSize), o.MaxNonPhysicalSize, 1, 65536);
        Check(errors, nameof(RegionOptions.MaxPhysicalSize), o.MaxPhysicalSize, 1, 2048);
        return new ValidationResult(errors);
    }

    private static void Check(Dictionary<string, string> errors, string key, int? value, int low, int high)
    {
        if (value is { } v && (v < low || v > high)) errors[key] = Range(low, high);
    }

    public static ValidationResult Program(string? physics, ProgramOptions o, int processorCount)
    {
        var errors = new Dictionary<string, string>();
        if (physics is not null && !PhysicsEngines.Contains(physics))
            errors["Physics"] = "Pick ubODE, BulletSim or BulletSimThreaded, or use the grid default.";
        if (o.MinTimerInterval is { } t && !(t >= 0.05 && t <= 10))
            errors[nameof(ProgramOptions.MinTimerInterval)] = "Use a number of seconds from 0.05 to 10, or leave it empty for OpenSim's default.";
        if (o.FramesPerSecond is { } f && (f < 5 || f > 55))
            errors[nameof(ProgramOptions.FramesPerSecond)] = "Use a number from 5 to 55, or leave it empty for OpenSim's default.";
        if (o.Cores is { Count: > 0 } cores)
        {
            if (cores.Any(c => c < 0 || c >= processorCount))
                errors[nameof(ProgramOptions.Cores)] = $"This computer has cores 0 to {processorCount - 1}. Pick only those.";
            else if (cores.Distinct().Count() != cores.Count)
                errors[nameof(ProgramOptions.Cores)] = "Each core can be picked only once.";
        }
        return new ValidationResult(errors);
    }

    /// <summary>The physics engine the program runs: its own, or the grid default.</summary>
    public static string EffectivePhysics(LauncherSettings s, ProgramSettings p) => p.Physics ?? s.DefaultPhysics;

    /// <summary>Whether the program runs scripts: its own setting, or the grid default.</summary>
    public static bool EffectiveScripts(LauncherSettings s, ProgramSettings p) => p.Options?.Scripts ?? s.DefaultScripts;

    /// <summary>One plain line for each program setting whose effective value differs, in tab order, in the tabs' words.</summary>
    public static IReadOnlyList<string> Differences(LauncherSettings s, ProgramSettings from, ProgramSettings to)
    {
        var lines = new List<string>();
        var a = from.Options ?? new ProgramOptions();
        var b = to.Options ?? new ProgramOptions();

        if (EffectivePhysics(s, from) != EffectivePhysics(s, to))
            lines.Add($"Physics will change from {SettingLabels.Physics(EffectivePhysics(s, from))} to {SettingLabels.Physics(EffectivePhysics(s, to))}.");
        if (EffectiveScripts(s, from) != EffectiveScripts(s, to))
            lines.Add(EffectiveScripts(s, to) ? "Scripts will be turned on." : "Scripts will be turned off.");
        if (a.MinTimerInterval != b.MinTimerInterval)
            lines.Add($"The script timer rate will change from {Seconds(a.MinTimerInterval)} to {Seconds(b.MinTimerInterval)} seconds.");
        if (a.FramesPerSecond != b.FramesPerSecond)
            lines.Add($"The frame rate will change from {a.FramesPerSecond?.ToString() ?? "OpenSim's default"} to {b.FramesPerSecond?.ToString() ?? "OpenSim's default"} per second.");
        if (a.Map != b.Map)
            lines.Add($"The map will change from {SettingLabels.Map(a.Map)} to {SettingLabels.Map(b.Map)}.");
        if (a.AllowGods != b.AllowGods || a.OwnerIsGod != b.OwnerIsGod || a.ManagerIsGod != b.ManagerIsGod)
            lines.Add("God permissions will change.");
        var coresA = Normalize(a.Cores);
        var coresB = Normalize(b.Cores);
        if (!coresA.SequenceEqual(coresB))
            lines.Add($"It will use {Describe(coresB)} instead of {Describe(coresA)}.");
        if (a.Priority != b.Priority)
            lines.Add($"Its priority will change from {SettingLabels.Priority(a.Priority)} to {SettingLabels.Priority(b.Priority)}.");
        return lines;
    }

    private static string Seconds(double? v) => v is { } d ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : "OpenSim's default";

    private static List<int> Normalize(IReadOnlyList<int>? cores) => cores is null ? [] : [.. cores.Distinct().Order()];

    private static string Describe(List<int> cores) => cores.Count == 0 ? "every core" : cores.Count == 1 ? $"core {cores[0]}" : $"cores {Ranges(cores)}";

    /// <summary>Sorted cores as runs: 0–3, 5.</summary>
    private static string Ranges(List<int> cores)
    {
        var parts = new List<string>();
        for (var i = 0; i < cores.Count;)
        {
            var j = i;
            while (j + 1 < cores.Count && cores[j + 1] == cores[j] + 1) j++;
            parts.Add(j == i ? cores[i].ToString() : j == i + 1 ? $"{cores[i]}, {cores[j]}" : $"{cores[i]}–{cores[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    /// <summary>The cores within range, in the order given. Empty means every core.</summary>
    public static IReadOnlyList<int> UsableCores(IReadOnlyList<int>? cores, int processorCount) =>
        cores is null ? [] : [.. cores.Where(c => c >= 0 && c < processorCount).Distinct()];

    /// <summary>Why <paramref name="p"/> cannot be chosen here, or null when it can.</summary>
    public static string? PriorityUnavailable(CpuPriority p) =>
        OperatingSystem.IsLinux() && !Environment.IsPrivilegedProcess && p > CpuPriority.Normal ? PriorityNeedsRoot : null;
}
