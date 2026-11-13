using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace GridGate.Launcher.Core;

/// <summary>
/// Writes a file so a reader sees the old content or the new, never half of it: a temp file next to it (owner-only on
/// Unix), flushed to disk, then renamed over the target, then the directory is synced. The directory must exist.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            fs.Write(new UTF8Encoding(false).GetBytes(text));
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

    /// <summary>Creates the folder when the path has one, then <see cref="WriteAllText"/>.</summary>
    public static void WriteAllTextCreatingFolder(string path, string text)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        WriteAllText(path, text);
    }

    /// <summary>Reads the whole file while writers may have it open (ReadWrite | Delete sharing). A missing file or folder throws.</summary>
    public static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    /// <summary>Moves a damaged file to <c>path.damaged-&lt;utc yyyyMMddHHmmss&gt;</c>, replacing an older one, and returns that path. A failed move throws.</summary>
    public static string SetAside(string path)
    {
        var aside = path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        File.Move(path, aside, overwrite: true);
        return aside;
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
}
