using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Modules;

/// <summary>
/// The spec 6.9 proving module: <c>settings.Modules["loglevel"]["level"]</c> sets the log4net level of the
/// generated Robust and region log configs. It contributes no INI keys.
/// </summary>
public sealed class LogLevelModule : IFeatureModule
{
    private static readonly string[] Levels = ["ALL", "DEBUG", "INFO", "WARN", "ERROR", "FATAL", "OFF"];

    public string Id => "loglevel";
    public string Title => "Log level";
    public IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string> { ["level"] = "INFO" };

    public IEnumerable<IniOverride> Contribute(LauncherSettings s) => [];

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
