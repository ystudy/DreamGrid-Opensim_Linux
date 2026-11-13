using System.Diagnostics;
using System.Reflection;
using GridGate.Launcher.Core.Processes;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The clean-up the grid test rigs run when a test ends, passed or not: a stand-in left running would serve until the
/// machine restarts and keep the rig's folder from being deleted.
/// </summary>
public sealed class RigProcessesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gg-rig-" + Guid.NewGuid().ToString("N"));
    private readonly string _other = Path.Combine(Path.GetTempPath(), "gg-rig-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = [];

    public RigProcessesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "pids"));
        Directory.CreateDirectory(_other);
    }

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { }
            p.Dispose();
        }
        foreach (var dir in new[] { _root, _other })
            try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public void A_stand_in_naming_the_folder_is_ended()
    {
        var fake = StandIn(Path.Combine(_root, "runtime", "OpenSim.dll"));

        RigProcesses.EndAll(_root, pids: null);

        Assert.True(fake.WaitForExit(5000), "the stand-in still runs");
    }

    [Fact]
    public void A_stand_in_whose_pid_file_was_written_over_is_still_ended()
    {
        // What a restart does: the PID file names the new program, the old one keeps running.
        var old = StandIn(Path.Combine(_root, "runtime", "OpenSim.dll"));
        PidFile.Write(Path.Combine(_root, "pids", "Home.pid"), Environment.ProcessId);

        RigProcesses.EndAll(_root, Path.Combine(_root, "pids"));

        Assert.True(old.WaitForExit(5000), "the stand-in still runs");
    }

    [Fact]
    public void A_process_that_does_not_name_the_folder_is_left_alone()
    {
        var someoneElses = StandIn(Path.Combine(_other, "OpenSim.dll"));
        PidFile.Write(Path.Combine(_root, "pids", "Home.pid"), someoneElses.Id); // a PID file naming a process that isn't ours

        RigProcesses.EndAll(_root, Path.Combine(_root, "pids"));

        Assert.False(someoneElses.WaitForExit(500), "a process outside the rig was ended");
        Assert.False(Process.GetCurrentProcess().HasExited);
    }

    [Fact]
    public void The_folder_a_stand_in_worked_in_can_be_deleted_once_it_is_ended()
    {
        // The controller starts OpenSim with the staged runtime as its working folder; on Windows that folder can't be
        // deleted while the program runs.
        var work = Path.Combine(_root, "runtime", "opensim");
        Directory.CreateDirectory(work);
        StandIn(Path.Combine(work, "OpenSim.dll"), work);

        RigProcesses.EndAll(_root, Path.Combine(_root, "pids"));
        RigProcesses.DeleteFolder(_root);

        Assert.False(Directory.Exists(_root));
    }

    private Process StandIn(string named, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(FakeChildExe()) { UseShellExecute = false };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        psi.ArgumentList.Add("--serve");
        psi.ArgumentList.Add(TestPorts.Free(1)[0].ToString());
        psi.ArgumentList.Add(named);
        var p = Process.Start(psi)!;
        _started.Add(p);
        Assert.False(p.WaitForExit(300), "the stand-in did not start");
        return p;
    }

    private static string FakeChildExe()
    {
        var dir = typeof(RigProcessesTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "FakeChildDir").Value!;
        return Path.Combine(dir, OperatingSystem.IsWindows() ? "GridGate.FakeChild.exe" : "GridGate.FakeChild");
    }
}
