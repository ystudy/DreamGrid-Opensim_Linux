using System.Diagnostics;
using DreamGrid.Launcher.Core.Processes;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class PidFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-pid-" + Guid.NewGuid().ToString("N"));

    public PidFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static DateTime ThisProcessStartUtc()
    {
        using var self = Process.GetCurrentProcess();
        return self.StartTime.ToUniversalTime();
    }

    [Fact]
    public void ReadLive_accepts_when_sidecar_matches_even_if_file_time_is_far_off()
    {
        var file = Path.Combine(_dir, "live.pid");
        PidFile.Write(file, Environment.ProcessId);
        PidFile.WriteStart(file, Environment.ProcessId);
        File.SetLastWriteTimeUtc(file, ThisProcessStartUtc().AddHours(-1));

        Assert.Equal(Environment.ProcessId, PidFile.ReadLive(file, Environment.ProcessPath));
    }

    [Fact]
    public void ReadLive_rejects_when_sidecar_differs()
    {
        var file = Path.Combine(_dir, "reused.pid");
        PidFile.Write(file, Environment.ProcessId);
        File.WriteAllText(file + ".start", "1");

        Assert.Null(PidFile.ReadLive(file, Environment.ProcessPath));
    }

    [Fact]
    public void ReadLive_accepts_on_image_match_without_sidecar()
    {
        var file = Path.Combine(_dir, "old.pid");
        PidFile.Write(file, Environment.ProcessId);

        Assert.Equal(Environment.ProcessId, PidFile.ReadLive(file, Environment.ProcessPath));
    }

    [Fact]
    public void Delete_removes_the_sidecar_too()
    {
        var file = Path.Combine(_dir, "gone.pid");
        PidFile.Write(file, Environment.ProcessId);
        PidFile.WriteStart(file, Environment.ProcessId);
        Assert.True(File.Exists(file + ".start"));

        PidFile.Delete(file);

        Assert.False(File.Exists(file));
        Assert.False(File.Exists(file + ".start"));
    }

    [Fact]
    public void StartId_is_stable_for_a_process()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsWindows(), "no start identity on this platform");
        var first = ProcessImage.StartId(Environment.ProcessId);
        Assert.NotNull(first);
        Assert.Equal(first, ProcessImage.StartId(Environment.ProcessId));
    }

    [Fact]
    public void Children_do_not_inherit_service_manager_variables()
    {
        var names = new[] { "NOTIFY_SOCKET", "LISTEN_FDS", "LISTEN_PID", "LISTEN_FDNAMES" };
        foreach (var n in names) Environment.SetEnvironmentVariable(n, "x");
        try
        {
            var psi = new ProcessStartInfo("anything");
            Assert.True(psi.Environment.ContainsKey("NOTIFY_SOCKET"));

            ProcessImage.WithoutServiceManagerEnvironment(psi);

            foreach (var n in names) Assert.False(psi.Environment.ContainsKey(n), n);
        }
        finally
        {
            foreach (var n in names) Environment.SetEnvironmentVariable(n, null);
        }
    }

    [Fact]
    public void ReadLive_rejects_a_different_image()
    {
        var file = Path.Combine(_dir, "other.pid");
        PidFile.Write(file, Environment.ProcessId);

        Assert.Null(PidFile.ReadLive(file, "no-such-program-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void ReadLive_missing_file_is_null() =>
        Assert.Null(PidFile.ReadLive(Path.Combine(_dir, "missing.pid"), Environment.ProcessPath));
}
