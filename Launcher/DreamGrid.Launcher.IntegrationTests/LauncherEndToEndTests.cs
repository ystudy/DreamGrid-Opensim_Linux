using System.Diagnostics;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Xunit;
using static DreamGrid.Launcher.IntegrationTests.GridTestSupport;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>
/// Runs the real launcher (<c>dotnet DreamGrid.Launcher.dll --data ... --opensim ... --mariadb-bin ...</c>) as a child
/// process against a pre-seeded settings.json, and checks the port-8001 contract and adoption across launcher kills.
/// </summary>
public sealed class LauncherEndToEndTests : IDisposable
{
    private const string GridRunning = "The grid is running.";
    private const string GridFailed = "The grid did not start";
    private static readonly TimeSpan BootTimeout = TimeSpan.FromMinutes(5);

    private readonly string _root = NewDataDir();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly List<LauncherProcess> _launchers = [];
    private string _lastConsoleReply = "";

    public void Dispose()
    {
        foreach (var l in _launchers) l.Dispose();
        KillEverythingUnder(_root);
        _http.Dispose();
        DeleteDataDir(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Launcher_boots_grid_and_answers_8001()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);

        await RunWithCleanupAsync(paths, store, async () =>
        {
            var launcher = StartLauncher(paths);
            await WaitForAsync(launcher, async () => await GetAsync(s, "") == "Test Completed", "GET / == Test Completed");
            // No password needed; with no region yet the reply is the "no region" answer, not a permission NAK.
            Assert.Equal("NAK No region has been created yet.", await GetAsync(s, "?command=RegionStatus"));
            Assert.StartsWith("NAK You do not have permission", await GetAsync(s, "?command=StopRobust"));

            await WaitForLogAsync(launcher, GridRunning);
            var db = LivePid(paths, "mariadb.pid");
            var robust = LivePid(paths, "robust.pid");
            Assert.True(await NewRobustClient(_http, s).IsReadyAsync(Ct), "Robust does not answer get_grid_info");

            // Kill the launcher only: the children it started live on (spec 4.12).
            launcher.KillOnly();
            Assert.True(ProcessImage.IsAlive(db), "MariaDB died with the launcher");
            Assert.True(ProcessImage.IsAlive(robust), "Robust died with the launcher");

            // A new launcher adopts them, and the MachineId stops them through the port-8001 contract.
            var second = StartLauncher(paths);
            await WaitForLogAsync(second, GridRunning);
            Assert.Equal(db, LivePid(paths, "mariadb.pid"));
            Assert.Equal(robust, LivePid(paths, "robust.pid"));
            Assert.Equal("ACK", await GetAsync(s, $"?command=StopMysql&password={Uri.EscapeDataString(s.MachineId)}"));
            Assert.False(ProcessImage.IsAlive(db), "MariaDB is still running after StopMysql");
            Assert.False(ProcessImage.IsAlive(robust), "Robust is still running after StopMysql");
            Assert.Empty(LivePidFiles(paths));
        });
    }

