using System.Numerics;
using System.Text;
using GridGate.ModelImport.Model;
using GridGate.ModelImport.Reading;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;
using Xunit;
using static GridGate.ModelImport.Tests.GltfReaderTests;

namespace GridGate.ModelImport.Tests;

public class ReaderRefusalTests
{
    private static ReadResult Read(byte[] bytes, ImportLimits? limits = null, Action<string>? onSatelliteRequest = null)
    {
        using var s = new MemoryStream(bytes);
        return GltfReader.Read(s, "test.gltf", limits ?? Limits, onSatelliteRequest);
    }

    private static Finding OnlyBlocking(ReadResult r)
    {
        Assert.Null(r.Model);
        var f = Assert.Single(r.Findings);
        Assert.Equal(FindingKind.Blocking, f.Kind);
        return f;
    }

    private static byte[] PngHeader(int width, int height)
    {
        var b = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        b.AddRange(new[] { (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width });
        b.AddRange(new[] { (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height });
        b.AddRange(new byte[] { 8, 2, 0, 0, 0 });
        b.AddRange(Encoding.ASCII.GetBytes("garbage after the header, not a real image"));
        return b.ToArray();
    }

    // ---- Outside files: refused, and nothing is requested ----

    [Fact]
    public void External_buffer_is_blocking_and_nothing_is_requested()
    {
        var requests = new List<string>();
        var f = OnlyBlocking(Read(GltfJson("[{\"mesh\":0}]", "[0]", bufferUri: "model.bin"), onSatelliteRequest: requests.Add));
        Assert.Equal("external-files", f.Code);
        Assert.Empty(requests);
    }

    [Fact]
    public void External_image_is_blocking_and_nothing_is_requested()
    {
        var requests = new List<string>();
        var bytes = GltfJson("[{\"mesh\":0}]", "[0]", top: ",\"images\":[{\"uri\":\"https://example.com/a.png\"}]");
        var f = OnlyBlocking(Read(bytes, onSatelliteRequest: requests.Add));
        Assert.Equal("external-files", f.Code);
        Assert.Empty(requests);
    }

    [Fact]
    public void A_path_that_climbs_out_is_just_another_external_file()
    {
        var f = OnlyBlocking(Read(GltfJson("[{\"mesh\":0}]", "[0]", bufferUri: "../../secret.bin")));
        Assert.Equal("external-files", f.Code);
    }

    [Fact]
    public void Embedded_buffer_makes_no_request()
    {
        var requests = new List<string>();
        var r = Read(GltfJson("[{\"mesh\":0}]", "[0]"), onSatelliteRequest: requests.Add);
        Assert.NotNull(r.Model);
        Assert.Empty(requests);
    }

    // ---- Compression ----

    [Fact]
    public void Draco_is_blocking()
    {
        var f = OnlyBlocking(ReadDownloaded(Path.Combine("Lantern-draco", "Lantern-draco.gltf")));
        Assert.Equal("compressed", f.Code);
    }

    [Fact]
    public void Meshopt_is_blocking()
    {
        var f = OnlyBlocking(ReadDownloaded(Path.Combine("MeshoptCubeTest", "MeshoptCubeTest.gltf")));
        Assert.Equal("compressed", f.Code);
    }

    [Theory]
    [InlineData("KHR_draco_mesh_compression")]
    [InlineData("EXT_meshopt_compression")]
    [InlineData("KHR_meshopt_compression")]
    public void Each_compression_extension_is_blocking_even_when_only_required(string extension)
    {
        var bytes = GltfJson("[{\"mesh\":0}]", "[0]", top: ",\"extensionsRequired\":[\"" + extension + "\"]");
        Assert.Equal("compressed", OnlyBlocking(Read(bytes)).Code);
    }

    // ---- Size caps ----

    [Fact]
    public void Image_above_8192_is_blocking_before_decode()
    {
        var root = ModelRoot.CreateModel();
        root.UseImage(new MemoryImage(PngHeader(10000, 10)));
        var glb = root.WriteGLB();
        var f = OnlyBlocking(Read(glb.ToArray()));
        Assert.Equal("too-large-file", f.Code);
        Assert.Equal("10000x10", f.Values["image"]);
        Assert.Equal("8192", f.Values["limit"]);
    }

    [Fact]
    public void Image_at_the_limit_is_kept()
    {
        var m = Read(TexturedGltf(PngHeader(8192, 8192))).Model!;
        Assert.Single(m.Images);
        Assert.Equal("image/png", m.Images[0].MimeType);
        Assert.Equal(0, m.Materials[0].BaseColourImage);
    }

    private static byte[] TexturedGltf(byte[] image, string mime = "image/png")
    {
        var top = ",\"images\":[{\"uri\":\"data:" + mime + ";base64," + Convert.ToBase64String(image) + "\"}],\"textures\":[{\"source\":0}],"
            + "\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}]";
        return GltfJson("[{\"mesh\":0}]", "[0]", top: top, primitiveExtra: ",\"material\":0");
    }

    // A .glb with the one-triangle mesh and one base colour image kept in the binary chunk (declared as mime, whatever it holds).
    private static byte[] TexturedGlb(byte[] image, string mime, bool webpTexture = false)
    {
        var bin = new List<byte>();
        foreach (var f in new float[] { 0, 1, 0, 1, 0, 0, 0, 0, 1 }) bin.AddRange(BitConverter.GetBytes(f));
        bin.AddRange(image);
        while (bin.Count % 4 != 0) bin.Add(0);
        var json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}],"
            + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"material\":0}]}],"
            + "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,1]}],"
            + "\"bufferViews\":[{\"buffer\":0,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":36,\"byteLength\":" + image.Length + "}],"
            + "\"buffers\":[{\"byteLength\":" + bin.Count + "}],"
            + "\"images\":[{\"bufferView\":1,\"mimeType\":\"" + mime + "\"}],\"textures\":[" + (webpTexture ? "{\"extensions\":{\"EXT_texture_webp\":{\"source\":0}}}" : "{\"source\":0}") + "],"
            + (webpTexture ? "\"extensionsUsed\":[\"EXT_texture_webp\"]," : "")
            + "\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}]}";
        var jsonBytes = new List<byte>(Encoding.UTF8.GetBytes(json));
        while (jsonBytes.Count % 4 != 0) jsonBytes.Add((byte)' ');
        var glb = new List<byte>();
        glb.AddRange(Encoding.ASCII.GetBytes("glTF"));
        glb.AddRange(BitConverter.GetBytes(2u));
        glb.AddRange(BitConverter.GetBytes((uint)(12 + 8 + jsonBytes.Count + 8 + bin.Count)));
        glb.AddRange(BitConverter.GetBytes((uint)jsonBytes.Count)); glb.AddRange(BitConverter.GetBytes(0x4E4F534Au)); glb.AddRange(jsonBytes);
        glb.AddRange(BitConverter.GetBytes((uint)bin.Count)); glb.AddRange(BitConverter.GetBytes(0x004E4942u)); glb.AddRange(bin);
        return glb.ToArray();
    }
    [Theory]
    [InlineData("bmp")]
    [InlineData("junk")]
    public void An_image_SharpGLTF_does_not_accept_makes_the_file_bad(string kind)
    {
        // A BMP header claiming 60000x60000, or junk bytes: SharpGLTF's validation refuses them, so the file is bad-file and
        // nothing is ever decoded.
        var bytes = kind == "bmp"
            ? new byte[] { (byte)'B', (byte)'M', 0, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0, 40, 0, 0, 0, 0x60, 0xEA, 0, 0, 0x60, 0xEA, 0, 0, 1, 0, 24, 0 }
            : Encoding.ASCII.GetBytes("this is not an image at all");
        Assert.Equal("bad-file", OnlyBlocking(Read(TexturedGlb(bytes, "image/png"))).Code);
    }

