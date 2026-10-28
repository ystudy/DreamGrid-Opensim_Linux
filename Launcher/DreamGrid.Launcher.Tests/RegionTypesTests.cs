using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Grid;
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
}
