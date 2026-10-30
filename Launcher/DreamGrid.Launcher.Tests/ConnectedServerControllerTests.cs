using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Ini;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Plans;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>
/// GridController on a Connected Server: like a Region Server it never starts Robust and watches the grid it uses, but it
/// reads that grid's get_grid_info on each start, finds the estate owner on that grid, and places a first region in the
/// map window near 1000,1000. The rig is <see cref="OtherGridControllerRig"/>.
/// </summary>
public sealed class ConnectedServerControllerTests : OtherGridControllerRig
{
    private const string Address = "http://10.0.0.9:8002/";

    private static readonly ConnectedGrid Elsewhere =
        new(null, "Elsewhere", Address, Address, "http://10.0.0.9:8003/", Address);

    public ConnectedServerControllerTests() : base(s => s with { GridName = "Elsewhere", Plan = OwnerPlan.Connected, Connected = Elsewhere })
    {
        _otherGrid.Result = new OtherGridInfo("Elsewhere", Address, Address, false);
    }

    [Fact]
    public async Task Connected_start_never_starts_robust()
    {
        var grid = NewGrid();

        await grid.StartAsync(TestContext.Current.CancellationToken);

        var status = grid.GetStatus();
        Assert.Equal(ProcessState.Running, status.Database.State);
        Assert.Equal(ProcessState.Running, status.Robust.State); // the other grid answered
        Assert.False(status.RunsRobust);
        Assert.Equal(Address, status.OtherGridAddress);
        Assert.Null(status.StartFailure);
        Assert.False(File.Exists(Path.Combine(_paths.Pids, "robust.pid")));
        Assert.Equal([new Uri(Address)], _otherGrid.Reads);
        Assert.Empty(_join.Reads);
    }

    [Fact]
    public async Task Connected_start_rereads_grid_info()
    {
        AddWelcome(created: false);
        _otherGrid.Result = new OtherGridInfo("Elsewhere 2", "http://10.0.0.10:8002/", null, false);
        var grid = NewGrid();

        await grid.StartAsync(TestContext.Current.CancellationToken);

        var s = _store.LoadOrCreate();
        Assert.Equal("Elsewhere 2", s.GridName);
        // The address, the services address and the preset are the owner's choice; the rest is what the grid publishes.
        Assert.Equal(Elsewhere with { GridName = "Elsewhere 2", LoginUri = "http://10.0.0.10:8002/", GatekeeperUri = null }, s.Connected);
        var os = IniDocument.Load(Path.Combine(_paths.RegionDir("Welcome"), "OpenSim.ini"));
        Assert.Equal("config-include/Grid.ini", os.Get("Architecture", "Include-Architecture"));
    }

    [Fact]
    public async Task Connected_start_keeps_an_address_changed_while_it_read()
    {
        AddWelcome(created: false);
        var other = Elsewhere with { GridName = "Yonder", Address = "http://10.0.0.20:8002/" };
        _otherGrid.Result = new OtherGridInfo("Elsewhere 2", Address, Address, false);
        var grid = NewGrid();
        _otherGrid.OnRead = () => grid.UpdateSettings(l => l with { Connected = other, GridName = "Yonder" });

        await grid.StartAsync(TestContext.Current.CancellationToken);

        var s = _store.LoadOrCreate();
        Assert.Equal(other, s.Connected);
        Assert.Equal("Yonder", s.GridName);
    }

    [Fact]
    public async Task Connected_start_waits_when_the_other_grid_is_off()
    {
        AddWelcome();
        _otherGrid.Error = new GridJoinException($"Nothing answered at {Address}.", noAnswer: true);
        var grid = NewGrid();

        await Assert.ThrowsAsync<OtherGridUnreachableException>(() => grid.StartAsync(TestContext.Current.CancellationToken));

        var status = grid.GetStatus();
        Assert.Contains("Elsewhere isn't answering at http://10.0.0.9:8002/", status.StartFailure!.Message);
        Assert.Equal(ProcessState.Running, status.Database.State);
        Assert.Equal(ProcessState.Failed, status.Robust.State);
        Assert.Contains("Elsewhere isn't answering", status.Robust.LastError);
        Assert.All(status.Programs, p => Assert.Equal(ProcessState.Stopped, p.Process.State));
        Assert.False(File.Exists(Path.Combine(_paths.Pids, "Welcome.pid")));
        Assert.Equal(Elsewhere, _store.LoadOrCreate().Connected);
    }

