using System.Numerics;
using System.Text;
using GridGate.ModelImport.Model;
using GridGate.ModelImport.Reading;
using ModelRoot = SharpGLTF.Schema2.ModelRoot;
using Xunit;

namespace GridGate.ModelImport.Tests;

public class GltfReaderTests
{
    internal static readonly ImportLimits Limits = new(
        MaxPartSize: 256, MinPartSize: 0.001, MaxPhysicalPartSize: 10, MaxLinksetParts: 0,
        MaxUploadBytes: 64L * 1024 * 1024, MaxImageSide: 8192, MaxTextureSide: 1024,
        MaxTriangles: 2_000_000, MaxNodes: 512, HeavyTriangles: 200_000, MaxAssetBytes: 64L * 1024 * 1024);

    internal static string ModelPath(string name) => Path.Combine(AppContext.BaseDirectory, "Models", name);

    internal static ReadResult Read(string name, ImportLimits? limits = null)
    {
        using var s = File.OpenRead(ModelPath(name));
        return GltfReader.Read(s, name, limits ?? Limits);
    }

    // A test that needs a model from fetch-models.sh skips when it was not fetched.
    internal static ReadResult ReadDownloaded(string relative, ImportLimits? limits = null)
    {
        var path = Path.Combine("downloaded", relative);
        if (!File.Exists(ModelPath(path)))
            Assert.Skip($"{relative} is not downloaded; run tests/GridGate.ModelImport.Tests/fetch-models.sh");
        return Read(path, limits);
    }

    private static int TriangleCount(ImportModel m) => m.Parts.Sum(p => p.Mesh.Faces.Sum(f => f.Triangles.Length / 3));

    // A one-triangle glTF with its buffer embedded, vertex 0 at (0,1,0); nodeExtras is added to the node's JSON.
    internal static byte[] TriangleGltf(string nodeExtras) => GltfJson("[{\"mesh\":0" + nodeExtras + "}]", "[0]");

