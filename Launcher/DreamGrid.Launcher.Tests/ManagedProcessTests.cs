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
}