    [Fact]
    public async Task Connected_start_names_an_answer_that_is_not_a_grid()
    {
        AddWelcome();
        const string reason = "http://10.0.0.9:8002/ answered, but it didn't answer like a grid.";
        _otherGrid.Error = new GridJoinException(reason);
        var grid = NewGrid();

        var e = await Assert.ThrowsAsync<GridJoinException>(() => grid.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(reason, e.Message);
        Assert.Equal(reason, grid.GetStatus().StartFailure!.Message);
        Assert.Equal(Elsewhere, _store.LoadOrCreate().Connected);
    }

    [Fact]
    public async Task Connected_refuses_robust_operations()
    {
        var ct = TestContext.Current.CancellationToken;
        var grid = NewGrid();

        foreach (var operation in new Func<Task>[]
                 {
                     () => grid.StartComponentAsync("robust", ct),
                     () => grid.StopComponentAsync("robust", ct),
                     () => grid.RestartComponentAsync("robust", ct),
                 })
        {
            var e = await Assert.ThrowsAnyAsync<InvalidOperationException>(operation);
            Assert.Contains("doesn't run a grid service", e.Message);
        }
        Assert.False(File.Exists(Path.Combine(_paths.Pids, "robust.pid")));
    }

    [Fact]
    public async Task Region_power_start_is_refused_while_the_other_grid_does_not_answer()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = AddWelcome();
        _otherGrid.Error = new GridJoinException($"Nothing answered at {Address}.", noAnswer: true);
        var grid = NewGrid();
        await Assert.ThrowsAsync<OtherGridUnreachableException>(() => grid.StartAsync(ct));

        var e = await Assert.ThrowsAsync<OtherGridUnreachableException>(() => grid.RegionPowerAsync(id, RegionPower.Start, ct));

        Assert.Equal(OtherGridUnreachableException.MessageFor("Elsewhere", Address), e.Message);
        Assert.False(File.Exists(Path.Combine(_paths.Pids, "Welcome.pid")));
    }

    [Fact]
    public async Task Estate_owner_on_the_other_grid()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        _robust.Accounts.Add(new RobustAccount(id, "Ann", "Lee", 0, ""));
        var grid = NewGrid();

        var account = await grid.SetEstateOwnerAsync("ann", "lee", ct);

        Assert.Equal(id, account.PrincipalId);
        Assert.Equal(new OwnerInfo("Ann", "Lee", id), _store.LoadOrCreate().Setup.Owner);

        var e = await Assert.ThrowsAsync<ArgumentException>(() => grid.SetEstateOwnerAsync("Bob", "Ray", ct));
        Assert.Equal("There's no avatar called Bob Ray on Elsewhere. Check the spelling, or sign up with Elsewhere first.", e.Message);
    }

    [Fact]
    public async Task Estate_owner_lookup_without_an_answer_says_the_other_grid_is_off()
    {
        _robust.AccountError = new HttpRequestException("Connection refused");
        var grid = NewGrid();

        var e = await Assert.ThrowsAsync<OtherGridUnreachableException>(() =>
            grid.SetEstateOwnerAsync("Ann", "Lee", TestContext.Current.CancellationToken));

        Assert.Contains("Elsewhere isn't answering at http://10.0.0.9:8002/", e.Message);
    }

    [Fact]
    public async Task First_region_takes_the_first_free_square_in_the_window()
    {
        _robust.Map.Add(new MapRegion(Guid.NewGuid(), "Hub", 1000, 1000, 1));
        var grid = NewGrid();

        // The program can't come up in this rig; the region is saved before it starts.
        await Record.ExceptionAsync(() => grid.CreateRegionAsync("Welcome", "island", TestContext.Current.CancellationToken));

        var region = Assert.Single(_store.LoadOrCreate().Regions);
        Assert.Equal((1001, 1000), (region.X, region.Y));
        Assert.Equal(new MapArea?[] { MapArea.Start }, _robust.MapQueries);
    }

    [Fact]
    public async Task First_region_with_a_full_window_is_refused()
    {
        for (var x = 1000; x < 1064; x += 16)
            for (var y = 1000; y < 1064; y += 16)
                _robust.Map.Add(new MapRegion(Guid.NewGuid(), $"Big {x},{y}", x, y, 16));
        var grid = NewGrid();

        var e = await Assert.ThrowsAsync<ArgumentException>(() =>
            grid.CreateRegionAsync("Welcome", "island", TestContext.Current.CancellationToken));

        Assert.Equal("The squares near 1000,1000 on Elsewhere's map are all taken. Add your region from the Regions page at a spot you choose.",
            e.Message);
        Assert.Empty(_store.LoadOrCreate().Regions);
    }

    [Fact]
    public async Task Adding_or_moving_onto_a_used_square_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = AddWelcome();
        _robust.Map.Add(new MapRegion(id, "Welcome", 1000, 1000, 1));
        _robust.Map.Add(new MapRegion(Guid.NewGuid(), "Hub", 1001, 1000, 1));
        var grid = NewGrid();
        var before = File.ReadAllText(_paths.SettingsFile);

        var add = await Assert.ThrowsAsync<ArgumentException>(() =>
            grid.AddRegionAsync(new NewRegion("Beach", 1001, 1000, 1, "island", null), ct));
        var move = await Assert.ThrowsAsync<ArgumentException>(() => grid.MoveRegionAsync(id, 1001, 1000, ct));

        Assert.Contains("Hub", add.Message);
        Assert.Contains("Hub", move.Message);
        Assert.Equal(before, File.ReadAllText(_paths.SettingsFile));
        Assert.Equal(new MapArea?[] { MapArea.Around(1001, 1000, 1), MapArea.Around(1001, 1000, 1) }, _robust.MapQueries);
    }

    [Fact]
    public async Task Joined_region_count_is_zero()
    {
        _robust.Map.Add(new MapRegion(Guid.NewGuid(), "Hub", 1001, 1000, 1));
        var grid = NewGrid();

        Assert.Equal(0, await grid.JoinedRegionCountAsync(TestContext.Current.CancellationToken));
    }
}
