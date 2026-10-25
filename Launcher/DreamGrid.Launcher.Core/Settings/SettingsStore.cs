using System.Text.Json;

namespace DreamGrid.Launcher.Core.Settings;

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public LauncherSettings LoadOrCreate()
    {
        if (!File.Exists(path))
        {
            var fresh = NewSettings();
            Save(fresh);
            return fresh;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), Options);
            return loaded ?? throw new JsonException("settings file contains null");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new SettingsCorruptException(path, e);
        }
    }

    public void Save(LauncherSettings s)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(fs, s, Options);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static LauncherSettings NewSettings() => new()
    {
        Db = new DatabaseSecrets(Secrets.NewPassword(), Secrets.NewPassword(), Secrets.NewPassword()),
        Console = new ConsoleSecrets("dg" + Secrets.NewPassword(8), Secrets.NewPassword()),
        MachineId = Secrets.NewMachineId(),
        SetupToken = Secrets.NewPassword(32),
    };
}
