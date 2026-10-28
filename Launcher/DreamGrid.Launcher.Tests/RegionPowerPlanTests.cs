using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Processes;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RegionPowerPlanTests
{
    private static readonly RegionSettings Home = new("Home", "Home", Guid.NewGuid(), 1000, 1000, UdpPort: 8004);
    private static readonly RegionSettings Beach = new("Home", "Beach", Guid.NewGuid(), 1001, 1000, UdpPort: 8005);
    private static readonly RegionSettings Isle = new("Isle", "Isle", Guid.NewGuid(), 1010, 1010, UdpPort: 8006);

    private static LauncherSettings World(params RegionSettings[] regions) => TestSettings.Create() with
    {
        Programs = [new ProgramSettings("Home", 8004, 8011), new ProgramSettings("Isle", 8006, 8012)],
        Regions = regions.ToList(),
    };

    private static readonly IReadOnlySet<Guid> NoneHeld = new HashSet<Guid>();
    private static readonly IReadOnlySet<string> AllRunning = new HashSet<string> { "Home", "Isle" };

    [Fact]
    public void Stop_in_shared_program_restarts_it_without_the_region()
    {
        var steps = RegionPowerPlan.For(World(Home, Beach, Isle), NoneHeld, AllRunning, Beach.Uuid, RegionPower.Stop);

        Assert.Contains(Beach.Uuid, steps.Held);
        Assert.Equal(["Home"], steps.Restart);
        Assert.Empty(steps.Stop);
        Assert.Equal(["Home"], steps.Affected);
    }

    [Fact]
    public void Stopping_last_region_stops_its_program()
    {
        var steps = RegionPowerPlan.For(World(Home, Beach, Isle), NoneHeld, AllRunning, Isle.Uuid, RegionPower.Stop);

        Assert.Equal(["Isle"], steps.Stop);
        Assert.Empty(steps.Restart);
        Assert.Empty(steps.Affected);
    }

    [Fact]
    public void Park_is_saved_and_unpark_starts_nothing()
    {
        var parked = RegionPowerPlan.For(World(Home, Beach), NoneHeld, AllRunning, Beach.Uuid, RegionPower.Park);
        Assert.True(RegionModel.Find(parked.Settings, Beach.Uuid)!.Parked);
        Assert.DoesNotContain(Beach.Uuid, parked.Held);
        Assert.Equal(["Home"], parked.Restart);

        var unparked = RegionPowerPlan.For(parked.Settings, parked.Held, AllRunning, Beach.Uuid, RegionPower.Unpark);
        Assert.False(RegionModel.Find(unparked.Settings, Beach.Uuid)!.Parked);
        Assert.Empty(unparked.Start);
        Assert.Empty(unparked.Restart);
        Assert.Empty(unparked.Stop);
    }

    [Fact]
    public void Unpark_and_start_loads_it()
    {
        var steps = RegionPowerPlan.For(World(Home, Beach with { Parked = true }), NoneHeld, AllRunning, Beach.Uuid, RegionPower.UnparkAndStart);

        Assert.False(RegionModel.Find(steps.Settings, Beach.Uuid)!.Parked);
        Assert.Equal(["Home"], steps.Restart);
    }

    [Fact]
    public void Start_of_a_held_region_in_a_stopped_program_starts_it()
    {
        var steps = RegionPowerPlan.For(World(Home, Isle), new HashSet<Guid> { Isle.Uuid }, new HashSet<string> { "Home" }, Isle.Uuid, RegionPower.Start);

        Assert.DoesNotContain(Isle.Uuid, steps.Held);
        Assert.Equal(["Isle"], steps.Start);
        Assert.Empty(steps.Affected);
    }

    [Fact]
    public void Restart_names_the_regions_running_with_it()
    {
        var steps = RegionPowerPlan.For(World(Home, Beach), NoneHeld, AllRunning, Home.Uuid, RegionPower.Restart);

        Assert.Equal(["Home"], steps.Restart);
        Assert.Equal(["Beach"], steps.Affected);
    }

    [Fact]
    public void Restart_of_a_parked_region_does_nothing()
    {
        var steps = RegionPowerPlan.For(World(Home, Beach with { Parked = true }), NoneHeld, AllRunning, Beach.Uuid, RegionPower.Restart);

        Assert.Empty(steps.Restart);
        Assert.Empty(steps.Stop);
        Assert.Empty(steps.Start);
    }

    [Fact]
    public void Start_of_a_running_region_does_nothing()
    {
        var loaded = new Dictionary<string, IReadOnlyCollection<Guid>?> { ["Home"] = [Home.Uuid, Beach.Uuid] };

        var steps = RegionPowerPlan.For(World(Home, Beach), NoneHeld, AllRunning, Beach.Uuid, RegionPower.Start, loaded);

        Assert.Empty(steps.Restart);
        Assert.Empty(steps.Start);
        Assert.Empty(steps.Affected);
    }

    [Fact]
    public void Start_of_a_region_its_running_program_does_not_load_restarts_it()
    {
        var loaded = new Dictionary<string, IReadOnlyCollection<Guid>?> { ["Home"] = [Home.Uuid] };

        var steps = RegionPowerPlan.For(World(Home, Beach), NoneHeld, AllRunning, Beach.Uuid, RegionPower.Start, loaded);

        Assert.Equal(["Home"], steps.Restart);
    }

    [Theory]
    [InlineData(ProcessState.Running, true)]
    [InlineData(ProcessState.Starting, true)]
    [InlineData(ProcessState.Crashed, true)]
    [InlineData(ProcessState.Stopping, true)]
    [InlineData(ProcessState.Stopped, false)]
    [InlineData(ProcessState.Failed, false)]
    public void A_crashed_or_starting_program_counts_as_running(ProcessState state, bool active)
    {
        Assert.Equal(active, RegionPowerPlan.IsActive(state));
    }

    [Fact]
    public void Deleted_region_cannot_be_started()
    {
        var gone = Beach with { DeletedAt = DateTimeOffset.UnixEpoch };

        var e = Assert.Throws<InvalidOperationException>(() =>
            RegionPowerPlan.For(World(Home, gone), NoneHeld, AllRunning, Beach.Uuid, RegionPower.Start));
        Assert.Equal("That region has been deleted.", e.Message);
    }

    [Fact]
    public void World_start_restarts_program_missing_a_region()
    {
        var loaded = new Dictionary<string, IReadOnlyCollection<Guid>?> { ["Home"] = [Home.Uuid], ["Isle"] = null };

        Assert.Equal(["Home"], RegionPowerPlan.MissingRegions(World(Home, Beach, Isle), NoneHeld, AllRunning, loaded));
        Assert.Empty(RegionPowerPlan.MissingRegions(World(Home, Beach, Isle), new HashSet<Guid> { Beach.Uuid }, AllRunning, loaded));
    }

    [Fact]
    public void Start_everything_unparks()
    {
        var s = RegionPowerPlan.Unparked(World(Home, Beach with { Parked = true }, Isle with { Parked = true }));

        Assert.All(s.Regions, r => Assert.False(r.Parked));
    }
}
