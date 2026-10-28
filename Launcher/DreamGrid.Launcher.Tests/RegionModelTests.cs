using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RegionModelTests
{
    private static LauncherSettings With(params RegionSettings[] regions) =>
        TestSettings.Create() with { Programs = [new ProgramSettings("Home", 8004, 8011)], Regions = regions.ToList() };

    [Fact]
    public void RegionModel_find_by_name_ignores_case_and_includes_deleted()
    {
        var gone = new RegionSettings("Home", "Beach", Guid.NewGuid(), DeletedAt: DateTimeOffset.UnixEpoch);
        var s = With(new RegionSettings("Home", "Home", Guid.NewGuid()), gone);

        Assert.Equal(gone, RegionModel.FindByName(s, "BEACH"));
        Assert.Null(RegionModel.FindByName(s, "Isle"));
    }

    [Fact]
    public void RegionModel_live_skips_deleted()
    {
        var home = new RegionSettings("Home", "Home", Guid.NewGuid());
        var s = With(home, new RegionSettings("Home", "Beach", Guid.NewGuid(), DeletedAt: DateTimeOffset.UnixEpoch));

        Assert.Equal([home], RegionModel.Live(s));
        Assert.Equal([home], RegionModel.In(s, "Home"));
        Assert.Equal(new ProgramSettings("Home", 8004, 8011), RegionModel.ProgramOf(s, home));
        Assert.Equal(home, RegionModel.Find(s, home.Uuid));
    }
}
