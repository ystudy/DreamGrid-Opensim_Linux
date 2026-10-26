using System.Diagnostics;
using System.Globalization;

namespace DreamGrid.Launcher.Core.Processes;

/// <summary>
/// One supervised child (Robust, a region, MariaDB). The child is started detached and recorded in a PID file, so a
/// launcher that exits or restarts finds it again and adopts it instead of starting a second copy (spec 4.12).
/// Cancelling <see cref="MonitorAsync"/> only stops watching; it never stops the child.
/// </summary>
public sealed class ManagedProcess(ProcessSpec spec, RestartPolicy policy, TimeProvider time)
{
    private const string SetsidPath = "/usr/bin/setsid";
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AdoptedPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private readonly List<DateTimeOffset> _failures = [];
    private ProcessState _state = ProcessState.Stopped;
    private int? _pid;
    private string? _lastError;
    private Process? _process;          // set only for a child this instance started; null when adopted
    // A Process is disposed only once nobody uses it: not the current child, not awaited by the monitor (a disposed
    // Process's WaitForExitAsync never completes) and not being stopped by StopAsync.
    private Process? _watched;
    private Process? _stopping;
    // Bumped by every StartAsync and StopAsync: a start, or a monitor's watch, changes state only while its epoch is current.
    private long _epoch;
    private volatile bool _stopRequested;

    public ProcessState State { get { lock (_gate) return _state; } }
    public int? Pid { get { lock (_gate) return _pid; } }
    public string? LastError { get { lock (_gate) return _lastError; } }
    public event Action<ProcessState>? StateChanged;

    private enum Wait { Healthy, Exited, TimedOut }

    /// <summary>A child one start launched, with its PID read while the Process was certainly not disposed.</summary>
    private sealed record Launched(Process Process, int Pid);

    /// <summary>Adopts the live process named by the PID file when its image matches, otherwise starts a new one; returns once healthy, crashed or timed out.</summary>
    public Task StartAsync(CancellationToken ct)
    {
        // An explicit start is a fresh request: it cancels an earlier stop and forgets earlier failures.
        lock (_gate) { _stopRequested = false; _failures.Clear(); _epoch++; }
        return StartCoreAsync(honorStop: false, ct);
    }

    // The monitor restarts through here with honorStop, so a StopAsync that lands during the backoff is never overridden.
    private async Task StartCoreAsync(bool honorStop, CancellationToken ct)
    {
        long epoch;
        lock (_gate)
        {
            if (honorStop && _stopRequested) return;
            epoch = _epoch;
            _state = ProcessState.Starting;
        }
        RaiseStateChanged(ProcessState.Starting);

        var recorded = PidFile.Read(spec.PidFile);
        if (PidFile.ReadLive(spec.PidFile, spec.ExpectedImage) is { } existing)
        {
            // The child we already track (a second start of a running child) keeps its Process, which the monitor
            // may be awaiting; any other live match is adopted.
            lock (_gate)
                if (_epoch == epoch && _pid != existing) Attach(existing, process: null);
            await AwaitStartedAsync(epoch, launched: null, ct);
            return;
        }
        if (recorded is not null) PidFile.Delete(spec.PidFile); // stale: dead, or reused by an unrelated program

        Process process;
        try
        {
            process = Launch();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Fail($"could not start {spec.Name}: {e.Message}", epoch);
            return;
        }
        var launched = new Launched(process, process.Id);
        bool current;
        lock (_gate)
        {
            current = _epoch == epoch;
            if (current)
            {
                Attach(launched.Pid, process);
                PidFile.Write(spec.PidFile, launched.Pid);
            }
        }
        if (!current)
        {
            // A stop (or a newer start) came in while this one launched: the child is nobody's.
            KillLaunched(process);
            process.Dispose();
            return;
        }
        await AwaitStartedAsync(epoch, launched, ct);
    }

    /// <summary>Asks the child to stop, waits <see cref="ProcessSpec.StopTimeout"/>, then kills its process tree and removes the PID file.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        lock (_gate) { _stopRequested = true; _epoch++; }

        if (Pid is null && PidFile.ReadLive(spec.PidFile, spec.ExpectedImage) is { } recorded)
            lock (_gate) Attach(recorded, process: null); // stop a child left by an earlier launcher run

