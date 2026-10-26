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

    /// <summary>The sidecar holding the start identity of the process the launcher recorded in <paramref name="path"/>.</summary>
    private static string StartPath(string path) => path + ".start";

    /// <summary>
    /// The PID in the file only when it names a live process with <paramref name="expectedImage"/>, and, when the
    /// launcher recorded the process's start identity beside the file, only when that identity still matches (a PID
    /// reused by an unrelated program has another one). The identity does not depend on the wall clock, which steps on
    /// some hosts. Without a sidecar (an older install, or a PID file the child wrote itself) the image match decides.
    /// </summary>
    public static int? ReadLive(string path, string? expectedImage)
    {
        if (Read(path) is not { } pid || !ProcessImage.Matches(pid, expectedImage)) return null;
        string? recorded;
        try
        {
            recorded = File.Exists(StartPath(path)) ? File.ReadAllText(StartPath(path)).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // present but unreadable: never adopt on doubt
        }
        if (recorded is null) return pid;
        return ProcessImage.StartId(pid) is { } live && live == recorded ? pid : null;
    }

    /// <summary>Records the start identity of <paramref name="pid"/> beside the PID file (no sidecar when it cannot be read).</summary>
    public static void WriteStart(string path, int pid)
    {
        var sidecar = StartPath(path);
        CreateFolder(path);
        if (ProcessImage.StartId(pid) is not { } id)
        {
            try { File.Delete(sidecar); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return;
        }
        var temp = sidecar + ".tmp";
        File.WriteAllText(temp, id);
        File.Move(temp, sidecar, overwrite: true);
    }

    public static void Write(string path, int pid)
    {
        CreateFolder(path);
        var temp = path + ".tmp";
        File.WriteAllText(temp, pid.ToString(CultureInfo.InvariantCulture));
        File.Move(temp, path, overwrite: true);
    }

    private static void CreateFolder(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public static void Delete(string path)
    {
        foreach (var file in new[] { path, StartPath(path) })
        {
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
