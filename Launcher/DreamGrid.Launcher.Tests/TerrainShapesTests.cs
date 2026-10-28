using System.Security.Cryptography;
using DreamGrid.Launcher.Core.Grid;
using Xunit;

namespace DreamGrid.Launcher.Tests;

public sealed class TerrainShapesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-shapes-" + Guid.NewGuid().ToString("N"));

    public TerrainShapesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Write(TerrainShape shape, int size)
    {
        var path = Path.Combine(_dir, shape + size.ToString() + ".r32");
        TerrainShapes.WriteR32(path, shape, size);
        return path;
    }

    private float[] Heights(TerrainShape shape, int size)
    {
        var bytes = File.ReadAllBytes(Write(shape, size));
        var h = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, h, 0, bytes.Length);
        return h;
    }

    private float Relief(TerrainShape shape)
    {
        var h = Heights(shape, 256);
        return h.Max() - h.Min();
    }

    [Theory, InlineData(TerrainShape.Island), InlineData(TerrainShape.Beach), InlineData(TerrainShape.Forest), InlineData(TerrainShape.Meadow)]
    public void Every_shape_is_land_in_the_middle_and_sea_at_the_edge(TerrainShape shape)
    {
        var h = Heights(shape, 256);
        Assert.True(h[128 * 256 + 128] > 20f);
        Assert.True(h[0] < 20f && h[255 * 256 + 255] < 20f);
        Assert.True(h.Max() - h.Min() >= 10f); // IslandTerrainLoadedAsync's check
    }

    [Fact] public void Forest_is_hillier_than_meadow() => Assert.True(Relief(TerrainShape.Forest) > Relief(TerrainShape.Meadow));

    [Fact] public void Size_512_writes_512_by_512() => Assert.Equal(512L * 512 * 4, new FileInfo(Write(TerrainShape.Beach, 512)).Length);

    [Fact]
    public void A_size_that_is_not_a_multiple_of_256_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TerrainShapes.WriteR32(Path.Combine(_dir, "x.r32"), TerrainShape.Island, 300));

    [Fact]
    public void Island_wrapper_writes_the_same_bytes_as_before()
    {
        // The hash of IslandTerrain's output for size 256, seed 1, taken before the maths moved.
        var path = Path.Combine(_dir, "island.r32");
        IslandTerrain.WriteR32(path, 256, 1);
        Assert.Equal("6257C8E921D78BEB10DBA227C654FB3844F740A87DB1D4FD12C03A1207D249A0", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }
}
