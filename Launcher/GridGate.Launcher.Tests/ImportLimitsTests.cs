using Xunit;
using GridGate.Launcher.Core.Ini;
using GridGate.Launcher.Core.ModelImport;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Tests;

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
        Assert.Equal(1024, limits.MaxLinksetParts);

        // The values every region gets from the prototype OpenSim.ini when its own options don't set them.
        var defaults = ImportLimitsRules.For(Settings(OwnerPlan.GridAsset, Region("a"), Region("b")));
        Assert.Equal(1024, defaults.MaxPartSize);
        Assert.Equal(128, defaults.MaxPhysicalPartSize);
        Assert.Equal(1024, defaults.MaxLinksetParts);
        Assert.Equal(64L * 1024 * 1024, defaults.MaxUploadBytes);
        Assert.Equal(64L * 1024 * 1024, defaults.MaxAssetBytes);
        Assert.Equal(8192, defaults.MaxImageSide);
        Assert.Equal(1024, defaults.MaxTextureSide);
        Assert.Equal(2_000_000, defaults.MaxTriangles);
        Assert.Equal(512, defaults.MaxNodes);
        Assert.Equal(200_000, defaults.HeavyTriangles);
    }

    [Fact]
    public void Defaults_are_the_ones_the_prototype_region_config_sets()
    {
        // GridConfigWriter starts every region's OpenSim.ini from this file; a region's own options are written over it.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridGate.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var proto = IniDocument.Load(Path.Combine(dir.FullName, "bin", "config-include", "Proto", "Robust", "OpenSim.ini"));
        Assert.Equal(ImportLimitsRules.DefaultMaxPartSize.ToString(), proto.Get("Startup", "NonPhysicalPrimMax"));
        Assert.Equal(ImportLimitsRules.DefaultMaxPhysicalPartSize.ToString(), proto.Get("Startup", "PhysicalPrimMax"));
        Assert.Equal(ImportLimitsRules.DefaultLinksetParts.ToString(), proto.Get("Startup", "LinksetPrims"));
        Assert.Null(proto.Get("Startup", "NonPhysicalPrimMin")); // left at OpenSim's own 0.001
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
