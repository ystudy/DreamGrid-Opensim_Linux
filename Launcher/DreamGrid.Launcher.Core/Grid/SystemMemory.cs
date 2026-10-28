using System.Globalization;
using System.Runtime.InteropServices;

namespace DreamGrid.Launcher.Core.Grid;

/// <summary>How much memory this computer has free, for the add-region form's advice.</summary>
public static class SystemMemory
{
    /// <summary>Memory available to new programs, or null when it can't be read (or on a system we don't read).</summary>
    public static long? AvailableBytes()
    {
        try
        {
            if (OperatingSystem.IsLinux()) return ParseMemInfo(File.ReadAllText("/proc/meminfo"));
            if (OperatingSystem.IsWindows()) return WindowsAvailable();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>The <c>MemAvailable</c> line of <c>/proc/meminfo</c> (in kB) as bytes; null when absent or unreadable.</summary>
    internal static long? ParseMemInfo(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kb))
                return kb * 1024;
        }
        return null;
    }

    private static long? WindowsAvailable()
    {
        var info = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref info) ? (long)info.ullAvailPhys : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
