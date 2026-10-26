using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using DreamGrid.Launcher.Core.Processes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class ManagedProcessTests : IAsyncLifetime
{
    private const string Image = "DreamGrid.FakeChild";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-mp-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _pidFiles = [];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return ValueTask.CompletedTask;
    }

    // Always kill whatever a test left behind, whether it passed or not.
    public ValueTask DisposeAsync()
    {
        foreach (var file in _pidFiles)
        {
            var pid = PidFile.Read(file);
            if (pid is null || !ProcessImage.Matches(pid.Value, Image)) continue;
            try { using var p = Process.GetProcessById(pid.Value); p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private static string FakeChildExe()
    {
        var dir = typeof(ManagedProcessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "FakeChildDir").Value!;
        return Path.Combine(dir, OperatingSystem.IsWindows() ? "DreamGrid.FakeChild.exe" : "DreamGrid.FakeChild");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private string NewPidFile()
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pid");
        _pidFiles.Add(path);
        return path;
    }

    private async Task<bool> HealthAsync(int port, CancellationToken ct)
    {
        try { return (await _http.GetAsync($"http://127.0.0.1:{port}/health", ct)).IsSuccessStatusCode; }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    private sealed class Counters { public int Starts; public int GracefulStops; }

    private ProcessSpec ServeSpec(int port, string pidFile, Counters counters, string? expectedImage = Image) => new(
        "fakechild",
        () =>
        {
            counters.Starts++;
            var psi = new ProcessStartInfo(FakeChildExe());
            psi.ArgumentList.Add("--serve");
            psi.ArgumentList.Add(port.ToString());
            return psi;
        },
        pidFile,
        ct => HealthAsync(port, ct),
        async ct =>
        {
            counters.GracefulStops++;
            await _http.GetAsync($"http://127.0.0.1:{port}/quit", ct);
        },
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(10),
        expectedImage);

    [Fact]
    public async Task Start_writes_pid_and_becomes_Running_when_healthy()
    {
        var port = FreePort();
        var pidFile = NewPidFile();
        var counters = new Counters();
        var mp = new ManagedProcess(ServeSpec(port, pidFile, counters), new RestartPolicy(), TimeProvider.System);
        var seen = new List<ProcessState>();
        mp.StateChanged += seen.Add;

        await mp.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Running, mp.State);
        Assert.NotNull(mp.Pid);
        Assert.Equal(mp.Pid, PidFile.Read(pidFile));
        Assert.Equal(ProcessImage.StartId(mp.Pid!.Value), File.ReadAllText(pidFile + ".start"));
        // setsid is exec'd in place, so the recorded PID is the FakeChild itself and not a wrapper.
        Assert.True(ProcessImage.Matches(mp.Pid!.Value, Image));
        Assert.Contains(ProcessState.Starting, seen);
        Assert.Contains(ProcessState.Running, seen);
        Assert.True(await HealthAsync(port, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stop_uses_graceful_stop_then_deletes_pid_file()
    {
        var port = FreePort();
        var pidFile = NewPidFile();
        var counters = new Counters();
        var mp = new ManagedProcess(ServeSpec(port, pidFile, counters), new RestartPolicy(), TimeProvider.System);
        await mp.StartAsync(TestContext.Current.CancellationToken);
        var pid = mp.Pid!.Value;

        var sw = Stopwatch.StartNew();
        await mp.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, counters.GracefulStops);
        Assert.Equal(ProcessState.Stopped, mp.State);
        Assert.False(File.Exists(pidFile));
        Assert.False(File.Exists(pidFile + ".start"));
        Assert.False(ProcessImage.IsAlive(pid));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), "graceful stop should not have needed the kill timeout");
    }

    [Fact]
    public async Task Adopts_live_process_from_pid_file_instead_of_starting_another()
    {
        var port = FreePort();
        var pidFile = NewPidFile();
        var first = new Counters();
        var firstProcess = new ManagedProcess(ServeSpec(port, pidFile, first), new RestartPolicy(), TimeProvider.System);
        await firstProcess.StartAsync(TestContext.Current.CancellationToken);

        var second = new Counters();
        var secondProcess = new ManagedProcess(ServeSpec(port, pidFile, second), new RestartPolicy(), TimeProvider.System);
        await secondProcess.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, second.Starts);
        Assert.Equal(firstProcess.Pid, secondProcess.Pid);
        Assert.Equal(ProcessState.Running, secondProcess.State);
    }

    [Fact]
    public async Task Stale_pid_of_unrelated_process_is_not_adopted()
    {
        var port = FreePort();
        var pidFile = NewPidFile();
        PidFile.Write(pidFile, Environment.ProcessId);
        var counters = new Counters();
        var mp = new ManagedProcess(ServeSpec(port, pidFile, counters), new RestartPolicy(), TimeProvider.System);

        await mp.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, counters.Starts);
        Assert.NotEqual(Environment.ProcessId, mp.Pid);
        Assert.Equal(mp.Pid, PidFile.Read(pidFile));
        Assert.Equal(ProcessState.Running, mp.State);
    }

    [Fact]
    public async Task Crash_loop_stops_at_MaxFailures_and_reports_Failed()
    {
        var pidFile = NewPidFile();
        var time = new FakeTimeProvider();
        var starts = 0;
        var spec = new ProcessSpec(
            "crasher",
            () =>
            {
                Interlocked.Increment(ref starts);
                var psi = new ProcessStartInfo(FakeChildExe());
                psi.ArgumentList.Add("--crash");
                return psi;
            },
            pidFile,
            _ => Task.FromResult(true),
            _ => Task.CompletedTask,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(5),
            Image);
        var mp = new ManagedProcess(spec, new RestartPolicy(MaxFailures: 3, Window: TimeSpan.FromMinutes(10)), time);
        var ct = TestContext.Current.CancellationToken;

        // The backoff delays run on the fake clock; a pump advances it so the test takes seconds, not minutes.
        using var pumpStop = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            while (!pumpStop.IsCancellationRequested)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(10);
            }
        }, ct);

        await mp.StartAsync(ct);
        var monitor = mp.MonitorAsync(ct);
        var finished = await Task.WhenAny(monitor, Task.Delay(TimeSpan.FromSeconds(60), ct));
        await pumpStop.CancelAsync();
        await pump;

        Assert.Same(monitor, finished);
        await monitor;
        Assert.Equal(ProcessState.Failed, mp.State);
        Assert.False(string.IsNullOrEmpty(mp.LastError));
        Assert.Equal(3, Volatile.Read(ref starts));

        // No fourth start after Failed, even when plenty of time passes.
        time.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(500, ct);
        Assert.Equal(3, Volatile.Read(ref starts));
    }

    [Fact]
    public async Task Cancelling_monitor_leaves_child_running()
    {
        var port = FreePort();
        var pidFile = NewPidFile();
        var mp = new ManagedProcess(ServeSpec(port, pidFile, new Counters()), new RestartPolicy(), TimeProvider.System);
        await mp.StartAsync(TestContext.Current.CancellationToken);
        var pid = mp.Pid!.Value;
        using var cts = new CancellationTokenSource();
        var monitor = mp.MonitorAsync(cts.Token);

        await cts.CancelAsync();
        await monitor.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(ProcessImage.IsAlive(pid));
        Assert.True(await HealthAsync(port, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(pidFile));
    }

    [Fact]
    public async Task Child_survives_exit_of_the_process_that_started_it()
    {
        var port = FreePort();
        var pidFile = NewPidFile();
        var ct = TestContext.Current.CancellationToken;

        // A short-lived "launcher" starts the server through ManagedProcess and exits immediately.
        var psi = new ProcessStartInfo(FakeChildExe()) { UseShellExecute = false };
        psi.ArgumentList.Add("--launch");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add(pidFile);
        using var launcher = Process.Start(psi)!;
        await launcher.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
        Assert.Equal(0, launcher.ExitCode);

        var pid = PidFile.Read(pidFile);
        Assert.NotNull(pid);
        Assert.NotEqual(launcher.Id, pid);

        // Each request makes the child write to stdout and stderr, whose pipes died with the launcher.
        for (var i = 0; i < 5; i++)
        {
            Assert.True(await HealthAsync(port, ct), $"/health request {i} failed after the launcher exited");
            await Task.Delay(200, ct);
        }
        Assert.True(ProcessImage.IsAlive(pid!.Value));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(40, 60)]
    public void DelayFor_doubles_and_caps_with_default_substitution(int failures, int expectedSeconds)
    {
        // default(TimeSpan) values select 2 s initial and 60 s max.
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), new RestartPolicy().DelayFor(failures));
    }

    [Fact]
    public void DelayFor_honours_explicit_delays()
    {
        var policy = new RestartPolicy(InitialDelay: TimeSpan.FromSeconds(1), MaxDelay: TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.DelayFor(3));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayFor(4));
    }

    [Fact]
    public async Task Process_matched_by_command_line_only_is_adopted()
    {
        // Robust and OpenSim run as "dotnet OpenSim.dll": the main module is dotnet, so only the command line can match.
        var port = FreePort();
        var pidFile = NewPidFile();
        var dll = Path.Combine(Path.GetDirectoryName(FakeChildExe())!, "DreamGrid.FakeChild.dll");
        ProcessSpec Spec(Counters c) => ServeSpec(port, pidFile, c, "DreamGrid.FakeChild.dll") with
        {
            StartInfo = () =>
            {
                c.Starts++;
                var psi = new ProcessStartInfo("dotnet");
                psi.ArgumentList.Add(dll);
                psi.ArgumentList.Add("--serve");
                psi.ArgumentList.Add(port.ToString());
                return psi;
            },
        };
        var first = new Counters();
        var firstProcess = new ManagedProcess(Spec(first), new RestartPolicy(), TimeProvider.System);
        await firstProcess.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProcessState.Running, firstProcess.State);
        Assert.True(ProcessImage.Matches(firstProcess.Pid!.Value, "DreamGrid.FakeChild.dll"));
        Assert.False(ProcessImage.Matches(Environment.ProcessId, "DreamGrid.FakeChild.dll"));

        var second = new Counters();
        var secondProcess = new ManagedProcess(Spec(second), new RestartPolicy(), TimeProvider.System);
        await secondProcess.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, second.Starts);
        Assert.Equal(firstProcess.Pid, secondProcess.Pid);
        Assert.Equal(ProcessState.Running, secondProcess.State);
    }

    [Fact]
    public async Task Stop_while_monitoring_completes_the_monitor_cleanly_without_restart()
    {
        var port = FreePort();
        var pidFile = NewPidFile();
        var counters = new Counters();
        var mp = new ManagedProcess(ServeSpec(port, pidFile, counters), new RestartPolicy(), TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;
        await mp.StartAsync(ct);
        var monitor = mp.MonitorAsync(ct);

        await mp.StopAsync(ct);
        await monitor.WaitAsync(TimeSpan.FromSeconds(15), ct); // rethrows a fault such as ObjectDisposedException

        Assert.Equal(ProcessState.Stopped, mp.State);
        Assert.Equal(1, counters.Starts);
    }

    [Fact]
    public async Task Stop_during_restart_backoff_prevents_the_restart()
    {
        var pidFile = NewPidFile();
        var time = new FakeTimeProvider();
        var starts = 0;
        var spec = new ProcessSpec("crasher", () =>
            {
                Interlocked.Increment(ref starts);
                var psi = new ProcessStartInfo(FakeChildExe());
                psi.ArgumentList.Add("--crash");
                return psi;
            },
            pidFile, _ => Task.FromResult(true), _ => Task.CompletedTask,
            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), Image);
        var mp = new ManagedProcess(spec, new RestartPolicy(MaxFailures: 5), time);
        var ct = TestContext.Current.CancellationToken;
        var crashed = new TaskCompletionSource();
        mp.StateChanged += s => { if (s == ProcessState.Crashed) crashed.TrySetResult(); };
        await mp.StartAsync(ct);
        var monitor = mp.MonitorAsync(ct);
        await crashed.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        await mp.StopAsync(ct);
        time.Advance(TimeSpan.FromMinutes(5));
        await monitor.WaitAsync(TimeSpan.FromSeconds(15), ct);

        Assert.Equal(1, Volatile.Read(ref starts));
        Assert.Equal(ProcessState.Stopped, mp.State);
    }

    [Fact]
    public async Task Monitor_detects_death_of_an_adopted_process_and_restarts_it()
    {
        var port = FreePort();
        var pidFile = NewPidFile();
        var owner = new ManagedProcess(ServeSpec(port, pidFile, new Counters()), new RestartPolicy(), TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;
        await owner.StartAsync(ct);
        var oldPid = owner.Pid!.Value;

        var counters = new Counters();
        var adopter = new ManagedProcess(ServeSpec(port, pidFile, counters), new RestartPolicy(InitialDelay: TimeSpan.FromMilliseconds(50)), TimeProvider.System);
        await adopter.StartAsync(ct);
        Assert.Equal(0, counters.Starts);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = adopter.MonitorAsync(cts.Token);
        try
        {
            using (var victim = Process.GetProcessById(oldPid)) { victim.Kill(entireProcessTree: true); victim.WaitForExit(5000); }

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !(counters.Starts == 1 && adopter.State == ProcessState.Running))
                await Task.Delay(100, ct);

            Assert.Equal(1, counters.Starts);
            Assert.Equal(ProcessState.Running, adopter.State);
            Assert.NotEqual(oldPid, adopter.Pid);
        }
        finally
        {
            await cts.CancelAsync();
            await monitor;
        }
    }

    [Fact]
    public async Task Explicit_start_after_Failed_begins_with_a_clean_failure_count()
    {
        var pidFile = NewPidFile();
        var port = FreePort();
        var crash = true;
        var spec = new ProcessSpec("flaky", () =>
            {
                var psi = new ProcessStartInfo(FakeChildExe());
                if (crash) psi.ArgumentList.Add("--crash");
                else { psi.ArgumentList.Add("--serve"); psi.ArgumentList.Add(port.ToString()); }
                return psi;
            },
            pidFile, async ct => !crash && await HealthAsync(port, ct), _ => Task.CompletedTask,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), Image);
        var mp = new ManagedProcess(spec, new RestartPolicy(MaxFailures: 2), TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;

        // Every explicit start begins a fresh failure count, so a crash right after a manual start is failure 1 of 2
        // (Crashed), never accumulated with the earlier manual attempts into Failed.
        await mp.StartAsync(ct);
        Assert.Equal(ProcessState.Crashed, mp.State);
        await mp.StartAsync(ct);
        Assert.Equal(ProcessState.Crashed, mp.State);
        await mp.StartAsync(ct);
        Assert.Equal(ProcessState.Crashed, mp.State);
    }

    [Fact]
    public async Task Start_timeout_of_a_started_child_kills_it_and_clears_pid_state()
    {
        var port = FreePort();
        var pidFile = NewPidFile();
        var time = new FakeTimeProvider();
        var spec = ServeSpec(port, pidFile, new Counters()) with
        {
            IsHealthy = _ => Task.FromResult(false),
            StartTimeout = TimeSpan.FromSeconds(3),
        };
        var mp = new ManagedProcess(spec, new RestartPolicy(), time);
        var ct = TestContext.Current.CancellationToken;
        using var pumpStop = new CancellationTokenSource();
        var pump = Task.Run(async () => { while (!pumpStop.IsCancellationRequested) { time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(10); } }, ct);

        await mp.StartAsync(ct);
        await pumpStop.CancelAsync();
        await pump;

        Assert.Equal(ProcessState.Failed, mp.State);
        Assert.Null(mp.Pid);
        Assert.False(File.Exists(pidFile));
    }

    [Fact]
    public async Task Throwing_StateChanged_handler_does_not_break_the_manager()
    {
        var port = FreePort();
        var mp = new ManagedProcess(ServeSpec(port, NewPidFile(), new Counters()), new RestartPolicy(), TimeProvider.System);
        mp.StateChanged += _ => throw new InvalidOperationException("handler bug");

        await mp.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Running, mp.State);
    }

    [Fact]
    public async Task Second_start_of_a_running_child_keeps_crash_supervision()
    {
        // I1: a Start* command on a running component must not end the monitor's watch of the child.
        var port = FreePort();
        var pidFile = NewPidFile();
        var counters = new Counters();
        var mp = new ManagedProcess(ServeSpec(port, pidFile, counters), new RestartPolicy(InitialDelay: TimeSpan.FromMilliseconds(50)), TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;
        await mp.StartAsync(ct);
        var firstPid = mp.Pid!.Value;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = mp.MonitorAsync(cts.Token);
        try
        {
            await Task.Delay(500, ct); // the monitor is now awaiting the child's exit
            await mp.StartAsync(ct);
            Assert.Equal(1, counters.Starts);
            Assert.Equal(firstPid, mp.Pid);
            Assert.Equal(ProcessState.Running, mp.State);

            using (var victim = Process.GetProcessById(firstPid)) { victim.Kill(entireProcessTree: true); victim.WaitForExit(5000); }

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !(counters.Starts == 2 && mp.State == ProcessState.Running))
                await Task.Delay(100, ct);
            Assert.Equal(2, counters.Starts);
            Assert.Equal(ProcessState.Running, mp.State);
            Assert.NotEqual(firstPid, mp.Pid);
        }
        finally
        {
            await cts.CancelAsync();
            await monitor.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
    }

    [Fact]
    public async Task Stop_during_a_start_health_wait_wins_and_ends_the_monitor()
    {
        // I2: a health check that answers only after StopAsync finished must not bring the state back to Running.
        var port = FreePort();
        var pidFile = NewPidFile();
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spec = ServeSpec(port, pidFile, new Counters()) with
        {
            IsHealthy = async ct =>
            {
                polled.TrySetResult();
                await release.Task.WaitAsync(ct);
                return true;
            },
        };
        var mp = new ManagedProcess(spec, new RestartPolicy(), TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var start = mp.StartAsync(ct);
        var monitor = mp.MonitorAsync(cts.Token);
        try
        {
            await polled.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            await mp.StopAsync(ct);
            release.SetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(15), ct);

            Assert.Equal(ProcessState.Stopped, mp.State);
            Assert.Null(mp.Pid);
            await monitor.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal(ProcessState.Stopped, mp.State);
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    [Fact]
    public async Task A_zombie_is_not_alive()
    {
        // A launcher-started child that outlived its launcher is reparented to PID 1; in a container whose PID 1
        // never reaps (GitHub's "tail -f /dev/null"), its exit leaves a zombie that must count as gone.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "zombies are a Linux /proc state");
        var ct = TestContext.Current.CancellationToken;
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        // "sleep 30" replaces the shell and never waits for the shell's background child.
        psi.ArgumentList.Add("sleep 0.1 & echo $!; exec sleep 30");
        using var parent = Process.Start(psi)!;
        try
        {
            var pid = int.Parse((await parent.StandardOutput.ReadLineAsync(ct))!);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ", StringComparison.Ordinal))
                await Task.Delay(50, ct);
            Assert.Contains(") Z ", File.ReadAllText($"/proc/{pid}/stat"));

            Assert.False(ProcessImage.IsAlive(pid));
        }
        finally
        {
            parent.Kill();
        }
    }
}
