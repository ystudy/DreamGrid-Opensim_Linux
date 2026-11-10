using Xunit;
using DreamGrid.Launcher.Core.ModelImport;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Tests;

public class ImportLimitsTests
{
    private static RegionSettings Region(string name, RegionOptions? options = null, DateTimeOffset? deleted = null) =>
        new("main", name, Guid.NewGuid(), Options: options, DeletedAt: deleted);

    private static LauncherSettings Settings(OwnerPlan plan, params RegionSettings[] regions) =>
        TestSettings.Create() with { Plan = plan, Regions = [.. regions] };

    [Fact]
    public void Limits_take_the_smallest_region_value()
    {
        var s = Settings(OwnerPlan.GridAsset,
            Region("a", new RegionOptions(MaxNonPhysicalSize: 128, MaxPhysicalSize: 5)),
            Region("b"),
            Region("gone", new RegionOptions(MaxNonPhysicalSize: 16), DateTimeOffset.UnixEpoch));
        var limits = ImportLimitsRules.For(s);
        Assert.Equal(128, limits.MaxPartSize);
        Assert.Equal(5, limits.MaxPhysicalPartSize);
        Assert.Equal(0.001, limits.MinPartSize);
        Assert.Equal(0, limits.MaxLinksetParts);

        var defaults = ImportLimitsRules.For(Settings(OwnerPlan.GridAsset, Region("a"), Region("b")));
        Assert.Equal(256, defaults.MaxPartSize);
        Assert.Equal(10, defaults.MaxPhysicalPartSize);
        Assert.Equal(64L * 1024 * 1024, defaults.MaxUploadBytes);
        Assert.Equal(8192, defaults.MaxImageSide);
        Assert.Equal(1024, defaults.MaxTextureSide);
        Assert.Equal(2_000_000, defaults.MaxTriangles);
        Assert.Equal(512, defaults.MaxNodes);
        Assert.Equal(200_000, defaults.HeavyTriangles);
    }

    [Fact]
    public void Upload_unavailable_on_region_and_connected_servers_and_without_regions()
    {
        Assert.False(ImportLimitsRules.UploadAvailable(Settings(OwnerPlan.RegionServer, Region("a"))));
        Assert.False(ImportLimitsRules.UploadAvailable(Settings(OwnerPlan.Connected, Region("a"))));
        Assert.False(ImportLimitsRules.UploadAvailable(Settings(OwnerPlan.GridAsset)));
        Assert.False(ImportLimitsRules.UploadAvailable(Settings(OwnerPlan.GridAsset, Region("a", deleted: DateTimeOffset.UnixEpoch))));
        Assert.True(ImportLimitsRules.UploadAvailable(Settings(OwnerPlan.GridAsset, Region("a"))));
    }
}
