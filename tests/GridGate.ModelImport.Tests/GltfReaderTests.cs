using System.Numerics;
using System.Text;
using GridGate.ModelImport.Model;
using GridGate.ModelImport.Reading;
using ModelRoot = SharpGLTF.Schema2.ModelRoot;
using Xunit;

namespace GridGate.ModelImport.Tests;

public class GltfReaderTests
{
    private static readonly ImportLimits Limits = new(
        MaxPartSize: 256, MinPartSize: 0.001, MaxPhysicalPartSize: 10, MaxLinksetParts: 0,
        MaxUploadBytes: 64L * 1024 * 1024, MaxImageSide: 8192, MaxTextureSide: 1024,
        MaxTriangles: 2_000_000, MaxNodes: 512, HeavyTriangles: 200_000);

    private static string ModelPath(string name) => Path.Combine(AppContext.BaseDirectory, "Models", name);

    private static ReadResult Read(string name)
    {
        using var s = File.OpenRead(ModelPath(name));
        return GltfReader.Read(s, name, Limits);
    }

    // A test that needs a model from fetch-models.sh skips when it was not fetched.
    private static ReadResult ReadDownloaded(string relative)
    {
        var path = Path.Combine("downloaded", relative);
        if (!File.Exists(ModelPath(path)))
            Assert.Skip($"{relative} is not downloaded; run tests/GridGate.ModelImport.Tests/fetch-models.sh");
        return Read(path);
    }

    private static int TriangleCount(ImportModel m) => m.Parts.Sum(p => p.Mesh.Faces.Sum(f => f.Triangles.Length / 3));

    // A one-triangle glTF with its buffer embedded, vertex 0 at (0,1,0); nodeExtras is added to the node's JSON.
    private static byte[] TriangleGltf(string nodeExtras)
    {
        var floats = new float[] { 0, 1, 0, 1, 0, 0, 0, 0, 1 };
        var bytes = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        var json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],"
            + "\"nodes\":[{\"mesh\":0" + nodeExtras + "}],"
            + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}],"
            + "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,1]}],"
            + "\"bufferViews\":[{\"buffer\":0,\"byteLength\":36}],"
            + "\"buffers\":[{\"byteLength\":36,\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(bytes) + "\"}]}";
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
        Assert.Equal(new Vector3(0, 1, 0), face.Positions[0]); // vertices stay in the part's own frame
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
        var up = part.Position + Vector3.Transform(new Vector3(0, 1, 0) * part.Scale, part.Rotation);
        Assert.Equal(0f, up.X, 4);
        Assert.Equal(0f, up.Y, 4);
        Assert.Equal(1f, up.Z, 4);
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
    public void Garbage_input_gives_no_model_and_a_blocking_finding()
    {
        using var s = new MemoryStream(Encoding.ASCII.GetBytes("this is not a model"));
        var r = GltfReader.Read(s, "x.glb", Limits);
        Assert.Null(r.Model);
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.Blocking);
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
        var m = ReadDownloaded("NodePerformanceTest.glb").Model!;
        Assert.True(m.Parts.Count > 1);
        Assert.Equal(0, m.RootPart);
    }

    [Fact]
    public void NegativeScaleTest_reads()
    {
        var m = ReadDownloaded("NegativeScaleTest.glb").Model!;
        Assert.NotEmpty(m.Parts);
    }

    [Fact]
    public void MeshPrimitiveModes_reads_only_the_triangle_primitives()
    {
        var m = ReadDownloaded(Path.Combine("MeshPrimitiveModes", "MeshPrimitiveModes.gltf")).Model!;
        Assert.NotEmpty(m.Parts);
        Assert.All(m.Parts.SelectMany(p => p.Mesh.Faces), f => Assert.True(f.Triangles.Length % 3 == 0));
    }
}