        lock (_gate) _stopping = _process;
        try
        {
            SetState(ProcessState.Stopping);
            try { await spec.GracefulStop(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { /* already gone, or console unreachable: fall through to the wait and kill */ }

            using (var expiry = new CancellationTokenSource(spec.StopTimeout, time))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token))
            {
                Process? process;
                int? pid;
                lock (_gate) { process = _process; pid = _pid; }
                try { await WaitForGoneAsync(process, pid, StopPollInterval, timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }

            KillTree();
            PidFile.Delete(spec.PidFile);
            lock (_gate)
            {
                var process = _process;
                _pid = null;
                _process = null;
                DisposeIfUnused(process);
            }
        }
        finally
        {
            lock (_gate)
            {
                var held = _stopping;
                _stopping = null;
                DisposeIfUnused(held);
            }
        }
        SetState(ProcessState.Stopped);
    }

    /// <summary>Watches the child and restarts it per policy. Returns when cancelled (child untouched), stopped, or Failed.</summary>
    public async Task MonitorAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                switch (State)
                {
                    case ProcessState.Running:
                        if (!await WatchAsync(ct)) return;
                        break;

                    case ProcessState.Crashed:
                        int recent;
                        lock (_gate) recent = _failures.Count;
                        await Task.Delay(policy.DelayFor(recent), time, ct);
                        if (_stopRequested) return;
                        await StartCoreAsync(honorStop: true, ct);
                        break;

                    case ProcessState.Starting:
                    case ProcessState.Stopping:
                        await Task.Delay(HealthPollInterval, time, ct);
                        break;

                    default: // Stopped, Failed
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown of the launcher: the child keeps running.
        }
    }

    /// <summary>
    /// The monitor's Running case: waits for the child to go and records the exit. False when the monitor should end
    /// (a stop is under way, or there is nothing to watch). A start or stop that came in meanwhile owns the state, so
    /// the exit is then left to the next round of the loop.
    /// </summary>
    private async Task<bool> WatchAsync(CancellationToken ct)
    {
        long epoch;
        Process? watched;
        int? pid;
        lock (_gate) { epoch = _epoch; watched = _process; pid = _pid; _watched = watched; }
        try
        {
            if (_stopRequested || pid is null) return false;
            await WaitForGoneAsync(watched, pid, AdoptedPollInterval, ct);
            if (_stopRequested) return false;
            RecordExit(ExitDescription(watched), epoch, ProcessState.Running);
            return true;
        }
        finally
        {
            lock (_gate)
            {
                _watched = null;
                DisposeIfUnused(watched);
            }
        }
    }

    private Process Launch()
    {
        var info = spec.StartInfo();
        info.UseShellExecute = false;
        info.RedirectStandardInput = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.CreateNoWindow = true;

        // setsid without --fork execs the program in place when we are not a group leader, so the PID we get is the
        // program's own; the program then lives in its own session and does not share the launcher's process group.
        if (OperatingSystem.IsLinux() && File.Exists(SetsidPath))
        {
            var program = info.FileName;
            if (string.IsNullOrEmpty(info.Arguments))
            {
                info.ArgumentList.Insert(0, program);
            }
            else
            {
                info.Arguments = $"\"{program}\" {info.Arguments}";
            }
            info.FileName = SetsidPath;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"{spec.Name} did not start");
        // Output is read and thrown away so a full pipe can never block the child while we are alive.
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void Attach(int pid, Process? process)
    {
        var old = _process;
        _process = process;
        _pid = pid;
        DisposeIfUnused(old);
    }

    /// <summary>Caller holds <see cref="_gate"/>. Disposes a Process that nobody uses any more.</summary>
    private void DisposeIfUnused(Process? process)
    {
        if (process is null || ReferenceEquals(process, _process) || ReferenceEquals(process, _watched) || ReferenceEquals(process, _stopping))
            return;
        process.Dispose();
    }

    private async Task AwaitStartedAsync(long epoch, Launched? launched, CancellationToken ct)
    {
        var outcome = await WaitForHealthyAsync(ct);
        bool current;
        lock (_gate) current = _epoch == epoch;
        if (!current)
        {
            // A StopAsync (or a newer start) took over while this one waited, so the state is theirs. A child this
            // start launched is killed unless the newer start kept it as its own.
            if (launched is not null) DropLaunched(launched, onlyIfOrphan: true);
            return;
        }

        switch (outcome)
        {
            case Wait.Healthy:
                SetState(ProcessState.Running, epoch);
                break;
            case Wait.Exited:
                RecordExit(ExitDescription(launched?.Process) + " during startup", epoch, ProcessState.Starting);
                break;
            default:
                // A started child that never became healthy is not left running; an adopted one is not ours to kill.
                if (launched is not null) DropLaunched(launched, onlyIfOrphan: false);
                Fail($"{spec.Name} was not healthy within {spec.StartTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s", epoch);
                break;
        }
    }

    /// <summary>Kills a child this instance launched, and forgets it (and its PID file) if it is still the current child.</summary>
    private void DropLaunched(Launched launched, bool onlyIfOrphan)
    {
        lock (_gate)
            if (onlyIfOrphan && ReferenceEquals(_process, launched.Process)) return;
        KillLaunched(launched.Process);
        lock (_gate)
        {
            if (ReferenceEquals(_process, launched.Process)) { _process = null; _pid = null; }
            if (PidFile.Read(spec.PidFile) == launched.Pid) PidFile.Delete(spec.PidFile);
            DisposeIfUnused(launched.Process);
        }
    }

    private static void KillLaunched(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or already disposed by the stop that took over.
        }
    }

