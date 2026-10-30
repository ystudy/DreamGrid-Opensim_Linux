using System.Diagnostics;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Ini;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// GridController on a Region Server: it never starts Robust, reads the Grid Asset Server's join information on each
/// start, watches whether that server answers, finds the estate owner by name and places regions clear of other
/// computers' regions. The rig is <see cref="OtherGridControllerRig"/>.
/// </summary>
public sealed class RegionServerControllerTests : OtherGridControllerRig
{
    private static readonly JoinedGrid Avalon =
        new("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", "7.1.0", false, 8004, 8006);

    public RegionServerControllerTests() : base(s => s with { GridName = "Avalon", Plan = OwnerPlan.RegionServer, Join = Avalon })
    {
        _join.Result = Avalon;
    }

    [Fact]
    public async Task Region_server_start_never_starts_robust()
    {
        var grid = NewGrid();

        await grid.StartAsync(TestContext.Current.CancellationToken);

        var status = grid.GetStatus();
        Assert.Equal(ProcessState.Running, status.Database.State);
        Assert.Equal(ProcessState.Running, status.Robust.State); // the Grid Asset Server answered
        Assert.False(status.RunsRobust);
        Assert.Equal("192.168.1.5:8002", status.OtherGridAddress);
        Assert.Equal("http://192.168.1.5:8002/", status.LoginUri);
        Assert.False(status.RobustOutdated);
        Assert.Null(status.StartFailure);
        Assert.False(File.Exists(Path.Combine(_paths.Pids, "robust.pid")));
        Assert.Equal([("192.168.1.5", 8002)], _join.Reads);
    }

    [Fact]
    public async Task Region_server_start_rereads_the_join_file()
    {
        AddWelcome(created: false);
        _join.Result = Avalon with { GridName = "Avalon 2", Hypergrid = true, Host = "elsewhere", PublicPort = 9999 };
        var grid = NewGrid();

        await grid.StartAsync(TestContext.Current.CancellationToken);

        var s = _store.LoadOrCreate();
        Assert.Equal("Avalon 2", s.GridName);
        Assert.Equal("Avalon 2", s.Join!.GridName);
        Assert.True(s.Join.Hypergrid);
        // The address is the one the owner chose.
        Assert.Equal(("192.168.1.5", 8002), (s.Join.Host, s.Join.PublicPort));
        var os = IniDocument.Load(Path.Combine(_paths.RegionDir("Welcome"), "OpenSim.ini"));
        Assert.Equal("config-include/GridHypergrid.ini", os.Get("Architecture", "Include-Architecture"));
    }

    [Fact]
    public async Task Region_server_start_waits_when_the_grid_asset_server_is_off()
    {
        AddWelcome();
        _join.Error = new GridJoinException("Nothing answered at 192.168.1.5:8002.", noAnswer: true);
        var before = _store.LoadOrCreate().Join;
        var grid = NewGrid();

        await Assert.ThrowsAsync<GridAssetUnreachableException>(() => grid.StartAsync(TestContext.Current.CancellationToken));

        var status = grid.GetStatus();
        Assert.Contains("192.168.1.5:8002", status.StartFailure!.Message);
        Assert.Equal(ProcessState.Running, status.Database.State);
        Assert.Equal(ProcessState.Failed, status.Robust.State);
        Assert.All(status.Programs, p => Assert.Equal(ProcessState.Stopped, p.Process.State));
        Assert.False(File.Exists(Path.Combine(_paths.Pids, "Welcome.pid")));
        Assert.Equal(before, _store.LoadOrCreate().Join);
    }

    [Fact]
    public void Region_server_status_before_the_first_check_is_stopped()
    {
        var grid = NewGrid();

        var status = grid.GetStatus();

        Assert.Equal(ProcessState.Stopped, status.Robust.State);
        Assert.False(status.RunsRobust);
    }

    [Fact]
    public async Task Region_server_watches_the_grid_asset_server()
    {
        var ct = TestContext.Current.CancellationToken;
        var grid = NewGrid(watchEvery: TimeSpan.FromMilliseconds(50));
        await grid.StartAsync(ct);
        Assert.Equal(ProcessState.Running, grid.GetStatus().Robust.State);

        _robust.Ready = false;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (grid.GetStatus().Robust.State != ProcessState.Failed && DateTime.UtcNow < deadline) await Task.Delay(20, ct);
        Assert.Equal(ProcessState.Failed, grid.GetStatus().Robust.State);

        _robust.Ready = true;
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (grid.GetStatus().Robust.State != ProcessState.Running && DateTime.UtcNow < deadline) await Task.Delay(20, ct);
        Assert.Equal(ProcessState.Running, grid.GetStatus().Robust.State);
    }

