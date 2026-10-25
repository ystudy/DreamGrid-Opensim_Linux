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
    private volatile bool _stopRequested;

    public ProcessState State { get { lock (_gate) return _state; } }
    public int? Pid { get { lock (_gate) return _pid; } }
    public string? LastError { get { lock (_gate) return _lastError; } }
    public event Action<ProcessState>? StateChanged;

    private enum Wait { Healthy, Exited, TimedOut }

    /// <summary>Adopts the live process named by the PID file when its image matches, otherwise starts a new one; returns once healthy, crashed or timed out.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _stopRequested = false;
        SetState(ProcessState.Starting);

        var recorded = PidFile.Read(spec.PidFile);
        if (recorded is { } existing && ProcessImage.Matches(existing, spec.ExpectedImage))
        {
            Attach(existing, process: null);
            await AwaitStartedAsync(adopted: true, ct);
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
            Fail($"could not start {spec.Name}: {e.Message}");
            return;
        }
        Attach(process.Id, process);
        PidFile.Write(spec.PidFile, process.Id);
        await AwaitStartedAsync(adopted: false, ct);
    }

    /// <summary>Asks the child to stop, waits <see cref="ProcessSpec.StopTimeout"/>, then kills its process tree and removes the PID file.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _stopRequested = true;

        if (Pid is null && PidFile.Read(spec.PidFile) is { } recorded && ProcessImage.Matches(recorded, spec.ExpectedImage))
            Attach(recorded, process: null); // stop a child left by an earlier launcher run

        SetState(ProcessState.Stopping);
        try { await spec.GracefulStop(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { /* already gone, or console unreachable: fall through to the wait and kill */ }

        using (var expiry = new CancellationTokenSource(spec.StopTimeout, time))
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token))
        {
            try { await WaitForGoneAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }

        KillTree();
        PidFile.Delete(spec.PidFile);
        lock (_gate) { _pid = null; _process?.Dispose(); _process = null; }
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
                        await WaitForGoneAsync(ct);
                        if (_stopRequested || State != ProcessState.Running) break;
                        RecordExit(ExitDescription());
                        break;

                    case ProcessState.Crashed:
                        int recent;
                        lock (_gate) recent = _failures.Count;
                        await Task.Delay(policy.DelayFor(recent), time, ct);
                        if (_stopRequested) return;
                        await StartAsync(ct);
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

    private void Attach(int pid, Process? process)
    {
        lock (_gate)
        {
            _process?.Dispose();
            _process = process;
            _pid = pid;
        }
    }

    private async Task AwaitStartedAsync(bool adopted, CancellationToken ct)
    {
        switch (await WaitForHealthyAsync(ct))
        {
            case Wait.Healthy:
                SetState(ProcessState.Running);
                break;
            case Wait.Exited:
                RecordExit(ExitDescription() + " during startup");
                break;
            default:
                // A started child that never became healthy is not left running; an adopted one is not ours to kill.
                if (!adopted) KillTree();
                Fail($"{spec.Name} was not healthy within {spec.StartTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s");
                break;
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
        if (process is not null) return !process.HasExited;
        return pid is { } p && ProcessImage.IsAlive(p);
    }

    private async Task WaitForGoneAsync(CancellationToken ct)
    {
        Process? process;
        lock (_gate) process = _process;
        if (process is not null)
        {
            await process.WaitForExitAsync(ct);
            return;
        }
        // Adopted processes are not our children, so there is no exit event: poll the PID.
        var interval = State == ProcessState.Stopping ? StopPollInterval : AdoptedPollInterval;
        while (IsRunning()) await Task.Delay(interval, time, ct);
    }

    private string ExitDescription()
    {
        Process? process;
        lock (_gate) process = _process;
        try
        {
            if (process is { HasExited: true }) return $"{spec.Name} exited with code {process.ExitCode}";
        }
        catch (InvalidOperationException) { }
        return $"{spec.Name} exited";
    }

    private void RecordExit(string reason)
    {
        bool failed;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            _failures.Add(now);
            _failures.RemoveAll(t => now - t > policy.EffectiveWindow);
            failed = _failures.Count >= policy.MaxFailures;
            _lastError = failed
                ? $"{reason}; gave up after {_failures.Count} failures within {policy.EffectiveWindow.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} min"
                : reason;
            _process?.Dispose();
            _process = null;
            _pid = null;
        }
        PidFile.Delete(spec.PidFile);
        SetState(failed ? ProcessState.Failed : ProcessState.Crashed);
    }

    private void Fail(string error)
    {
        lock (_gate) _lastError = error;
        SetState(ProcessState.Failed);
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
            else if (pid is { } p && ProcessImage.Matches(p, spec.ExpectedImage))
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

    private void SetState(ProcessState state)
    {
        lock (_gate)
        {
            if (_state == state) return;
            _state = state;
        }
        StateChanged?.Invoke(state);
    }
}
