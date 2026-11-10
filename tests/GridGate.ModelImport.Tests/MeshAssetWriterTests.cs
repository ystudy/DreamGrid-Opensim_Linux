using System.IO.Compression;
using GridGate.ModelImport.Building;
using GridGate.ModelImport.Model;
using OpenMetaverse;
using OpenMetaverse.Assets;
using OpenMetaverse.Rendering;
using OpenMetaverse.StructuredData;
using Xunit;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace GridGate.ModelImport.Tests;

public class MeshAssetWriterTests
{
    private static readonly MeshAssetOptions Plain = new(false, null);

    // The asset decoded by OpenMetaverse's FacetedMesh, the reader the tests trust for OpenSim's view of the format.
    private static FacetedMesh Decode(byte[] asset, DetailLevel level)
    {
        var prim = new Primitive { Textures = new Primitive.TextureEntry(UUID.Zero) };
        var meshAsset = new AssetMesh(UUID.Zero, asset);
        Assert.True(FacetedMesh.TryDecodeFromAsset(prim, meshAsset, level, out var mesh), $"{level} did not decode");
        return mesh;
    }

    // The header map and the bytes after it, read the way OpenSim's Meshmerizer and MeshCost read them.
    private static (OSDMap Header, int Start) Header(byte[] asset)
    {
        using var ms = new MemoryStream(asset);
        var header = (OSDMap)OSDParser.DeserializeLLSDBinary(ms);
        return (header, (int)ms.Position);
    }

    // A block inflated the way OpenSim does: skip the 2-byte zlib header, then deflate.
    private static OSD Block(byte[] asset, string name)
    {
        var (header, start) = Header(asset);
        var info = (OSDMap)header[name];
        int offset = info["offset"].AsInteger() + start, size = info["size"].AsInteger();
        Assert.True(asset[offset] == 0x78, "block is not zlib");
        using var outMs = new MemoryStream();
        using (var inMs = new MemoryStream(asset, offset + 2, size - 2))
        using (var inflate = new DeflateStream(inMs, CompressionMode.Decompress))
            inflate.CopyTo(outMs);
        outMs.Position = 0;
        return OSDParser.DeserializeLLSDBinary(outMs);
    }

    private static int Triangles(FacetedMesh m) => m.Faces.Sum(f => f.Indices.Count / 3);