    [Fact]
    public void A_webp_image_is_left_out_with_a_finding()
    {
        // SharpGLTF 1.0.7 does not read EXT_texture_webp or KHR_texture_basisu (those files are bad-file), but it accepts a WebP image used as a plain
        // texture source. That is not PNG or JPEG, so it is dropped and the material loses its texture.
        var webp = Encoding.ASCII.GetBytes("RIFF\x20\0\0\0WEBPVP8 and some more bytes that are not a real image");
        var r = Read(TexturedGlb(webp, "image/webp"));
        Assert.True(r.Model != null, string.Join(";", r.Findings.Select(x => x.Code + string.Join(",", x.Values))));
        var f = Assert.Single(r.Findings);
        Assert.Equal("texture-unsupported", f.Code);
        Assert.Equal(FindingKind.Risky, f.Kind);
        Assert.Equal("webp", f.Values["format"]);
        Assert.False(string.IsNullOrEmpty(f.Values["image"]));
        Assert.Empty(r.Model!.Images);
        Assert.Null(r.Model.Materials[0].BaseColourImage);
    }

    [Fact]
    public void A_webp_texture_extension_is_bad_file_because_SharpGLTF_does_not_read_it()
    {
        var webp = Encoding.ASCII.GetBytes("RIFF\x20\0\0\0WEBPVP8 and some more bytes that are not a real image");
        Assert.Equal("bad-file", OnlyBlocking(Read(TexturedGlb(webp, "image/webp", webpTexture: true))).Code);
    }

    [Fact]
    public void A_utf8_byte_order_mark_before_the_json_is_accepted()
    {
        var json = GltfJson("[{\"mesh\":0}]", "[0]");
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(json).ToArray();
        Assert.NotNull(Read(withBom).Model);
    }

