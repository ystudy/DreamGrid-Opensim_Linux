using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RegionPlacementTests
{
    private static readonly RegionSettings Home = new("Home", "Home", Guid.NewGuid(), 1000, 1000);

    private static LauncherSettings With(params RegionSettings[] regions) => TestSettings.Create() with { Regions = regions.ToList() };

    [Fact]
    public void Touching_east_edge_is_a_neighbour()
    {
        var check = RegionPlacement.Check(With(Home), 1001, 1000, 1);

        Assert.True(check.Ok);
        Assert.Null(check.OverlapsWith);
        Assert.Equal([new Neighbour("Home", "east")], check.Neighbours);
    }

    [Fact]
    public void Each_side_is_named_from_the_existing_region()
    {
        var s = With(Home);

        Assert.Equal("north", Assert.Single(RegionPlacement.Check(s, 1000, 1001, 1).Neighbours).Side);
        Assert.Equal("south", Assert.Single(RegionPlacement.Check(s, 1000, 999, 1).Neighbours).Side);
        Assert.Equal("west", Assert.Single(RegionPlacement.Check(s, 999, 1000, 1).Neighbours).Side);
    }

    [Fact]
    public void Corner_only_is_not_a_neighbour()
    {
        var check = RegionPlacement.Check(With(Home), 1001, 1001, 1);

        Assert.True(check.Ok);
        Assert.Empty(check.Neighbours);
    }

    [Fact]
    public void Overlap_with_parked_or_deleted_is_refused()
    {
        var parked = Home with { Parked = true };
        var deleted = Home with { DeletedAt = DateTimeOffset.UnixEpoch };

        foreach (var s in new[] { With(parked), With(deleted) })
        {
            var check = RegionPlacement.Check(s, 1000, 1000, 1);
            Assert.False(check.Ok);
            Assert.Equal("Home", check.OverlapsWith);
        }
    }

    [Fact]
    public void Large_region_overlaps_by_area()
    {
        var check = RegionPlacement.Check(With(Home), 998, 998, 4);

        Assert.False(check.Ok);
        Assert.Equal("Home", check.OverlapsWith);
    }

    [Fact]
    public void Large_existing_region_is_a_neighbour_along_its_whole_edge()
    {
        var big = new RegionSettings("Big", "Big", Guid.NewGuid(), 1000, 1000, Size: 4);

        Assert.Equal([new Neighbour("Big", "east")], RegionPlacement.Check(With(big), 1004, 1003, 1).Neighbours);
    }

    [Fact]
    public void Ignore_skips_the_region_itself()
    {
        Assert.True(RegionPlacement.Check(With(Home), 1000, 1000, 1, ignore: Home.Uuid).Ok);
    }
}
