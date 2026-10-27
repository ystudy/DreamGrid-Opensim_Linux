using DreamGrid.Launcher.Core.Grid;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class GridStatusTests
{
    private static RegionStatus Region(RegionState state, bool logins = false) =>
        new(Guid.NewGuid(), "R", "P", state, logins, 1000, 1000, 1, []);

    [Fact]
    public void Logins_enabled_needs_every_runnable_region()
    {
        var none = new HashSet<Guid>();
        var running = Region(RegionState.Running, logins: true);
        var stopped = Region(RegionState.Stopped);

        Assert.False(GridStatus.ComputeLoginsEnabled([], none));
        Assert.True(GridStatus.ComputeLoginsEnabled([running, Region(RegionState.Parked)], none));
        Assert.True(GridStatus.ComputeLoginsEnabled([running, stopped], new HashSet<Guid> { stopped.Id }));
        Assert.False(GridStatus.ComputeLoginsEnabled([running, stopped], none));
        Assert.False(GridStatus.ComputeLoginsEnabled([running, Region(RegionState.Starting)], none));
    }
}
