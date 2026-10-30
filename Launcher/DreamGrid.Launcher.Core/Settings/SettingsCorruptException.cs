namespace DreamGrid.Launcher.Core.Settings;

/// <summary>
/// The settings file exists but can't be used: it doesn't parse, or (a hand edit) holds a value that isn't allowed. It is left
/// untouched, and the launcher and its verbs refuse to run (exit 2) with this message.
/// </summary>
public sealed class SettingsCorruptException : Exception
{
    public SettingsCorruptException(string path, Exception inner)
        : base($"Settings file '{path}' exists but could not be parsed; it was left untouched. {inner.Message}", inner) => Path = path;

    /// <param name="reasons">What isn't allowed, one "key: reason" each.</param>
    public SettingsCorruptException(string path, IEnumerable<string> reasons)
        : base($"Settings file '{path}' has a value that isn't allowed; it was left untouched. {string.Join(" ", reasons)}") => Path = path;

    public string Path { get; }
}
