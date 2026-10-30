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

    [Fact]
    public void Check_counts_regions_on_other_computers()
    {
        IReadOnlyList<MapRegion> elsewhere = [new(Guid.NewGuid(), "Hub", 1000, 1000, 2)];
        var s = With();

        var overlap = RegionPlacement.Check(s, 1001, 1001, 1, elsewhere: elsewhere);
        Assert.False(overlap.Ok);
        Assert.Equal("Hub", overlap.OverlapsWith);

        var next = RegionPlacement.Check(s, 1002, 1000, 1, elsewhere: elsewhere);
        Assert.True(next.Ok);
        Assert.Equal([new Neighbour("Hub", "east")], next.Neighbours);
    }

    [Fact]
    public void Check_ignores_our_own_regions_on_the_map()
    {
        IReadOnlyList<MapRegion> elsewhere = [new(Home.Uuid, "Home", 1000, 1000, 1)];

        var check = RegionPlacement.Check(With(Home), 1001, 1000, 1, elsewhere: elsewhere);

        Assert.True(check.Ok);
        Assert.Equal([new Neighbour("Home", "east")], check.Neighbours);
    }

    [Fact]
    public void FirstFree_skips_used_squares()
    {
        IReadOnlyList<MapRegion> elsewhere = [new(Guid.NewGuid(), "Hub", 1000, 1000, 2)];

        Assert.Equal((1002, 1000), RegionPlacement.FirstFree(With(), elsewhere));
        Assert.Equal((1000, 1000), RegionPlacement.FirstFree(With(), []));
    }

    [Fact]
    public void FirstFreeIn_skips_used_squares()
    {
        IReadOnlyList<MapRegion> elsewhere = [new(Guid.NewGuid(), "Hub", 1000, 1000, 2)];

        Assert.Equal((1002, 1000), RegionPlacement.FirstFreeIn(With(), elsewhere, MapArea.Start));
    }

    [Fact]
    public void FirstFreeIn_returns_null_when_the_area_is_full()
    {
        IReadOnlyList<MapRegion> elsewhere = [new(Guid.NewGuid(), "Hub", 1000, 1000, 2)];

        Assert.Null(RegionPlacement.FirstFreeIn(With(), elsewhere, new MapArea(1000, 1000, 1001, 1001), 2));
        Assert.Null(RegionPlacement.FirstFreeIn(With(), [], new MapArea(1000, 1000, 1001, 1001), 3));
    }

    [Fact]
    public void MapArea_around_clamps_at_the_edge()
    {
        Assert.Equal(new MapArea(0, 0, 1, 1), MapArea.Around(0, 0, 1));
        Assert.Equal(new MapArea(1099, 1099, 1102, 1102), MapArea.Around(1100, 1100, 2));
        Assert.Equal(new MapArea(65534, 65534, RegionPlacement.MaxCoordinate, RegionPlacement.MaxCoordinate), MapArea.Around(65535, 65535, 1));
    }
}
