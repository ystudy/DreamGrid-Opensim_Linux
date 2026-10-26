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
    public void ReadLive_rejects_process_started_after_file_was_written()
    {
        var file = Path.Combine(_dir, "stale.pid");
        PidFile.Write(file, Environment.ProcessId);
        File.SetLastWriteTimeUtc(file, ThisProcessStartUtc().AddHours(-1));

        Assert.Null(PidFile.ReadLive(file, Environment.ProcessPath));
    }

    [Fact]
    public void ReadLive_accepts_process_started_before_file_was_written()
    {
        var file = Path.Combine(_dir, "live.pid");
        PidFile.Write(file, Environment.ProcessId);
        File.SetLastWriteTimeUtc(file, ThisProcessStartUtc().AddSeconds(1));

        Assert.Equal(Environment.ProcessId, PidFile.ReadLive(file, Environment.ProcessPath));
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
