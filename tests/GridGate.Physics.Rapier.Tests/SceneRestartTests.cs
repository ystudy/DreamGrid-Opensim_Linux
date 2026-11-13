using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace GridGate.Physics.Rapier.Tests;

/// <summary>
/// A second BSScene in the same process (a region restarted in-process, or several regions in one simulator) is
/// handed the shapes BulletS's static mesh and hull caches hold, which an earlier scene's world built.
/// </summary>
public class SceneRestartTests
{
    private const float TerrainHeight = 21f;
    private const uint SlabId = 7001;
    private const uint BoxId = 7002;

    // A size no other test uses, so BulletS's cache key for this mesh is new when the test starts.
    private static readonly Vector3 SlabSize = new Vector3(4.25f, 4.25f, 0.5f);
    private static readonly Vector3 SlabAt = new Vector3(128f, 128f, 30f);

    // A hollow box is not a native shape: a static one becomes a trimesh, which BSShapeMesh caches. The hole runs
    // through the middle, so the box is dropped beside it.
    private static PhysicsActor AddSlab(BSScene scene)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.Scale = SlabSize;
        pbs.ProfileHollow = 20000;
        return scene.AddPrimShape("slab", pbs, SlabAt, SlabSize, Quaternion.Identity, false, SlabId);
    }

    private static BSScene NewScene()
    {
        BSScene scene = RapierTestScene.Create();
        float[] map = new float[256 * 256];
        Array.Fill(map, TerrainHeight);
        scene.SetTerrain(map);
        AddSlab(scene);
        RapierTestScene.Step(scene, 0.3f);
        return scene;
    }

    private static IntPtr NativeShapeOf(BSScene scene, uint id)
    {
        return ((BSAPIRapier.BulletShapeRapier)scene.PhysObjects[id].PhysShape.physShapeInfo).Shape;
    }

    private static List<IntPtr> InBulletSCaches()
    {
        List<BSShape> cached = new List<BSShape>();
        lock (BSShapeMesh.Meshes)
            cached.AddRange(BSShapeMesh.Meshes.Values);
        lock (BSShapeHull.Hulls)
            cached.AddRange(BSShapeHull.Hulls.Values);
        lock (BSShapeConvexHull.ConvexHulls)
            cached.AddRange(BSShapeConvexHull.ConvexHulls.Values);
        lock (BSShapeGImpact.GImpacts)
            cached.AddRange(BSShapeGImpact.GImpacts.Values);
        return cached.Select(c => c.physShapeInfo as BSAPIRapier.BulletShapeRapier)
            .Where(c => c != null && c.Shape != IntPtr.Zero)
            .Select(c => c!.Shape)
            .ToList();
    }

    private static bool Kept(IntPtr shape)
    {
        lock (BSAPIRapier.KeptForBulletSCaches)
            return BSAPIRapier.KeptForBulletSCaches.Contains(shape);
    }

    /// <summary>
    /// The first scene builds the slab's trimesh, which BulletS caches, and shuts down. Freeing that shape would leave
    /// the cache with a dangling pointer; whether using it then crashes depends on what the allocator did with the
    /// memory, so the test asserts the ownership that prevents it (the shape is kept for the cache, not freed) and then
    /// uses the shape in a second scene: its bounds, a box landing on it, and the collision BulletS is told about.
    /// </summary>
    [Fact]
    public void CachedMesh_OutlivesItsScene_AndCollidesInTheNextOne()
    {
        using ErrorLog log = new ErrorLog();
        IntPtr mesh;
        BSScene first = NewScene();
        try
        {
            mesh = NativeShapeOf(first, SlabId);
            Assert.NotEqual(IntPtr.Zero, mesh);
            Assert.Contains(mesh, InBulletSCaches());
            Assert.Contains(mesh, ((BSAPIRapier.BulletWorldRapier)first.World).State.Shapes);
        }
        finally
        {
            first.Dispose();
        }
        Assert.Contains(mesh, InBulletSCaches());
        Assert.True(Kept(mesh), "Shutdown freed a shape BulletS's mesh cache still holds");

        BSScene second = NewScene();
        try
        {
            // The second scene's slab is the cached shape, not one of its own.
            Assert.Equal(mesh, NativeShapeOf(second, SlabId));
            Assert.DoesNotContain(mesh, ((BSAPIRapier.BulletWorldRapier)second.World).State.Shapes);

            RapierNative.Pose identity = new RapierNative.Pose
            {
                translation = new RapierNative.Vector(),
                rotation = new RapierNative.Rotation { w = 1f }
            };
            RapierNative.Aabb bounds = RapierNative.r3SharedShape_ComputeAabb(mesh, identity);
            Assert.Equal(SlabSize.X, bounds.maxs.x - bounds.mins.x, 2);
            Assert.Equal(SlabSize.Y, bounds.maxs.y - bounds.mins.y, 2);
            Assert.Equal(SlabSize.Z, bounds.maxs.z - bounds.mins.z, 2);

            PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
            Vector3 edge = new Vector3(0.5f, 0.5f, 0.5f);
            pbs.Scale = edge;
            PhysicsActor box = second.AddPrimShape("box", pbs, SlabAt + new Vector3(1.5f, 0f, 1.5f), edge, Quaternion.Identity, true, BoxId);
            box.SubscribeEvents(50);

            bool touched = false;
            for (int ii = 0; ii < 40; ii++)
            {
                second.Simulate(RapierTestScene.FrameSeconds);
                int count = ((BSAPIRapier.BulletWorldRapier)second.World).State.LastCollisionCount;
                for (int jj = 0; jj < count; jj++)
                {
                    CollisionDesc c = second.m_collisionArray[jj];
                    if (c.aID == SlabId && c.bID == BoxId)
                        touched = true;
                }
            }

            Assert.True(touched, "the box never touched the slab");
            // It rests on the slab's top face, not on the terrain nine metres below.
            float restZ = SlabAt.Z + SlabSize.Z / 2f + edge.Z / 2f;
            Assert.InRange(box.Position.Z, restZ - 0.05f, restZ + 0.05f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            second.Dispose();
        }
        // Still held for the cache, so a third scene would get a valid shape too.
        Assert.True(Kept(mesh));
    }
}