    private async Task<Wait> WaitForHealthyAsync(CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + spec.StartTimeout;
        while (true)
        {
            if (!IsRunning()) return Wait.Exited;
            bool healthy;
            try { healthy = await spec.IsHealthy(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { healthy = false; }
            if (healthy) return Wait.Healthy;
            if (time.GetUtcNow() >= deadline) return Wait.TimedOut;
            await Task.Delay(HealthPollInterval, time, ct);
        }
    }

    private bool IsRunning()
    {
        Process? process;
        int? pid;
        lock (_gate) { process = _process; pid = _pid; }
        try
        {
            if (process is not null) return !process.HasExited;
        }
        catch (InvalidOperationException) { return false; } // disposed by a stop that took over
        return pid is { } p && ProcessImage.IsAlive(p);
    }

    private async Task WaitForGoneAsync(Process? process, int? pid, TimeSpan pollInterval, CancellationToken ct)
    {
        if (process is not null)
        {
            await process.WaitForExitAsync(ct);
            return;
        }
        // Adopted processes are not our children, so there is no exit event: poll the PID.
        while (pid is { } p && ProcessImage.IsAlive(p)) await Task.Delay(pollInterval, time, ct);
    }

    private string ExitDescription(Process? process)
    {
        try
        {
            if (process is { HasExited: true }) return $"{spec.Name} exited with code {process.ExitCode}";
        }
        catch (InvalidOperationException) { }
        return $"{spec.Name} exited";
    }

    /// <summary>Records a crash, only while <paramref name="epoch"/> is current and the state is still <paramref name="expected"/>.</summary>
    private void RecordExit(string reason, long epoch, ProcessState expected)
    {
        bool failed;
        lock (_gate)
        {
            if (_epoch != epoch || _state != expected) return;
            var now = time.GetUtcNow();
            _failures.Add(now);
            _failures.RemoveAll(t => now - t > policy.EffectiveWindow);
            failed = _failures.Count >= policy.MaxFailures;
            _lastError = failed
                ? $"{reason}; gave up after {_failures.Count} failures within {policy.EffectiveWindow.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} min"
                : reason;
            var process = _process;
            _process = null;
            _pid = null;
            DisposeIfUnused(process);
            PidFile.Delete(spec.PidFile);
        }
        SetState(failed ? ProcessState.Failed : ProcessState.Crashed, epoch);
    }

    private void Fail(string error, long epoch)
    {
        lock (_gate)
        {
            if (_epoch != epoch) return;
            _lastError = error;
        }
        SetState(ProcessState.Failed, epoch);
    }

    private void KillTree()
    {
        Process? process;
        int? pid;
        lock (_gate) { process = _process; pid = _pid; }
        try
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            else if (pid is { } p && PidFile.ReadLive(spec.PidFile, spec.ExpectedImage) == p)
            {
                using var adopted = Process.GetProcessById(p);
                adopted.Kill(entireProcessTree: true);
                adopted.WaitForExit(5000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // It exited between the check and the kill.
        }
    }

    /// <summary>Sets the state; with <paramref name="epoch"/>, only while that epoch is current (a newer start or a stop owns it otherwise).</summary>
    private void SetState(ProcessState state, long? epoch = null)
    {
        lock (_gate)
        {
            if (epoch is { } e && _epoch != e) return;
            if (_state == state) return;
            _state = state;
        }
        RaiseStateChanged(state);
    }

    private void RaiseStateChanged(ProcessState state)
    {
        try { StateChanged?.Invoke(state); }
        catch (Exception) { /* a faulty subscriber must not break supervision */ }
    }
}