    [Fact]
    public void Upload_above_cap_is_blocking()
    {
        using var s = File.OpenRead(ModelPath("Box.glb"));
        var f = OnlyBlocking(GltfReader.Read(s, "Box.glb", Limits with { MaxUploadBytes = 1000 }));
        Assert.Equal("too-large-file", f.Code);
        Assert.Equal("upload", f.Values["what"]);
        Assert.Equal("1000", f.Values["limit"]);
    }

    [Fact]
    public void Over_the_triangle_cap_is_blocking()
    {
        using var s = File.OpenRead(ModelPath("Box.glb"));
        var f = OnlyBlocking(GltfReader.Read(s, "Box.glb", Limits with { MaxTriangles = 11 }));
        Assert.Equal("too-large-file", f.Code);
        Assert.Equal("triangles", f.Values["what"]);
    }

    [Fact]
    public void Box_at_exactly_the_triangle_cap_is_read()
    {
        using var s = File.OpenRead(ModelPath("Box.glb"));
        Assert.NotNull(GltfReader.Read(s, "Box.glb", Limits with { MaxTriangles = 12 }).Model);
    }

    [Fact]
    public void Over_the_node_cap_is_blocking()
    {
        using var s = File.OpenRead(ModelPath("Box.glb"));
        var f = OnlyBlocking(GltfReader.Read(s, "Box.glb", Limits with { MaxNodes = 0 }));
        Assert.Equal("too-large-file", f.Code);
        Assert.Equal("nodes", f.Values["what"]);
    }

    // ---- Not a usable file ----

    [Fact]
    public void Not_glTF_is_blocking()
    {
        var f = OnlyBlocking(Read(Encoding.UTF8.GetBytes("Hello, this is a text file.")));
        Assert.Equal("bad-file", f.Code);
    }

    [Fact]
    public void Cut_short_glb_is_blocking()
    {
        var bytes = File.ReadAllBytes(ModelPath("Box.glb"));
        Assert.Equal("bad-file", OnlyBlocking(Read(bytes[..100])).Code);
        Assert.Equal("bad-file", OnlyBlocking(Read(bytes[..10])).Code);
    }

    [Fact]
    public void File_with_no_mesh_is_blocking()
    {
        var f = OnlyBlocking(Read(GltfJson("[{\"name\":\"empty\"}]", "[0]")));
        Assert.Equal("bad-file", f.Code);
    }

    [Fact]
    public void Json_that_is_not_an_object_is_blocking()
    {
        Assert.Equal("bad-file", OnlyBlocking(Read(Encoding.UTF8.GetBytes("[1,2,3]"))).Code);
    }

    // ---- Placement ----

    [Fact]
    public void Nested_transforms_are_baked_into_part_placement()
    {
        // Parent: turned 90 degrees about glTF Y and scaled (2,1,1). Child: moved to (1,0,0) and holds the mesh.
        // The child sits at (2,0,0) after the scale, then (0,0,-2) after the turn: (0,2,0) in Z-up.
        var s = (float)Math.Sin(Math.PI / 4);
        var nodes = "[{\"children\":[1],\"rotation\":[0," + s.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",0,"
            + s.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "],\"scale\":[2,1,1]},{\"mesh\":0,\"translation\":[1,0,0]}]";
        var part = Assert.Single(Read(GltfJson(nodes, "[0]")).Model!.Parts);
        Assert.Equal(0f, part.Position.X, 1e-4f);
        Assert.Equal(2f, part.Position.Y, 1e-4f);
        Assert.Equal(0f, part.Position.Z, 1e-4f);
    }

    [Fact]
    public void A_matrix_with_shear_is_baked_into_the_vertices()
    {
        // Parent scaled (2,1,1); child turned 45 degrees about glTF Z. Together they shear, so there is no position, rotation
        // and scale for them. Vertex 0 (0,1,0): turned to (-sin45, cos45, 0), scaled to (-1.4142, 0.7071, 0); in Z-up (x, -z, y).
        var k = Math.Sin(Math.PI / 8).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var c = Math.Cos(Math.PI / 8).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var nodes = "[{\"children\":[1],\"scale\":[2,1,1]},{\"mesh\":0,\"rotation\":[0,0," + k + "," + c + "]}]";
        var part = Assert.Single(Read(GltfJson(nodes, "[0]")).Model!.Parts);

        Assert.Equal(Vector3.Zero, part.Position);
        Assert.Equal(Quaternion.Identity, part.Rotation);
        Assert.Equal(Vector3.One, part.Scale);
        var v = part.Mesh.Faces[0].Positions[0];
        Assert.Equal(-1.4142135f, v.X, 1e-3f);
        Assert.Equal(0f, v.Y, 1e-3f);
        Assert.Equal(0.7071068f, v.Z, 1e-3f);
        Assert.All(part.Mesh.Faces[0].Normals, n => Assert.Equal(1f, n.Length(), 1e-3f));
    }
}
