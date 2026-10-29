using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Ini;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Xunit;
using static DreamGrid.Launcher.IntegrationTests.GridTestSupport;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>Region settings (plan 4d) on a real grid: options, access, physics, scripts, map, rename, resize and CPU.</summary>
[Collection(GridCollection.Name)]
public sealed class RegionSettingsTests : IDisposable
{
    private readonly string _root = GridTestSupport.NewDataDir();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(100) };

    public void Dispose()
    {
        KillEverythingUnder(_root);
        _http.Dispose();
        GridTestSupport.DeleteDataDir(_root);
    }

    private sealed record Rig(GridController Grid, LauncherPaths Paths, RegionSettings Welcome);

    /// <summary>
    /// Boots MariaDB, Robust, the owner and the Welcome region, runs <paramref name="body"/>, then stops the grid. A failure
    /// that is not an assertion carries the log tails.
    /// </summary>
    private async Task OnGridAsync(Func<Rig, CancellationToken, Task> body)
    {
        var paths = GridTestSupport.RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var settings = GridTestSupport.SeedSettings(store);
        var grid = GridTestSupport.NewController(paths, store, GridTestSupport.NewRobustClient(_http, settings));
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await grid.StartAsync(ct);
            await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            using var alerts = new AlertListener(settings.WebPort, grid.OnRegionAlert);
            await grid.CreateRegionAsync("Welcome", "island", ct);
            await body(new Rig(grid, paths, RegionModel.FindByName(grid.CurrentSettings(), "Welcome")!), ct);
            await grid.StopAllAsync(ct);
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            throw new Exception(e.Message + Environment.NewLine + LogTail(paths), e);
        }
        finally
        {
            await grid.DetachAsync();
        }
    }

    /// <summary>A second region in a program of its own, so restarting it leaves Welcome alone.</summary>
    private static Task<RegionSettings> AddIsleAsync(GridController grid, CancellationToken ct) =>
        grid.AddRegionAsync(new NewRegion("Isle", 1010, 1010, 1, "island", null), ct);

    /// <summary>Restarts the region's program and waits until the region takes logins again.</summary>
    private static async Task RestartAsync(GridController grid, Guid regionId, CancellationToken ct)
    {
        await grid.RegionPowerAsync(regionId, RegionPower.Restart, ct);
        await WaitForLoginsAsync(grid, regionId, ct);
    }

    private static Task WaitForLoginsAsync(GridController grid, Guid regionId, CancellationToken ct) =>
        WaitUntilAsync(() => grid.GetStatus().Regions.Any(r => r.Id == regionId && r.State == RegionState.Running && r.LoginsEnabled),
            TimeSpan.FromMinutes(5), ct);

    private async Task AssertLoginAsync(GridController grid, string regionName, CancellationToken ct)
    {
        var login = await XmlRpcLogin.LoginAsync(_http, grid.GetStatus().LoginUri, "Test", "Owner", "pass-word1",
            $"uri:{regionName}&128&128&25", ct);
        Assert.True(login.Ok, $"login to {regionName} refused: " + login.Message);
        Assert.Equal(RegionModel.FindByName(grid.CurrentSettings(), regionName)!.UdpPort, login.SimPort);
    }

    private static IniDocument OpenSimIni(LauncherPaths paths, string instance) =>
        IniDocument.Load(Path.Combine(paths.RegionDir(instance), "OpenSim.ini"));

    private static string RegionLog(LauncherPaths paths, string instance) => Path.Combine(paths.Logs, $"region-{instance}.log");

    /// <summary>The log's text from byte <paramref name="from"/> up to byte <paramref name="to"/> (the program still has it open).</summary>
    private static string ReadBytes(string file, long from, long to = long.MaxValue)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var end = Math.Min(to, fs.Length);
        var start = Math.Min(from, end);
        fs.Seek(start, SeekOrigin.Begin);
        var bytes = new byte[end - start];
        fs.ReadExactly(bytes);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>The [ERROR] lines of a log, without their time.</summary>
    private static IEnumerable<string> ErrorLines(string log) =>
        log.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Contains("[ERROR]")).Select(l => l[(l.IndexOf("[ERROR]", StringComparison.Ordinal))..]);

    private static int? PidOf(GridController grid, string instance) =>
        grid.GetStatus().Programs.Single(p => p.Instance == instance).Process.Pid;

    [Fact]
    public async Task Options_and_access_apply_after_restart_now()
    {
        await OnGridAsync(async (rig, ct) =>
        {
            var (grid, paths, _) = rig;
            var isle = await AddIsleAsync(grid, ct);
            Assert.False(grid.NeedsRestart(isle.Instance));

            await grid.SaveRegionOptionsAsync(isle.Uuid, new RegionOptions(MaxAgents: 20, KeepResidentsOut: true), ct);
            Assert.True(grid.NeedsRestart(isle.Instance));

            var log = RegionLog(paths, isle.Instance);
            var before = new FileInfo(log).Length;
            await RestartAsync(grid, isle.Uuid, ct);
            await AssertLoginAsync(grid, "Isle", ct);

            var file = IniDocument.Load(Path.Combine(paths.RegionDefinitionDir(isle.Instance), "Isle.ini"));
            Assert.Equal("20", file.Get("Isle", "MaxAgents"));
            Assert.Equal("DisallowResidents", OpenSimIni(paths, isle.Instance).Get("AuthorizationService", "Region_Isle"));
            // Every OpenSim start here logs two [ERROR] lines that no setting causes: the PID file the launcher writes before
            // OpenSim reads it, and jOpenSimSearch without a search server. So the restart may log only what the first start did.
            var baseline = ErrorLines(ReadBytes(log, 0, before)).ToHashSet();
            var errors = ErrorLines(ReadBytes(log, before)).Where(e => !baseline.Contains(e)).ToList();
            Assert.True(errors.Count == 0, "the restart logged new errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
            Assert.False(grid.NeedsRestart(isle.Instance));
        });
    }

    [Fact]
    public async Task BulletSim_threaded_region_accepts_logins()
    {
        await OnGridAsync(async (rig, ct) =>
        {
            var (grid, paths, welcome) = rig;
            Assert.False(await grid.SaveProgramOptionsAsync(welcome.Instance, "BulletSimThreaded", new ProgramOptions(), ct));
            Assert.True(grid.NeedsRestart(welcome.Instance));

            await RestartAsync(grid, welcome.Uuid, ct);
            var ini = OpenSimIni(paths, welcome.Instance);
            Assert.Equal("BulletSim", ini.Get("Startup", "physics"));
            Assert.Equal("true", ini.Get("BulletSim", "UseSeparatePhysicsThread"));
            await AssertLoginAsync(grid, "Welcome", ct);
            Assert.False(grid.NeedsRestart(welcome.Instance));
        });
    }

    [Fact]
    public async Task Scripts_off_region_starts_and_accepts_logins()
    {
        await OnGridAsync(async (rig, ct) =>
        {
            var (grid, paths, welcome) = rig;
            await grid.SaveProgramOptionsAsync(welcome.Instance, null, new ProgramOptions(Scripts: false), ct);
            Assert.True(grid.NeedsRestart(welcome.Instance));

            await RestartAsync(grid, welcome.Uuid, ct);
            Assert.Equal("false", OpenSimIni(paths, welcome.Instance).Get("YEngine", "Enabled"));
            await AssertLoginAsync(grid, "Welcome", ct);
        });
    }

    [Fact]
    public async Task Map_none_and_best_both_start()
    {
        await OnGridAsync(async (rig, ct) =>
        {
            var (grid, paths, welcome) = rig;
            var isle = await AddIsleAsync(grid, ct);
            await grid.SaveProgramOptionsAsync(welcome.Instance, null, new ProgramOptions(Map: MapQuality.None), ct);
            await grid.SaveProgramOptionsAsync(isle.Instance, null, new ProgramOptions(Map: MapQuality.Best), ct);

            await RestartAsync(grid, welcome.Uuid, ct);
            await RestartAsync(grid, isle.Uuid, ct);
            Assert.Equal("false", OpenSimIni(paths, welcome.Instance).Get("Map", "GenerateMaptiles"));
            var best = OpenSimIni(paths, isle.Instance);
            Assert.Equal("true", best.Get("Map", "GenerateMaptiles"));
            Assert.Equal("true", best.Get("Map", "RenderMeshes"));
            await AssertLoginAsync(grid, "Welcome", ct);
            await AssertLoginAsync(grid, "Isle", ct);
        });
    }

    [Fact]
    public async Task Renamed_region_logs_in_by_its_new_name()
    {
        await OnGridAsync(async (rig, ct) =>
        {
            var (grid, paths, _) = rig;
            var isle = await AddIsleAsync(grid, ct);

            await grid.RegionPowerAsync(isle.Uuid, RegionPower.Stop, ct);
            await grid.RenameRegionAsync(isle.Uuid, "Coral Isle", ct);
            await grid.RegionPowerAsync(isle.Uuid, RegionPower.Start, ct);
            await WaitForLoginsAsync(grid, isle.Uuid, ct);

            Assert.Equal("Coral Isle", grid.GetStatus().Regions.Single(r => r.Id == isle.Uuid).Name);
            Assert.True(File.Exists(Path.Combine(paths.RegionDefinitionDir(isle.Instance), "Coral Isle.ini")));
            Assert.False(File.Exists(Path.Combine(paths.RegionDefinitionDir(isle.Instance), "Isle.ini")));
            await AssertLoginAsync(grid, "Coral Isle", ct);
        });
    }

    [Fact]
    public async Task Grown_region_registers_its_new_size()
    {
        await OnGridAsync(async (rig, ct) =>
        {
            var (grid, _, _) = rig;
            var isle = await AddIsleAsync(grid, ct);

            await grid.ResizeRegionAsync(isle.Uuid, 2, ct);
            Assert.True(grid.NeedsRestart(isle.Instance));
            await RestartAsync(grid, isle.Uuid, ct);

            Assert.Equal(512, await ScalarAsync(grid, "SELECT `sizeX` FROM `robust`.`regions` WHERE `uuid` = @id", isle.Uuid, ct));
            Assert.Equal(512, await ScalarAsync(grid, "SELECT `sizeY` FROM `robust`.`regions` WHERE `uuid` = @id", isle.Uuid, ct));
            await AssertLoginAsync(grid, "Isle", ct);
        });
    }

    [Fact]
    public async Task Cpu_settings_apply_to_the_running_program()
    {
        // The same assertions hold on Linux (sched_setaffinity and nice on the main thread) and on Windows.
        await OnGridAsync(async (rig, ct) =>
        {
            var (grid, _, welcome) = rig;
            var pid = PidOf(grid, welcome.Instance);
            Assert.NotNull(pid);

            var applied = await grid.SaveProgramOptionsAsync(welcome.Instance, null,
                new ProgramOptions(Cores: [0], Priority: CpuPriority.BelowNormal), ct);
            Assert.True(applied, "the CPU settings were not applied to the running program");
            Assert.False(grid.NeedsRestart(welcome.Instance));
            Assert.Equal(pid, PidOf(grid, welcome.Instance));

            var now = CpuTuning.Read(pid!.Value);
            Assert.NotNull(now);
            Assert.Equal([0], now.Value.Cores);
            Assert.Equal(CpuPriority.BelowNormal, now.Value.Priority);
        });
    }

    /// <summary>Runs a one-value query with <c>@id</c> as the region's id, as root on the grid's own MariaDB.</summary>
    private static async Task<long> ScalarAsync(GridController grid, string sql, Guid id, CancellationToken ct)
    {
        var s = grid.CurrentSettings();
        var root = new MySqlConnector.MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1", Port = (uint)s.MariaDbPort, UserID = "root", Password = s.Db.RootPassword,
            Pooling = false, SslMode = MySqlConnector.MySqlSslMode.Disabled,
        }.ConnectionString;
        await using var conn = new MySqlConnector.MySqlConnection(root);
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlConnector.MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task WaitUntilAsync(Func<bool> done, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not hold within " + timeout);
            await Task.Delay(500, ct);
        }
    }
}
