using DreamGrid.Launcher.Core.Grid;
using System.Diagnostics;
using System.Text.Json;
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
/// The website's newcomer path is in WebsiteEndToEndTests.cs (the same class, sharing these helpers).
/// </summary>
[Collection(GridCollection.Name)]
public sealed partial class LauncherEndToEndTests : IDisposable
{
    private const string GridRunning = "The grid is running.";
    private const string GridFailed = "The grid did not start";
    private static readonly TimeSpan BootTimeout = TimeSpan.FromMinutes(5);

    private readonly string _root = NewDataDir();

    /// <summary>A second launcher's data directory, for the tests that run two (created only by them).</summary>
    private readonly string _secondRoot = NewDataDir();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly List<LauncherProcess> _launchers = [];
    private string _lastConsoleReply = "";

    public void Dispose()
    {
        foreach (var l in _launchers) l.Dispose();
        KillEverythingUnder(_root);
        if (Directory.Exists(_secondRoot)) KillEverythingUnder(_secondRoot);
        _http.Dispose();
        DeleteDataDir(_root);
        DeleteDataDir(_secondRoot);
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
            await WaitForAsync(launcher, async () => await GetAsync(s, "?type=Booted") == "ACK", "GET /?type=Booted == ACK");
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
                await grid.CreateOwnerAsync("Test", "Owner", Password, null, Ct);
                // The host's /dreamgrid/regionready is not running yet: stand in for it on the web port.
                using (new AlertListener(s.WebPort, grid.OnRegionAlert))
                    await grid.CreateRegionAsync("Welcome", "island", Ct);
            }
            finally { await grid.DetachAsync(); }

            s = store.LoadOrCreate();
            Assert.True(s.Setup.RegionCreated);
            var instance = RegionModel.First(s)!.Instance;

            // 2. The PIDs the launcher must adopt.
            var expected = (Db: LivePid(paths, "mariadb.pid"), Robust: LivePid(paths, "robust.pid"), Region: LivePid(paths, instance + ".pid"));

