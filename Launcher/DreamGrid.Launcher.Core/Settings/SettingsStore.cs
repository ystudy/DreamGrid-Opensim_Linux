using System.Runtime.InteropServices;
using System.Text.Json;

namespace DreamGrid.Launcher.Core.Settings;

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, RespectNullableAnnotations = true };

    private int _swept;

    public LauncherSettings LoadOrCreate()
    {
        // A .tmp found at the first load is a save that never reached the rename (this process has not saved yet);
        // the real file is the truth. Later loads leave it alone: it may be a save in flight.
        if (Interlocked.Exchange(ref _swept, 1) == 0)
            try { File.Delete(path + ".tmp"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        if (!File.Exists(path))
        {
            var fresh = NewSettings();
            Save(fresh);
            return fresh;
        }

        return Read();
    }

    /// <summary>The saved settings, or null when the file does not exist. Creates nothing.</summary>
    public LauncherSettings? TryLoad() => File.Exists(path) ? Read() : null;

    private LauncherSettings Read()
    {
        try
        {
            // ReadWrite | Delete sharing keeps the read from blocking writers longer than it must (see ReplaceWithRetry).
            string json;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
                json = reader.ReadToEnd();
            var loaded = JsonSerializer.Deserialize<LauncherSettings>(json, Options)
                ?? throw new JsonException("settings file contains null");
            if (loaded.SchemaVersion >= LauncherSettings.CurrentSchema) return loaded;
            var migrated = Migrate(loaded);
            Save(migrated);
            return migrated;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new SettingsCorruptException(path, e);
        }
    }

    /// <summary>
    /// Schema 1 to 2: the single region becomes one program and one region with the same folder, ports and UUID, so a
    /// running region is adopted with no change to its config.
    /// </summary>
    private static LauncherSettings Migrate(LauncherSettings s)
    {
        var programs = new List<ProgramSettings>();
        var regions = new List<RegionSettings>();
        if (s.LegacyRegion is { } r)
        {
            programs.Add(new ProgramSettings(r.Instance, s.RegionPort, s.RegionConsolePort, r.Physics));
            regions.Add(new RegionSettings(r.Instance, r.Name, r.Uuid, r.X, r.Y, r.Content, 1, s.RegionPort));
        }
        return s with { SchemaVersion = LauncherSettings.CurrentSchema, Programs = programs, Regions = regions, LegacyRegion = null };
    }

    public void Save(LauncherSettings s)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) LauncherPaths.CreateOwnerOnly(dir); // <data>: it holds the secrets

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(fs, s, Options);
            fs.Flush(true);
        }
        try
        {
            ReplaceWithRetry(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        if (!OperatingSystem.IsWindows()) SyncDirectory(dir);
    }

    /// <summary>Best effort: makes the rename itself durable. Errors are ignored.</summary>
    private static void SyncDirectory(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            var fd = Open(dir, 0 /* O_RDONLY */);
            if (fd < 0) return;
            try { Fsync(fd); }
            finally { Close(fd); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("libc", EntryPoint = "open")] private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync")] private static extern int Fsync(int fd);
    [DllImport("libc", EntryPoint = "close")] private static extern int Close(int fd);

    /// <summary>
    /// Atomic replace. On Windows the overwrite fails while another handle has the target open (even with delete
    /// sharing), so a short read elsewhere is waited out: up to ~2 s, then the last error is thrown. Only sharing and
    /// lock violations (HResult 32 and 33) are retried, plus access denied on an existing file: that is how an open
    /// handle on the target shows up on Windows. Anything else (a directory in the way, say) fails at once.
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
            catch (Exception e) when (attempt < 40 && OperatingSystem.IsWindows() && IsSharingError(e, path))
            {
                Thread.Sleep(50);
            }
        }
    }

    private static bool IsSharingError(Exception e, string path) => e switch
    {
        UnauthorizedAccessException => File.Exists(path),
        IOException io => (io.HResult & 0xFFFF) is 32 or 33,
        _ => false,
    };

    private static LauncherSettings NewSettings() => new()
    {
        Db = new DatabaseSecrets(Secrets.NewPassword(), Secrets.NewPassword(), Secrets.NewPassword()),
        Console = new ConsoleSecrets("dg" + Secrets.NewPassword(8), Secrets.NewPassword()),
        MachineId = Secrets.NewMachineId(),
        SetupToken = Secrets.NewPassword(32),
    };
}
