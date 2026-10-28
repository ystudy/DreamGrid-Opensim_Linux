namespace DreamGrid.Launcher.Core.Grid;

/// <summary>The shapes of land a region type is generated as when it has no OAR.</summary>
public enum TerrainShape { Island, Beach, Forest, Meadow }

/// <summary>
/// Generates heightmaps in OpenSim's <c>.r32</c> format: <c>size*size</c> little-endian float32 values, row-major
/// with y as the outer index (index = y * size + x), which matches OpenSim's RAW32 loader. The region water level
/// is 20 m; every shape is land in the middle and sea at the edges, with at least 10 m of relief.
/// </summary>
public static class TerrainShapes
{
    public static void WriteR32(string path, TerrainShape shape, int size = 256, int seed = 1)
    {
        // OpenSim region sizes are multiples of 256 m.
        if (size <= 0 || size % 256 != 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "The terrain size must be a positive multiple of 256.");

        var (ripple, cell) = shape switch
        {
            TerrainShape.Beach => (0.3f, 24),
            TerrainShape.Forest => (4f, 24),
            TerrainShape.Meadow => (2f, 96),
            _ => (1.5f, 24),
        };

        var buffer = new byte[checked(size * size * 4)];
        var half = size / 2f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x - half) / half;
                var dy = (y - half) / half;
                var d = MathF.Min(1f, MathF.Sqrt(dx * dx + dy * dy));
                var height = Profile(shape, d) + ripple * Noise(x, y, seed, cell);
                BitConverter.TryWriteBytes(buffer.AsSpan((y * size + x) * 4, 4), MathF.Max(0f, height));
            }
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(buffer);
        stream.Flush();
    }

    // The height in metres at distance d (0 in the middle, 1 at the edge) before the ripples are added.
    private static float Profile(TerrainShape shape, float d)
    {
        switch (shape)
        {
            case TerrainShape.Beach:
                // Down to a wide flat band just above the water, then the drop to the sea.
                if (d < 0.55f) return 23f + (20.5f - 23f) * Smoothstep(d / 0.55f);
                if (d < 0.7f) return 20.5f;
                return 20.5f + (5f - 20.5f) * Smoothstep((d - 0.7f) / 0.3f);
            case TerrainShape.Forest: return Slope(30f, 5f, d);
            case TerrainShape.Meadow: return Slope(26f, 5f, d);
            default: return Slope(25f, 5f, d);
        }
    }

    private static float Slope(float centre, float edge, float d) => centre + (edge - centre) * Smoothstep(d); // flat top, flat shore

    private static float Smoothstep(float t) => t * t * (3f - 2f * t);

    // Value noise in [-1, 1]: smooth-interpolated hashed lattice values, one lattice point every `cell` metres.
    private static float Noise(int x, int y, int seed, int cell)
    {
        var fx = x / (float)cell;
        var fy = y / (float)cell;
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var tx = Smoothstep(fx - x0);
        var ty = Smoothstep(fy - y0);
        var a = Lattice(x0, y0, seed);
        var b = Lattice(x0 + 1, y0, seed);
        var c = Lattice(x0, y0 + 1, seed);
        var d = Lattice(x0 + 1, y0 + 1, seed);
        var top = a + (b - a) * tx;
        var bottom = c + (d - c) * tx;
        return top + (bottom - top) * ty;
    }

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
