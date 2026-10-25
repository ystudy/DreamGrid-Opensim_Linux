using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Modules;

/// <summary>
/// The spec 6.9 proving module: <c>settings.Modules["loglevel"]["level"]</c> sets the log4net level of the
/// generated Robust and region log configs. It contributes no INI keys and has an Advanced Settings page but no
/// dashboard switch.
/// </summary>
public sealed class LogLevelModule : IFeatureModule, ISettingsPage
{
    private static readonly string[] Levels = ["ALL", "DEBUG", "INFO", "WARN", "ERROR", "FATAL", "OFF"];

    /// <summary>The levels the settings page offers (the others stay valid in a hand-edited settings file).</summary>
    private static readonly string[] Offered = ["DEBUG", "INFO", "WARN", "ERROR"];

    public string Id => "loglevel";
    public string Title => "Log level";
    public IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string> { ["level"] = "INFO" };

    public IEnumerable<IniOverride> Contribute(LauncherSettings s) => [];

    public string ModuleId => Id;

    public IReadOnlyList<SettingField> Fields(LauncherSettings s) =>
    [
        new("level", "Log level", LevelFor(s), "select", Offered,
            "How much Robust and the region write to their log files. DEBUG writes the most, ERROR only problems. " +
            "Applies when your world restarts."),
    ];

    public ValidationResult Validate(IReadOnlyDictionary<string, string> values) =>
        values.TryGetValue("level", out var level) && Offered.Contains(level)
            ? ValidationResult.Valid
            : new ValidationResult(new Dictionary<string, string> { ["level"] = "Pick DEBUG, INFO, WARN or ERROR." });

    /// <summary>Copies both dictionaries: the current settings instance is shared and must not change.</summary>
    public LauncherSettings Apply(LauncherSettings s, IReadOnlyDictionary<string, string> values)
    {
        var mine = s.Modules.TryGetValue(Id, out var m) ? new Dictionary<string, string>(m) : new Dictionary<string, string>();
        mine["level"] = values["level"];
        return s with { Modules = new Dictionary<string, Dictionary<string, string>>(s.Modules) { [Id] = mine } };
    }

    /// <summary>The configured level (upper-cased), or INFO when unset or not a log4net level.</summary>
    public static string LevelFor(LauncherSettings s)
    {
        if (s.Modules.TryGetValue("loglevel", out var m) && m.TryGetValue("level", out var level))
        {
            var upper = level.Trim().ToUpperInvariant();
            if (Levels.Contains(upper)) return upper;
        }
        return "INFO";
    }
}
