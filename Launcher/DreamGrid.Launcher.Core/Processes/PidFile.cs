using System.Globalization;

namespace DreamGrid.Launcher.Core.Processes;

/// <summary>A one-line file holding the PID of a supervised child, so a restarted launcher can find it again.</summary>
public static class PidFile
{
    /// <summary>The PID in the file, or null when the file is missing, unreadable or does not hold a positive integer.</summary>
    public static int? Read(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The PID in the file only when it names a live process with <paramref name="expectedImage"/> that started no later
    /// than the file's last write plus a 2 s slack. Every genuine child is started before its PID file is written, so a
    /// process that started later is an unrelated program that reused the PID.
    /// </summary>
    public static int? ReadLive(string path, string? expectedImage)
    {
        if (Read(path) is not { } pid || !ProcessImage.Matches(pid, expectedImage)) return null;
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime() <= written + TimeSpan.FromSeconds(2) ? pid : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null; // gone, or start time unreadable: never adopt on doubt
        }
    }

    public static void Write(string path, int pid)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, pid.ToString(CultureInfo.InvariantCulture));
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
