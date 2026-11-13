using System.IO.Compression;
using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using Xunit;

namespace GridGate.Physics.Rapier.Tests;

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

    // ---- Static and physical toggles, with the asset fetched as the simulator fetches it ----

    // A mesh prim whose asset is not loaded yet, as at region start: BulletS asks the scene's asset provider, here a
    // stub that answers at once and counts the requests.
    private static PhysicsActor AddFetchedMesh(BSScene scene, uint id, byte[] asset, bool physical, Vector3 at, List<UUID> fetches)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.SculptEntry = true;
        pbs.SculptType = (byte)SculptType.Mesh;
        pbs.SculptTexture = UUID.Random();
        pbs.Scale = MeshSize;
        UUID assetId = pbs.SculptTexture;
        scene.RequestAssetMethod = (requested, callback) =>
        {
            fetches.Add(requested);
            AssetBase found = new AssetBase(requested, "mesh", (sbyte)AssetType.Mesh, UUID.Zero.ToString());
            found.Data = asset;
            callback(requested == assetId ? found : null);
        };
        PhysicsActor prim = scene.AddPrimShape("mesh" + id, pbs, at, pbs.Scale, Quaternion.Identity, physical, id);
        Assert.NotNull(prim);
        return prim;
    }

    // The display mesh is a closed box, the physics mesh a cup, and physics_convex has two 0.4 m walls at either end
    // along X (an uploader's "Analyze"): static, the prim is the cup; physical, it is the two walls.
    private static byte[] CupWithWallHulls()
    {
        Vector3[] Slab(float x0, float x1) => Corners.Select(c => new Vector3(c.X < 0f ? x0 : x1, c.Y, c.Z)).ToArray();
        return Asset(Box, ("physics_mesh", MeshBlock(Cup)),
                     ("physics_convex", ConvexBlock(Corners, new[] { Slab(-0.5f, -0.3f), Slab(0.3f, 0.5f) })));
    }

    // Built physical first (rezzed physical), then made static (parked): the static prim is its physics mesh again.
    // Before, the hull built from the asset's hulls kept the asset "fetched" though the mesher had used its data, so the
    // static mesh found no data, was declared failed and became a box for good (and so did every copy at that size).
    [Theory]
    [InlineData("Rapier")]
    [InlineData("BulletUnmanaged")]
    public void MeshBuiltPhysical_MadeStatic_IsItsPhysicsMesh(string engine)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = Scene(engine: engine);
        try
        {
            List<UUID> fetches = new List<UUID>();
            PhysicsActor prim = AddFetchedMesh(scene, 871, CupWithWallHulls(), physical: true,
                                               new Vector3(128f, 128f, GroundZ + 0.6f), fetches);
            RapierTestScene.Step(scene, 2f);
            Assert.IsType<BSShapeHull>(ShapeOf(scene, 871));
            Assert.Equal(GroundZ + 0.5f, prim.Position.Z, 0.1f);   // Bullet adds its collision margins

            prim.IsPhysical = false;
            prim.Position = MeshAt;     // lifted off the ground, so the cup's bottom is not on it
            RapierTestScene.Step(scene, 0.5f);

            BSShape shape = ShapeOf(scene, 871);
            Assert.False(shape.physShapeInfo.isNativeShape, $"the static prim is a {shape.ShapeType}");
            Assert.IsType<BSShapeMesh>(shape);
            Assert.Equal(BSPhysicsShapeType.SHAPE_MESH, shape.ShapeType);
            Assert.Equal(2, fetches.Count);
            if (engine == "Rapier")
            {
                // The ray down the middle stops on the cup's bottom (24.5), not on the ground through the walls' gap.
                RaycastHit middle = RayDown(scene, prim.Position.X, prim.Position.Y);
                Assert.True(middle.hasHit());
                Assert.Equal(871u, middle.ID);
                Assert.Equal(MeshBottom, middle.Point.Z, 0.01f);
            }
            Assert.DoesNotContain(log.Warnings, w => w.Contains("would not mesh"));
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.RequestAssetMethod = null;
            scene.Dispose();
        }
    }

    // Built static first, then made physical: the hull is not in BulletS's caches and the mesher keeps no copy, so the
    // asset is fetched again (the prim is a box until it comes), and then the prim is the asset's hulls.
    [Theory]
    [InlineData("Rapier")]
    [InlineData("BulletUnmanaged")]
    public void MeshBuiltStatic_MadePhysical_RefetchesAndIsItsAssetsHulls(string engine)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = Scene(engine: engine);
        try
        {
            List<UUID> fetches = new List<UUID>();
            PhysicsActor prim = AddFetchedMesh(scene, 881, CupWithWallHulls(), physical: false,
                                               new Vector3(128f, 128f, GroundZ + 0.6f), fetches);
            RapierTestScene.Step(scene, 0.5f);
            Assert.IsType<BSShapeMesh>(ShapeOf(scene, 881));
            Assert.Equal(BSPhysicsShapeType.SHAPE_MESH, ShapeOf(scene, 881).ShapeType);
            Assert.Single(fetches);

            prim.IsPhysical = true;
            RapierTestScene.Step(scene, 2f);

            Assert.Equal(2, fetches.Count);
            BSShape shape = ShapeOf(scene, 881);
            Assert.IsType<BSShapeHull>(shape);
            Assert.Equal(BSPhysicsShapeType.SHAPE_HULL, shape.ShapeType);
            Assert.Equal(GroundZ + 0.5f, prim.Position.Z, 0.1f);   // Bullet adds its collision margins
            if (engine == "Rapier")
            {
                // Through the gap between the two hulls to the ground.
                RaycastHit middle = RayDown(scene, prim.Position.X, prim.Position.Y);
                Assert.True(middle.hasHit());
                Assert.NotEqual(881u, middle.ID);
                Assert.Equal(GroundZ, middle.Point.Z, 0.02f);
            }
            Assert.DoesNotContain(log.Warnings, w => w.Contains("would not mesh"));
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.RequestAssetMethod = null;
            scene.Dispose();
        }
    }

    // ---- Fix 2: a mesh with no physics mesh ----

    // The display mesh is a cup, open at the top; the asset's physics_convex block has the bounding hull (the whole
    // 2 x 2 x 1 box) and no decomposition, as the viewer uploads a mesh with no physics shape. Under Rapier the prim is
    // that hull: a ray down its middle stops at the top, not at the cup's bottom, and a ball dropped on it rests on top.
    [Fact]
    public void MeshWithoutPhysicsMesh_IsItsBoundingHull()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = Scene();
        try
        {
            Assert.True(BSParam.MeshWithoutPhysicsMeshIsConvex);
            AddMesh(scene, 811, Asset(Cup, ("physics_convex", ConvexBlock(Corners, null))), physical: false);
            RapierTestScene.Step(scene, 0.3f);

            BSShape shape = ShapeOf(scene, 811);
            Assert.NotEqual(BSPhysicsShapeType.SHAPE_MESH, shape.ShapeType);
            Assert.Equal(BSPhysicsShapeType.SHAPE_HULL, shape.ShapeType);
            Assert.Equal((int)BSPhysicsShapeType.SHAPE_HULL, scene.PE.GetShapeType(shape.physShapeInfo));
            Assert.False(shape.physShapeInfo.isNativeShape);

            RaycastHit hit = RayDown(scene, 128f, 128f);
            Assert.True(hit.hasHit());
            Assert.Equal(MeshTop, hit.Point.Z, 0.01f);

            PhysicsActor ball = scene.AddPrimShape("ball812", PrimitiveBaseShape.CreateSphere(), new Vector3(128f, 128f, 27f),
                                                    new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity, true, 812);
            RapierTestScene.Step(scene, 3f);
            Assert.Equal(MeshTop + 0.25f, ball.Position.Z, 0.03f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // When the physics_convex block has the decomposition's hulls (here two 0.4 m wide walls, at either end along X),
    // those are used, as ubODE uses them: the gap between them is open, though the bounding hull would close it.
    [Fact]
    public void MeshWithoutPhysicsMesh_UsesTheDecompositionsHulls()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = Scene();
        try
        {
            Vector3[] Slab(float x0, float x1) => Corners.Select(c => new Vector3(c.X < 0f ? x0 : x1, c.Y, c.Z)).ToArray();
            OSD convex = ConvexBlock(Corners, new[] { Slab(-0.5f, -0.3f), Slab(0.3f, 0.5f) });
            AddMesh(scene, 821, Asset(Cup, ("physics_convex", convex)), physical: false);
            RapierTestScene.Step(scene, 0.3f);

            Assert.Equal(BSPhysicsShapeType.SHAPE_HULL, ShapeOf(scene, 821).ShapeType);
            RaycastHit gap = RayDown(scene, 128f, 128f);
            Assert.True(gap.hasHit());
            Assert.Equal(GroundZ, gap.Point.Z, 0.02f);
            RaycastHit wall = RayDown(scene, 128.8f, 128f);
            Assert.True(wall.hasHit());
            Assert.Equal(MeshTop, wall.Point.Z, 0.01f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // The same kind of asset with a physics mesh (a cup, under a closed-box display mesh) is still a trimesh of the
    // physics mesh: the ray goes into the cup.
    [Fact]
    public void MeshWithPhysicsMesh_IsStillItsPhysicsMesh()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = Scene();
        try
        {
            byte[] asset = Asset(Box, ("physics_mesh", MeshBlock(Cup)), ("physics_convex", ConvexBlock(Corners, null)));
            AddMesh(scene, 831, asset, physical: false);
            RapierTestScene.Step(scene, 0.3f);

            Assert.Equal(BSPhysicsShapeType.SHAPE_MESH, ShapeOf(scene, 831).ShapeType);
            RaycastHit hit = RayDown(scene, 128f, 128f);
            Assert.True(hit.hasHit());
            Assert.Equal(MeshBottom, hit.Point.Z, 0.01f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // MeshWithoutPhysicsMeshIsConvex=false in the INI wins over Rapier's default: the display mesh is the trimesh again.
    [Fact]
    public void MeshWithoutPhysicsMesh_IniSwitchOff_IsTheDisplayMeshAgain()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = Scene(new Dictionary<string, string> { { "MeshWithoutPhysicsMeshIsConvex", "false" } });
        try
        {
            Assert.False(BSParam.MeshWithoutPhysicsMeshIsConvex);
            AddMesh(scene, 841, Asset(Cup, ("physics_convex", ConvexBlock(Corners, null))), physical: false);
            RapierTestScene.Step(scene, 0.3f);

            Assert.Equal(BSPhysicsShapeType.SHAPE_MESH, ShapeOf(scene, 841).ShapeType);
            RaycastHit hit = RayDown(scene, 128f, 128f);
            Assert.True(hit.hasHit());
            Assert.Equal(MeshBottom, hit.Point.Z, 0.01f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // With no physics_convex block, one with no hull in it, or one whose only hull is flat (no volume, as for a floor
    // mesh), there is nothing convex to use: the display mesh stays.
    [Theory]
    [InlineData("no block")]
    [InlineData("no hull")]
    [InlineData("flat hull")]
    public void MeshWithoutPhysicsMesh_NoUsableHull_KeepsTheDisplayMesh(string convex)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = Scene();
        try
        {
            byte[] asset = convex switch
            {
                "no hull" => Asset(Cup, ("physics_convex", ConvexBlock(null, null))),
                "flat hull" => Asset(Cup, ("physics_convex", ConvexBlock(Corners.Take(4).ToArray(), null))),
                _ => Asset(Cup),
            };
            AddMesh(scene, 851, asset, physical: false);
            RapierTestScene.Step(scene, 0.3f);

            Assert.Equal(BSPhysicsShapeType.SHAPE_MESH, ShapeOf(scene, 851).ShapeType);
            RaycastHit hit = RayDown(scene, 128f, 128f);
            Assert.True(hit.hasHit());
            Assert.Equal(MeshBottom, hit.Point.Z, 0.01f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // Bullet is unchanged: the display mesh is its trimesh.
    [Fact]
    public void MeshWithoutPhysicsMesh_OnBullet_IsTheDisplayMesh()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = Scene(engine: "BulletUnmanaged");
        try
        {
            Assert.False(BSParam.MeshWithoutPhysicsMeshIsConvex);
            AddMesh(scene, 861, Asset(Cup, ("physics_convex", ConvexBlock(Corners, null))), physical: false);
            RapierTestScene.Step(scene, 0.3f);

            Assert.IsType<BSShapeMesh>(ShapeOf(scene, 861));
            Assert.Equal(BSPhysicsShapeType.SHAPE_MESH, ShapeOf(scene, 861).ShapeType);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }
}