    [Fact]
    public async Task Launcher_adopts_a_running_grid_twice()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);

        await RunWithCleanupAsync(paths, store, async () =>
        {
            // 1. In-process provisioning: owner and region, then detach. The children keep running.
            var grid = NewController(paths, store, NewRobustClient(_http, s));
            try
            {
                await grid.StartAsync(Ct);
                await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, Ct);
                // The host's /dreamgrid/regionready is not running yet: stand in for it on the web port.
                using (new AlertListener(s.WebPort, grid.OnRegionAlert))
                    await grid.CreateRegionAsync("Welcome", "island", Ct);
            }
            finally { await grid.DetachAsync(); }

            s = store.LoadOrCreate();
            Assert.True(s.Setup.RegionCreated);
            var instance = s.Region!.Instance;

            // 2. The PIDs the launcher must adopt.
            var expected = (Db: LivePid(paths, "mariadb.pid"), Robust: LivePid(paths, "robust.pid"), Region: LivePid(paths, instance + ".pid"));

            // 3. and 4. Two adoptions, killing only the launcher in between.
            for (var run = 1; run <= 2; run++)
            {
                var launcher = StartLauncher(paths);
                await WaitForAsync(launcher, async () => await GetAsync(s, "") == "Test Completed", "GET / == Test Completed");
                await WaitForAsync(launcher, async () => await GetAsync(s, "?command=RegionStatus") == "Running", "RegionStatus == Running");
                await WaitForLogAsync(launcher, GridRunning);
                await WaitForAsync(launcher, () => RegionLoginsEnabledAsync(s), "the region reports logins enabled", () => "last console reply: " + _lastConsoleReply);

                var actual = (Db: LivePid(paths, "mariadb.pid"), Robust: LivePid(paths, "robust.pid"), Region: LivePid(paths, instance + ".pid"));
                Assert.Equal(expected, actual);
                Assert.Equal(1, CountProcessesMatching(Path.Combine(paths.RuntimeOpenSim, "OpenSim.dll")));
                Assert.Equal(1, CountProcessesMatching(Path.Combine(paths.RuntimeOpenSim, "Robust.dll")));

                launcher.KillOnly();
                Assert.True(ProcessImage.IsAlive(expected.Db), $"run {run}: MariaDB died with the launcher");
                Assert.True(ProcessImage.IsAlive(expected.Robust), $"run {run}: Robust died with the launcher");
                Assert.True(ProcessImage.IsAlive(expected.Region), $"run {run}: the region died with the launcher");
            }
        });
    }

    // ---- helpers ------------------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="body"/>, then always cleans up: kills the launchers (only them, so no monitor restarts a
    /// child), stops the grid through an in-process controller on the same data directory, and kills anything left
    /// as a backstop. On success, no PID file may name a live process of this data directory.
    /// </summary>
    private async Task RunWithCleanupAsync(LauncherPaths paths, SettingsStore store, Func<Task> body)
    {
        IReadOnlyList<string> leftovers;
        try
        {
            await body();
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            throw new Exception(e.Message + Environment.NewLine + LogTail(paths), e);
        }
        catch (Xunit.Sdk.XunitException e)
        {
            throw new Xunit.Sdk.XunitException(e.Message + Environment.NewLine + LogTail(paths), e);
        }
        finally
        {
            leftovers = await CleanupAsync(paths, store);
        }
        Assert.True(leftovers.Count == 0, "Processes left behind: " + string.Join(", ", leftovers));
        Assert.False(AnyProcessMatches(paths.Data), "a process of this data directory is still running");
    }

    private async Task<IReadOnlyList<string>> CleanupAsync(LauncherPaths paths, SettingsStore store)
    {
        foreach (var l in _launchers) l.KillOnly();
        try
        {
            var s = store.LoadOrCreate();
            var grid = NewController(paths, store, NewRobustClient(_http, s));
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                await grid.StopAllAsync(timeout.Token);
            }
            finally { await grid.DetachAsync(); }
        }
        catch (Exception) { /* the backstop below still runs */ }
        var leftovers = LivePidFiles(paths);
        KillEverythingUnder(paths.Data);
        return leftovers;
    }

    private LauncherProcess StartLauncher(LauncherPaths paths)
    {
        var launcher = LauncherProcess.Start(paths, _launchers.Count + 1);
        _launchers.Add(launcher);
        return launcher;
    }

    private async Task<string?> GetAsync(LauncherSettings s, string pathAndQuery)
    {
        try { return await _http.GetStringAsync($"http://127.0.0.1:{s.WebPort}/{pathAndQuery}", Ct); }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!Ct.IsCancellationRequested) { return null; }
    }

    /// <summary>The region's own answer to <c>login status</c>, on its REST console (the reply is kept for failure messages).</summary>
    private async Task<bool> RegionLoginsEnabledAsync(LauncherSettings s)
    {
        var console = new RestConsoleClient(_http, new Uri($"http://127.0.0.1:{s.RegionConsolePort}"), s.Console.User, s.Console.Password);
        try
        {
            var reply = await console.RunAsync("login status", TimeSpan.FromSeconds(2), Ct);
            _lastConsoleReply = string.Join(" | ", reply);
            // AccessModule's exact answer to "login status".
            return reply.Any(l => l.Trim() == $"Login in {s.Region!.Name} are enabled");
        }
        catch (Exception e) when (!Ct.IsCancellationRequested)
        {
            _lastConsoleReply = e.GetType().Name + ": " + e.Message; // not up yet
            return false;
        }
        finally { await console.CloseAsync(); }
    }

    /// <summary>The live PID in a PID file of this data directory; fails when the file is missing or its process is gone.</summary>
    private static int LivePid(LauncherPaths paths, string pidFile)
    {
        var pid = PidFile.Read(Path.Combine(paths.Pids, pidFile));
        Assert.True(pid is not null, $"{pidFile} is missing");
        Assert.True(ProcessImage.Matches(pid!.Value, paths.Data), $"{pidFile} names {pid}, which is not a process of this grid");
        return pid.Value;
    }

    private static async Task WaitForAsync(LauncherProcess launcher, Func<Task<bool>> condition, string what, Func<string>? detail = null)
    {
        var deadline = DateTime.UtcNow + BootTimeout;
        while (!await condition())
        {
            if (launcher.HasExited) Assert.Fail($"The launcher exited (code {launcher.ExitCode}) while waiting for {what}.");
            Assert.False(launcher.Output.Contains(GridFailed, StringComparison.Ordinal), $"The grid did not start while waiting for {what}.");
            Assert.True(DateTime.UtcNow < deadline, $"Timed out after {BootTimeout.TotalMinutes} min waiting for {what}. {detail?.Invoke()}");
            await Task.Delay(500, Ct);
        }
    }

    private static Task WaitForLogAsync(LauncherProcess launcher, string line) =>
        WaitForAsync(launcher, () => Task.FromResult(launcher.Output.Contains(line, StringComparison.Ordinal)), $"the launcher to log \"{line}\"");

    /// <summary>
    /// The launcher as a child process. Its output is kept in memory and in <c>state/logs/launcher-N.log</c>, which
    /// is collected with the other logs on failure.
    /// </summary>
    private sealed class LauncherProcess : IDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _log;
        private readonly System.Text.StringBuilder _output = new();
        private readonly object _gate = new();
        private bool _closed;

        private LauncherProcess(Process process, StreamWriter log)
        {
            _process = process;
            _log = log;
        }

        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.ExitCode;
        public string Output { get { lock (_gate) return _output.ToString(); } }

        public static LauncherProcess Start(LauncherPaths paths, int run)
        {
            var psi = new ProcessStartInfo(DotnetHost())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var a in new[] { IntegrationEnvironment.LauncherDll, "--data", paths.Data, "--opensim", paths.OpenSimProgram, "--mariadb-bin", paths.MariaDbBin! })
                psi.ArgumentList.Add(a);

            var log = new StreamWriter(Path.Combine(paths.Logs, $"launcher-{run}.log")) { AutoFlush = true };
            var process = Process.Start(psi) ?? throw new InvalidOperationException("The launcher did not start.");
            var launcher = new LauncherProcess(process, log);
            process.OutputDataReceived += (_, e) => launcher.Append(e.Data);
            process.ErrorDataReceived += (_, e) => launcher.Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return launcher;
        }

        private void Append(string? line)
        {
            if (line is null) return;
            lock (_gate)
            {
                _output.AppendLine(line);
                if (!_closed) _log.WriteLine(line);
            }
        }

        /// <summary>Kills the launcher process only, never its tree (SIGKILL of this PID on Linux).</summary>
        public void KillOnly()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: false);
                // Not WaitForExit(): on Windows the children may hold inherited copies of the output pipe.
                _process.WaitForExit(10_000);
            }
            catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            KillOnly();
            lock (_gate)
            {
                _closed = true;
                _log.Dispose();
            }
            _process.Dispose();
        }

        /// <summary>DOTNET_HOST_PATH (set by <c>dotnet test</c>), else "dotnet" on PATH.</summary>
        private static string DotnetHost()
        {
            var env = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            return !string.IsNullOrWhiteSpace(env) && File.Exists(env) ? env : "dotnet";
        }
    }
}
