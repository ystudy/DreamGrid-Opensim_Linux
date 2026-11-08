using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

/// <summary>Meshes, hulls, hull lists, scaling, deletion and duplication of shapes (H9).</summary>
public class ShapeTests
{
    // The engine is driven directly through scene.PE, with bodies of ids BSScene does not use.
    private static BSAPIRapier.BulletWorldRapier WorldOf(BSScene scene)
    {
        return (BSAPIRapier.BulletWorldRapier)scene.World;
    }

    private static BulletShape NativeShape(BSScene scene, BSPhysicsShapeType type, Vector3 scale)
    {
        ShapeData data = new ShapeData { Type = type, Scale = scale };
        return scene.PE.BuildNativeShape(scene.World, data);
    }

    private static BulletBody StaticBody(BSScene scene, BulletShape shape, uint id, Vector3 position)
    {
        BulletBody body = scene.PE.CreateBodyWithDefaultMotionState(shape, id, position, Quaternion.Identity);
        InWorld(scene, body);
        return body;
    }

    // A body made straight from the engine collides with nothing until it has a filter group and mask.
    private static void InWorld(BSScene scene, BulletBody body)
    {
        Assert.True(scene.PE.AddObjectToWorld(scene.World, body));
        Assert.True(scene.PE.SetCollisionGroupMask(body, 0xffff, 0xffff));
    }

    private static BulletBody DynamicBody(BSScene scene, BulletShape shape, uint id, Vector3 position, float mass = 1f)
    {
        BulletBody body = scene.PE.CreateBodyFromShape(scene.World, shape, id, position, Quaternion.Identity);
        scene.PE.SetMassProps(body, mass, scene.PE.CalculateLocalInertia(shape, mass));
        InWorld(scene, body);
        return body;
    }

    private static void EngineStep(BSScene scene, float seconds)
    {
        const float step = 1f / 60f;
        for (float done = 0f; done < seconds; done += step)
            scene.PE.PhysicsStep(scene.World, step, 0, step, out int _, out int _);
    }

    // A flat slab to rest things on: its top face is at z = 50 over 40 m x 40 m.
    private static BulletBody Floor(BSScene scene, uint id = 9000)
    {
        BulletShape slab = NativeShape(scene, BSPhysicsShapeType.SHAPE_BOX, new Vector3(40f, 40f, 1f));
        return StaticBody(scene, slab, id, new Vector3(128f, 128f, 49.5f));
    }

    // Rapier's broad phase learns of new and changed colliders in a step, and the simulator always steps between a
    // change and the next ray.
    private static RaycastHit Ray(BSScene scene, Vector3 from, Vector3 to)
    {
        EngineStep(scene, 0.02f);
        return scene.PE.RayTest2(scene.World, from, to, 0xffff, 0xffff);
    }

    // The 8 corners of a cube with this half edge, as BulletS passes vertices (3 floats each).
    private static float[] CubeCorners(float half, Vector3 offset)
    {
        List<float> v = new List<float>();
        foreach (float x in new[] { -half, half })
            foreach (float y in new[] { -half, half })
                foreach (float z in new[] { -half, half })
                    v.AddRange(new[] { x + offset.X, y + offset.Y, z + offset.Z });
        return v.ToArray();
    }

    // BulletSim's hull list: [hull count], then per hull [vertex count, centroid x, y, z, the vertices x, y, z...].
    private static float[] HullList(params (Vector3 centroid, float[] verts)[] hulls)
    {
        List<float> list = new List<float> { hulls.Length };
        foreach ((Vector3 centroid, float[] verts) in hulls)
        {
            list.Add(verts.Length / 3);
            list.AddRange(new[] { centroid.X, centroid.Y, centroid.Z });
            list.AddRange(verts);
        }
        return list.ToArray();
    }

    [Fact]
    public void Trimesh_StaticFloor_SphereRests()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            // 10 x 10 m, two triangles, at z = 50 (the body sits at the origin, so the mesh is in world coordinates).
            float[] vertices = { 100f, 100f, 50f, 110f, 100f, 50f, 110f, 110f, 50f, 100f, 110f, 50f };
            int[] indices = { 0, 1, 2, 0, 2, 3 };
            BulletShape mesh = scene.PE.CreateMeshShape(scene.World, indices.Length, indices, 4, vertices);
            Assert.True(mesh.HasPhysicalShape);
            Assert.Equal((int)BSPhysicsShapeType.SHAPE_MESH, scene.PE.GetShapeType(mesh));
            StaticBody(scene, mesh, 9100, Vector3.Zero);

