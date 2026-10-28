using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Settings;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class RegionTypesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-types-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;

    public RegionTypesTests() => _paths = new LauncherPaths(Path.Combine(_root, "data"), Path.Combine(_root, "program"), null);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void Built_in_types_in_order() =>
        Assert.Equal(["Default Island", "Sandy Beach", "Forest", "Meadow"], RegionTypes.BuiltIn.Select(t => t.Name));

    [Fact]
    public void Built_in_ids_in_order() =>
        Assert.Equal(["island", "beach", "forest", "meadow"], RegionTypes.BuiltIn.Select(t => t.Id));

    [Fact]
    public void Oar_and_picture_are_null_until_the_file_exists()
    {
        Assert.Null(RegionTypes.Oar(_paths, "beach"));
        Assert.Null(RegionTypes.Picture(_paths, "beach"));

        var dir = Path.Combine(_root, "content", "region-types");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "beach.oar"), [1]);
        File.WriteAllBytes(Path.Combine(dir, "beach.png"), [1]);

        Assert.Equal(Path.Combine(dir, "beach.oar"), RegionTypes.Oar(_paths, "beach"));
        Assert.Equal(Path.Combine(dir, "beach.png"), RegionTypes.Picture(_paths, "beach"));
        Assert.Null(RegionTypes.Oar(_paths, "forest"));
    }

    [Fact]
    public void An_unknown_id_has_no_files()
    {
        var dir = Path.Combine(_root, "content", "region-types");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "lava.oar"), [1]);

        Assert.Null(RegionTypes.Oar(_paths, "lava"));
        Assert.Null(RegionTypes.Picture(_paths, "../x"));
    }

    [Theory]
    [InlineData("starter", true), InlineData("island", true), InlineData("beach", true), InlineData("forest", true), InlineData("meadow", true)]
    [InlineData("../x", false), InlineData("", false), InlineData("lava", false)]
    public void Starter_and_built_in_ids_are_content(string id, bool expected) => Assert.Equal(expected, RegionTypes.IsContent(id));

    [Fact]
    public void All_lists_built_in_then_custom()
    {
        var s = TestSettings.Create() with { CustomTypes = [new("c-0a1b2c3d", "My Castle", "castle.oar")] };

        Assert.Equal(["island", "beach", "forest", "meadow", "c-0a1b2c3d"], RegionTypes.All(s).Select(t => t.Id));
        Assert.True(RegionTypes.IsContent(s, "c-0a1b2c3d"));
        Assert.False(RegionTypes.IsContent(TestSettings.Create(), "c-0a1b2c3d"));
        Assert.True(RegionTypes.IsContent(s, "starter"));
        var custom = RegionTypes.Find(s, "c-0a1b2c3d")!;
        Assert.True(custom.Custom);
        Assert.Equal(TerrainShape.Island, custom.Shape);
        Assert.Equal("Made from castle.oar.", custom.Description);
        Assert.False(RegionTypes.Find(s, "beach")!.Custom);
    }

    [Theory]
    [InlineData("c-0a1b2c3d", true), InlineData("c-0A1B2C3D", false), InlineData("c-0a1b2c3", false), InlineData("c-0a1b2c3dd", false)]
    [InlineData("island", false), InlineData("", false), InlineData("x-0a1b2c3d", false)]
    public void IsCustom_wants_c_and_8_lowercase_hex(string id, bool expected) => Assert.Equal(expected, RegionTypes.IsCustom(id));

    [Fact]
    public void Custom_type_oar_is_its_library_file()
    {
        var s = TestSettings.Create() with { CustomTypes = [new("c-0a1b2c3d", "My Castle", "castle.oar")] };
        Assert.Null(RegionTypes.Oar(_paths, s, "c-0a1b2c3d"));

        Directory.CreateDirectory(_paths.Oars);
        var file = Path.Combine(_paths.Oars, "castle.oar");
        File.WriteAllBytes(file, [1]);
        Assert.Equal(file, RegionTypes.Oar(_paths, s, "c-0a1b2c3d"));

        File.Delete(file);
        Assert.Null(RegionTypes.Oar(_paths, s, "c-0a1b2c3d"));
    }

    [Fact]
    public void Built_in_oar_still_comes_from_the_content_folder_with_settings()
    {
        var dir = Path.Combine(_root, "content", "region-types");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "beach.oar"), [1]);

        Assert.Equal(Path.Combine(dir, "beach.oar"), RegionTypes.Oar(_paths, TestSettings.Create(), "beach"));
    }
}
