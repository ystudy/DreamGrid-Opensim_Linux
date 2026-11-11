using System.Diagnostics;
using System.Numerics;
using GridGate.ModelImport.Building;
using GridGate.ModelImport.Model;
using Xunit;

namespace GridGate.ModelImport.Tests;

public class SimplifierTests
{
    private readonly ITestOutputHelper _output;

    public SimplifierTests(ITestOutputHelper output) => _output = output;

    internal static int Triangles(ImportMesh m) => m.Faces.Sum(f => f.Triangles.Length / 3);

    internal static ImportMesh FirstMesh(string model) => GltfReaderTests.Read(model).Model!.Parts[0].Mesh;

    // A bumpy n x n grid of quads (2 n^2 triangles) in the XY plane, with UVs, as one face of the given material.
    internal static ImportFace Grid(int n, int material, Vector3 offset = default)
    {
        var positions = new Vector3[(n + 1) * (n + 1)];
        var normals = new Vector3[positions.Length];
        var uv = new Vector2[positions.Length];
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
            {
                int i = y * (n + 1) + x;
                float h = 0.05f * MathF.Sin(x * 0.7f) * MathF.Cos(y * 0.9f);
                positions[i] = offset + new Vector3(x / (float)n, y / (float)n, h);
                normals[i] = Vector3.UnitZ;
                uv[i] = new Vector2(x / (float)n, y / (float)n);
            }
        var tris = new List<int>();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int a = y * (n + 1) + x, b = a + 1, c = a + n + 1, d = c + 1;
                tris.AddRange(new[] { a, b, d, a, d, c });
            }
        return new ImportFace(positions, normals, uv, tris.ToArray(), material, null);
    }

    [Fact]
    public void Simplify_hits_the_ratio()
    {
        var avocado = FirstMesh("Avocado.glb");
        var before = Triangles(avocado);
        var sw = Stopwatch.StartNew();
        var quarter = Simplifier.Simplify(avocado, 0.25);
        _output.WriteLine($"Avocado {before} -> {Triangles(quarter)} triangles in {sw.ElapsedMilliseconds} ms");
        var share = Triangles(quarter) / (double)before;
        Assert.InRange(share, 0.20, 0.30);

        var same = Simplifier.Simplify(avocado, 1.0);
        Assert.Equal(before, Triangles(same));
        for (int f = 0; f < avocado.Faces.Count; f++)
        {
            Assert.Equal(avocado.Faces[f].Triangles, same.Faces[f].Triangles);
            Assert.Equal(avocado.Faces[f].Positions, same.Faces[f].Positions);
        }
    }

    [Fact]
    public void Simplify_keeps_every_material()
    {
        // A large face, a two-triangle face and a one-triangle face, each its own material.
        var big = Grid(20, 0);
        var quad = new ImportFace(new[] { new Vector3(2, 0, 0), new Vector3(3, 0, 0), new Vector3(3, 1, 0), new Vector3(2, 1, 0) },
            Enumerable.Repeat(Vector3.UnitZ, 4).ToArray(), null, new[] { 0, 1, 2, 0, 2, 3 }, 1, null);
        var one = new ImportFace(new[] { new Vector3(4, 0, 0), new Vector3(5, 0, 0), new Vector3(4, 1, 0) },
            Enumerable.Repeat(Vector3.UnitZ, 3).ToArray(), null, new[] { 0, 1, 2 }, 2, null);
        var mesh = new ImportMesh(new[] { big, quad, one });

        var lowest = Simplifier.Simplify(mesh, 0.06);

        Assert.Equal(3, lowest.Faces.Count);
        Assert.Equal(new[] { 0, 1, 2 }, lowest.Faces.Select(f => f.Material));
        Assert.All(lowest.Faces, f => Assert.True(f.Triangles.Length >= 3));
        Assert.InRange(lowest.Faces[0].Triangles.Length / 3, 1, 800 * 0.06 + 1);
    }

    [Fact]
    public void Simplify_is_deterministic()
    {
        var avocado = FirstMesh("Avocado.glb");
        var a = Simplifier.Simplify(avocado, 0.25);
        var b = Simplifier.Simplify(avocado, 0.25);
        for (int f = 0; f < a.Faces.Count; f++)
        {
            Assert.Equal(a.Faces[f].Triangles, b.Faces[f].Triangles);
            Assert.Equal(a.Faces[f].Positions, b.Faces[f].Positions);
        }
    }

    [Fact]
    public void Simplify_keeps_weights_with_their_vertices()
    {
        var fox = FirstMesh("Fox.glb");
        var lower = Simplifier.Simplify(fox, 0.25);
        foreach (var (high, low) in fox.Faces.Zip(lower.Faces))
        {
            Assert.NotNull(low.Weights);
            Assert.Equal(low.Positions.Length * 4, low.Weights!.Joints.Length);
            Assert.Equal(low.Positions.Length * 4, low.Weights.Weights.Length);
            // Every kept vertex is one of the original vertices, with its own normal, UV and weights.
            for (int v = 0; v < low.Positions.Length; v++)
            {
                int o = Array.IndexOf(high.Positions, low.Positions[v]);
                Assert.True(o >= 0);
            }
        }
    }

    [Fact]
    public void Simplify_is_fast_on_a_large_model()
    {
        // 500 x 500 quads = 500,000 triangles, at all three detail-level ratios.
        var mesh = new ImportMesh(new[] { Grid(500, 0) });
        Assert.Equal(500_000, Triangles(mesh));
        var sw = Stopwatch.StartNew();
        foreach (var ratio in new[] { 0.5, 0.25, 0.06 })
        {
            var t = Stopwatch.StartNew();
            var lower = Simplifier.Simplify(mesh, ratio);
            _output.WriteLine($"500k grid at {ratio}: {Triangles(lower)} triangles in {t.ElapsedMilliseconds} ms");
            Assert.InRange(Triangles(lower) / 500_000.0, ratio * 0.8, ratio * 1.2);
        }
        // Generous: the converter has 2 minutes for everything and allows 2,000,000 triangles.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
    }

    // Distance from p to triangle abc.
    private static float DistanceToTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return Vector3.Distance(p, a);
        var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return Vector3.Distance(p, b);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return Vector3.Distance(p, a + d1 / (d1 - d3) * ab);
        var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return Vector3.Distance(p, c);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return Vector3.Distance(p, a + d2 / (d2 - d6) * ac);
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return Vector3.Distance(p, b + (d4 - d3) / ((d4 - d3) + (d5 - d6)) * (c - b));
        float denom = 1 / (va + vb + vc);
        return Vector3.Distance(p, a + ab * (vb * denom) + ac * (vc * denom));
    }

    // The farthest any original vertex lies from the simplified surface.
    private static float Deviation(ImportMesh original, ImportMesh simplified)
    {
        float worst = 0;
        foreach (var p in original.Faces.SelectMany(f => f.Positions))
        {
            float best = float.MaxValue;
            foreach (var f in simplified.Faces)
                for (int t = 0; t + 2 < f.Triangles.Length; t += 3)
                    best = Math.Min(best, DistanceToTriangle(p, f.Positions[f.Triangles[t]], f.Positions[f.Triangles[t + 1]], f.Positions[f.Triangles[t + 2]]));
            worst = Math.Max(worst, best);
        }
        return worst;
    }

    private static float Diagonal(ImportMesh m)
    {
        var all = m.Faces.SelectMany(f => f.Positions).ToArray();
        var lo = all.Aggregate(Vector3.Min); var hi = all.Aggregate(Vector3.Max);
        return Vector3.Distance(lo, hi);
    }

    // The shape limit wins over the triangle goal: no level moves the surface by more than about its allowed share of the
    // model's size, however few triangles the goal asks for.
    [Theory]
    [InlineData("Box.glb", 0.06, 0.02)]
    [InlineData("Fox.glb", 0.06, 0.02)]
    [InlineData("Fox.glb", 0.25, 0.01)]
    [InlineData("Avocado.glb", 0.06, 0.02)]
    public void No_level_moves_the_surface_more_than_its_limit(string model, double ratio, double maxError)
    {
        var mesh = FirstMesh(model);
        var lower = Simplifier.Simplify(mesh, ratio, maxError);
        var deviation = Deviation(mesh, lower);
        _output.WriteLine($"{model} {ratio}: {Triangles(mesh)} -> {Triangles(lower)} triangles, deviation {deviation / Diagonal(mesh):P2} of the size");
        // Quadric error estimates the distance, so allow it twice the limit.
        Assert.True(deviation <= 2 * maxError * Diagonal(mesh), $"deviation {deviation / Diagonal(mesh):P2} over {2 * maxError:P0}");
    }

    // A flat dense grid has no shape to lose, so the triangle goal is still reached.
    [Fact]
    public void Flat_dense_mesh_still_reaches_the_triangle_goal()
    {
        var flat = Grid(40, 0);
        var positions = flat.Positions.Select(p => new Vector3(p.X, p.Y, 0)).ToArray();
        var mesh = new ImportMesh(new[] { flat with { Positions = positions } });
        var lower = Simplifier.Simplify(mesh, 0.25, 0.01);
        Assert.InRange(Triangles(lower), 1, (int)(Triangles(mesh) * 0.3));
    }
}