            BulletShape ball = NativeShape(scene, BSPhysicsShapeType.SHAPE_SPHERE, new Vector3(1f, 1f, 1f));
            BulletBody sphere = DynamicBody(scene, ball, 9101, new Vector3(105f, 105f, 53f));
            EngineStep(scene, 3f);

            Assert.InRange(scene.PE.GetPosition(sphere).Z, 50.5f - 0.03f, 50.5f + 0.03f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // BSShapes passes the number of indices and the number of vertices (not floats), and the arrays can be longer than
    // those counts because degenerate triangles are removed in place.
    [Fact]
    public void Trimesh_CountsSayHowMuchOfTheArraysIsUsed()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            float[] vertices = { 100f, 100f, 50f, 110f, 100f, 50f, 110f, 110f, 50f, 100f, 110f, 50f, 9999f, 9999f, 9999f };
            // The tail past indicesCount is stale, and refers to a vertex that does not exist.
            int[] indices = { 0, 1, 2, 0, 2, 3, 0, 1, 77 };
            BulletShape mesh = scene.PE.CreateMeshShape(scene.World, 6, indices, 4, vertices);
            Assert.True(mesh.HasPhysicalShape);
            StaticBody(scene, mesh, 9100, Vector3.Zero);

            Assert.True(Ray(scene, new Vector3(105f, 105f, 60f), new Vector3(105f, 105f, 40f)).hasHit());
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Trimesh_BadIndex_GivesNoShapeInsteadOfThrowing()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            float[] vertices = { 0f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 0f };
            int[] indices = { 0, 1, 5 };
            BulletShape mesh = scene.PE.CreateMeshShape(scene.World, 3, indices, 3, vertices);
            Assert.False(mesh.HasPhysicalShape);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Hull_OfCubeVerts_BehavesLikeBox()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            Floor(scene);
            BulletShape box = NativeShape(scene, BSPhysicsShapeType.SHAPE_BOX, new Vector3(1f, 1f, 1f));
            BulletShape hull = scene.PE.CreateConvexHullShape(scene.World, 0, new int[0], 8, CubeCorners(0.5f, Vector3.Zero));
            Assert.True(hull.HasPhysicalShape);
            BulletBody boxBody = DynamicBody(scene, box, 9101, new Vector3(120f, 128f, 53f));
            BulletBody hullBody = DynamicBody(scene, hull, 9102, new Vector3(136f, 128f, 53f));
            EngineStep(scene, 3f);

            float boxZ = scene.PE.GetPosition(boxBody).Z;
            float hullZ = scene.PE.GetPosition(hullBody).Z;
            Assert.InRange(boxZ, 50.5f - 0.03f, 50.5f + 0.03f);
            Assert.InRange(hullZ, boxZ - 0.03f, boxZ + 0.03f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void ConvexHullFromMesh_OfCubeMesh_RestsLikeBox()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            Floor(scene);
            float[] vertices = CubeCorners(0.5f, Vector3.Zero);
            // Two triangles per face of the cube whose corners are numbered by their (x, y, z) bits.
            int[] indices =
            {
                0, 1, 3, 0, 3, 2,  4, 6, 7, 4, 7, 5,  0, 4, 5, 0, 5, 1,
                2, 3, 7, 2, 7, 6,  0, 2, 6, 0, 6, 4,  1, 5, 7, 1, 7, 3
            };
            BulletShape mesh = scene.PE.CreateMeshShape(scene.World, indices.Length, indices, 8, vertices);
            BulletShape hull = scene.PE.BuildConvexHullShapeFromMesh(scene.World, mesh);
            Assert.True(hull.HasPhysicalShape);
            Assert.Equal((int)BSPhysicsShapeType.SHAPE_CONVEXHULL, scene.PE.GetShapeType(hull));
            BulletBody body = DynamicBody(scene, hull, 9101, new Vector3(128f, 128f, 53f));
            EngineStep(scene, 3f);

            Assert.InRange(scene.PE.GetPosition(body).Z, 50.5f - 0.03f, 50.5f + 0.03f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void HullList_TwoHulls_BothCollide()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            // Two unit cubes 2 m apart: the first has its corners at x = -1 and the second is placed by its centroid.
            float[] hulls = HullList(
                (Vector3.Zero, CubeCorners(0.5f, new Vector3(-1f, 0f, 0f))),
                (new Vector3(1f, 0f, 0f), CubeCorners(0.5f, Vector3.Zero)));
            BulletShape shape = scene.PE.CreateHullShape(scene.World, 2, hulls);
            Assert.True(shape.HasPhysicalShape);
            Assert.Equal((int)BSPhysicsShapeType.SHAPE_HULL, scene.PE.GetShapeType(shape));
            StaticBody(scene, shape, 9100, new Vector3(128f, 128f, 50f));

            foreach (float x in new[] { 127f, 129f })
            {
                RaycastHit hit = Ray(scene, new Vector3(x, 128f, 60f), new Vector3(x, 128f, 40f));
                Assert.True(hit.hasHit(), $"no hit at x={x}");
                Assert.Equal(50.5f, hit.Point.Z, 0.01f);
            }
            Assert.False(Ray(scene, new Vector3(128f, 128f, 60f), new Vector3(128f, 128f, 40f)).hasHit(), "the gap is not solid");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void HullList_AsDynamicBody_RestsOnFloor()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            Floor(scene);
            float[] hulls = HullList(
                (Vector3.Zero, CubeCorners(0.5f, new Vector3(-1f, 0f, 0f))),
                (Vector3.Zero, CubeCorners(0.5f, new Vector3(1f, 0f, 0f))));
            BulletShape shape = scene.PE.CreateHullShape(scene.World, 2, hulls);
            BulletBody body = DynamicBody(scene, shape, 9101, new Vector3(128f, 128f, 53f), mass: 2f);
            EngineStep(scene, 3f);

            Assert.InRange(scene.PE.GetPosition(body).Z, 50.5f - 0.05f, 50.5f + 0.05f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void SetLocalScaling_RebuildsShape()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            BulletShape box = NativeShape(scene, BSPhysicsShapeType.SHAPE_BOX, new Vector3(1f, 1f, 1f));
            StaticBody(scene, box, 9100, new Vector3(128f, 128f, 50f));
            RaycastHit before = Ray(scene, new Vector3(128f, 128f, 60f), new Vector3(128f, 128f, 40f));
            Assert.Equal(50.5f, before.Point.Z, 0.01f);

            scene.PE.SetLocalScaling(box, new Vector3(2f, 2f, 2f));

            Assert.Equal(new Vector3(2f, 2f, 2f), scene.PE.GetLocalScaling(box));
            RaycastHit top = Ray(scene, new Vector3(128f, 128f, 60f), new Vector3(128f, 128f, 40f));
            Assert.True(top.hasHit());
            Assert.Equal(51f, top.Point.Z, 0.01f);
            RaycastHit side = Ray(scene, new Vector3(140f, 128f, 50f), new Vector3(120f, 128f, 50f));
            Assert.True(side.hasHit());
            Assert.Equal(129f, side.Point.X, 0.01f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void SetLocalScaling_SharedShape_MovesEveryColliderAndKeepsTheTableConsistent()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            RapierWorldState state = WorldOf(scene).State;
            int baseline = state.Shapes.Count;
            BulletShape box = NativeShape(scene, BSPhysicsShapeType.SHAPE_BOX, new Vector3(1f, 1f, 1f));
            // Two bodies on one wrapper's shape: each collider must follow the new size.
            StaticBody(scene, box, 9100, new Vector3(128f, 128f, 50f));
            StaticBody(scene, box, 9101, new Vector3(148f, 128f, 50f));

            scene.PE.SetLocalScaling(box, new Vector3(2f, 2f, 2f));
            scene.PE.SetLocalScaling(box, new Vector3(4f, 4f, 4f));

            Assert.Equal(52f, Ray(scene, new Vector3(128f, 128f, 60f), new Vector3(128f, 128f, 40f)).Point.Z, 0.01f);
            Assert.Equal(52f, Ray(scene, new Vector3(148f, 128f, 60f), new Vector3(148f, 128f, 40f)).Point.Z, 0.01f);
            // One live native shape for the wrapper, however many times it was rebuilt.
            Assert.Equal(baseline + 1, state.Shapes.Count);
            Assert.Contains(((BSAPIRapier.BulletShapeRapier)box).Shape, state.Shapes);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void SetLocalScaling_Mesh_ScalesTheVertices()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            float[] vertices = CubeCorners(0.5f, Vector3.Zero);
            BulletShape hull = scene.PE.CreateConvexHullShape(scene.World, 0, new int[0], 8, vertices);
            StaticBody(scene, hull, 9100, new Vector3(128f, 128f, 50f));
            Assert.Equal(50.5f, Ray(scene, new Vector3(128f, 128f, 60f), new Vector3(128f, 128f, 40f)).Point.Z, 0.01f);

            scene.PE.SetLocalScaling(hull, new Vector3(1f, 1f, 4f));

            Assert.Equal(52f, Ray(scene, new Vector3(128f, 128f, 60f), new Vector3(128f, 128f, 40f)).Point.Z, 0.01f);
            // X did not change.
            Assert.Equal(128.5f, Ray(scene, new Vector3(140f, 128f, 50f), new Vector3(120f, 128f, 50f)).Point.X, 0.01f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void DeleteShape_FreesNative()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            RapierWorldState state = WorldOf(scene).State;
            int baseline = state.Shapes.Count;
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            for (int ii = 0; ii < 1000; ii++)
            {
                BulletShape shape = (ii % 3) switch
                {
                    0 => NativeShape(scene, BSPhysicsShapeType.SHAPE_BOX, new Vector3(1f, 2f, 3f)),
                    1 => scene.PE.CreateConvexHullShape(scene.World, 0, new int[0], 8, CubeCorners(0.5f, Vector3.Zero)),
                    _ => scene.PE.CreateHullShape(scene.World, 1, HullList((Vector3.Zero, CubeCorners(0.5f, Vector3.Zero))))
                };
                Assert.True(shape.HasPhysicalShape);
                Assert.True(scene.PE.DeleteCollisionShape(scene.World, shape));
                Assert.False(shape.HasPhysicalShape);
                // Deleting again does nothing.
                Assert.True(scene.PE.DeleteCollisionShape(scene.World, shape));
            }
            clock.Stop();
            Console.WriteLine($"1000 shapes created and deleted in {clock.ElapsedMilliseconds} ms");

            Assert.Equal(baseline, state.Shapes.Count);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // A collider keeps its own reference to the geometry, so deleting the shape detaches it: the body goes on working
    // and later steps and queries are safe.
    [Fact]
    public void DeleteShape_WhileABodyUsesIt_LeavesTheBodyWorking()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            Floor(scene);
            BulletShape box = NativeShape(scene, BSPhysicsShapeType.SHAPE_BOX, new Vector3(1f, 1f, 1f));
            BulletBody body = DynamicBody(scene, box, 9101, new Vector3(128f, 128f, 53f));
            Assert.True(scene.PE.DeleteCollisionShape(scene.World, box));
            // Scaling a deleted shape is ignored, not a crash.
            scene.PE.SetLocalScaling(box, new Vector3(3f, 3f, 3f));
            EngineStep(scene, 3f);

            Assert.InRange(scene.PE.GetPosition(body).Z, 50.5f - 0.03f, 50.5f + 0.03f);
            Assert.True(Ray(scene, new Vector3(128f, 128f, 60f), new Vector3(128f, 128f, 40f)).hasHit());
            scene.PE.DestroyObject(scene.World, body);
            EngineStep(scene, 0.2f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void DuplicateShape_IsIndependentOfTheOriginal()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            RapierWorldState state = WorldOf(scene).State;
            int baseline = state.Shapes.Count;
            BulletShape box = NativeShape(scene, BSPhysicsShapeType.SHAPE_BOX, new Vector3(2f, 2f, 2f));
            BulletShape copy = scene.PE.DuplicateCollisionShape(scene.World, box, 7);
            Assert.True(copy.HasPhysicalShape);
            Assert.False(copy.ReferenceSame(box));
            Assert.Equal(new Vector3(2f, 2f, 2f), scene.PE.GetLocalScaling(copy));
            Assert.Equal(baseline + 2, state.Shapes.Count);

            Assert.True(scene.PE.DeleteCollisionShape(scene.World, box));
            StaticBody(scene, copy, 9100, new Vector3(128f, 128f, 50f));
            Assert.Equal(51f, Ray(scene, new Vector3(128f, 128f, 60f), new Vector3(128f, 128f, 40f)).Point.Z, 0.01f);
            Assert.True(scene.PE.DeleteCollisionShape(scene.World, copy));
            Assert.Equal(baseline, state.Shapes.Count);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void ShapeType_IsWhatTheShapeWasBuiltAs()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            Assert.Equal((int)BSPhysicsShapeType.SHAPE_BOX, scene.PE.GetShapeType(NativeShape(scene, BSPhysicsShapeType.SHAPE_BOX, Vector3.One)));
            Assert.Equal((int)BSPhysicsShapeType.SHAPE_SPHERE, scene.PE.GetShapeType(NativeShape(scene, BSPhysicsShapeType.SHAPE_SPHERE, Vector3.One)));
            Assert.True(scene.PE.IsNativeShape(NativeShape(scene, BSPhysicsShapeType.SHAPE_BOX, Vector3.One)));
            BulletShape hull = scene.PE.CreateConvexHullShape(scene.World, 0, new int[0], 8, CubeCorners(0.5f, Vector3.Zero));
            Assert.False(scene.PE.IsNativeShape(hull));
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // BulletS only builds a dynamic trimesh when ShouldUseGImpactShapeForPrims is set. The body takes its mass from
    // SetMassProps; the inertia BulletS asks for comes from the mesh's bounding box, because a trimesh has no volume.
    [Fact]
    public void GImpact_DynamicMesh_HasInertiaAndRestsOnFloor()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            Floor(scene);
            float[] vertices = CubeCorners(0.5f, Vector3.Zero);
            int[] indices =
            {
                0, 1, 3, 0, 3, 2,  4, 6, 7, 4, 7, 5,  0, 4, 5, 0, 5, 1,
                2, 3, 7, 2, 7, 6,  0, 2, 6, 0, 6, 4,  1, 5, 7, 1, 7, 3
            };
            BulletShape mesh = scene.PE.CreateGImpactShape(scene.World, indices.Length, indices, 8, vertices);
            Assert.True(mesh.HasPhysicalShape);
            Assert.Equal((int)BSPhysicsShapeType.SHAPE_GIMPACT, scene.PE.GetShapeType(mesh));
            // A unit cube of mass 6 has an inertia of 1 about each axis.
            Vector3 inertia = scene.PE.CalculateLocalInertia(mesh, 6f);
            Assert.Equal(1f, inertia.X, 2);
            Assert.Equal(1f, inertia.Y, 2);
            Assert.Equal(1f, inertia.Z, 2);

            BulletBody body = DynamicBody(scene, mesh, 9101, new Vector3(128f, 128f, 53f), mass: 6f);
            EngineStep(scene, 3f);
            Assert.InRange(scene.PE.GetPosition(body).Z, 50.5f - 0.05f, 50.5f + 0.05f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }
    // ---- Trimeshes keep no managed copy of their geometry (memory fix 4) ----

    // The unit cube as a closed trimesh: corners numbered by their (x, y, z) bits, two triangles per face.
    private static readonly int[] CubeTriangles =
    {
        0, 1, 3, 0, 3, 2,  4, 6, 7, 4, 7, 5,  0, 4, 5, 0, 5, 1,
        2, 3, 7, 2, 7, 6,  0, 2, 6, 0, 6, 4,  1, 5, 7, 1, 7, 3
    };

    // The cube's 8 vertices (3 floats each) and 36 indices, as 4-byte values.
    private const long CubeMeshBytes = (8 * 3 + 36) * 4;

    [Fact]
    public void Trimesh_KeepsNoManagedCopyOfItsGeometry()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            RapierWorldState state = WorldOf(scene).State;
            long before = state.MeshCopyBytesNotKept;
            BulletShape mesh = scene.PE.CreateMeshShape(scene.World, CubeTriangles.Length, CubeTriangles, 8, CubeCorners(0.5f, Vector3.Zero));
            BulletShape gimpact = scene.PE.CreateGImpactShape(scene.World, CubeTriangles.Length, CubeTriangles, 8, CubeCorners(0.5f, Vector3.Zero));
            Assert.True(mesh.HasPhysicalShape);
            Assert.True(gimpact.HasPhysicalShape);

            Assert.Null(((BSAPIRapier.BulletShapeRapier)mesh).MeshVerts);
            Assert.Null(((BSAPIRapier.BulletShapeRapier)gimpact).MeshVerts);
            Assert.Equal(before + 2 * CubeMeshBytes, state.MeshCopyBytesNotKept);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // A static prim that is not a native shape (a hollow box) is a BulletS mesh: its shape holds no copy either.
    [Fact]
    public void StaticMeshPrim_ShapeKeepsNoManagedCopy()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = GroundScene();
        try
        {
            long before = WorldOf(scene).State.MeshCopyBytesNotKept;
            PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
            pbs.ProfileHollow = 20000;
            Vector3 size = new Vector3(1f, 1f, 1f);
            pbs.Scale = size;
            scene.AddPrimShape("hollow", pbs, new Vector3(128f, 128f, GroundZ + 0.5f), size, Quaternion.Identity, false, 510);
            RapierTestScene.Step(scene, 0.3f);

            BSShape shape = scene.PhysObjects[510].PhysShape;
            Assert.Equal(BSPhysicsShapeType.SHAPE_MESH, shape.ShapeType);
            Assert.Null(((BSAPIRapier.BulletShapeRapier)shape.physShapeInfo).MeshVerts);
            Assert.True(WorldOf(scene).State.MeshCopyBytesNotKept > before);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // SetLocalScaling reads a trimesh's vertices back from Rapier and builds it again at the new size.
    [Fact]
    public void SetLocalScaling_Trimesh_RebuildsFromTheNativeMesh()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            BulletShape mesh = scene.PE.CreateMeshShape(scene.World, CubeTriangles.Length, CubeTriangles, 8, CubeCorners(0.5f, Vector3.Zero));
            StaticBody(scene, mesh, 9100, new Vector3(128f, 128f, 50f));
            Assert.Equal(50.5f, Ray(scene, new Vector3(128f, 128f, 60f), new Vector3(128f, 128f, 40f)).Point.Z, 0.01f);

            scene.PE.SetLocalScaling(mesh, new Vector3(1f, 1f, 4f));
            Assert.Equal(new Vector3(1f, 1f, 4f), scene.PE.GetLocalScaling(mesh));
            Assert.Equal(52f, Ray(scene, new Vector3(128f, 128f, 60f), new Vector3(128f, 128f, 40f)).Point.Z, 0.01f);
            Assert.Equal(128.5f, Ray(scene, new Vector3(140f, 128f, 50f), new Vector3(120f, 128f, 50f)).Point.X, 0.01f);

            // Scaled again: from the rebuilt mesh, not from the first size.
            scene.PE.SetLocalScaling(mesh, new Vector3(2f, 1f, 2f));
            Assert.Equal(51f, Ray(scene, new Vector3(128f, 128f, 60f), new Vector3(128f, 128f, 40f)).Point.Z, 0.01f);
            Assert.Equal(129f, Ray(scene, new Vector3(140f, 128f, 50f), new Vector3(120f, 128f, 50f)).Point.X, 0.01f);
            Assert.Null(((BSAPIRapier.BulletShapeRapier)mesh).MeshVerts);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // The bounding box kept for the inertia follows a rescale: a cube of edge 2 and mass 6 has an inertia of 4.
    [Fact]
    public void GImpact_Rescaled_InertiaFollowsTheNewSize()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            BulletShape mesh = scene.PE.CreateGImpactShape(scene.World, CubeTriangles.Length, CubeTriangles, 8, CubeCorners(0.5f, Vector3.Zero));
            scene.PE.SetLocalScaling(mesh, new Vector3(2f, 2f, 2f));
            Vector3 inertia = scene.PE.CalculateLocalInertia(mesh, 6f);
            Assert.Equal(4f, inertia.X, 2);
            Assert.Equal(4f, inertia.Y, 2);
            Assert.Equal(4f, inertia.Z, 2);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // HACD (ShouldUseBulletHACD) decomposes a mesh shape: the mesh's geometry is read back from Rapier for it.
    [Fact]
    public void HullFromMesh_OfCubeMesh_RestsLikeBox()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            Floor(scene);
            BulletShape mesh = scene.PE.CreateMeshShape(scene.World, CubeTriangles.Length, CubeTriangles, 8, CubeCorners(0.5f, Vector3.Zero));
            BulletShape hulls = scene.PE.BuildHullShapeFromMesh(scene.World, mesh, new HACDParams());
            Assert.True(hulls.HasPhysicalShape);
            Assert.Equal((int)BSPhysicsShapeType.SHAPE_HULL, scene.PE.GetShapeType(hulls));
            BulletBody body = DynamicBody(scene, hulls, 9101, new Vector3(128f, 128f, 53f));
            EngineStep(scene, 3f);

            Assert.InRange(scene.PE.GetPosition(body).Z, 50.5f - 0.03f, 50.5f + 0.03f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // ---- Prim cylinders (the Rapier engine's native cylinder) ----

    private const float GroundZ = 21f;

    private static PhysicsActor AddCylinder(BSScene scene, uint localId, Vector3 position, Vector3 size, Quaternion rotation,
                                            Action<PrimitiveBaseShape>? change = null)
    {
        // A viewer's cylinder: a circle profile on a straight path (PrimitiveBaseShape.CreateCylinder makes something else).
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.ProfileShape = ProfileShape.Circle;
        pbs.Scale = size;
        change?.Invoke(pbs);
        return scene.AddPrimShape("cylinder" + localId, pbs, position, size, rotation, true, localId);
    }

    private static BSScene GroundScene()
    {
        BSScene scene = RapierTestScene.Create();
        float[] map = new float[256 * 256];
        Array.Fill(map, GroundZ);
        scene.SetTerrain(map);
        RapierTestScene.Step(scene, 0.3f);
        return scene;
    }

    [Fact]
    public void PrimCylinder_Plain_IsNativeCylinderAndStandsAtHalfItsHeight()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = GroundScene();
        try
        {
            // 0.6 m across, 1.2 m tall, standing on its end.
            PhysicsActor cylinder = AddCylinder(scene, 501, new Vector3(128f, 128f, GroundZ + 0.7f), new Vector3(0.6f, 0.6f, 1.2f),
                                                Quaternion.Identity);
            RapierTestScene.Step(scene, 3f);

            BSShape shape = scene.PhysObjects[501].PhysShape;
            Assert.Equal(BSPhysicsShapeType.SHAPE_CYLINDER, shape.ShapeType);
            Assert.True(shape.physShapeInfo.isNativeShape);
            Assert.Equal((int)BSPhysicsShapeType.SHAPE_CYLINDER, scene.PE.GetShapeType(shape.physShapeInfo));
            // The axis is the prim's Z: it stands on its flat end, centre half its height above the ground, and upright.
            Assert.Equal(GroundZ + 0.6f, cylinder.Position.Z, 0.02f);
            Vector3 axis = Vector3.UnitZ * cylinder.Orientation;
            Assert.True(axis.Z > 0.999f, $"axis {axis}");
            // A ray down the side misses beyond the radius and hits inside it at the top face.
            Assert.False(scene.PE.RayTest2(scene.World, new Vector3(128.35f, 128f, 30f), new Vector3(128.35f, 128f, GroundZ + 0.1f), 0xffffffff, 0xffffffff).hasHit());
            RaycastHit top = scene.PE.RayTest2(scene.World, new Vector3(128.25f, 128f, 30f), new Vector3(128.25f, 128f, GroundZ + 0.1f), 0xffffffff, 0xffffffff);
            Assert.True(top.hasHit());
            Assert.Equal(GroundZ + 1.2f, top.Point.Z, 0.02f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void PrimCylinder_OnItsSide_RestsAtItsRadiusAndRollsAboutItsAxis()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = GroundScene();
        try
        {
            // Turned a quarter about X, so its axis lies along Y; 0.6 m across, so the centre rests 0.3 m up.
            Quaternion onSide = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);
            PhysicsActor cylinder = AddCylinder(scene, 502, new Vector3(128f, 128f, GroundZ + 0.4f), new Vector3(0.6f, 0.6f, 1.2f), onSide);
            RapierTestScene.Step(scene, 2f);
            Assert.Equal(BSPhysicsShapeType.SHAPE_CYLINDER, scene.PhysObjects[502].PhysShape.ShapeType);
            Assert.Equal(GroundZ + 0.3f, cylinder.Position.Z, 0.02f);

            // Pushed along X, across its axis, friction sets it rolling: it turns about +Y, one radian per 0.3 m, and keeps
            // its line.
            scene.TaintedObject(502, "ShapeTests", () =>
            {
                BulletBody body = scene.PhysObjects[502].PhysBody;
                scene.PE.SetLinearVelocity(body, new Vector3(1.5f, 0f, 0f));
                scene.PE.Activate(body, true);
            });
            RapierTestScene.Step(scene, 0.5f);
            Vector3 spin = cylinder.RotationalVelocity;
            Vector3 velocity = cylinder.Velocity;
            Assert.True(velocity.X > 0.5f, $"velocity {velocity}");
            Assert.True(spin.Y > 0.5f / 0.3f, $"spin {spin}");
            Assert.Equal(velocity.X / 0.3f, spin.Y, 0.5f);
            Assert.InRange(spin.X, -0.2f, 0.2f);
            Assert.InRange(spin.Z, -0.2f, 0.2f);
            Assert.Equal(128f, cylinder.Position.Y, 0.05f);
            Assert.Equal(GroundZ + 0.3f, cylinder.Position.Z, 0.02f);
            Vector3 axis = Vector3.UnitZ * cylinder.Orientation;
            Assert.True(MathF.Abs(axis.Y) > 0.99f, $"axis {axis}");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Theory]
    [InlineData("hollow")]
    [InlineData("cut")]
    [InlineData("taper")]
    [InlineData("oval")]
    public void PrimCylinder_NotPlain_KeepsTheHull(string kind)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = GroundScene();
        try
        {
            Vector3 size = kind == "oval" ? new Vector3(0.6f, 0.9f, 1.2f) : new Vector3(0.6f, 0.6f, 1.2f);
            AddCylinder(scene, 503, new Vector3(128f, 128f, GroundZ + 0.7f), size, Quaternion.Identity, pbs =>
            {
                if (kind == "hollow")
                    pbs.ProfileHollow = 20000;
                else if (kind == "cut")
                    pbs.ProfileEnd = 10000;
                else if (kind == "taper")
                    pbs.PathTaperX = 50;
            });
            RapierTestScene.Step(scene, 0.5f);
            BSShape shape = scene.PhysObjects[503].PhysShape;
            Assert.NotEqual(BSPhysicsShapeType.SHAPE_CYLINDER, shape.ShapeType);
            Assert.False(shape.physShapeInfo.isNativeShape);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }
    // A plain cylinder edited in world: hollowed it becomes a hull, made plain again it is native again, and made oval
    // (X != Y) it is a hull again. Each time it still rests on its flat end.
    [Fact]
    public void PrimCylinder_EditedAtRuntime_RebuildsTheRightShape()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = GroundScene();
        try
        {
            Vector3 size = new Vector3(0.6f, 0.6f, 1.2f);
            PhysicsActor cylinder = AddCylinder(scene, 504, new Vector3(128f, 128f, GroundZ + 0.7f), size, Quaternion.Identity);
            RapierTestScene.Step(scene, 1f);
            Assert.Equal(BSPhysicsShapeType.SHAPE_CYLINDER, scene.PhysObjects[504].PhysShape.ShapeType);

            PrimitiveBaseShape Plain(Vector3 scale)
            {
                PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
                pbs.ProfileShape = ProfileShape.Circle;
                pbs.Scale = scale;
                return pbs;
            }

            PrimitiveBaseShape hollow = Plain(size);
            hollow.ProfileHollow = 20000;
            cylinder.Shape = hollow;
            RapierTestScene.Step(scene, 1f);
            Assert.NotEqual(BSPhysicsShapeType.SHAPE_CYLINDER, scene.PhysObjects[504].PhysShape.ShapeType);
            Assert.False(scene.PhysObjects[504].PhysShape.physShapeInfo.isNativeShape);
            Assert.Equal(GroundZ + 0.6f, cylinder.Position.Z, 0.05f);

            cylinder.Shape = Plain(size);
            RapierTestScene.Step(scene, 1f);
            Assert.Equal(BSPhysicsShapeType.SHAPE_CYLINDER, scene.PhysObjects[504].PhysShape.ShapeType);
            Assert.Equal(GroundZ + 0.6f, cylinder.Position.Z, 0.02f);

            Vector3 oval = new Vector3(0.6f, 0.9f, 1.2f);
            cylinder.Shape = Plain(oval);
            cylinder.Size = oval;
            RapierTestScene.Step(scene, 1f);
            Assert.NotEqual(BSPhysicsShapeType.SHAPE_CYLINDER, scene.PhysObjects[504].PhysShape.ShapeType);
            Assert.False(scene.PhysObjects[504].PhysShape.physShapeInfo.isNativeShape);
            Assert.Equal(GroundZ + 0.6f, cylinder.Position.Z, 0.05f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }
}
