using System.IO.Compression;
using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

/// <summary>
/// Mesh assets, made here the way the viewer uploads them (a binary LLSD header map, then zlib-compressed LLSD blocks):
/// BulletS's mesh requests do not fill the Meshmerizer's own cache, and under Rapier a mesh with no physics mesh collides
/// as the convex hull in its physics_convex block, as on ubODE, instead of as a trimesh of its display LOD.
/// </summary>
public class MeshAssetTests
{
    private const float GroundZ = 21f;
    // The test mesh is 2 x 2 x 1 m, centred 4 m above the ground: its bottom is at 24.5 and its top at 25.5.
    private static readonly Vector3 MeshAt = new Vector3(128f, 128f, 25f);
    private static readonly Vector3 MeshSize = new Vector3(2f, 2f, 1f);
    private const float MeshBottom = 24.5f;
    private const float MeshTop = 25.5f;

    // ---- Building a mesh asset ----

    // The corners of the unit cube [-0.5, 0.5]^3: 0-3 the bottom (counter-clockwise from -x -y), 4-7 the top above them.
    private static readonly Vector3[] Corners =
    {
        new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
        new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f), new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
    };
    private static readonly int[] Walls = { 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7 };
    private static readonly int[] Bottom = { 0, 2, 1, 0, 3, 2 };
    private static readonly int[] Top = { 4, 5, 6, 4, 6, 7 };

    // A cup: the cube without its top, so a ray down its middle hits the bottom (24.5), not the top.
    private static readonly int[] Cup = Bottom.Concat(Walls).ToArray();
    // A closed box.
    private static readonly int[] Box = Bottom.Concat(Walls).Concat(Top).ToArray();

    private static byte[] Quantize(IEnumerable<Vector3> points)
    {
        List<byte> bytes = new List<byte>();
        foreach (Vector3 p in points)
        {
            foreach (float v in new[] { p.X, p.Y, p.Z })
            {
                ushort u = (ushort)Math.Round((v + 0.5f) * ushort.MaxValue);
                bytes.Add((byte)(u & 0xff));
                bytes.Add((byte)(u >> 8));
            }
        }
        return bytes.ToArray();
    }

    private static OSDMap Domain()
    {
        return new OSDMap { { "Min", OSD.FromVector3(new Vector3(-0.5f, -0.5f, -0.5f)) }, { "Max", OSD.FromVector3(new Vector3(0.5f, 0.5f, 0.5f)) } };
    }

    // A LOD or physics_mesh block: one submesh of the cube's corners and these triangles.
    private static OSD MeshBlock(int[] triangles)
    {
        List<byte> tris = new List<byte>();
        foreach (int i in triangles)
        {
            tris.Add((byte)(i & 0xff));
            tris.Add((byte)(i >> 8));
        }
        OSDMap submesh = new OSDMap
        {
            { "PositionDomain", Domain() },
            { "Position", OSD.FromBinary(Quantize(Corners)) },
            { "TriangleList", OSD.FromBinary(tris.ToArray()) },
        };
        return new OSDArray { submesh };
    }

    // A physics_convex block: the bounding hull, and the decomposition's hulls when there are any.
    private static OSD ConvexBlock(Vector3[]? boundingVerts, Vector3[][]? hulls)
    {
        OSDMap block = Domain();
        if (boundingVerts != null)
            block["BoundingVerts"] = OSD.FromBinary(Quantize(boundingVerts));
        if (hulls != null)
        {
            block["HullList"] = OSD.FromBinary(hulls.Select(h => (byte)h.Length).ToArray());
            block["Positions"] = OSD.FromBinary(Quantize(hulls.SelectMany(h => h)));
        }
        return block;
    }

    // The viewer's layout: the header map, then each block as zlib-compressed binary LLSD, at the offset (from the end
    // of the header) and size the header gives.
    private static byte[] Asset(params (string name, OSD block)[] blocks)
    {
        OSDMap header = new OSDMap();
        List<byte> body = new List<byte>();
        foreach ((string name, OSD block) in blocks)
        {
            byte[] raw = OSDParser.SerializeLLSDBinary(block, false);
            using MemoryStream compressed = new MemoryStream();
            using (ZLibStream zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw, 0, raw.Length);
            header[name] = new OSDMap { { "offset", OSD.FromInteger(body.Count) }, { "size", OSD.FromInteger((int)compressed.Length) } };
            body.AddRange(compressed.ToArray());
        }
        return OSDParser.SerializeLLSDBinary(header, false).Concat(body).ToArray();
    }

    // A display LOD (as both medium_lod and high_lod) and these other blocks.
    private static byte[] Asset(int[] display, params (string name, OSD block)[] others)
    {
        List<(string, OSD)> blocks = new List<(string, OSD)> { ("high_lod", MeshBlock(display)), ("medium_lod", MeshBlock(display)) };
        blocks.AddRange(others);
        return Asset(blocks.ToArray());
    }

    // ---- Scenes and prims ----

    private static BSScene Scene(Dictionary<string, string>? ini = null, string engine = "Rapier")
    {
        // MeshSculptedPrim: a mesh prim is a box with a sculpt entry, which without it becomes a native box.
        Dictionary<string, string> config = new Dictionary<string, string> { { "BulletEngine", engine }, { "MeshSculptedPrim", "true" } };
        if (ini != null)
            foreach (KeyValuePair<string, string> kvp in ini)
                config[kvp.Key] = kvp.Value;
        BSScene scene = RapierTestScene.Create(config);
        float[] map = new float[256 * 256];
        Array.Fill(map, GroundZ);
        scene.SetTerrain(map);
        RapierTestScene.Step(scene, 0.3f);
        return scene;
    }

    // A mesh prim as the simulator makes one, with its asset already loaded (each gets its own asset id, so BulletS's
    // process-wide shape caches never hand a test a shape another test built).
    private static PhysicsActor AddMesh(BSScene scene, uint id, byte[] asset, bool physical, Vector3? at = null, Vector3? size = null)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.SculptEntry = true;
        pbs.SculptType = (byte)SculptType.Mesh;
        pbs.SculptTexture = UUID.Random();
        pbs.SculptData = asset;
        pbs.Scale = size ?? MeshSize;
        PhysicsActor prim = scene.AddPrimShape("mesh" + id, pbs, at ?? MeshAt, pbs.Scale, Quaternion.Identity, physical, id);
        Assert.NotNull(prim);
        return prim;
    }

    private static RaycastHit RayDown(BSScene scene, float x, float y)
    {
        return scene.PE.RayTest2(scene.World, new Vector3(x, y, 30f), new Vector3(x, y, GroundZ - 1f), 0xffffffff, 0xffffffff);
    }

    private static BSShape ShapeOf(BSScene scene, uint id)
    {
        return scene.PhysObjects[id].PhysShape;
    }

    // ---- Fix 1: the Meshmerizer's cache ----

    private static Dictionary<ulong, OpenSim.Region.PhysicsModule.Meshing.Mesh> MeshmerizerCache()
    {
        FieldInfo? field = typeof(OpenSim.Region.PhysicsModule.Meshing.Meshmerizer).GetField("m_uniqueMeshes",
                                BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (Dictionary<ulong, OpenSim.Region.PhysicsModule.Meshing.Mesh>)field!.GetValue(null)!;
    }

    private static HashSet<ulong> CachedKeys()
    {
        Dictionary<ulong, OpenSim.Region.PhysicsModule.Meshing.Mesh> cache = MeshmerizerCache();
        lock (cache)
            return new HashSet<ulong>(cache.Keys);
    }

    // BulletS keeps the shapes it builds in its own caches (BSShapeMesh.Meshes, BSShapeHull.Hulls), so the mesh the
    // Meshmerizer made for each must not also stay in the Meshmerizer's: on DragonFyre that second copy was 280 MB.
    [Theory]
    [InlineData("Rapier")]
    [InlineData("BulletUnmanaged")]
    public void BulletSMeshes_AreNotKeptInTheMeshmerizersCache(string engine)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = Scene(engine: engine);
        try
        {
            HashSet<ulong> before = CachedKeys();
            byte[] asset = Asset(Box, ("physics_mesh", MeshBlock(Cup)));
            AddMesh(scene, 801, asset, physical: false);
            AddMesh(scene, 802, asset, physical: true, at: new Vector3(140f, 128f, 23f));
            RapierTestScene.Step(scene, 0.5f);

            Assert.IsType<BSShapeMesh>(ShapeOf(scene, 801));
            Assert.Equal(BSPhysicsShapeType.SHAPE_MESH, ShapeOf(scene, 801).ShapeType);
            Assert.IsType<BSShapeHull>(ShapeOf(scene, 802));
            Assert.Equal(BSPhysicsShapeType.SHAPE_HULL, ShapeOf(scene, 802).ShapeType);

            HashSet<ulong> added = CachedKeys();
            added.ExceptWith(before);
            Assert.Empty(added);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // A physical mesh is still made of the hulls in its asset (ShouldUseAssetHulls), which BSShapeHull reads from the
    // Meshmerizer right after the uncached CreateMesh: here two 0.4 m walls at either end along X, so a ray down the
    // middle of the prim, resting on the ground, reaches the ground.
    [Fact]
    public void PhysicalMesh_IsStillMadeOfItsAssetsHulls()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = Scene();
        try
        {
            Vector3[] Slab(float x0, float x1) => Corners.Select(c => new Vector3(c.X < 0f ? x0 : x1, c.Y, c.Z)).ToArray();
            OSD convex = ConvexBlock(Corners, new[] { Slab(-0.5f, -0.3f), Slab(0.3f, 0.5f) });
            HashSet<ulong> before = CachedKeys();
            PhysicsActor prim = AddMesh(scene, 803, Asset(Box, ("physics_convex", convex)), physical: true,
                                        at: new Vector3(128f, 128f, GroundZ + 0.6f));
            RapierTestScene.Step(scene, 2f);

            Assert.IsType<BSShapeHull>(ShapeOf(scene, 803));
            Assert.Equal(GroundZ + 0.5f, prim.Position.Z, 0.03f);
            RaycastHit middle = RayDown(scene, prim.Position.X, prim.Position.Y);
            Assert.True(middle.hasHit());
            Assert.Equal(GroundZ, middle.Point.Z, 0.02f);
            RaycastHit wall = RayDown(scene, prim.Position.X + 0.8f, prim.Position.Y);
            Assert.True(wall.hasHit());
            Assert.Equal(GroundZ + 1f, wall.Point.Z, 0.03f);

            HashSet<ulong> added = CachedKeys();
            added.ExceptWith(before);
            Assert.Empty(added);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

}