    [Fact]
    public async Task Region_server_refuses_robust_operations()
    {
        var ct = TestContext.Current.CancellationToken;
        var grid = NewGrid();

        foreach (var operation in new Func<Task>[]
                 {
                     () => grid.StartComponentAsync("robust", ct),
                     () => grid.StopComponentAsync("robust", ct),
                     () => grid.RestartComponentAsync("robust", ct),
                     () => grid.CreateOwnerAsync("Ann", "Lee", "secret1", null, ct),
                 })
        {
            var e = await Assert.ThrowsAnyAsync<InvalidOperationException>(operation);
            Assert.Contains("doesn't run a grid service", e.Message);
        }
        Assert.False(File.Exists(Path.Combine(_paths.Pids, "robust.pid")));
    }

    [Fact]
    public async Task Estate_owner_is_found_by_name()
    {
        var id = Guid.NewGuid();
        _robust.Accounts.Add(new RobustAccount(id, "Ann", "Lee", 0, ""));
        var grid = NewGrid();

        var account = await grid.SetEstateOwnerAsync("ann", "lee", TestContext.Current.CancellationToken);

        Assert.Equal(id, account.PrincipalId);
        var setup = _store.LoadOrCreate().Setup;
        Assert.True(setup.OwnerCreated);
        Assert.Equal(new OwnerInfo("Ann", "Lee", id), setup.Owner);
    }

