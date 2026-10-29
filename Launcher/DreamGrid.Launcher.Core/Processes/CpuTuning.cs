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
        new($"This computer refused these CPU settings: {reason.TrimEnd('.')}.");

    /// <summary>Every core the system has, from the system affinity mask (not the launcher's own, which may be narrower).</summary>
    private static long SystemMask()
    {
        if (OperatingSystem.IsWindows())
        {
            using var self = Process.GetCurrentProcess();
            if (GetProcessAffinityMask(self.Handle, out _, out var system) && (long)system != 0) return (long)system;
        }
        return (1L << Math.Min(Environment.ProcessorCount, 63)) - 1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);

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
            if (mask == 0) mask = SystemMask();
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
                Array.Fill(mask, (byte)0xFF); // every bit: the kernel intersects with the CPUs this program may use
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

            // Affinity on every thread first, then priority, so a refused priority never leaves a mix of cores.
            var alive = new List<int>();
            foreach (var tid in threads)
            {
                if (sched_setaffinity(tid, MaskBytes, mask) != 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == Esrch) continue; // the thread ended in the middle
                    throw Refused(new Win32Exception(errno).Message);
                }
                alive.Add(tid);
            }
            foreach (var tid in alive)
            {
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
            if (ParseNice(File.ReadAllText($"/proc/{pid}/stat")) is not { } nice) return null;
            return (cores, FromNice(nice));
        }

        /// <summary>The nice value (field 19) of a /proc stat line, or null when the line is malformed.</summary>
        internal static int? ParseNice(string stat)
        {
            var close = stat.LastIndexOf(')');
            if (close < 0 || close + 2 > stat.Length) return null;
            var fields = stat[(close + 2)..].Split(' ');
            return fields.Length >= 17 && int.TryParse(fields[16], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var nice) ? nice : null;
        }
    }
}
