using GridGate.ModelImport.Building;
using GridGate.ModelImport.Checking;
using GridGate.ModelImport.Model;
using GridGate.ModelImport.Reading;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using Xunit;
using PermissionMask = OpenSim.Framework.PermissionMask;
using NQuaternion = System.Numerics.Quaternion;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace GridGate.ModelImport.Tests;

public class ObjectAssetBuilderTests
{
    private static readonly ImportLimits Limits = GltfReaderTests.Limits;
    private static readonly Guid Uploader = new("6f1c2a7e-3b4d-4e8a-9c21-5d7f0a8b9e10");

    private static ImportOptions Options(NextOwner? next = null) =>
        new("Thing", "thing.glb", 100, next ?? new NextOwner(true, true, false), Uploader, "Tester");

    private static ImportModel Checked(ReadResult read) => ModelChecker.Check(read.Model!, Limits, Options()).Fixed;

    private static SceneObjectGroup Build(ImportModel model, IReadOnlyDictionary<int, Guid>? textures = null, ImportOptions? options = null,
        IReadOnlyList<Guid>? meshIds = null)
    {
        meshIds ??= model.Parts.Select(_ => Guid.NewGuid()).ToList();
        var xml = ObjectAssetBuilder.Build(model, meshIds, textures ?? new Dictionary<int, Guid>(), options ?? Options(), Limits);
        var group = SceneObjectSerializer.FromOriginalXmlFormat(xml);
        Assert.NotNull(group);
        return group;
    }

    private static SceneObjectPart[] ByLink(SceneObjectGroup g) => g.Parts.OrderBy(p => p.LinkNum).ToArray();

    // A part's place and turn in the world, as SceneObjectPart.GetWorldPosition and GetWorldRotation work them out once the
    // object is in a scene (outside one every ParentID is 0, so those methods take every part for the root).
    private static Vector3 WorldPosition(SceneObjectGroup g, SceneObjectPart p) =>
        p == g.RootPart ? g.AbsolutePosition : g.AbsolutePosition + p.OffsetPosition * g.RootPart.RotationOffset;

    private static Quaternion WorldRotation(SceneObjectGroup g, SceneObjectPart p) =>
        p == g.RootPart ? p.RotationOffset : g.RootPart.RotationOffset * p.RotationOffset;