    // A glTF whose mesh 0 is one triangle (vertex 0 at (0,1,0)). nodes is the JSON array of nodes; sceneNodes the scene's root
    // node indices; bufferUri the buffer's uri (embedded by default); top is extra top-level JSON, starting with a comma.
    internal static byte[] GltfJson(string nodes, string sceneNodes, string? bufferUri = null, string top = "", string primitiveExtra = "")
    {
        var floats = new float[] { 0, 1, 0, 1, 0, 0, 0, 0, 1 };
        var bytes = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        bufferUri ??= "data:application/octet-stream;base64," + Convert.ToBase64String(bytes);
        var json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":" + sceneNodes + "}],"
            + "\"nodes\":" + nodes + ","
            + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}" + primitiveExtra + "}]}],"
            + "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,1]}],"
            + "\"bufferViews\":[{\"buffer\":0,\"byteLength\":36}],"
            + "\"buffers\":[{\"byteLength\":36,\"uri\":\"" + bufferUri + "\"}]" + top + "}";
        return Encoding.UTF8.GetBytes(json);
    }

    [Fact]
    public void Box_reads_as_one_part_with_12_triangles()
    {
        var r = Read("Box.glb");
        Assert.Single(r.Model!.Parts);
        Assert.Equal(12, r.Model.Parts[0].Mesh.Faces.Sum(f => f.Triangles.Length / 3));
    }

    [Fact]
    public void Y_up_becomes_z_up()
    {
        // A vertex at glTF (0,1,0), under a node scaled 2 and moved to glTF (1,0,0): glTF world (1,2,0), so (1,0,2) in Z-up.
        using var s = new MemoryStream(TriangleGltf(",\"translation\":[1,0,0],\"scale\":[2,2,2]"));
        var r = GltfReader.Read(s, "triangle.gltf", Limits);
        var part = r.Model!.Parts[0];
        var face = part.Mesh.Faces[0];
        // The turn to Z-up is in the vertices; the node has no turn of its own, so neither has the part.
        AssertNear(new Vector3(0, 0, 1), face.Positions[0]);
        Assert.Equal(Quaternion.Identity, part.Rotation);
        var world = part.Position + Vector3.Transform(face.Positions[0] * part.Scale, part.Rotation);
        Assert.Equal(1f, world.X, 4);
        Assert.Equal(0f, world.Y, 4);
        Assert.Equal(2f, world.Z, 4);
    }

    [Fact]
    public void Part_at_the_origin_turns_glTF_up_into_z()
    {
        using var s = new MemoryStream(TriangleGltf(""));
        var part = GltfReader.Read(s, "triangle.gltf", Limits).Model!.Parts[0];
        var up = part.Position + Vector3.Transform(part.Mesh.Faces[0].Positions[0] * part.Scale, part.Rotation); // glTF (0,1,0)
        Assert.Equal(0f, up.X, 4);
        Assert.Equal(0f, up.Y, 4);
        Assert.Equal(1f, up.Z, 4);
    }

    [Fact]
    public void Unrotated_box_has_no_rotation_and_its_height_on_z()
    {
        // Box.glb's root node turns the box; with that turn taken away and the box stretched along glTF's up (Y) instead, the part
        // has no rotation and its height, 2, is along Z: the prim stands upright without a turn.
        var root = ModelRoot.Load(ModelPath("Box.glb"));
        root.DefaultScene.VisualChildren.First().LocalMatrix = Matrix4x4.CreateScale(1, 2, 1);
        using var glb = new MemoryStream();
        root.WriteGLB(glb);
        glb.Position = 0;
        var part = Assert.Single(GltfReader.Read(glb, "box.glb", Limits).Model!.Parts);

        Assert.Equal(Quaternion.Identity, part.Rotation);
        var place = Building.UnitMesh.Placement(part, Limits.MinPartSize);
        AssertNear(new Vector3(1, 1, 2), place.Scale);
    }

    [Fact]
    public void A_turned_node_gives_its_own_turn_in_z_up()
    {
        // A node turned 90 degrees about glTF Y (up) is turned 90 degrees about Z on the grid.
        var s = MathF.Sin(MathF.PI / 4).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        using var stream = new MemoryStream(TriangleGltf(",\"rotation\":[0," + s + ",0," + s + "]"));
        var part = GltfReader.Read(stream, "triangle.gltf", Limits).Model!.Parts[0];
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        Assert.InRange(MathF.Abs(Quaternion.Dot(expected, part.Rotation)), 1 - 1e-4f, 1 + 1e-4f);
        // Vertex glTF (1,0,0) turned about glTF Y goes to glTF (0,0,-1), which is (0,1,0) in Z-up.
        var world = part.Position + Vector3.Transform(part.Mesh.Faces[0].Positions[1] * part.Scale, part.Rotation);
        AssertNear(new Vector3(0, 1, 0), world);
    }

    [Fact]
    public void Skinned_part_keeps_glTF_axes_and_the_turn_in_its_rotation()
    {
        // Fox's mesh node is not turned; the part carries the 90 degree turn about X and its vertices are as the file has them.
        var part = Read("Fox.glb").Model!.Parts.Single();
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
        Assert.InRange(MathF.Abs(Quaternion.Dot(turn, part.Rotation)), 1 - 1e-4f, 1 + 1e-4f);
        var first = ModelRoot.Load(ModelPath("Fox.glb")).LogicalMeshes[0].Primitives[0].GetVertexAccessor("POSITION").AsVector3Array()[0];
        Assert.Equal(first, part.Mesh.Faces[0].Positions[0]);
    }

    [Fact]
    public void FlightHelmet_vertices_land_where_glTF_puts_them()
    {
        // Every vertex, through its part's scale, rotation and position, is its glTF world position turned to Z-up.
        var path = ModelPath(Path.Combine("downloaded", "FlightHelmet", "FlightHelmet.gltf"));
        if (!File.Exists(path)) Assert.Skip("FlightHelmet is not downloaded; run tests/GridGate.ModelImport.Tests/fetch-models.sh");
        var root = ModelRoot.Load(path);
        using var glb = new MemoryStream();
        root.WriteGLB(glb);
        glb.Position = 0;
        var model = GltfReader.Read(glb, "FlightHelmet.glb", Limits).Model!;

        var turn = Matrix4x4.CreateRotationX(MathF.PI / 2);
        var nodes = root.LogicalNodes.Where(n => n.Mesh != null).ToList();
        Assert.Equal(nodes.Count, model.Parts.Count);
        foreach (var part in model.Parts)
        {
            var node = Assert.Single(nodes, n => n.Name == part.Name);
            var expected = node.Mesh.Primitives.SelectMany(p => p.GetVertexAccessor("POSITION").AsVector3Array())
                .Select(v => Vector3.Transform(v, node.WorldMatrix * turn)).ToArray();
            var actual = part.Mesh.Faces.SelectMany(f => f.Positions)
                .Select(v => part.Position + Vector3.Transform(v * part.Scale, part.Rotation)).ToArray();
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
                Assert.True(Vector3.Distance(expected[i], actual[i]) < 1e-4f, $"{part.Name} vertex {i}: {expected[i]} vs {actual[i]}");
        }
    }

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 1e-4f);
        Assert.Equal(expected.Y, actual.Y, 1e-4f);
        Assert.Equal(expected.Z, actual.Z, 1e-4f);
    }

    [Fact]
    public void Non_indexed_primitive_gets_sequential_indices()
    {
        using var s = new MemoryStream(TriangleGltf(""));
        var face = GltfReader.Read(s, "triangle.gltf", Limits).Model!.Parts[0].Mesh.Faces[0];
        Assert.Equal(new[] { 0, 1, 2 }, face.Triangles);
        Assert.Equal(3, face.Normals.Length);
    }

    [Fact]
    public void Avocado_has_one_png_or_jpeg_base_colour_image()
    {
        var m = Read("Avocado.glb").Model!;
        Assert.NotNull(m.Materials[m.Parts[0].Mesh.Faces[0].Material].BaseColourImage);
        Assert.Contains(m.Images[0].MimeType, new[] { "image/png", "image/jpeg" });
        Assert.NotEmpty(m.Images[0].Bytes);
    }

    [Fact]
    public void Textured_box_keeps_uvs_and_its_image()
    {
        var m = Read("BoxTextured.glb").Model!;
        Assert.NotNull(m.Parts[0].Mesh.Faces[0].Uv0);
        Assert.Single(m.Images);
    }

    [Fact]
    public void Plain_box_has_no_image_and_is_opaque()
    {
        var m = Read("Box.glb").Model!;
        Assert.Empty(m.Images);
        Assert.Null(m.Skeleton);
        Assert.Equal(AlphaMode.Opaque, m.Materials[m.Parts[0].Mesh.Faces[0].Material].Alpha);
    }

    [Fact]
    public void Fox_skeleton_matches_the_file()
    {
        var m = Read("Fox.glb").Model!;
        var skin = ModelRoot.Load(ModelPath("Fox.glb")).LogicalSkins[0];
        var expected = Enumerable.Range(0, skin.JointsCount).Select(i => skin.GetJoint(i).Joint.Name).ToArray();
        Assert.NotNull(m.Skeleton);
        Assert.Equal(expected.Length, m.Skeleton!.Joints.Count);
        Assert.Equal(expected, m.Skeleton.Joints.Select(j => j.Name).ToArray());
        foreach (var f in m.Parts.SelectMany(p => p.Mesh.Faces))
        {
            Assert.NotNull(f.Weights);
            Assert.Equal(f.Positions.Length * 4, f.Weights!.Joints.Length);
            Assert.Equal(f.Positions.Length * 4, f.Weights.Weights.Length);
            Assert.All(f.Weights.Joints, j => Assert.True(j < expected.Length));
        }
    }

    [Fact]
    public void Fox_animations_are_listed_as_dropped()
    {
        Assert.Contains("animation", Read("Fox.glb").Model!.Dropped);
    }

    [Fact]
    public void Box_drops_nothing()
    {
        Assert.Empty(Read("Box.glb").Model!.Dropped);
    }

    [Fact]
    public void Vertex_colour_box_reads()
    {
        Assert.Equal(12, TriangleCount(Read("BoxVertexColors.glb").Model!));
    }

    [Fact]
    public void Vertex_colours_are_listed_as_dropped()
    {
        Assert.Contains("vertex-colours", Read("BoxVertexColors.glb").Model!.Dropped);
    }

    // A one-pixel PNG.
    private const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    // The triangle with a material whose base colour picture is textureInfo (JSON object).
    private static byte[] TexturedTriangle(string textureInfo, string extensionsUsed = "") => GltfJson("[{\"mesh\":0}]", "[0]",
        top: ",\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":" + textureInfo + "}}],\"textures\":[{\"source\":0}],"
            + "\"images\":[{\"uri\":\"data:image/png;base64," + Pixel + "\"}]" + extensionsUsed,
        primitiveExtra: ",\"material\":0");

    [Theory]
    [InlineData("{\"index\":0,\"texCoord\":1}", "")]
    [InlineData("{\"index\":0,\"extensions\":{\"KHR_texture_transform\":{\"offset\":[0.5,0]}}}", ",\"extensionsUsed\":[\"KHR_texture_transform\"]")]
    public void Other_texture_coordinates_are_listed_as_dropped(string textureInfo, string extensionsUsed)
    {
        using var s = new MemoryStream(TexturedTriangle(textureInfo, extensionsUsed));
        Assert.Contains("texture-coords", GltfReader.Read(s, "t.gltf", Limits).Model!.Dropped);
    }

    [Fact]
    public void The_first_texture_coordinates_drop_nothing()
    {
        using var s = new MemoryStream(TexturedTriangle("{\"index\":0}"));
        Assert.Empty(GltfReader.Read(s, "t.gltf", Limits).Model!.Dropped);
    }

    [Fact]
    public void Garbage_input_gives_no_model_and_a_blocking_finding()
    {
        using var s = new MemoryStream(Encoding.ASCII.GetBytes("this is not a model"));
        var r = GltfReader.Read(s, "x.glb", Limits);
        Assert.Null(r.Model);
        var f = Assert.Single(r.Findings);
        Assert.Equal("bad-file", f.Code);
        Assert.Equal(FindingKind.Blocking, f.Kind);
    }

    // ---- Models from fetch-models.sh ----

    [Fact]
    public void DamagedHelmet_reads_with_its_base_colour_image()
    {
        var m = ReadDownloaded("DamagedHelmet.glb").Model!;
        Assert.Equal(15_452, TriangleCount(m));
        Assert.NotEmpty(m.Images);
    }

    [Fact]
    public void CesiumMan_has_a_skeleton_with_weights()
    {
        var m = ReadDownloaded("CesiumMan.glb").Model!;
        Assert.NotNull(m.Skeleton);
        Assert.NotEmpty(m.Skeleton!.Joints);
        Assert.Contains("animation", m.Dropped);
        Assert.All(m.Parts.SelectMany(p => p.Mesh.Faces), f => Assert.NotNull(f.Weights));
    }

    [Fact]
    public void NodePerformanceTest_gives_one_part_per_mesh_node()
    {
        // The model has more nodes than the 512 cap: refused with the cap, read with a higher one.
        Assert.Equal("too-large-file", Assert.Single(ReadDownloaded("NodePerformanceTest.glb").Findings).Code);
        var m = ReadDownloaded("NodePerformanceTest.glb", Limits with { MaxNodes = 1_000_000 }).Model!;
        Assert.True(m.Parts.Count > 1);
        Assert.Equal(0, m.RootPart);
    }

    [Fact]
    public void NegativeScaleTest_reads()
    {
        var m = ReadDownloaded("NegativeScaleTest.glb").Model!;
        Assert.NotEmpty(m.Parts);
        // The reader keeps a mirror as a negative Scale component (it does not bake it), so a later stage can report it as inside-out.
        Assert.Contains(m.Parts, p => p.Scale.X < 0 || p.Scale.Y < 0 || p.Scale.Z < 0);
        Assert.Contains(m.Parts, p => p.Scale.X > 0 && p.Scale.Y > 0 && p.Scale.Z > 0); // and the positive ones are left alone
    }

    [Fact]
    public void MeshPrimitiveModes_reads_triangles_strips_and_fans_and_reports_lines_and_points()
    {
        var path = Path.Combine("downloaded", "MeshPrimitiveModes", "MeshPrimitiveModes.gltf");
        var m = ReadDownloaded(Path.Combine("MeshPrimitiveModes", "MeshPrimitiveModes.gltf")).Model!;

        // The expected count, worked out from SharpGLTF's accessors alone: a list gives n/3 triangles, a strip or fan n-2.
        var root = SharpGLTF.Schema2.ModelRoot.Load(ModelPath(path));
        long expected = 0;
        bool anyLines = false, anyPoints = false;
        void Visit(SharpGLTF.Schema2.Node node)
        {
            if (node.Mesh != null)
                foreach (var prim in node.Mesh.Primitives)
                {
                    long n = prim.IndexAccessor?.Count ?? prim.GetVertexAccessor("POSITION").Count;
                    switch (prim.DrawPrimitiveType)
                    {
                        case SharpGLTF.Schema2.PrimitiveType.TRIANGLES: expected += n / 3; break;
                        case SharpGLTF.Schema2.PrimitiveType.TRIANGLE_STRIP:
                        case SharpGLTF.Schema2.PrimitiveType.TRIANGLE_FAN: expected += n - 2; break;
                        case SharpGLTF.Schema2.PrimitiveType.POINTS: anyPoints = true; break;
                        default: anyLines = true; break;
                    }
                }
            foreach (var c in node.VisualChildren) Visit(c);
        }
        foreach (var n in (root.DefaultScene ?? root.LogicalScenes[0]).VisualChildren) Visit(n);

        Assert.True(expected > 0);
        Assert.True(anyLines && anyPoints, "the model is expected to hold lines and points");
        Assert.Equal(expected, TriangleCount(m));
        Assert.Contains("lines", m.Dropped);
        Assert.Contains("points", m.Dropped);
    }
}
