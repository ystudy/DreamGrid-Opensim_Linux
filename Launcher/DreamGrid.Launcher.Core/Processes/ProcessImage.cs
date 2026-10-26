using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DreamGrid.Launcher.Core.Processes;

/// <summary>
/// Decides whether a live PID really is the program we expect, so a stale PID file that now names an unrelated process
/// is never adopted (or killed). Any access problem counts as "no match": we never adopt on doubt.
/// </summary>
public static class ProcessImage
{
    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && !(OperatingSystem.IsLinux() && IsZombie(pid));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A child that outlived the launcher belongs to PID 1. If PID 1 never reaps (a container's "tail -f /dev/null"),
    /// the exited child stays a zombie, which signal 0 (and so HasExited) still reports as alive.
    /// </summary>
    private static bool IsZombie(int pid)
    {
        try
        {
            // "pid (comm) S ...": the state follows the last ')', since comm may itself contain ')'.
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var end = stat.LastIndexOf(')');
            return end >= 0 && end + 2 < stat.Length && stat[end + 2] is 'Z' or 'X';
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return true; // /proc entry gone: the process is gone
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // exists but unreadable (e.g. another user's hidepid /proc): not known to be a zombie
        }
    }

    public static bool Matches(int pid, string? expectedImage)
    {
        if (string.IsNullOrEmpty(expectedImage) || !IsAlive(pid)) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            return OperatingSystem.IsWindows()
                ? WindowsImageContains(pid, expectedImage, comparison)
                : LinuxImageContains(pid, expectedImage, comparison);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool LinuxImageContains(int pid, string expected, StringComparison comparison)
    {
        try
        {
            var cmdline = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
            if (cmdline.Contains(expected, comparison)) return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try
        {
            var exe = File.ResolveLinkTarget($"/proc/{pid}/exe", returnFinalTarget: false)?.FullName;
            if (exe is not null && exe.Contains(expected, comparison)) return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private static bool WindowsImageContains(int pid, string expected, StringComparison comparison)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var module = p.MainModule?.FileName;
            if (module is not null && module.Contains(expected, comparison)) return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        var commandLine = WindowsCommandLine(pid);
        return commandLine is not null && commandLine.Contains(expected, comparison);
    }

    // NtQueryInformationProcess(ProcessCommandLineInformation = 60) needs only PROCESS_QUERY_LIMITED_INFORMATION (Windows 8.1+).
    private const int ProcessCommandLineInformation = 60;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returnLength);

    private static string? WindowsCommandLine(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var size = 1024;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, size, out var needed);
                    if (status == 0)
                    {
                        // UNICODE_STRING { ushort Length; ushort MaximumLength; (padding) IntPtr Buffer }
                        var length = Marshal.ReadInt16(buffer);
                        var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                        return Marshal.PtrToStringUni(text, length / 2);
                    }
                    // STATUS_INFO_LENGTH_MISMATCH / STATUS_BUFFER_OVERFLOW / STATUS_BUFFER_TOO_SMALL
                    if ((uint)status is 0xC0000004 or 0x80000005 or 0xC0000023) size = Math.Max(needed, size * 2);
                    else return null;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return null;
        }
        finally { CloseHandle(handle); }
    }
}
