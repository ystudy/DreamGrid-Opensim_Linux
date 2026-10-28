using System.Diagnostics;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class CpuTuningTests : IDisposable
{
    private readonly List<Process> _children = [];

    private Process Start()
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 60 127.0.0.1")
            : new ProcessStartInfo("sleep", "60");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        var p = Process.Start(info)!;
        _children.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _children)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            p.Dispose();
        }
    }

    [Fact]
    public void Applies_cores_to_a_running_process()
    {
        Assert.SkipWhen(Environment.ProcessorCount == 1, "One core only");
        var p = Start();
        CpuTuning.Apply(p.Id, [0], CpuPriority.Normal);
        Assert.Equal([0], CpuTuning.Read(p.Id)!.Value.Cores);
    }

    [Fact]
    public void Empty_cores_means_every_core()
    {
        var fresh = Start(); // a fresh child gets the default: every core this program may use
        var p = Start();
        CpuTuning.Apply(p.Id, [0], CpuPriority.Normal);
        CpuTuning.Apply(p.Id, [], CpuPriority.Normal);
        Assert.Equal(CpuTuning.Read(fresh.Id)!.Value.Cores, CpuTuning.Read(p.Id)!.Value.Cores);
    }

    [Fact]
    public void Below_normal_is_applied()
    {
        var p = Start();
        CpuTuning.Apply(p.Id, [], CpuPriority.BelowNormal);
        Assert.Equal(CpuPriority.BelowNormal, CpuTuning.Read(p.Id)!.Value.Priority);
    }

    [Fact]
    public void Applies_to_every_thread_on_linux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        var p = Start();
        CpuTuning.Apply(p.Id, [], CpuPriority.BelowNormal);
        var stats = Directory.GetDirectories($"/proc/{p.Id}/task").Select(d => File.ReadAllText(Path.Combine(d, "stat"))).ToList();
        Assert.NotEmpty(stats);
        foreach (var stat in stats)
        {
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            Assert.Equal("10", fields[16]); // field 19 overall
        }
    }

    [Theory]
    [InlineData("1234 (sleep) S 1 1234 1234 0 -1 4194304 100 0 0 0 0 0 0 0 20 0 1 0 5 1000 50 18446744073709551615", 0)]
    [InlineData("1234 (a) b) S 1 1234 1234 0 -1 4194304 100 0 0 0 0 0 0 0 20 10 1 0 5 1000 50 18446744073709551615", 10)]
    [InlineData("1234 (x) S 1 1234 1234 0 -1 4194304 100 0 0 0 0 0 0 0 -100 -20 1 0 5 1000 50 1", -20)]
    public void Nice_is_read_after_the_last_bracket(string stat, int nice)
        => Assert.Equal(nice, CpuTuning.LinuxCpu.ParseNice(stat));

    [Fact]
    public void A_process_that_exited_throws_plain_words()
    {
        var p = Start();
        p.Kill();
        p.WaitForExit();
        var e = Assert.Throws<InvalidOperationException>(() => CpuTuning.Apply(p.Id, [0], CpuPriority.BelowNormal));
        Assert.Contains("This computer refused", e.Message);
    }
}