    // A box of the given corners, every face with the given material; normals point out of the box.
    private static ImportFace BoxFace(NVector3 lo, NVector3 hi, int material = 0)
    {
        var pos = new List<NVector3>();
        var nor = new List<NVector3>();
        var tris = new List<int>();
        void Quad(NVector3 n, NVector3 a, NVector3 b, NVector3 c, NVector3 d)
        {
            int s = pos.Count;
            pos.AddRange(new[] { a, b, c, d });
            nor.AddRange(new[] { n, n, n, n });
            tris.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 });
        }
        Quad(-NVector3.UnitZ, new(lo.X, lo.Y, lo.Z), new(lo.X, hi.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, lo.Y, lo.Z));
        Quad(NVector3.UnitZ, new(lo.X, lo.Y, hi.Z), new(hi.X, lo.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(lo.X, hi.Y, hi.Z));
        Quad(-NVector3.UnitX, new(lo.X, lo.Y, lo.Z), new(lo.X, lo.Y, hi.Z), new(lo.X, hi.Y, hi.Z), new(lo.X, hi.Y, lo.Z));
        Quad(NVector3.UnitX, new(hi.X, lo.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, lo.Y, hi.Z));
        Quad(-NVector3.UnitY, new(lo.X, lo.Y, lo.Z), new(hi.X, lo.Y, lo.Z), new(hi.X, lo.Y, hi.Z), new(lo.X, lo.Y, hi.Z));
        Quad(NVector3.UnitY, new(lo.X, hi.Y, lo.Z), new(lo.X, hi.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, hi.Y, lo.Z));
        var uv = pos.Select(_ => NVector2.Zero).ToArray();
        return new ImportFace(pos.ToArray(), nor.ToArray(), uv, tris.ToArray(), material, null);
    }

    private static ImportPart Part(string name, NVector3 position, NQuaternion rotation, params ImportFace[] faces) =>
        new(name, position, rotation, NVector3.One, new ImportMesh(faces));

    private static ImportModel Model(IReadOnlyList<ImportPart> parts, IReadOnlyList<ImportMaterial>? materials = null, int images = 0) =>
        new(parts, 0, materials ?? new[] { new ImportMaterial("m", NVector4.One, null, AlphaMode.Opaque, 0.5f, false) },
            Enumerable.Range(0, images).Select(i => new ImportImage($"image{i}", "image/png", Array.Empty<byte>())).ToList(), null, Array.Empty<string>());

    private static void Near(NVector3 expected, Vector3 actual, float tolerance)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
        Assert.InRange(actual.Z, expected.Z - tolerance, expected.Z + tolerance);
    }

    // Two rotations are the same turn when |dot| is 1 (q and -q turn alike).
    private static void SameTurn(NQuaternion expected, Quaternion actual)
    {
        var e = NQuaternion.Normalize(expected);
        var dot = MathF.Abs(e.X * actual.X + e.Y * actual.Y + e.Z * actual.Z + e.W * actual.W);
        Assert.InRange(dot, 1 - 1e-4f, 1 + 1e-4f);
    }

    [Fact]
    public void Object_round_trips_through_OpenSims_serializer()
    {
        var model = Checked(GltfReaderTests.Read("Box.glb"));
        var meshId = Guid.NewGuid();
        var g = Build(model, meshIds: new[] { meshId });

        var part = Assert.Single(g.Parts);
        Assert.Equal((byte)SculptType.Mesh, part.Shape.SculptType);
        Assert.Equal(new UUID(meshId), part.Shape.SculptTexture);
        Assert.Equal(new UUID(Uploader), part.CreatorID);
        Assert.Equal(new UUID(Uploader), part.OwnerID);
        Assert.Equal((uint)PermissionMask.All | (uint)PermissionMask.Export, part.OwnerMask);
        Assert.Equal("Thing", part.Name);
        // Copy and modify for the next owner, and move, which a next owner always keeps.
        Assert.Equal((uint)(PermissionMask.Move | PermissionMask.Copy | PermissionMask.Modify), part.NextOwnerMask);
    }

    [Fact]
    public void No_next_owner_rights_gives_transfer()
    {
        var model = Checked(GltfReaderTests.Read("Box.glb"));
        var g = Build(model, options: Options(new NextOwner(false, false, false)));
        Assert.Equal((uint)(PermissionMask.Move | PermissionMask.Transfer), Assert.Single(g.Parts).NextOwnerMask);
    }

    [Fact]
    public void Linkset_keeps_part_positions()
    {
        // Flight Helmet is published as a .gltf with separate files, which the converter refuses; the test packs it into one GLB.
        var path = GltfReaderTests.ModelPath(Path.Combine("downloaded", "FlightHelmet", "FlightHelmet.gltf"));
        if (!File.Exists(path)) Assert.Skip("FlightHelmet is not downloaded; run tests/GridGate.ModelImport.Tests/fetch-models.sh");
        using var glb = new MemoryStream();
        SharpGLTF.Schema2.ModelRoot.Load(path).WriteGLB(glb);
        glb.Position = 0;
        var model = Checked(GltfReader.Read(glb, "FlightHelmet.glb", Limits));
        Assert.True(model.Parts.Count > 1);
        var g = Build(model);
        Assert.Equal(model.Parts.Count, g.Parts.Length);

        // Link 1 is the root part; the other parts follow in the model's order.
        var order = new[] { model.RootPart }.Concat(Enumerable.Range(0, model.Parts.Count).Where(i => i != model.RootPart)).ToArray();
        var prims = ByLink(g);
        var rootPlace = UnitMesh.Placement(model.Parts[model.RootPart], Limits.MinPartSize);
        var rootWorld = WorldPosition(g, prims[0]);
        for (int k = 0; k < prims.Length; k++)
        {
            var part = model.Parts[order[k]];
            var place = UnitMesh.Placement(part, Limits.MinPartSize);
            Near(place.Position - rootPlace.Position, WorldPosition(g, prims[k]) - rootWorld, 1e-3f);
            SameTurn(part.Rotation, WorldRotation(g, prims[k]));
            Assert.Equal(k == 0 ? "Thing" : $"Thing #{k}", prims[k].Name);
        }
    }

    [Fact]
    public void Off_centre_part_rezzes_where_its_geometry_is()
    {
        // The root is a unit cube turned 30 degrees about X; the child's geometry spans x 10..12 in its own frame,
        // and the child is turned 90 degrees about Z and placed at (1, 2, 3).
        var rootTurn = NQuaternion.CreateFromAxisAngle(NVector3.UnitX, MathF.PI / 6);
        var childTurn = NQuaternion.CreateFromAxisAngle(NVector3.UnitZ, MathF.PI / 2);
        var root = Part("root", NVector3.Zero, rootTurn, BoxFace(new(-0.5f), new(0.5f)));
        var child = Part("child", new(1, 2, 3), childTurn, BoxFace(new(10, -0.5f, -0.5f), new(12, 0.5f, 0.5f)));
        var g = Build(Model(new[] { root, child }));

        var prims = ByLink(g);
        Near(new(0, 0, 0), g.AbsolutePosition, 1e-5f);
        Near(new(1, 1, 1), prims[0].Scale, 1e-5f);
        Near(new(2, 1, 1), prims[1].Scale, 1e-5f);
        // The geometry's centre (11, 0, 0) turned 90 degrees about Z is (0, 11, 0); plus the part's position.
        Near(new(1, 13, 3), WorldPosition(g, prims[1]) - WorldPosition(g, prims[0]), 1e-4f);
        SameTurn(childTurn, WorldRotation(g, prims[1]));

        // The mesh written for the child fills -0.5..0.5 on each axis.
        var fitted = UnitMesh.Fit(child, Limits.MinPartSize);
        var all = fitted.Faces.SelectMany(f => f.Positions).ToArray();
        Near(new(-0.5f), ToOmv(all.Aggregate(NVector3.Min)), 1e-6f);
        Near(new(0.5f), ToOmv(all.Aggregate(NVector3.Max)), 1e-6f);
    }

    [Fact]
    public void Flat_part_gets_the_smallest_size_not_zero()
    {
        var flat = new ImportFace(new[] { NVector3.Zero, new NVector3(1, 0, 0), new NVector3(0, 1, 0) },
            new[] { NVector3.UnitZ, NVector3.UnitZ, NVector3.UnitZ }, null, new[] { 0, 1, 2 }, 0, null);
        var part = Part("flat", NVector3.Zero, NQuaternion.Identity, flat);
        var g = Build(Model(new[] { part }));
        Near(new(1, 1, (float)Limits.MinPartSize), Assert.Single(g.Parts).Scale, 1e-6f);

        // The fitted normals still point along Z.
        Assert.All(UnitMesh.Fit(part, Limits.MinPartSize).Faces[0].Normals, n => Near(NVector3.UnitZ, ToOmv(n), 1e-5f));
    }

    [Fact]
    public void Untextured_face_keeps_its_colour_and_alpha()
    {
        var materials = new[]
        {
            new ImportMaterial("red glass", new NVector4(1, 0, 0, 0.5f), null, AlphaMode.Blend, 0.5f, false),
            new ImportMaterial("pictured", new NVector4(1, 1, 1, 0.25f), 0, AlphaMode.Opaque, 0.5f, false),
            new ImportMaterial("broken image", new NVector4(0, 1, 0, 1), 1, AlphaMode.Opaque, 0.5f, false),
        };
        var faces = new[] { BoxFace(new(-0.5f), new(0.5f), 0), BoxFace(new(-0.5f), new(0.5f), 1), BoxFace(new(-0.5f), new(0.5f), 2) };
        var textureId = Guid.NewGuid();
        // Image 1 could not be used, so it has no texture id.
        var g = Build(Model(new[] { Part("p", NVector3.Zero, NQuaternion.Identity, faces) }, materials, images: 2),
            new Dictionary<int, Guid> { [0] = textureId });

        var shape = Assert.Single(g.Parts).Shape;
        var te = new Primitive.TextureEntry(shape.TextureEntry, 0, shape.TextureEntry.Length);
        const float step = 1 / 255f;

        var red = te.GetFace(0);
        Assert.Equal(Primitive.TextureEntry.WHITE_TEXTURE, red.TextureID);
        Assert.InRange(red.RGBA.R, 1 - step, 1);
        Assert.InRange(red.RGBA.G, 0, step);
        Assert.InRange(red.RGBA.B, 0, step);
        Assert.InRange(red.RGBA.A, 0.5f - step, 0.5f + step);

        var pictured = te.GetFace(1);
        Assert.Equal(new UUID(textureId), pictured.TextureID);
        Assert.InRange(pictured.RGBA.A, 1 - step, 1); // opaque forces alpha 1

        var broken = te.GetFace(2);
        Assert.Equal(Primitive.TextureEntry.WHITE_TEXTURE, broken.TextureID);
        Assert.InRange(broken.RGBA.G, 1 - step, 1);
        Assert.InRange(broken.RGBA.R, 0, step);
    }

    private static Primitive.TextureEntryFace OnlyFace(ImportMaterial material)
    {
        var g = Build(Model(new[] { Part("p", NVector3.Zero, NQuaternion.Identity, BoxFace(new(-0.5f), new(0.5f))) }, new[] { material }));
        var shape = Assert.Single(g.Parts).Shape;
        return new Primitive.TextureEntry(shape.TextureEntry, 0, shape.TextureEntry.Length).GetFace(0);
    }

    [Fact]
    public void Mask_material_is_drawn_fully_opaque()
    {
        // A mask cuts pixels by the picture's alpha; the colour's own alpha does not make the face see-through.
        var face = OnlyFace(new ImportMaterial("leaves", new NVector4(1, 1, 1, 0.3f), null, AlphaMode.Mask, 0.5f, false));
        Assert.InRange(face.RGBA.A, 1 - 1 / 255f, 1);
    }

    [Fact]
    public void Base_colour_is_turned_from_linear_to_srgb()
    {
        // glTF's baseColorFactor is linear; a viewer's face colour is sRGB. Linear 0.214 is sRGB 0.5. Alpha is not converted.
        var face = OnlyFace(new ImportMaterial("grey glass", new NVector4(0.214f, 0.214f, 0.214f, 0.5f), null, AlphaMode.Blend, 0.5f, false));
        Assert.InRange(face.RGBA.R, 0.5f - 2 / 255f, 0.5f + 2 / 255f);
        Assert.InRange(face.RGBA.G, 0.5f - 2 / 255f, 0.5f + 2 / 255f);
        Assert.InRange(face.RGBA.B, 0.5f - 2 / 255f, 0.5f + 2 / 255f);
        Assert.InRange(face.RGBA.A, 0.5f - 1 / 255f, 0.5f + 1 / 255f);
    }

    [Fact]
    public void Child_names_fit_in_63_characters()
    {
        var parts = Enumerable.Range(0, 12).Select(i => Part("p" + i, new NVector3(i * 2, 0, 0), NQuaternion.Identity, BoxFace(new(-0.5f), new(0.5f)))).ToArray();
        var options = Options() with { ItemName = new string('a', 63) };
        var prims = ByLink(Build(Model(parts), options: options));
        Assert.Equal(new string('a', 63), prims[0].Name);
        for (int k = 1; k < prims.Length; k++)
        {
            Assert.True(prims[k].Name.Length <= 63, prims[k].Name);
            Assert.EndsWith($" #{k}", prims[k].Name);
        }
        Assert.Equal(new string('a', 59) + " #11", prims[11].Name);

        // A cut never keeps half of a character made of two UTF-16 units (here at 59 and 60, where " #1" cuts at 60).
        var name = new string('a', 59) + char.ConvertFromUtf32(0x1F600) + new string('a', 10);
        var cut = ByLink(Build(Model(parts[..2]), options: options with { ItemName = name }));
        Assert.Equal(new string('a', 59) + " #1", cut[1].Name);
    }

    private static Vector3 ToOmv(NVector3 v) => new(v.X, v.Y, v.Z);
}