            // 3. and 4. Two adoptions, killing only the launcher in between.
            for (var run = 1; run <= 2; run++)
            {
                var launcher = StartLauncher(paths);
                var restarted = DateTime.UtcNow;
                await WaitForAsync(launcher, async () => await GetAsync(s, "?type=Booted") == "ACK", "GET /?type=Booted == ACK");
                await WaitForAsync(launcher, async () => await GetAsync(s, "?command=RegionStatus") == "Running", "RegionStatus == Running");
                await WaitForLogAsync(launcher, GridRunning);
                await WaitForAsync(launcher, () => RegionLoginsEnabledAsync(s), "the region reports logins enabled", () => "last console reply: " + _lastConsoleReply);

                var actual = (Db: LivePid(paths, "mariadb.pid"), Robust: LivePid(paths, "robust.pid"), Region: LivePid(paths, instance + ".pid"));
                Assert.Equal(expected, actual);
                Assert.Equal(1, CountProcessesMatching(Path.Combine(paths.RuntimeOpenSim, "OpenSim.dll")));
                Assert.Equal(1, CountProcessesMatching(Path.Combine(paths.RuntimeOpenSim, "Robust.dll")));

                // Logins_enabled_after_adoption: the launcher itself knows the adopted region takes logins, although
                // the region's one-time "logins enabled" alert went out before this launcher was listening.
                using (var web = new WebSession(s.WebPort))
                {
                    var page = await web.FollowAsync("/login");
                    await web.SubmitAsync(page, new() { ["Name"] = "Test Owner", ["Password"] = Password });
                    await WaitForAsync(launcher, () => AdminSeesLoginsEnabledAsync(web, restarted + TimeSpan.FromSeconds(60)),
                        "/api/status to report logins enabled", () => "last status: " + _lastStatus);
                }

                launcher.KillOnly();
                Assert.True(ProcessImage.IsAlive(expected.Db), $"run {run}: MariaDB died with the launcher");
                Assert.True(ProcessImage.IsAlive(expected.Robust), $"run {run}: Robust died with the launcher");
                Assert.True(ProcessImage.IsAlive(expected.Region), $"run {run}: the region died with the launcher");
            }
        });
    }

    // ---- many regions (plan 4a) ----------------------------------------------------------------------

    [Fact]
    public async Task Login_to_second_region_returns_its_port()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);

        await RunWithCleanupAsync(paths, store, async () =>
        {
            var grid = NewController(paths, store, NewRobustClient(_http, s));
            try
            {
                await grid.StartAsync(Ct);
                await grid.CreateOwnerAsync("Test", "Owner", Password, null, Ct);
                RegionSettings beach;
                using (new AlertListener(s.WebPort, grid.OnRegionAlert))
                {
                    await grid.CreateRegionAsync("Welcome", "island", Ct);
                    beach = await grid.AddRegionAsync(new NewRegion("Beach", 1001, 1000, 1, "island", "Welcome"), Ct);
                }

                var login = await XmlRpcLogin.LoginAsync(_http, grid.GetStatus().LoginUri, "Test", "Owner", Password, "uri:Beach&128&128&25", Ct);

                Assert.True(login.Ok, "login refused: " + login.Message);
                Assert.Equal(beach.UdpPort, login.SimPort);
                await grid.StopAllAsync(Ct);
            }
            finally { await grid.DetachAsync(); }
        });
    }

    [Fact]
    public async Task Upgraded_v1_grid_is_adopted_unchanged()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);

        await RunWithCleanupAsync(paths, store, async () =>
        {
            var grid = NewController(paths, store, NewRobustClient(_http, s));
            try
            {
                await grid.StartAsync(Ct);
                await grid.CreateOwnerAsync("Test", "Owner", Password, null, Ct);
                using (new AlertListener(s.WebPort, grid.OnRegionAlert))
                    await grid.CreateRegionAsync("Welcome", "island", Ct);
            }
            finally { await grid.DetachAsync(); }

            // What a version 1 launcher left: the same folder, ports and INIs, but settings in schema 1.
            var region = RegionModel.First(store.LoadOrCreate())!;
            var regionPid = LivePid(paths, region.Instance + ".pid");
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(paths.SettingsFile))!.AsObject();
            node["schemaVersion"] = 1;
            node.Remove("programs");
            node.Remove("regions");
            node["region"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new
            {
                instance = region.Instance, name = region.Name, uuid = region.Uuid, x = region.X, y = region.Y, physics = "ubODE", content = region.Content,
            }));
            File.WriteAllText(paths.SettingsFile, node.ToJsonString());

            var launcher = StartLauncher(paths);
            await WaitForLogAsync(launcher, GridRunning);
            await WaitForAsync(launcher, async () => await GetAsync(s, "?command=RegionStatus") == "Running", "RegionStatus == Running");

            Assert.Equal(regionPid, LivePid(paths, region.Instance + ".pid"));
            var migrated = new SettingsStore(paths.SettingsFile).LoadOrCreate();
            Assert.Equal(LauncherSettings.CurrentSchema, migrated.SchemaVersion);
            Assert.Equal([new ProgramSettings(region.Instance, s.RegionPort, s.RegionConsolePort)], migrated.Programs);
            Assert.Equal(s.RegionPort, migrated.Regions.Single().UdpPort);
            Assert.Equal(region.Uuid, migrated.Regions.Single().Uuid);
        });
    }

    [Fact]
    public async Task Adopts_two_programs_after_launcher_restart()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);

        await RunWithCleanupAsync(paths, store, async () =>
        {
            var grid = NewController(paths, store, NewRobustClient(_http, s));
            try
            {
                await grid.StartAsync(Ct);
                await grid.CreateOwnerAsync("Test", "Owner", Password, null, Ct);
                using (new AlertListener(s.WebPort, grid.OnRegionAlert))
                {
                    await grid.CreateRegionAsync("Welcome", "island", Ct);
                    await grid.AddRegionAsync(new NewRegion("Isle", 1010, 1010, 1, "island", null), Ct);
                }
            }
            finally { await grid.DetachAsync(); }

            var instances = store.LoadOrCreate().Programs.Select(p => p.Instance).ToList();
            Assert.Equal(2, instances.Count);
            var pids = instances.Select(i => LivePid(paths, i + ".pid")).ToList();

            var launcher = StartLauncher(paths);
            await WaitForLogAsync(launcher, GridRunning);
            foreach (var name in new[] { "Welcome", "Isle" })
                await WaitForAsync(launcher, async () => await GetAsync(s, "?command=RegionStatus&RegionName=" + name) == "Running", name + " == Running");

            Assert.Equal(pids, instances.Select(i => LivePid(paths, i + ".pid")).ToList());
            Assert.Equal(2, CountProcessesMatching(Path.Combine(paths.RuntimeOpenSim, "OpenSim.dll")));
        });
    }

    // ---- the Regions page (plan 4b) ------------------------------------------------------------------

    /// <summary>
    /// Provisions the owner and "Welcome" in-process (then runs <paramref name="more"/> there, if given), detaches, then
    /// starts the launcher, which adopts the grid, and signs in.
    /// </summary>
    private async Task<(LauncherProcess Launcher, WebSession Web)> LauncherWithSignedInAdminAsync(LauncherPaths paths, SettingsStore store, LauncherSettings s,
        Func<GridController, Task>? more = null)
    {
        var grid = NewController(paths, store, NewRobustClient(_http, s));
        try
        {
            await grid.StartAsync(Ct);
            await grid.CreateOwnerAsync("Test", "Owner", Password, null, Ct);
            using (new AlertListener(s.WebPort, grid.OnRegionAlert))
            {
                await grid.CreateRegionAsync("Welcome", "island", Ct);
                if (more is not null) await more(grid);
            }
        }
        finally { await grid.DetachAsync(); }

        var launcher = StartLauncher(paths);
        await WaitForLogAsync(launcher, GridRunning);
        var web = new WebSession(s.WebPort);
        var page = await web.FollowAsync("/login");
        await web.SubmitAsync(page, new() { ["Name"] = "Test Owner", ["Password"] = Password });
        return (launcher, web);
    }

    [Fact]
    public async Task Website_adds_a_second_region_and_it_accepts_logins()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);

        await RunWithCleanupAsync(paths, store, async () =>
        {
            var (launcher, web) = await LauncherWithSignedInAdminAsync(paths, store, s);
            using var _ = web;

            var page = await web.FollowAsync("/regions/add");
            Assert.Equal("/regions/add", page.Path);
            page = await web.SubmitAsync(page, new()
            {
                ["Name"] = "Beach", ["Type"] = "beach", ["X"] = "1001", ["Y"] = "1000", ["Size"] = "1", ["Group"] = "own",
            });
            Assert.Equal("/regions/add?working=1", page.PathAndQuery);

            var progress = "";
            await WaitForAsync(launcher, async () =>
            {
                progress = await web.GetStringAsync("/api/regions/progress");
                using var doc = JsonDocument.Parse(progress);
                if (doc.RootElement.GetProperty("error").GetString() is { } error)
                    Assert.Fail("Adding the region failed: " + error + Environment.NewLine + progress);
                return !doc.RootElement.GetProperty("running").GetBoolean();
            }, "the add-region job to finish", () => "last progress: " + progress);

            using var list = JsonDocument.Parse(await web.GetStringAsync("/api/regions"));
            var beach = list.RootElement.GetProperty("regions").EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Beach");
            Assert.Equal("running", beach.GetProperty("state").GetString());

            var udp = RegionModel.FindByName(store.LoadOrCreate(), "Beach")!.UdpPort;
            var login = await XmlRpcLogin.LoginAsync(_http, $"http://127.0.0.1:{s.RobustPublicPort}", "Test", "Owner", Password, "uri:Beach&128&128&25", Ct);
            Assert.True(login.Ok, "login refused: " + login.Message);
            Assert.Equal(udp, login.SimPort);
        });
    }

    [Fact]
    public async Task Map_tile_proxy_answers_for_a_running_grid()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);

        await RunWithCleanupAsync(paths, store, async () =>
        {
            var (_, web) = await LauncherWithSignedInAdminAsync(paths, store, s);
            using var __ = web;

            var (status, type) = await web.GetStatusAsync("/api/map/tile/1/1000/1000");
            Assert.True(status is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.NotFound, $"the tile answered {(int)status}");
            if (status == System.Net.HttpStatusCode.OK) Assert.StartsWith("image/", type);
        });
    }

    // ---- the OAR library (plan 4c) -------------------------------------------------------------------

    [Fact]
    public async Task Upload_import_and_download_through_the_website()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);

        await RunWithCleanupAsync(paths, store, async () =>
        {
            // A real OAR to upload: Welcome saved in-process, then taken out of the library.
            var upload = Path.Combine(paths.Data, "upload", "Seaside.oar");
            var (launcher, web) = await LauncherWithSignedInAdminAsync(paths, store, s, async grid =>
            {
                var welcome = RegionModel.FindByName(grid.CurrentSettings(), "Welcome")!;
                var saved = await grid.RunOarJobAsync(new OarJobRequest(welcome.Uuid, OarJobKind.Save), Ct);
                Directory.CreateDirectory(Path.GetDirectoryName(upload)!);
                File.Move(Path.Combine(paths.Oars, saved!), upload);
            });
            using var _ = web;
            var id = RegionModel.FindByName(store.LoadOrCreate(), "Welcome")!.Uuid;

            var library = await web.FollowAsync("/regions/library");
            var (status, body) = await web.UploadAsync("/regions/library/upload", web.FormToken(library, "#upload"), "oar", upload);
            Assert.True(status == System.Net.HttpStatusCode.OK, $"the upload answered {(int)status}: {body}");
            using (var reply = JsonDocument.Parse(body))
            {
                Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(), body);
                Assert.Equal("Seaside.oar", reply.RootElement.GetProperty("name").GetString());
            }
            Assert.Contains("Seaside.oar", (await web.FollowAsync("/regions/library")).Html);

            // Import it: choose the file, say yes, then follow the job until it ends.
            var page = await web.FollowAsync($"/regions/region/{id}/import");
            page = await web.SubmitAsync(page, new() { ["file"] = "Seaside.oar" }, redirects: false);
            Assert.Contains("Import Seaside.oar into Welcome?", page.Html);
            page = await web.SubmitAsync(page, new());
            Assert.Equal($"/regions/region/{id}/job", page.Path);

            var job = "";
            await WaitForAsync(launcher, async () =>
            {
                job = await web.GetStringAsync($"/api/regions/{id}/job");
                using var doc = JsonDocument.Parse(job);
                if (doc.RootElement.GetProperty("running").GetBoolean()) return false;
                Assert.True(doc.RootElement.GetProperty("error").ValueKind == JsonValueKind.Null, "the import failed: " + job);
                return true;
            }, "the import job to finish", () => "last job: " + job);
            Assert.Contains("Importing Seaside.oar into Welcome", job);
            var login = await XmlRpcLogin.LoginAsync(_http, $"http://127.0.0.1:{s.RobustPublicPort}", "Test", "Owner", Password, "uri:Welcome&128&128&25", Ct);
            Assert.True(login.Ok, "login after the import refused: " + login.Message);

            Assert.Equal(await File.ReadAllBytesAsync(upload, Ct), await web.GetBytesAsync("/regions/library/file/Seaside.oar"));
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
            return reply.Any(l => l.Trim() == $"Login in {RegionModel.First(s)!.Name} are enabled");
        }
        catch (Exception e) when (!Ct.IsCancellationRequested)
        {
            _lastConsoleReply = e.GetType().Name + ": " + e.Message; // not up yet
            return false;
        }
        finally { await console.CloseAsync(); }
    }

    private string _lastStatus = "";

    /// <summary>True once a signed-in admin's <c>/api/status</c> says logins are enabled; fails once <paramref name="deadline"/> has passed.</summary>
    private async Task<bool> AdminSeesLoginsEnabledAsync(WebSession web, DateTime deadline)
    {
        var json = await web.GetStringAsync("/api/status");
        _lastStatus = json;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("grid").GetProperty("loginsEnabled").GetBoolean()) return true;
        Assert.True(DateTime.UtcNow < deadline, $"/api/status did not report logins enabled within 60 s of the launcher's restart: {json}");
        return false;
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