    [Theory]
    [InlineData("Box.glb")]
    [InlineData("Avocado.glb")]
    public void Round_trip_through_OpenSims_reader(string model)
    {
        var mesh = SimplifierTests.FirstMesh(model);
        var asset = MeshAssetWriter.Write(mesh, Plain);
        var decoded = Decode(asset, DetailLevel.Highest);

        Assert.Equal(mesh.Faces.Count, decoded.Faces.Count);
        Assert.Equal(SimplifierTests.Triangles(mesh), Triangles(decoded));
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var face = mesh.Faces[f];
            var back = decoded.Faces[f];
            Assert.Equal(face.Triangles, back.Indices.Select(i => (int)i));
            Assert.Equal(face.Positions.Length, back.Vertices.Count);

            var min = face.Positions.Aggregate(Vector3.Min);
            var max = face.Positions.Aggregate(Vector3.Max);
            var step = (max - min) / 65535f;
            for (int v = 0; v < face.Positions.Length; v++)
            {
                var p = back.Vertices[v].Position;
                Assert.InRange(MathF.Abs(p.X - face.Positions[v].X), 0, step.X + 1e-6f);
                Assert.InRange(MathF.Abs(p.Y - face.Positions[v].Y), 0, step.Y + 1e-6f);
                Assert.InRange(MathF.Abs(p.Z - face.Positions[v].Z), 0, step.Z + 1e-6f);
            }

            if (face.Uv0 == null) continue;
            // Second Life's texture V runs bottom to top, glTF's top to bottom: the writer turns v into 1 - v.
            var flipped = face.Uv0.Select(t => new Vector2(t.X, 1 - t.Y)).ToArray();
            var uvMin = flipped.Aggregate(Vector2.Min);
            var uvMax = flipped.Aggregate(Vector2.Max);
            var uvStep = (uvMax - uvMin) / 65535f;
            for (int v = 0; v < flipped.Length; v++)
            {
                var t = back.Vertices[v].TexCoord;
                Assert.InRange(MathF.Abs(t.X - flipped[v].X), 0, uvStep.X + 1e-6f);
                Assert.InRange(MathF.Abs(t.Y - flipped[v].Y), 0, uvStep.Y + 1e-6f);
            }
        }
    }

    [Fact]
    public void Normals_are_written_from_minus_one_to_one()
    {
        var mesh = SimplifierTests.FirstMesh("Avocado.glb");
        var face = (OSDMap)((OSDArray)Block(MeshAssetWriter.Write(mesh, Plain), "high_lod"))[0];
        var bytes = face["Normal"].AsBinary();
        var normals = mesh.Faces[0].Normals;
        Assert.Equal(normals.Length * 6, bytes.Length);
        for (int v = 0; v < normals.Length; v++)
            for (int axis = 0; axis < 3; axis++)
            {
                var q = BitConverter.ToUInt16(bytes, v * 6 + axis * 2);
                var n = q / 65535f * 2 - 1;
                var want = axis == 0 ? normals[v].X : axis == 1 ? normals[v].Y : normals[v].Z;
                Assert.InRange(MathF.Abs(n - want), 0, 2f / 65535 + 1e-6f);
            }
    }

    [Theory]
    [InlineData("Box.glb")]
    [InlineData("Avocado.glb")]
    public void All_four_detail_levels_decode(string model)
    {
        var mesh = SimplifierTests.FirstMesh(model);
        var asset = MeshAssetWriter.Write(mesh, Plain);
        int above = int.MaxValue;
        foreach (var level in new[] { DetailLevel.Highest, DetailLevel.High, DetailLevel.Medium, DetailLevel.Low })
        {
            var decoded = Decode(asset, level);
            Assert.Equal(mesh.Faces.Count, decoded.Faces.Count);
            var n = Triangles(decoded);
            Assert.True(n > 0);
            Assert.True(n <= above, $"{level} has {n} triangles, more than the level above ({above})");
            above = n;
        }
    }

    [Fact]
    public void Header_and_physics_block_read_as_OpenSim_reads_them()
    {
        var mesh = SimplifierTests.FirstMesh("Avocado.glb");
        var asset = MeshAssetWriter.Write(mesh, Plain);
        var (header, _) = Header(asset);
        Assert.Equal(new[] { "high_lod", "low_lod", "lowest_lod", "medium_lod", "physics_convex" }, header.Keys.OrderBy(k => k, StringComparer.Ordinal));

        var convex = (OSDMap)Block(asset, "physics_convex");
        var all = mesh.Faces.SelectMany(f => f.Positions).ToArray();
        var min = all.Aggregate(Vector3.Min);
        var max = all.Aggregate(Vector3.Max);
        var cmin = convex["Min"].AsVector3();
        var cmax = convex["Max"].AsVector3();
        Assert.Equal(min.X, cmin.X, 5); Assert.Equal(min.Y, cmin.Y, 5); Assert.Equal(min.Z, cmin.Z, 5);
        Assert.Equal(max.X, cmax.X, 5); Assert.Equal(max.Y, cmax.Y, 5); Assert.Equal(max.Z, cmax.Z, 5);
        var verts = convex["BoundingVerts"].AsBinary();
        Assert.Equal(8 * 6, verts.Length);
        var corners = Enumerable.Range(0, 8).Select(i => (BitConverter.ToUInt16(verts, i * 6), BitConverter.ToUInt16(verts, i * 6 + 2), BitConverter.ToUInt16(verts, i * 6 + 4))).Distinct().ToArray();
        Assert.Equal(8, corners.Length);
        Assert.All(corners, c => Assert.True(c.Item1 is 0 or 65535 && c.Item2 is 0 or 65535 && c.Item3 is 0 or 65535));
    }

    private static ImportModel SkinnedModel(string name, bool downloaded)
    {
        var r = downloaded ? GltfReaderTests.ReadDownloaded(name) : GltfReaderTests.Read(name);
        Assert.NotNull(r.Model!.Skeleton);
        return r.Model;
    }

    private static IEnumerable<float> Flat(Matrix4x4 m) => new[]
    {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
    };

    // A face's Weights decoded as the Second Life viewer reads them: per vertex, up to four (joint byte, weight uint16 LE) pairs,
    // ended by joint 0xFF when there are fewer than four.
    private static List<(int Joint, int Weight)[]> DecodeWeights(byte[] bytes, int vertices)
    {
        var result = new List<(int, int)[]>();
        int i = 0;
        for (int v = 0; v < vertices; v++)
        {
            var list = new List<(int, int)>();
            while (list.Count < 4)
            {
                int joint = bytes[i++];
                if (joint == 0xFF) break;
                list.Add((joint, bytes[i] | (bytes[i + 1] << 8)));
                i += 2;
            }
            result.Add(list.ToArray());
        }
        Assert.Equal(bytes.Length, i);
        return result;
    }

    [Theory]
    [InlineData("Fox.glb", false)]
    [InlineData("CesiumMan.glb", true)]
    public void Skin_round_trip(string name, bool downloaded)
    {
        var model = SkinnedModel(name, downloaded);
        var mesh = model.Parts[0].Mesh;
        var skeleton = model.Skeleton!;
        var asset = MeshAssetWriter.Write(mesh, new MeshAssetOptions(true, skeleton));

        var skin = (OSDMap)Block(asset, "skin");
        Assert.Equal(skeleton.Joints.Select(j => j.Name), ((OSDArray)skin["joint_names"]).Select(o => o.AsString()));
        var inverse = (OSDArray)skin["inverse_bind_matrix"];
        Assert.Equal(skeleton.Joints.Count, inverse.Count);
        for (int j = 0; j < inverse.Count; j++)
        {
            var values = ((OSDArray)inverse[j]).Select(o => (float)o.AsReal()).ToArray();
            Assert.Equal(16, values.Length);
            Assert.Equal(Flat(skeleton.Joints[j].InverseBind), values);
        }
        Assert.Equal(Flat(skeleton.BindShape), ((OSDArray)skin["bind_shape_matrix"]).Select(o => (float)o.AsReal()));

        var lod = (OSDArray)Block(asset, "high_lod");
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var face = mesh.Faces[f];
            var weights = DecodeWeights(((OSDMap)lod[f])["Weights"].AsBinary(), face.Positions.Length);
            for (int v = 0; v < weights.Count; v++)
            {
                var sum = weights[v].Sum(w => w.Weight) / 65535.0;
                Assert.InRange(Math.Abs(sum - 1), 0, 1.0 / 65535);
                // Each influence is one of the vertex's own joints with its share of the weight.
                var total = Enumerable.Range(0, 4).Sum(k => face.Weights!.Weights[v * 4 + k]);
                foreach (var (joint, weight) in weights[v])
                {
                    var share = Enumerable.Range(0, 4).Where(k => face.Weights!.Joints[v * 4 + k] == joint).Sum(k => face.Weights!.Weights[v * 4 + k]) / total;
                    Assert.InRange(Math.Abs(weight / 65535.0 - share), 0, 2.0 / 65535);
                }
            }
        }

        // The lower detail levels carry weights too.
        foreach (var level in new[] { "medium_lod", "low_lod", "lowest_lod" })
            Assert.All(((OSDArray)Block(asset, level)).Cast<OSDMap>(), m => Assert.True(m.ContainsKey("Weights")));
        Assert.Equal(mesh.Faces.Count, Decode(asset, DetailLevel.Highest).Faces.Count);
    }

    [Fact]
    public void No_skin_block_unless_asked()
    {
        var model = SkinnedModel("Fox.glb", false);
        var asset = MeshAssetWriter.Write(model.Parts[0].Mesh, new MeshAssetOptions(false, model.Skeleton));
        var (header, _) = Header(asset);
        Assert.False(header.ContainsKey("skin"));
        Assert.All(((OSDArray)Block(asset, "high_lod")).Cast<OSDMap>(), m => Assert.False(m.ContainsKey("Weights")));
    }
}
