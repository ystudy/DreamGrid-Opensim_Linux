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
            // ReadWrite | Delete sharing keeps the read from blocking writers longer than it must (see ReplaceWithRetry).
            string json;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
                json = reader.ReadToEnd();
            var loaded = JsonSerializer.Deserialize<LauncherSettings>(json, Options);
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
        ReplaceWithRetry(tmp, path);
    }

    /// <summary>
    /// Atomic replace. On Windows the overwrite fails while another handle has the target open (even with delete
    /// sharing), so a short read elsewhere is waited out: up to ~2 s, then the last error is thrown.
    /// </summary>
    private static void ReplaceWithRetry(string tmp, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && OperatingSystem.IsWindows() && attempt < 40)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static LauncherSettings NewSettings() => new()
    {
        Db = new DatabaseSecrets(Secrets.NewPassword(), Secrets.NewPassword(), Secrets.NewPassword()),
        Console = new ConsoleSecrets("dg" + Secrets.NewPassword(8), Secrets.NewPassword()),
        MachineId = Secrets.NewMachineId(),
        SetupToken = Secrets.NewPassword(32),
    };
}
