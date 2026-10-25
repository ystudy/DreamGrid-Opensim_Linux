using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Modules;

/// <summary>
/// A module's on/off switch in the dashboard's features area (spec 6.9). The host saves the result of
/// <see cref="Toggle"/> through IGridController.UpdateSettings.
/// </summary>
public interface IDashboardSwitch
{
    string ModuleId { get; }
    string Label { get; }
    string Description { get; }
    bool IsOn(LauncherSettings s);

    /// <summary>Returns new settings; never changes <paramref name="s"/> (or its dictionaries) in place.</summary>
    LauncherSettings Toggle(LauncherSettings s, bool on);
}

/// <summary>
/// A module's page under Advanced Settings (<c>/advanced/module/{ModuleId}</c>). The host renders
/// <see cref="Fields"/>, posts every field back as a string, and saves <see cref="Apply"/> only when
/// <see cref="Validate"/> is ok.
/// </summary>
public interface ISettingsPage
{
    string ModuleId { get; }
    string Title { get; }
    IReadOnlyList<SettingField> Fields(LauncherSettings s);

    /// <summary>Errors keyed by <see cref="SettingField.Key"/>.</summary>
    ValidationResult Validate(IReadOnlyDictionary<string, string> values);

    /// <summary>Returns new settings; never changes <paramref name="s"/> (or its dictionaries) in place.</summary>
    LauncherSettings Apply(LauncherSettings s, IReadOnlyDictionary<string, string> values);
}

/// <param name="Kind">"text", "number", "select" (with <paramref name="Options"/>) or "secret" (never echoed into the page).</param>
public sealed record SettingField(
    string Key,
    string Label,
    string Value,
    string Kind,
    IReadOnlyList<string>? Options = null,
    string? Help = null);

/// <summary>Field errors keyed by field name; plain sentences meant for the page.</summary>
public sealed record ValidationResult(IReadOnlyDictionary<string, string> Errors)
{
    public static readonly ValidationResult Valid = new(new Dictionary<string, string>());

    public bool Ok => Errors.Count == 0;
}
