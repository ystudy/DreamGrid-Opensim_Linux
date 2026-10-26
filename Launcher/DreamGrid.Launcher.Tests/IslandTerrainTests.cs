using DreamGrid.Launcher.Core.Grid;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class IslandTerrainTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-island-" + Guid.NewGuid().ToString("N"));

    public IslandTerrainTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // OpenSim's RAW32 loader reads y in the outer loop and x in the inner one: index = y * size + x.
    private static float[,] Read(string path, int size)
    {
        var bytes = File.ReadAllBytes(path);
        var h = new float[size, size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                h[x, y] = BitConverter.ToSingle(bytes, (y * size + x) * 4);
        return h;
    }

    [Fact]
    public void R32_has_expected_size()
    {
        var path = Path.Combine(_dir, "t.r32");
        IslandTerrain.WriteR32(path);
        Assert.Equal(256L * 256 * 4, new FileInfo(path).Length);
    }

    [Fact]
    public void Center_is_land_and_corners_are_water()
    {
        var path = Path.Combine(_dir, "t.r32");
        IslandTerrain.WriteR32(path);
        var h = Read(path, 256);
        Assert.True(h[128, 128] > 20f, $"center {h[128, 128]}");
        foreach (var (x, y) in new[] { (0, 0), (255, 0), (0, 255), (255, 255) })
        {
            Assert.True(h[x, y] < 20f, $"corner {x},{y} = {h[x, y]}");
            Assert.True(h[x, y] >= 0f);
        }
    }

    [Fact]
    public void Same_seed_same_bytes_and_different_seed_differs()
    {
        var a = Path.Combine(_dir, "a.r32");
        var b = Path.Combine(_dir, "b.r32");
        var c = Path.Combine(_dir, "c.r32");
        IslandTerrain.WriteR32(a, seed: 7);
        IslandTerrain.WriteR32(b, seed: 7);
        IslandTerrain.WriteR32(c, seed: 8);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        Assert.NotEqual(File.ReadAllBytes(a), File.ReadAllBytes(c));
    }

    [Fact]
    public void StarterOar_null_when_absent_path_when_present()
    {
        Assert.Null(StarterContent.StarterOar(_dir));
        Directory.CreateDirectory(Path.Combine(_dir, "content"));
        var oar = Path.Combine(_dir, "content", "starter-region.oar");
        File.WriteAllBytes(oar, new byte[] { 1 });
        Assert.Equal(oar, StarterContent.StarterOar(_dir));
    }
}