    [Fact]
    public async Task Estate_owner_not_on_the_grid_is_refused()
    {
        var grid = NewGrid();
        var before = File.ReadAllText(_paths.SettingsFile);

        var e = await Assert.ThrowsAsync<ArgumentException>(() => grid.SetEstateOwnerAsync("Ann", "Lee", TestContext.Current.CancellationToken));

        Assert.Equal("There's no avatar called Ann Lee on Avalon. Check the spelling, or create the avatar on the Grid Asset Server first.", e.Message);
        Assert.Equal(before, File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public async Task First_region_avoids_squares_used_elsewhere()
    {
        _robust.Map.Add(new MapRegion(Guid.NewGuid(), "Hub", 1000, 1000, 1));
        var grid = NewGrid();

        // The program can't come up in this rig; the region is saved before it starts.
        await Record.ExceptionAsync(() => grid.CreateRegionAsync("Welcome", "island", TestContext.Current.CancellationToken));

        var region = Assert.Single(_store.LoadOrCreate().Regions);
        Assert.Equal((1001, 1000), (region.X, region.Y));
    }

    [Fact]
    public async Task First_region_is_not_placed_blindly_when_the_grid_asset_server_is_off()
    {
        _robust.MapError = new HttpRequestException("Connection refused");
        var grid = NewGrid();

        var e = await Assert.ThrowsAsync<GridAssetUnreachableException>(() =>
            grid.CreateRegionAsync("Welcome", "island", TestContext.Current.CancellationToken));

        Assert.Contains("192.168.1.5:8002", e.Message);
        Assert.Empty(_store.LoadOrCreate().Regions);
    }

    [Fact]
    public async Task Adding_a_region_on_a_used_square_is_refused_on_a_region_server()
    {
        AddWelcome();
        _robust.Map.Add(new MapRegion(Guid.NewGuid(), "Hub", 1001, 1000, 1));
        var grid = NewGrid();
        var before = File.ReadAllText(_paths.SettingsFile);

        var e = await Assert.ThrowsAsync<ArgumentException>(() =>
            grid.AddRegionAsync(new NewRegion("Beach", 1001, 1000, 1, "island", null), TestContext.Current.CancellationToken));

        Assert.Contains("Hub", e.Message);
        Assert.Equal(before, File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public async Task Moving_a_region_onto_a_used_square_is_refused_on_a_region_server()
    {
        var id = AddWelcome();
        _robust.Map.Add(new MapRegion(id, "Welcome", 1000, 1000, 1));
        _robust.Map.Add(new MapRegion(Guid.NewGuid(), "Hub", 1005, 1000, 1));
        var grid = NewGrid();
        var before = File.ReadAllText(_paths.SettingsFile);

        var e = await Assert.ThrowsAsync<ArgumentException>(() => grid.MoveRegionAsync(id, 1005, 1000, TestContext.Current.CancellationToken));

        Assert.Contains("Hub", e.Message);
        Assert.Equal(before, File.ReadAllText(_paths.SettingsFile));
        Assert.Equal(new MapArea?[] { null }, _robust.MapQueries); // the whole map
    }

    [Fact]
    public async Task Joined_region_count_ignores_our_own()
    {
        var ours = AddWelcome();
        Save(s => s with { Plan = OwnerPlan.GridAsset, Join = null });
        _robust.Map.Add(new MapRegion(ours, "Welcome", 1000, 1000, 1));
        _robust.Map.Add(new MapRegion(Guid.NewGuid(), "Hub", 1001, 1000, 1));
        var grid = NewGrid();

        Assert.Equal(1, await grid.JoinedRegionCountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Joined_region_count_needs_robust_answering()
    {
        Save(s => s with { Plan = OwnerPlan.GridAsset, Join = null });
        _robust.MapError = new HttpRequestException("Connection refused");
        var grid = NewGrid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => grid.JoinedRegionCountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Region_server_start_names_a_grid_that_is_not_a_grid_asset_server()
    {
        AddWelcome();
        const string reason = "The grid at 192.168.1.5:8002 runs as a Local grid. A Region Server can only join a Grid Asset Server.";
        _join.Error = new GridJoinException(reason);
        var before = _store.LoadOrCreate().Join;
        var grid = NewGrid();

        var e = await Assert.ThrowsAnyAsync<Exception>(() => grid.StartAsync(TestContext.Current.CancellationToken));

        Assert.IsNotType<GridAssetUnreachableException>(e);
        var status = grid.GetStatus();
        Assert.Contains("runs as a Local grid", status.StartFailure!.Message);
        Assert.DoesNotContain("isn't answering", status.StartFailure.Message);
        Assert.Equal(ProcessState.Failed, status.Robust.State);
        Assert.Equal(before, _store.LoadOrCreate().Join);
    }

    [Fact]
    public async Task Region_server_start_keeps_a_join_re_pointed_while_it_read()
    {
        AddWelcome(created: false);
        var elsewhere = Avalon with { Host = "192.168.1.9", GridName = "Elsewhere", LoginUri = "http://192.168.1.9:8002/" };
        _join.Result = Avalon with { GridName = "Avalon 2" };
        var grid = NewGrid();
        // Advanced -> Grid Asset Server saves another server after this start read the old one, before it saves.
        _join.OnRead = () => grid.UpdateSettings(l => l with { Join = elsewhere, GridName = "Elsewhere" });

        await grid.StartAsync(TestContext.Current.CancellationToken);

        var s = _store.LoadOrCreate();
        Assert.Equal(elsewhere, s.Join);
        Assert.Equal("Elsewhere", s.GridName);
    }

    [Fact]
    public async Task Region_power_start_is_refused_while_the_grid_asset_server_does_not_answer()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = AddWelcome();
        _join.Error = new GridJoinException("Nothing answered at 192.168.1.5:8002.", noAnswer: true);
        var grid = NewGrid();
        await Assert.ThrowsAsync<GridAssetUnreachableException>(() => grid.StartAsync(ct));
        Assert.Equal(ProcessState.Failed, grid.GetStatus().Robust.State);

        var e = await Assert.ThrowsAsync<GridAssetUnreachableException>(() => grid.RegionPowerAsync(id, RegionPower.Start, ct));

        Assert.Contains("192.168.1.5:8002", e.Message);
        Assert.False(File.Exists(Path.Combine(_paths.Pids, "Welcome.pid")));
    }

    [Fact]
    public async Task Adding_a_region_when_the_map_does_not_answer_says_the_grid_asset_server_is_off()
    {
        AddWelcome();
        _robust.MapError = new HttpRequestException("Connection refused");
        var grid = NewGrid();
        var before = File.ReadAllText(_paths.SettingsFile);

        var e = await Assert.ThrowsAsync<GridAssetUnreachableException>(() =>
            grid.AddRegionAsync(new NewRegion("Beach", 1001, 1000, 1, "island", null), TestContext.Current.CancellationToken));

        Assert.Contains("192.168.1.5:8002", e.Message);
        Assert.Equal(before, File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public async Task A_robust_left_running_before_the_plan_was_chosen_stops_on_a_region_server()
    {
        // A fresh install starts before the owner picks a plan; that start runs this computer's Robust (here a stand-in
        // adopted through robust.pid). Once the plan is Region Server, the next start must stop it: it would otherwise
        // count as the grid service and hide a Grid Asset Server that is off.
        var ct = TestContext.Current.CancellationToken;
        Save(s => s with { Plan = null, Join = null });
        var robustDll = Path.Combine(_paths.RuntimeOpenSim, "Robust.dll");
        var robustPid = Path.Combine(_paths.Pids, "robust.pid");
        using var fake = Process.Start(StandIn(robustDll))!;
        try
        {
            _paths.EnsureCreated();
            PidFile.Write(robustPid, fake.Id);
            _consoles.OnCommand = port =>
            {
                // What Robust does on "quit".
                if (port == _ports[5])
                    try { fake.Kill(); } catch (InvalidOperationException) { }
            };
            var grid = NewGrid();
            await grid.StartAsync(ct);
            Assert.True(grid.GetStatus().RunsRobust);
            Assert.Equal(ProcessState.Running, grid.GetStatus().Robust.State);

            var id = AddWelcome();
            Save(s => s with { Plan = OwnerPlan.RegionServer, Join = Avalon });
            _join.Error = new GridJoinException("Nothing answered at 192.168.1.5:8002.", noAnswer: true);
            await Assert.ThrowsAsync<GridAssetUnreachableException>(() => grid.StartAsync(ct));

            Assert.True(fake.WaitForExit(10_000), "this computer's Robust still runs");
            Assert.Null(PidFile.ReadLive(robustPid, robustDll));
            await Assert.ThrowsAsync<GridAssetUnreachableException>(() => grid.RegionPowerAsync(id, RegionPower.Start, ct));
        }
        finally
        {
            if (!fake.HasExited) fake.Kill();
        }
    }
}
