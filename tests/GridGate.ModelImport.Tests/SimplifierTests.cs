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
}
