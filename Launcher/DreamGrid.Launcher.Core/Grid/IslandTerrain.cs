namespace DreamGrid.Launcher.Core.Grid;

/// <summary>
/// Generates a gentle island heightmap in OpenSim's <c>.r32</c> format: <c>size*size</c> little-endian
/// float32 values, row-major with y as the outer index (index = y * size + x), which matches
/// OpenSim's RAW32 loader. The region water level is 20 m.
/// </summary>
public static class IslandTerrain
{
    private const float CenterHeight = 25f;   // 20 m water level + 5 m
    private const float EdgeHeight = 5f;      // 15 m below water
    private const float RippleAmplitude = 1.5f;
    private const int RippleCell = 24;        // lattice spacing of the value noise, in metres

    public static void WriteR32(string path, int size = 256, int seed = 1)
    {
        // OpenSim region sizes are multiples of 256 m.
        if (size <= 0 || size % 256 != 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "The island size must be a positive multiple of 256.");

        var buffer = new byte[checked(size * size * 4)];
        var half = size / 2f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x - half) / half;
                var dy = (y - half) / half;
                var d = MathF.Min(1f, MathF.Sqrt(dx * dx + dy * dy));
                var s = d * d * (3f - 2f * d);                       // smoothstep: flat top, flat shore
                var height = CenterHeight + (EdgeHeight - CenterHeight) * s;
                height += RippleAmplitude * Noise(x, y, seed);
                BitConverter.TryWriteBytes(buffer.AsSpan((y * size + x) * 4, 4), MathF.Max(0f, height));
            }
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(buffer);
        stream.Flush();
    }

    // Value noise in [-1, 1]: smooth-interpolated hashed lattice values.
    private static float Noise(int x, int y, int seed)
    {
        var fx = x / (float)RippleCell;
        var fy = y / (float)RippleCell;
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var tx = Smooth(fx - x0);
        var ty = Smooth(fy - y0);
        var a = Lattice(x0, y0, seed);
        var b = Lattice(x0 + 1, y0, seed);
        var c = Lattice(x0, y0 + 1, seed);
        var d = Lattice(x0 + 1, y0 + 1, seed);
        var top = a + (b - a) * tx;
        var bottom = c + (d - c) * tx;
        return top + (bottom - top) * ty;
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    private static float Lattice(int x, int y, int seed)
    {
        unchecked
        {
            var h = (uint)seed * 374761393u + (uint)x * 668265263u + (uint)y * 2246822519u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return h / (float)uint.MaxValue * 2f - 1f;
        }
    }
}
