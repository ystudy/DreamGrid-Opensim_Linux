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
