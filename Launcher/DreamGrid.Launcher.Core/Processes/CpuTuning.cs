using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Processes;

/// <summary>Pins a running program to some CPU cores and sets its scheduling priority, on Windows and Linux.</summary>
public static class CpuTuning
{
    /// <summary>
    /// Applies <paramref name="cores"/> (already filtered by RegionSettingsRules.UsableCores; empty means every core)
    /// and <paramref name="priority"/> to the process. Throws InvalidOperationException in plain words when the
    /// computer refuses.
    /// </summary>
    public static void Apply(int pid, IReadOnlyList<int> cores, CpuPriority priority)
    {
        try
        {
            if (OperatingSystem.IsWindows()) ApplyWindows(pid, cores, priority);
            else if (OperatingSystem.IsLinux()) LinuxCpu.Apply(pid, cores, priority);
            else throw Refused("CPU settings are not supported on this system");
        }
        catch (Exception e) when (e is Win32Exception or ArgumentException or NotSupportedException)
        {
            throw Refused(e.Message);
        }
    }

    /// <summary>What the process has now (on Linux, read from the main thread), or null when it cannot be read.</summary>
    public static (IReadOnlyList<int> Cores, CpuPriority Priority)? Read(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ReadWindows(pid);
            if (OperatingSystem.IsLinux()) return LinuxCpu.Read(pid);
        }
        catch (Exception e) when (e is Win32Exception or ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
        }
        return null;
    }

    internal static InvalidOperationException Refused(string reason) =>
        new($"This computer refused the priority: {reason.TrimEnd('.')}.");

    private static long AllCoresMask() => (1L << Math.Min(Environment.ProcessorCount, 63)) - 1;

    private static void ApplyWindows(int pid, IReadOnlyList<int> cores, CpuPriority priority)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            long mask = 0;
            foreach (var core in cores)
            {
                if (core is >= 0 and < 63) mask |= 1L << core;
            }
            if (mask == 0) mask = AllCoresMask();
            process.ProcessorAffinity = (IntPtr)mask;
            process.PriorityClass = priority switch
            {
                CpuPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
                CpuPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
                CpuPriority.High => ProcessPriorityClass.High,
                CpuPriority.RealTime => ProcessPriorityClass.RealTime,
                _ => ProcessPriorityClass.Normal,
            };
        }
        catch (InvalidOperationException e)
        {
            throw Refused(e.Message); // the process ended
        }
    }

    private static (IReadOnlyList<int> Cores, CpuPriority Priority)? ReadWindows(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var process = Process.GetProcessById(pid);
        var mask = (long)process.ProcessorAffinity;
        var cores = new List<int>();
        for (var i = 0; i < 64; i++)
        {
            if ((mask & (1L << i)) != 0) cores.Add(i);
        }
        var priority = process.PriorityClass switch
        {
            ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal => CpuPriority.BelowNormal,
            ProcessPriorityClass.AboveNormal => CpuPriority.AboveNormal,
            ProcessPriorityClass.High => CpuPriority.High,
            ProcessPriorityClass.RealTime => CpuPriority.RealTime,
            _ => CpuPriority.Normal,
        };
        return (cores, priority);
    }

    /// <summary>The Linux side: libc sched_setaffinity and setpriority on every thread of the process.</summary>
    internal static class LinuxCpu
    {
        private const int PrioProcess = 0;
        private const int Esrch = 3;
        private const int MaskBytes = 128; // 1024 CPUs, the size of glibc cpu_set_t

        [DllImport("libc", SetLastError = true)]
        private static extern int sched_setaffinity(int pid, nuint size, byte[] mask);

        [DllImport("libc", SetLastError = true)]
        private static extern int sched_getaffinity(int pid, nuint size, byte[] mask);

        [DllImport("libc", SetLastError = true)]
        private static extern int setpriority(int which, uint who, int prio);

        internal static int Nice(CpuPriority priority) => priority switch
        {
            CpuPriority.BelowNormal => 10,
            CpuPriority.AboveNormal => -5,
            CpuPriority.High => -10,
            CpuPriority.RealTime => -20,
            _ => 0,
        };

        internal static CpuPriority FromNice(int nice) => nice switch
        {
            > 0 => CpuPriority.BelowNormal,
            0 => CpuPriority.Normal,
            >= -7 => CpuPriority.AboveNormal,
            >= -15 => CpuPriority.High,
            _ => CpuPriority.RealTime,
        };

        public static void Apply(int pid, IReadOnlyList<int> cores, CpuPriority priority)
        {
            if (!OperatingSystem.IsLinux()) return;
            var mask = new byte[MaskBytes];
            foreach (var core in cores)
            {
                if (core >= 0 && core < MaskBytes * 8) mask[core / 8] |= (byte)(1 << (core % 8));
            }
            if (mask.All(b => b == 0))
            {
                for (var i = 0; i < Math.Min(Environment.ProcessorCount, MaskBytes * 8); i++) mask[i / 8] |= (byte)(1 << (i % 8));
            }
            var nice = Nice(priority);

            int[] threads;
            try
            {
                threads = Directory.GetDirectories($"/proc/{pid}/task")
                    .Select(d => int.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : 0)
                    .Where(t => t > 0).ToArray();
            }
            catch (Exception e) when (e is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            {
                throw Refused(new Win32Exception(Esrch).Message);
            }
            if (threads.Length == 0) throw Refused(new Win32Exception(Esrch).Message);

            foreach (var tid in threads)
            {
                if (sched_setaffinity(tid, MaskBytes, mask) != 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == Esrch) continue; // the thread ended in the middle
                    throw Refused(new Win32Exception(errno).Message);
                }
                if (setpriority(PrioProcess, (uint)tid, nice) != 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == Esrch) continue;
                    throw Refused(new Win32Exception(errno).Message);
                }
            }
        }

        public static (IReadOnlyList<int> Cores, CpuPriority Priority)? Read(int pid)
        {
            if (!OperatingSystem.IsLinux()) return null;
            var mask = new byte[MaskBytes];
            if (sched_getaffinity(pid, MaskBytes, mask) != 0) return null;
            var cores = new List<int>();
            for (var i = 0; i < MaskBytes * 8; i++)
            {
                if ((mask[i / 8] & (1 << (i % 8))) != 0) cores.Add(i);
            }
            // /proc/<pid>/stat is "pid (comm) state ..."; comm may hold spaces and parentheses, so split after the last ')'.
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            if (fields.Length < 17 || !int.TryParse(fields[16], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var nice)) return null;
            return (cores, FromNice(nice));
        }
    }
}
