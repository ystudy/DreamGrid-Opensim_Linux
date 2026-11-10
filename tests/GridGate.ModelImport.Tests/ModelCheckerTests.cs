using System.Globalization;
using System.Numerics;
using GridGate.ModelImport.Checking;
using GridGate.ModelImport.Model;
using Xunit;

namespace GridGate.ModelImport.Tests;

public class ModelCheckerTests
{
    private static readonly ImportLimits Limits = GltfReaderTests.Limits;

    private static ImportOptions Options(int scale = 100) =>
        new("Thing", "thing.glb", scale, new NextOwner(true, true, true), Guid.Empty, "Tester");

    private static ImportModel Model(IEnumerable<ImportPart> parts, IReadOnlyList<ImportMaterial>? materials = null,
        IReadOnlyList<string>? dropped = null, ImportSkeleton? skeleton = null) =>
        new(parts.ToList(), 0, materials ?? new[] { Plain() }, Array.Empty<ImportImage>(), skeleton, dropped ?? Array.Empty<string>());

    private static ImportMaterial Plain() => new("m", Vector4.One, null, AlphaMode.Opaque, 0.5f, false);

    // One triangle with legs of the given length along X and Y.
    private static ImportFace Triangle(float size, int material = 0, bool uv = false) => new(
        new[] { Vector3.Zero, new Vector3(size, 0, 0), new Vector3(0, size, 0) },
        new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
        uv ? new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY } : null,
        new[] { 0, 1, 2 }, material, null);

    private static ImportPart Part(string name, params ImportFace[] faces) =>
        new(name, Vector3.Zero, Quaternion.Identity, Vector3.One, new ImportMesh(faces));

    private static Finding? Find(CheckResult r, string code) => r.Findings.FirstOrDefault(f => f.Code == code);

    private static CheckResult Check(ImportModel m, ImportLimits? limits = null, int scale = 100) =>
        ModelChecker.Check(m, limits ?? Limits, Options(scale));

    // A strip of columns x 2 vertices, so (columns - 1) * 2 triangles. Vertex column i has joint i % 7.
    private static ImportFace Strip(int columns)
    {
        var pos = new Vector3[columns * 2];
        var nor = new Vector3[columns * 2];
        var joints = new ushort[columns * 8];
        var wts = new float[columns * 8];
        for (int i = 0; i < columns; i++)
        {
            pos[i * 2] = new Vector3(i * 0.001f, 0, 0); pos[i * 2 + 1] = new Vector3(i * 0.001f, 1, 0);
            nor[i * 2] = nor[i * 2 + 1] = Vector3.UnitZ;
            joints[i * 8] = (ushort)(i % 7); wts[i * 8] = 1;
            joints[i * 8 + 4] = (ushort)(i % 7); wts[i * 8 + 4] = 1;
        }
        var tris = new List<int>();
        for (int i = 0; i < columns - 1; i++)
        {
            int a = i * 2, b = i * 2 + 1, c = i * 2 + 2, d = i * 2 + 3;
            tris.AddRange(new[] { a, c, b, b, c, d });
        }
        return new ImportFace(pos, nor, null, tris.ToArray(), 0, new ImportWeights(joints, wts));
    }

    private static int Triangles(ImportModel m) => m.Parts.Sum(p => p.Mesh.Faces.Sum(f => f.Triangles.Length / 3));

    [Fact]
    public void Twenty_materials_split_into_three_parts()
    {
        var mats = Enumerable.Range(0, 20).Select(_ => Plain()).ToArray();
        var part = Part("Tower", Enumerable.Range(0, 20).Select(i => Triangle(1, i)).ToArray());
        var r = Check(Model(new[] { part }, mats));
        var f = Find(r, "split-materials")!;
        Assert.Equal(FindingKind.Safe, f.Kind);
        Assert.Equal("20", f.Values["materials"]);
        Assert.Equal("8", f.Values["perPart"]);
        Assert.Equal("3", f.Values["parts"]);
        Assert.Equal(3, r.Fixed.Parts.Count);
        Assert.All(r.Fixed.Parts, p => Assert.True(p.Mesh.Faces.Count <= 8));
        Assert.Equal(new[] { "Tower", "Tower 2", "Tower 3" }, r.Fixed.Parts.Select(p => p.Name).ToArray());
        Assert.False(r.Blocked);
    }

    [Fact]
    public void Face_over_65535_vertices_is_split()
    {
        var part = Part("Grid", Strip(35_000)); // 70,000 vertices
        var r = Check(Model(new[] { part }));
        var f = Find(r, "split-vertices")!;
        Assert.Equal(FindingKind.Safe, f.Kind);
        Assert.All(r.Fixed.Parts.SelectMany(p => p.Mesh.Faces), face => Assert.True(face.Positions.Length <= 65_535));
        Assert.Equal(Triangles(Model(new[] { part })), Triangles(r.Fixed));
        Assert.Null(Find(r, "split-materials"));
    }

    [Fact]
    public void Split_faces_carry_the_weights_of_the_vertices_they_copy()
    {
        var part = Part("Grid", Strip(35_000));
        var r = Check(Model(new[] { part }));
        var faces = r.Fixed.Parts.SelectMany(p => p.Mesh.Faces).ToArray();
        Assert.True(faces.Length > 1);
        foreach (var face in faces)
        {
            Assert.NotNull(face.Weights);
            Assert.Equal(face.Positions.Length * 4, face.Weights!.Joints.Length);
            Assert.Equal(face.Positions.Length * 4, face.Weights.Weights.Length);
            Assert.Equal(face.Positions.Length, face.Normals.Length);
        }
        // Each copied vertex keeps its own joint: the strip column is x / 0.001, its joint column % 7.
        var last = faces[^1];
        for (int v = 0; v < last.Positions.Length; v++)
            Assert.Equal((ushort)((int)MathF.Round(last.Positions[v].X / 0.001f) % 7), last.Weights!.Joints[v * 4]);
    }

    [Fact]
    public void Too_big_offers_a_fit_percent()
    {
        var r = Check(Model(new[] { Part("Wall", Triangle(300)) }));
        var f = Find(r, "too-big")!;
        Assert.Equal(FindingKind.Blocking, f.Kind);
        Assert.Equal("300", f.Values["side"]);
        Assert.Equal("256", f.Values["limit"]);
        Assert.Equal("85", f.Values["fitPercent"]);
        Assert.Equal(85, r.Summary.LargestFitPercent);
        Assert.True(r.Blocked);
    }

    [Fact]
    public void Scaled_to_fit_is_not_blocking()
    {
        var r = Check(Model(new[] { Part("Wall", Triangle(300)) }), scale: 85);
        Assert.Null(Find(r, "too-big"));
        Assert.False(r.Blocked);
        Assert.Equal(0.85f, r.Fixed.Parts[0].Scale.X, 4);
        Assert.Null(r.Summary.LargestFitPercent);
    }

    [Fact]
    public void Scaling_moves_parts_too()
    {
        var part = Part("A", Triangle(1)) with { Position = new Vector3(10, 0, 0) };
        var r = Check(Model(new[] { part }), scale: 50);
        Assert.Equal(5f, r.Fixed.Parts[0].Position.X, 4);
    }

    [Fact]
    public void Size_is_measured_in_world_axes_after_rotation()
    {
        // 300 along X, turned a quarter about Z: 300 along Y, still too big. Legs of 200 m fit, but turned 45 degrees their box is 283 m wide.
        var turned = Part("Wall", Triangle(300)) with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) };
        Assert.NotNull(Find(Check(Model(new[] { turned })), "too-big"));
        var diag = Part("Beam", Triangle(200)) with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4) };
        Assert.Null(Find(Check(Model(new[] { Part("Beam", Triangle(200)) })), "too-big"));
        Assert.NotNull(Find(Check(Model(new[] { diag })), "too-big"));
    }

    [Fact]
    public void Small_parts_dropped_when_under_half()
    {
        var parts = new[] { Part("A", Triangle(1)), Part("B", Triangle(1)), Part("Speck", Triangle(0.0001f)) };
        var r = Check(Model(parts));
        var f = Find(r, "too-small")!;
        Assert.Equal(FindingKind.Safe, f.Kind);
        Assert.Equal("1", f.Values["dropped"]);
        Assert.Equal(2, r.Fixed.Parts.Count);
        Assert.False(r.Blocked);

        var two = new[] { Part("A", Triangle(1)), Part("S1", Triangle(0.0001f)), Part("S2", Triangle(0.0001f)) };
        var r2 = Check(Model(two));
        Assert.Equal(FindingKind.Blocking, Find(r2, "too-small")!.Kind);
        Assert.True(r2.Blocked);
    }

    [Fact]
    public void Nothing_left_after_dropping_is_blocking()
    {
        var r = Check(Model(new[] { Part("S1", Triangle(0.0001f)), Part("S2", Triangle(0.0001f)) }));
        Assert.True(r.Blocked);
        Assert.Empty(r.Fixed.Parts);

        var lines = Check(Model(Array.Empty<ImportPart>(), dropped: new[] { "lines" }));
        var f = Find(lines, "not-triangles")!;
        Assert.Equal(FindingKind.Blocking, f.Kind);
        Assert.True(lines.Blocked);
    }

    [Fact]
    public void Lines_beside_triangles_are_a_safe_finding()
    {
        var r = Check(Model(new[] { Part("A", Triangle(1)) }, dropped: new[] { "lines", "points" }));
        var f = Find(r, "not-triangles")!;
        Assert.Equal(FindingKind.Safe, f.Kind);
        Assert.False(r.Blocked);
    }

    [Fact]
    public void Linkset_limit_zero_means_no_limit()
    {
        var parts = Enumerable.Range(0, 600).Select(i => Part("P" + i, Triangle(1))).ToArray();
        Assert.Null(Find(Check(Model(parts)), "too-many-parts"));
        var r = Check(Model(parts), Limits with { MaxLinksetParts = 512 });
        var f = Find(r, "too-many-parts")!;
        Assert.Equal(FindingKind.Blocking, f.Kind);
        Assert.Equal("600", f.Values["parts"]);
        Assert.Equal("512", f.Values["limit"]);
        Assert.True(r.Blocked);
    }

    [Fact]
    public void Heavy_mesh_is_risky()
    {
        var part = Part("Big", Strip(125_001)); // 250,000 triangles
        var r = Check(Model(new[] { part }));
        var f = Find(r, "heavy-mesh")!;
        Assert.Equal(FindingKind.Risky, f.Kind);
        Assert.Equal("250000", f.Values["triangles"]);
        Assert.Equal("200000", f.Values["threshold"]);
    }

    [Fact]
    public void Negative_scale_is_risky_and_baked_into_the_vertices()
    {
        var part = Part("Mirror", Triangle(1)) with { Scale = new Vector3(-2, 1, 1) };
        var r = Check(Model(new[] { part }));
        Assert.Equal(FindingKind.Risky, Find(r, "inside-out")!.Kind);
        var fixedPart = r.Fixed.Parts[0];
        Assert.Equal(new Vector3(2, 1, 1), fixedPart.Scale);
        var face = fixedPart.Mesh.Faces[0];
        Assert.Equal(-1f, face.Positions[1].X); // the X mirror is in the vertices now; the part's scale is the positive 2
        Assert.Equal(new[] { 0, 2, 1 }, face.Triangles); // one mirror reverses the winding
    }

    [Fact]
    public void Two_negative_axes_keep_the_winding()
    {
        var part = Part("Turned", Triangle(1)) with { Scale = new Vector3(-1, -1, 1) };
        var r = Check(Model(new[] { part }));
        Assert.Equal(new[] { 0, 1, 2 }, r.Fixed.Parts[0].Mesh.Faces[0].Triangles);
        Assert.All(r.Fixed.Parts, p => Assert.True(p.Scale.X > 0 && p.Scale.Y > 0 && p.Scale.Z > 0));
    }

    [Fact]
    public void NegativeScaleTest_model_is_risky_with_only_positive_scales()
    {
        var read = GltfReaderTests.ReadDownloaded("NegativeScaleTest.glb");
        var r = Check(read.Model!);
        Assert.Equal(FindingKind.Risky, Find(r, "inside-out")!.Kind);
        Assert.All(r.Fixed.Parts, p => Assert.True(p.Scale.X > 0 && p.Scale.Y > 0 && p.Scale.Z > 0));
    }

    [Fact]
    public void Textured_face_without_uv_is_risky()
    {
        var mats = new[] { new ImportMaterial("tex", Vector4.One, 0, AlphaMode.Opaque, 0.5f, false) };
        var model = Model(new[] { Part("A", Triangle(1)) }, mats) with { Images = new[] { new ImportImage("i", "image/png", new byte[] { 1 }) } };
        var r = Check(model);
        Assert.Equal(FindingKind.Risky, Find(r, "no-uv")!.Kind);
        var withUv = model with { Parts = new[] { Part("A", Triangle(1, 0, uv: true)) } };
        Assert.Null(Find(Check(withUv), "no-uv"));
        Assert.Null(Find(Check(Model(new[] { Part("A", Triangle(1)) })), "no-uv")); // an untextured material needs none
    }

    [Fact]
    public void Rigged_model_is_risky_and_made_static()
    {
        var m = GltfReaderTests.Read("Fox.glb").Model!;
        var r = Check(m);
        Assert.Equal(FindingKind.Risky, Find(r, "skin-ignored")!.Kind);
        Assert.NotNull(r.Fixed.Skeleton);
        Assert.All(r.Fixed.Parts.SelectMany(p => p.Mesh.Faces), f => Assert.NotNull(f.Weights));
    }

    [Fact]
    public void Dropped_things_are_safe_findings()
    {
        var r = Check(GltfReaderTests.Read("Fox.glb").Model!);
        Assert.Equal(FindingKind.Safe, Find(r, "dropped-animation")!.Kind);

        var m = Model(new[] { Part("A", Triangle(1)) }, dropped: new[] { "morphs", "cameras", "lights" });
        var r2 = Check(m);
        Assert.Equal(FindingKind.Safe, Find(r2, "dropped-morphs")!.Kind);
        var extras = Find(r2, "dropped-extras")!;
        Assert.Equal(FindingKind.Safe, extras.Kind);
        Assert.Equal("cameras,lights", extras.Values["what"]);
    }

    [Fact]
    public void Summary_counts_what_is_left()
    {
        var r = Check(Model(new[] { Part("A", Triangle(2), Triangle(2)) }));
        Assert.Equal(1, r.Summary.Parts);
        Assert.Equal(2, r.Summary.Faces);
        Assert.Equal(2, r.Summary.Triangles);
        Assert.Equal(new[] { 2.0, 2.0, 0.0 }, r.Summary.SizeMetres);
    }
}
