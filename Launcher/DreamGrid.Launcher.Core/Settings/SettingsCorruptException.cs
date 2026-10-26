namespace DreamGrid.Launcher.Core.Settings;

public sealed class SettingsCorruptException(string path, Exception inner)
    : Exception($"Settings file '{path}' exists but could not be parsed; it was left untouched. {inner.Message}", inner)
{
    public string Path { get; } = path;
}
