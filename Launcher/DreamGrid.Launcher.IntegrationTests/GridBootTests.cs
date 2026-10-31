using System.Net;
using System.Net.Sockets;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Xunit;
using static DreamGrid.Launcher.IntegrationTests.GridTestSupport;

namespace DreamGrid.Launcher.IntegrationTests;

/// <summary>Boots the real MariaDB, Robust and one region through <see cref="GridController"/>.</summary>
[Collection(GridCollection.Name)]
public sealed class GridBootTests : IDisposable
{
    private readonly string _root = GridTestSupport.NewDataDir();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(100) };

    public void Dispose()
    {
        KillEverythingUnder(_root);
        _http.Dispose();
        GridTestSupport.DeleteDataDir(_root);
    }

    private sealed record Rig(GridController Grid, LauncherPaths Paths, LauncherSettings Settings, RobustClient Robust);

    private Rig Make()
    {
        var paths = GridTestSupport.RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var settings = GridTestSupport.SeedSettings(store);
        var robust = GridTestSupport.NewRobustClient(_http, settings);
        return new Rig(GridTestSupport.NewController(paths, store, robust), paths, settings, robust);
    }

    [Fact]
    public async Task Fresh_grid_boots_owner_and_region()
    {
        var (grid, paths, s, robust) = Make();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await grid.StartAsync(ct);
            var status = grid.GetStatus();
            Assert.Equal(ProcessState.Running, status.Database.State);
            Assert.Equal(ProcessState.Running, status.Robust.State);
            Assert.Empty(status.Regions);
            Assert.Equal($"http://127.0.0.1:{s.RobustPublicPort}", status.LoginUri);

            var account = await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            Assert.Equal(200, account.UserLevel);
            Assert.True(await robust.AuthenticateAsync(account.PrincipalId, "pass-word1", ct));
            Assert.False(await robust.AuthenticateAsync(account.PrincipalId, "wrong", ct));

            // Task 11's /dreamgrid/regionready endpoint does not exist yet: stand in for it on the web port.
            using var alerts = new AlertListener(s.WebPort, grid.OnRegionAlert);
            var stages = new StageRecorder();
            await grid.CreateRegionAsync("Welcome", "island", ct, stages);
            Assert.True(grid.GetStatus().LoginsEnabled);
            Assert.Equal(["Creating your region", "Loading the region content", "Waiting for the region to accept logins"], stages.Stages);

            status = grid.GetStatus();
            Assert.Equal(RegionState.Running, status.Regions.Single().State);
            Assert.Equal($"http://127.0.0.1:{s.RobustPublicPort}", status.LoginUri);
            Assert.True(new SettingsStore(paths.SettingsFile).LoadOrCreate().Setup.RegionCreated);

            var dbPid = status.Database.Pid;
            Assert.NotNull(dbPid);
            await grid.StopAllAsync(ct);

            Assert.False(AnyProcessMatches(Path.Combine(paths.RuntimeOpenSim, "OpenSim.dll")), "an OpenSim.dll process remains");
            Assert.False(AnyProcessMatches(Path.Combine(paths.RuntimeOpenSim, "Robust.dll")), "a Robust.dll process remains");
            Assert.False(ProcessImage.IsAlive(dbPid.Value), "MariaDB is still running");
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

    /// <summary>Adds a program with one region of its own, on ports from the allocator (plan 4a's rules).</summary>
    private static LauncherSettings WithOwnProgram(LauncherSettings l, string name, int x, int y)
    {
        var http = PortAllocator.Next(l, l.RegionPort);
        var console = PortAllocator.Next(l, l.RegionConsolePort, new HashSet<int> { http });
        return l with
        {
            Programs = [.. l.Programs, new ProgramSettings(name, http, console)],
            Regions = [.. l.Regions, new RegionSettings(name, name, Guid.NewGuid(), x, y, "island", 1, http)],
        };
    }

    [Fact]
    public async Task Two_programs_boot_and_stop()
    {
        var (grid, paths, _, _) = Make();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await grid.StartAsync(ct);
            await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            using var alerts = new AlertListener(grid.CurrentSettings().WebPort, grid.OnRegionAlert);
            grid.UpdateSettings(l => WithOwnProgram(WithOwnProgram(l, "Home", 1000, 1000), "Isle", 1010, 1010) with
            {
                Setup = l.Setup with { RegionCreated = true },
            });

            await grid.StartAsync(ct);
            await WaitUntilAsync(() => grid.GetStatus().Regions.All(r => r.State == RegionState.Running), TimeSpan.FromMinutes(5), ct);

            var status = grid.GetStatus();
            Assert.Equal(2, status.Programs.Count);
            Assert.All(status.Programs, p => Assert.Equal(ProcessState.Running, p.Process.State));
            Assert.Equal(["Home", "Isle"], status.Regions.Select(r => r.Name));
            Assert.True(status.LoginsEnabled);

            await grid.StopAllAsync(ct);
            Assert.False(AnyProcessMatches(Path.Combine(paths.RuntimeOpenSim, "OpenSim.dll")), "an OpenSim.dll process remains");
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

    [Fact]
    public async Task Second_region_shared_and_third_on_its_own()
    {
        var (grid, paths, _, _) = Make();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await grid.StartAsync(ct);
            await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            using var alerts = new AlertListener(grid.CurrentSettings().WebPort, grid.OnRegionAlert);
            await grid.CreateRegionAsync("Welcome", "island", ct);

            var beach = await grid.AddRegionAsync(new NewRegion("Beach", 1001, 1000, 1, "island", "Welcome"), ct);
            var isle = await grid.AddRegionAsync(new NewRegion("Isle", 1010, 1010, 1, "island", null), ct);

            var status = grid.GetStatus();
            Assert.Equal(["Welcome", "Beach", "Isle"], status.Regions.Select(r => r.Name));
            Assert.All(status.Regions, r => Assert.Equal(RegionState.Running, r.State));
            Assert.Equal(2, status.Programs.Count);
            Assert.Equal(["Welcome"], status.Regions.Single(r => r.Id == beach.Uuid).RunsWith);
            Assert.Empty(status.Regions.Single(r => r.Id == isle.Uuid).RunsWith);
            Assert.True(status.LoginsEnabled);

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

    [Fact]
    public async Task Park_and_unpark_a_shared_region()
    {
        var (grid, paths, _, robust) = Make();
        var ct = TestContext.Current.CancellationToken;
        GridController? second = null;
        try
        {
            await grid.StartAsync(ct);
            await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            using var alerts = new AlertListener(grid.CurrentSettings().WebPort, a => (second ?? grid).OnRegionAlert(a));
            await grid.CreateRegionAsync("Welcome", "island", ct);
            var beach = await grid.AddRegionAsync(new NewRegion("Beach", 1001, 1000, 1, "island", "Welcome"), ct);

            await grid.RegionPowerAsync(beach.Uuid, RegionPower.Park, ct);
            await WaitUntilAsync(() => grid.GetStatus().Regions.Single(r => r.Name == "Welcome").State == RegionState.Running, TimeSpan.FromMinutes(5), ct);
            Assert.Equal(RegionState.Parked, grid.GetStatus().Regions.Single(r => r.Id == beach.Uuid).State);

            // A launcher restart: a new controller on the same data directory adopts the running program.
            await grid.DetachAsync();
            second = GridTestSupport.NewController(paths, new SettingsStore(paths.SettingsFile), robust);
            await second.StartAsync(ct);
            Assert.Equal(RegionState.Parked, second.GetStatus().Regions.Single(r => r.Id == beach.Uuid).State);

            await second.StartWorldAsync(everything: true, ct);
            await WaitUntilAsync(() => second.GetStatus().Regions.All(r => r.State == RegionState.Running), TimeSpan.FromMinutes(5), ct);
            Assert.False(RegionModel.Find(second.CurrentSettings(), beach.Uuid)!.Parked);

            await second.StopAllAsync(ct);
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            throw new Exception(e.Message + Environment.NewLine + LogTail(paths), e);
        }
        finally
        {
            await grid.DetachAsync();
            if (second is not null) await second.DetachAsync();
        }
    }

    /// <summary>The REST console of the program that runs <paramref name="regionId"/>.</summary>
    private RestConsoleClient ConsoleOf(GridController grid, Guid regionId)
    {
        var s = grid.CurrentSettings();
        var program = s.Programs.Single(p => p.Instance == RegionModel.Find(s, regionId)!.Instance);
        return new RestConsoleClient(_http, new Uri($"http://127.0.0.1:{program.ConsolePort}"), s.Console.User, s.Console.Password);
    }

    [Fact]
    public async Task Two_by_two_beach_region_boots()
    {
        var (grid, paths, s, _) = Make();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await grid.StartAsync(ct);
            await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            using var alerts = new AlertListener(s.WebPort, grid.OnRegionAlert);
            await grid.CreateRegionAsync("Welcome", "island", ct);
            var big = await grid.AddRegionAsync(new NewRegion("Big Beach", 1010, 1010, 2, "beach", null), ct);

            var login = await XmlRpcLogin.LoginAsync(_http, grid.GetStatus().LoginUri, "Test", "Owner", "pass-word1", "uri:Big Beach&128&128&25", ct);
            Assert.True(login.Ok, "login refused: " + login.Message);
            Assert.Equal(big.UdpPort, login.SimPort);

            var console = ConsoleOf(grid, big.Uuid);
            try { Assert.True(await GridConsoleCommands.IslandTerrainLoadedAsync(console, TimeSpan.FromSeconds(2), TimeProvider.System, ct), "the beach shows no relief"); }
            finally { await console.CloseAsync(); }

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

    [Fact]
    public async Task Move_then_runs_change_keep_the_region_reachable()
    {
        var (grid, paths, s, _) = Make();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await grid.StartAsync(ct);
            await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            using var alerts = new AlertListener(s.WebPort, grid.OnRegionAlert);
            await grid.CreateRegionAsync("Welcome", "island", ct);
            var isle = await grid.AddRegionAsync(new NewRegion("Isle", 1010, 1010, 1, "island", null), ct);

            await grid.MoveRegionAsync(isle.Uuid, 1012, 1010, ct);
            var saved = RegionModel.Find(grid.CurrentSettings(), isle.Uuid)!;
            Assert.Equal((1012, 1010), (saved.X, saved.Y));
            await WaitUntilAsync(() => grid.GetStatus().Regions.All(r => r.State == RegionState.Running), TimeSpan.FromMinutes(5), ct);
            var moved = await XmlRpcLogin.LoginAsync(_http, grid.GetStatus().LoginUri, "Test", "Owner", "pass-word1", "uri:Isle&128&128&25", ct);
            Assert.True(moved.Ok, "login to the moved region refused: " + moved.Message);

            await grid.ChangeRunsWithAsync(isle.Uuid, "Welcome", ct);
            await WaitUntilAsync(() => grid.GetStatus().Regions.All(r => r.State == RegionState.Running), TimeSpan.FromMinutes(5), ct);
            Assert.Equal(["Welcome"], grid.GetStatus().Regions.Single(r => r.Id == isle.Uuid).RunsWith);
            foreach (var name in new[] { "Isle", "Welcome" })
            {
                var login = await XmlRpcLogin.LoginAsync(_http, grid.GetStatus().LoginUri, "Test", "Owner", "pass-word1", $"uri:{name}&128&128&25", ct);
                Assert.True(login.Ok, $"login to {name} refused: " + login.Message);
            }

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

    // ---- OAR jobs and Recently deleted (plan 4c) ----------------------------------------------------

    [Fact]
    public async Task Save_then_import_round_trips_a_region()
    {
        var (grid, paths, s, _) = Make();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await grid.StartAsync(ct);
            await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            using var alerts = new AlertListener(s.WebPort, grid.OnRegionAlert);
            await grid.CreateRegionAsync("Welcome", "island", ct);
            var welcome = RegionModel.FindByName(grid.CurrentSettings(), "Welcome")!;

            // Nothing is rezzed: an empty region still saves. The save ends on the region log's "Finished writing out OAR".
            var started = DateTime.UtcNow;
            var saved = await grid.RunOarJobAsync(new OarJobRequest(welcome.Uuid, OarJobKind.Save), ct);
            var took = DateTime.UtcNow - started;
            Assert.Equal($"Welcome-{DateTime.Now:yyyy-MM-dd}.oar", saved);
            Assert.True(OarFile.LooksLikeOar(Path.Combine(paths.Oars, saved!)), "the saved file is not an OAR");
            Assert.True(took < TimeSpan.FromMinutes(1), $"the save took {took}");
            Assert.Empty(Directory.GetFiles(paths.OarsSaving));
            Assert.Null(grid.BusyWith(welcome.Instance));

            Assert.Null(await grid.RunOarJobAsync(new OarJobRequest(welcome.Uuid, OarJobKind.Import, saved), ct));
            Assert.Null(grid.BusyWith(welcome.Instance));
            Assert.Equal(RegionState.Running, grid.GetStatus().Regions.Single().State);
            var login = await XmlRpcLogin.LoginAsync(_http, grid.GetStatus().LoginUri, "Test", "Owner", "pass-word1", "uri:Welcome&128&128&25", ct);
            Assert.True(login.Ok, "login after the import refused: " + login.Message);

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

    [Fact]
    public async Task Delete_restore_and_erase_on_a_real_grid()
    {
        var (grid, paths, s, _) = Make();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await grid.StartAsync(ct);
            await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            using var alerts = new AlertListener(s.WebPort, grid.OnRegionAlert);
            await grid.CreateRegionAsync("Welcome", "island", ct);
            var isle = await grid.AddRegionAsync(new NewRegion("Isle", 1010, 1010, 1, "island", null), ct);
            var pidFile = Path.Combine(paths.Pids, isle.Instance + ".pid");
            var pid = PidFile.Read(pidFile);
            Assert.True(pid is not null && ProcessImage.Matches(pid.Value, paths.Data), "Isle's program is not running");
            Assert.Equal(1, await CountAsync(grid, "SELECT COUNT(*) FROM `robust`.`regions` WHERE `uuid` = @id", isle.Uuid, ct));
            Assert.True(await CountAsync(grid, Terrain, isle.Uuid, ct) > 0, "Isle has no terrain rows");

            // Delete: its program stops and leaves, the map loses it, its land stays.
            await grid.DeleteRegionAsync(isle.Uuid, ct);
            Assert.False(ProcessImage.IsAlive(pid!.Value) && ProcessImage.Matches(pid.Value, paths.Data), "Isle's program still runs");
            Assert.DoesNotContain(grid.GetStatus().Programs, p => p.Instance == isle.Instance);
            Assert.DoesNotContain(grid.GetStatus().Regions, r => r.Id == isle.Uuid);
            Assert.Equal(0, await CountAsync(grid, "SELECT COUNT(*) FROM `robust`.`regions` WHERE `uuid` = @id", isle.Uuid, ct));
            Assert.True(await CountAsync(grid, Terrain, isle.Uuid, ct) > 0, "the delete removed Isle's terrain rows");
            var space = await grid.DeletedSpaceAsync(ct);
            Assert.True(space is not null && space[isle.Uuid] > 0, "Recently deleted shows no space for Isle");

            // Restore, then Start: it takes logins again at the same spot.
            await grid.RestoreRegionAsync(isle.Uuid, ct);
            await grid.RegionPowerAsync(isle.Uuid, RegionPower.Start, ct);
            await WaitUntilAsync(() => grid.GetStatus().Regions.Any(r => r.Id == isle.Uuid && r.State == RegionState.Running && r.LoginsEnabled),
                TimeSpan.FromMinutes(5), ct);
            var restored = RegionModel.Find(grid.CurrentSettings(), isle.Uuid)!;
            Assert.Equal((1010, 1010), (restored.X, restored.Y));
            Assert.Equal(1, await CountAsync(grid, "SELECT COUNT(*) FROM `robust`.`regions` WHERE `uuid` = @id AND `locX` = 1010 * 256 AND `locY` = 1010 * 256",
                isle.Uuid, ct));
            var login = await XmlRpcLogin.LoginAsync(_http, grid.GetStatus().LoginUri, "Test", "Owner", "pass-word1", "uri:Isle&128&128&25", ct);
            Assert.True(login.Ok, "login to the restored region refused: " + login.Message);
            Assert.Equal(restored.UdpPort, login.SimPort);

            // Delete forever: every row goes, and the name is free again.
            await grid.DeleteRegionAsync(isle.Uuid, ct);
            await grid.EraseRegionAsync(isle.Uuid, ct);
            Assert.Null(RegionModel.Find(grid.CurrentSettings(), isle.Uuid));
            Assert.Equal(0, await CountAsync(grid, Terrain, isle.Uuid, ct));
            Assert.Equal(0, await CountAsync(grid, "SELECT COUNT(*) FROM `opensim`.`land` WHERE `RegionUUID` = @id", isle.Uuid, ct));
            Assert.Equal(0, await CountAsync(grid, "SELECT COUNT(*) FROM `opensim`.`regionsettings` WHERE `regionUUID` = @id", isle.Uuid, ct));
            Assert.Equal(0, await CountAsync(grid, "SELECT COUNT(*) FROM `robust`.`regions` WHERE `uuid` = @id", isle.Uuid, ct));
            var again = await grid.AddRegionAsync(new NewRegion("Isle", 1010, 1010, 1, "island", null), ct);
            Assert.NotEqual(isle.Uuid, again.Uuid);
            Assert.Equal(RegionState.Running, grid.GetStatus().Regions.Single(r => r.Id == again.Uuid).State);

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

    // ---- estates (plan 7b) --------------------------------------------------------------------------

    [Fact]
    public async Task Region_moves_to_owner_estate_and_back()
    {
        var (grid, paths, s, robust) = Make();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await grid.StartAsync(ct);
            await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            using var alerts = new AlertListener(s.WebPort, grid.OnRegionAlert);
            await grid.CreateRegionAsync("Welcome", "island", ct);
            var beach = await grid.AddRegionAsync(new NewRegion("Beach", 1001, 1000, 1, "island", "Welcome"), ct);
            var gridEstate = await EstateIdAsync(grid, beach.Uuid, ct);

            // A guest: a second avatar on the grid.
            var annId = Guid.NewGuid();
            var robustConsole = new RestConsoleClient(_http, new Uri($"http://127.0.0.1:{s.RobustConsolePort}"), s.Console.User, s.Console.Password);
            try { await GridConsoleCommands.CreateUserAsync(robustConsole, "Ann", "Guest", "pass-word2", null, annId, TimeSpan.FromSeconds(2), ct); }
            finally { await robustConsole.CloseAsync(); }
            await WaitUntilAsync(() => robust.GetAccountAsync("Ann", "Guest", ct).GetAwaiter().GetResult() is not null, TimeSpan.FromSeconds(30), ct);

            var rights = new DreamGrid.Launcher.Guests.RegionRights(Path.Combine(paths.Data, "guests", "region-rights.json"), TimeProvider.System);
            using var audit = new Core.Logs.AuditLog(Path.Combine(paths.Logs, "audit.log"), TimeProvider.System);
            var sync = new DreamGrid.Launcher.Guests.RegionEstateSync(grid, rights, audit, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DreamGrid.Launcher.Guests.RegionEstateSync>.Instance);

            // Granted: it moves into Ann's own estate the next time the sync looks.
            rights.Grant(beach.Uuid, annId, "Ann Guest");
            await sync.SyncOnceAsync(ct);
            Assert.Null(rights.Of(beach.Uuid)!.Pending);
            var annEstate = rights.EstateOf(annId);
            Assert.NotNull(annEstate);
            Assert.NotEqual(gridEstate, annEstate);
            Assert.Equal(gridEstate, rights.HomeEstateOf(beach.Uuid));
            Assert.Equal("Ann Guest Estate", await EstateNameAsync(grid, beach.Uuid, ct));
            var console = ConsoleOf(grid, beach.Uuid);
            try
            {
                var shown = await console.RunAsync("estate show", TimeSpan.FromSeconds(2), ct);
                Assert.Contains(shown, l => l.Contains("Ann Guest Estate", StringComparison.Ordinal) && l.Contains($" {annEstate} ", StringComparison.Ordinal)
                    && l.TrimEnd().EndsWith("Ann Guest", StringComparison.Ordinal));
            }
            finally { await console.CloseAsync(); }

            // It stays there through a restart and a change of how it runs.
            await grid.RegionPowerAsync(beach.Uuid, RegionPower.Restart, ct);
            await WaitUntilAsync(() => grid.GetStatus().Regions.All(r => r.State == RegionState.Running), TimeSpan.FromMinutes(5), ct);
            Assert.Equal(annEstate, await EstateIdAsync(grid, beach.Uuid, ct));
            await grid.ChangeRunsWithAsync(beach.Uuid, null, ct);
            await WaitUntilAsync(() => grid.GetStatus().Regions.All(r => r.State == RegionState.Running), TimeSpan.FromMinutes(5), ct);
            Assert.Empty(grid.GetStatus().Regions.Single(r => r.Id == beach.Uuid).RunsWith);
            Assert.Equal(annEstate, await EstateIdAsync(grid, beach.Uuid, ct));

            // Rights taken away: back in the grid's estate; Ann's estate stays.
            rights.Remove(beach.Uuid);
            await sync.SyncOnceAsync(ct);
            Assert.Null(rights.Of(beach.Uuid));
            Assert.Equal(gridEstate, await EstateIdAsync(grid, beach.Uuid, ct));
            Assert.Equal(1L, await ScalarAsync(grid, "SELECT COUNT(*) FROM `opensim`.`estate_settings` WHERE `EstateID` = @id", annEstate!.Value, ct));

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

    private static async Task<int?> EstateIdAsync(GridController grid, Guid region, CancellationToken ct) =>
        await ScalarAsync(grid, "SELECT `EstateID` FROM `opensim`.`estate_map` WHERE `RegionID` = @id", region.ToString(), ct) is { } id ? Convert.ToInt32(id) : null;

    private static async Task<string?> EstateNameAsync(GridController grid, Guid region, CancellationToken ct) =>
        (string?)await ScalarAsync(grid, "SELECT s.`EstateName` FROM `opensim`.`estate_map` m JOIN `opensim`.`estate_settings` s " +
            "ON s.`EstateID` = m.`EstateID` WHERE m.`RegionID` = @id", region.ToString(), ct);

    private const string Terrain = "SELECT COUNT(*) FROM `opensim`.`terrain` WHERE `RegionUUID` = @id";

    /// <summary>Runs a COUNT(*) query with <c>@id</c> as the region's id, as root on the grid's own MariaDB.</summary>
    private static async Task<long> CountAsync(GridController grid, string sql, Guid id, CancellationToken ct) =>
        Convert.ToInt64(await ScalarAsync(grid, sql, id.ToString(), ct));

    /// <summary>Runs a query with <c>@id</c> as <paramref name="id"/>, as root on the grid's own MariaDB; null for no row.</summary>
    private static async Task<object?> ScalarAsync(GridController grid, string sql, object id, CancellationToken ct)
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
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteScalarAsync(ct) is { } value and not DBNull ? value : null;
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

    [Fact]
    public async Task A_world_named_after_Robust_started_shows_as_outdated_until_a_restart()
    {
        var (grid, paths, s, _) = Make();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await grid.StartAsync(ct);
            Assert.False(grid.GetStatus().RobustOutdated);

            grid.UpdateSettings(l => l with { GridName = "Renamed World" });
            Assert.True(grid.GetStatus().RobustOutdated);

            await grid.RestartComponentAsync("robust", ct);
            Assert.False(grid.GetStatus().RobustOutdated);
            var info = await _http.GetStringAsync($"http://127.0.0.1:{s.RobustPublicPort}/get_grid_info", ct);
            Assert.Contains("<gridname>Renamed World</gridname>", info);

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

    [Fact]
    public async Task Port_conflict_on_Robust_public_port_is_reported_before_anything_starts()
    {
        var (grid, paths, s, _) = Make();
        var holder = new TcpListener(IPAddress.Any, s.RobustPublicPort);
        holder.Start();
        try
        {
            var e = await Assert.ThrowsAsync<PortConflictException>(() => grid.StartAsync(TestContext.Current.CancellationToken));
            Assert.Contains(s.RobustPublicPort.ToString(), e.Message);
            Assert.Null(PidFile.Read(Path.Combine(paths.Pids, "mariadb.pid")));
            Assert.False(AnyProcessMatches(paths.MariaDbData), "MariaDB was started");
        }
        finally
        {
            holder.Stop();
            await grid.DetachAsync();
        }
    }
}

/// <summary>Records reported stages synchronously (Progress&lt;T&gt; would post them to the thread pool, out of order).</summary>
internal sealed class StageRecorder : IProgress<string>
{
    public List<string> Stages { get; } = new();

    public void Report(string value)
    {
        lock (Stages) Stages.Add(value);
    }
}
