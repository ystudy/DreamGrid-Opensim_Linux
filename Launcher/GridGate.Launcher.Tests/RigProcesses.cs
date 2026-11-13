using System.Diagnostics;
using GridGate.Launcher.Core.Processes;

namespace GridGate.Launcher.Tests;

/// <summary>
/// Ends what a grid test rig left running and removes its folder. Every program a rig starts names the rig's folder on
/// its command line (a stand-in's argument, or OpenSim's -inidirectory), so that is what picks them out: a PID that was
/// reused by someone else's process is never touched.
/// </summary>
internal static class RigProcesses
{
    /// <summary>
    /// Ends every stand-in or dotnet program whose command line names <paramref name="root"/>, and anything the PID files
    /// in <paramref name="pids"/> name that does, then waits for each to exit. Whatever started them should have been
    /// stopped first (a controller's monitor would start a program again).
    /// </summary>
    public static void EndAll(string root, string? pids)
    {
        var candidates = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
                if (IsRigImage(p.ProcessName)) candidates.Add(p.Id);
        }
        if (pids is not null && Directory.Exists(pids))
            foreach (var file in Directory.GetFiles(pids, "*.pid"))
                if (PidFile.Read(file) is { } id) candidates.Add(id);
        candidates.Remove(Environment.ProcessId);

        foreach (var id in candidates)
        {
            if (!ProcessImage.Matches(id, root)) continue;
            try
            {
                using var p = Process.GetProcessById(id);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    /// <summary>Deletes <paramref name="root"/>, trying again for a few seconds while a program that just ended lets go of it.</summary>
    public static void DeleteFolder(string root)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 10) return; // a temp folder left behind is not worth failing a test over
                Thread.Sleep(500);
            }
        }
    }

    // Linux keeps only the first 15 characters of a process name, so "GridGate.FakeChild" can show as "GridGate.FakeCh".
    private static bool IsRigImage(string name) =>
        name.StartsWith("GridGate.FakeCh", StringComparison.OrdinalIgnoreCase) || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
}
