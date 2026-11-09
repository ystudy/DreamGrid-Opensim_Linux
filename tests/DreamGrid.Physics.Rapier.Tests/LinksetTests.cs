using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

/// <summary>
/// Compound shapes and linksets (H5a): one collider per compound child, all on the root body. Linksets are built the
/// way the simulator builds them, through PhysicsActor.link, which BSLinksetCompound turns into a compound shape on the
/// root body.
/// </summary>
public class LinksetTests
{
    private const float TerrainHeight = 21f;
    private const float Edge = 0.5f;
    // A box of this edge resting on the terrain has its centre here.
    private const float RestZ = TerrainHeight + Edge / 2f;

    private static PhysicsActor AddBox(BSScene scene, uint localId, Vector3 position, bool physical)
    {
        return AddBox(scene, localId, position, Quaternion.Identity, physical);
    }

    private static PhysicsActor AddBox(BSScene scene, uint localId, Vector3 position, Quaternion rotation, bool physical)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        Vector3 size = new Vector3(Edge, Edge, Edge);
        pbs.Scale = size;
        return scene.AddPrimShape("box" + localId, pbs, position, size, rotation, physical, localId);
    }

    private static void SetFlatTerrain(BSScene scene)
    {
        float[] map = new float[256 * 256];
        Array.Fill(map, TerrainHeight);
        scene.SetTerrain(map);
        RapierTestScene.Step(scene, 0.3f);
    }

    private static BSAPIRapier.BulletBodyRapier BodyOf(BSScene scene, uint id)
    {
        return (BSAPIRapier.BulletBodyRapier)scene.PhysObjects[id].PhysBody;
    }

    private static BSAPIRapier.BulletWorldRapier WorldOf(BSScene scene)
    {
        return (BSAPIRapier.BulletWorldRapier)scene.World;
    }

    /// <summary>The pairs one BSScene.Simulate call put in the collision array.</summary>
    private static List<CollisionDesc> Frame(BSScene scene)
    {
        scene.Simulate(RapierTestScene.FrameSeconds);
        int count = WorldOf(scene).State.LastCollisionCount;
        List<CollisionDesc> pairs = new List<CollisionDesc>();
        for (int ii = 0; ii < count; ii++)
            pairs.Add(scene.m_collisionArray[ii]);
        return pairs;
    }

    private static bool IsPair(CollisionDesc c, uint a, uint b)
    {
        return c.aID == Math.Min(a, b) && c.bID == Math.Max(a, b);
    }

    private static bool Involves(CollisionDesc c, uint id)
    {
        return c.aID == id || c.bID == id;
    }

    /// <summary>Links the children to the root as SceneObjectGroup does: each child's actor is linked to the root's.</summary>
    private static void Link(PhysicsActor root, params PhysicsActor[] children)
    {
        foreach (PhysicsActor child in children)
            child.link(root);
    }

    private static Vector3 ColliderPosition(RapierNative.ColliderHandle collider)
    {
        return BSAPIRapier.ToVector3(RapierNative.r3Collider_Position(collider).translation);
    }

    private static Quaternion ColliderRotation(RapierNative.ColliderHandle collider)
    {
        return BSAPIRapier.ToQuaternion(RapierNative.r3Collider_Position(collider).rotation);
    }

    // The Hamilton product a b, as btQuaternion multiplies, written out so the test does not depend on how OpenMetaverse
    // orders its operator.
    private static Quaternion Hamilton(Quaternion a, Quaternion b)
    {
        return new Quaternion(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
    }

    private static Quaternion Conjugate(Quaternion q)
    {
        return new Quaternion(-q.X, -q.Y, -q.Z, q.W);
    }

    // v turned by the unit quaternion q (q v q*).
    private static Vector3 Turn(Quaternion q, Vector3 v)
    {
        Quaternion r = Hamilton(Hamilton(q, new Quaternion(v.X, v.Y, v.Z, 0f)), Conjugate(q));
        return new Vector3(r.X, r.Y, r.Z);
    }

    private static Quaternion AxisAngle(Vector3 axis, float degrees)
    {
        float half = degrees * MathF.PI / 360f;
        return new Quaternion(axis.X * MathF.Sin(half), axis.Y * MathF.Sin(half), axis.Z * MathF.Sin(half), MathF.Cos(half));
    }

    private static void AssertSameRotation(Quaternion expected, Quaternion actual, string what)
    {
        // q and -q are the same rotation.
        float dot = expected.X * actual.X + expected.Y * actual.Y + expected.Z * actual.Z + expected.W * actual.W;
        Assert.True(MathF.Abs(dot) > 0.9999f, $"{what}: expected {expected}, got {actual}");
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance, string what)
    {
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"{what}: expected {expected}, got {actual}");
    }

    private static RaycastHit Ray(BSScene scene, Vector3 from, Vector3 to)
    {
        return scene.PE.RayTest2(scene.World, from, to, 0xffffffff, 0xffffffff);
    }

    // ---- The engine driven directly (ids BSScene does not use) ----

    private static BulletShape Box(BSScene scene, float edge)
    {
        return scene.PE.BuildNativeShape(scene.World, new ShapeData { Type = BSPhysicsShapeType.SHAPE_BOX, Scale = new Vector3(edge, edge, edge) });
    }

    private static void InWorld(BSScene scene, BulletBody body)
    {
        Assert.True(scene.PE.AddObjectToWorld(scene.World, body));
        Assert.True(scene.PE.SetCollisionGroupMask(body, 0xffff, 0xffff));
    }

    private static BulletBody FixedBody(BSScene scene, BulletShape shape, uint id, Vector3 position)
    {
        BulletBody body = scene.PE.CreateBodyFromShape(scene.World, shape, id, position, Quaternion.Identity);
        scene.PE.AddToCollisionFlags(body, CollisionFlags.CF_STATIC_OBJECT);
        InWorld(scene, body);
        return body;
    }

    private static BulletBody DynamicBody(BSScene scene, BulletShape shape, uint id, Vector3 position, Quaternion rotation)
    {
        BulletBody body = scene.PE.CreateBodyFromShape(scene.World, shape, id, position, rotation);
        scene.PE.SetMassProps(body, 1f, scene.PE.CalculateLocalInertia(shape, 1f));
        InWorld(scene, body);
        return body;
    }

    private static void EngineStep(BSScene scene, float seconds)
    {
        const float step = 1f / 60f;
        for (float done = 0f; done < seconds; done += step)
            scene.PE.PhysicsStep(scene.World, step, 0, step, out int _, out int _);
    }

    /// <summary>One engine step after the body was pushed; the update it got.</summary>
    private static EntityProperties PushedUpdate(BSScene scene, BulletBody body)
    {
        scene.PE.PushUpdate(body);
        scene.PE.PhysicsStep(scene.World, 1f / 60f, 0, 1f / 60f, out int count, out int _);
        EntityProperties[] updates = WorldOf(scene).State.UpdateArray;
        for (int ii = 0; ii < count; ii++)
        {
            if (updates[ii].ID == body.ID)
                return updates[ii];
        }
        throw new Xunit.Sdk.XunitException("no update for body " + body.ID);
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

    private static float[] CubeCorners(float half, Vector3 offset)
    {
        List<float> v = new List<float>();
        foreach (float x in new[] { -half, half })
            foreach (float y in new[] { -half, half })
                foreach (float z in new[] { -half, half })
                    v.AddRange(new[] { x + offset.X, y + offset.Y, z + offset.Z });
        return v.ToArray();
    }

    // ---- Linksets through BSScene ----

    [Fact]
    public void Linkset_ThreePrims_FallsAndRestsAsOne()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor root = AddBox(scene, 701, new Vector3(128f, 128f, 23f), physical: true);
            PhysicsActor second = AddBox(scene, 702, new Vector3(128.6f, 128f, 23f), physical: true);
            PhysicsActor third = AddBox(scene, 703, new Vector3(129.2f, 128f, 23f), physical: true);
            Frame(scene);
            Link(root, second, third);
            RapierTestScene.Step(scene, 4f);

            BSAPIRapier.BulletBodyRapier body = BodyOf(scene, 701);
            Assert.Equal(3, body.Colliders.Count);
            // The children's own bodies take no part: their shapes are on the root.
            Assert.Equal(0u, RapierNative.r3RigidBody_IsEnabled(BodyOf(scene, 702).Body));
            Assert.Equal(0u, RapierNative.r3RigidBody_IsEnabled(BodyOf(scene, 703).Body));

            // All three landed, and they kept their places relative to each other.
            Quaternion frame = ColliderRotation(body.Colliders[0]);
            Vector3 origin = ColliderPosition(body.Colliders[0]);
            for (int ii = 0; ii < 3; ii++)
            {
                Vector3 p = ColliderPosition(body.Colliders[ii]);
                Assert.InRange(p.Z, RestZ - 0.03f, RestZ + 0.03f);
                Vector3 offset = Turn(Conjugate(frame), p - origin);
                AssertNear(new Vector3(0.6f * ii, 0f, 0f), offset, 0.01f, $"collider {ii} offset");
            }
            // The root prim is where its collider is.
            AssertNear(origin, root.Position, 0.01f, "root prim position");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Linkset_CollisionReportsRootId()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            // The child is underneath, so it is the child's shape that meets the terrain.
            PhysicsActor root = AddBox(scene, 711, new Vector3(128f, 128f, 23.5f), physical: true);
            PhysicsActor child = AddBox(scene, 712, new Vector3(128f, 128f, 23f), physical: true);
            Frame(scene);
            Link(root, child);
            root.SubscribeEvents(50);

            bool rootPair = false;
            for (int ii = 0; ii < 40; ii++)
            {
                foreach (CollisionDesc c in Frame(scene))
                {
                    Assert.False(Involves(c, 712), $"a pair carries the child's ID: {c.aID}-{c.bID}");
                    rootPair |= IsPair(c, BSScene.TERRAIN_ID, 711);
                }
            }
            Assert.True(rootPair, "the linkset's landing was not reported under the root's ID");
            Assert.InRange(root.Position.Z, RestZ + Edge - 0.03f, RestZ + Edge + 0.03f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // A subscribed box resting on a linkset child: the child's shape is on the root body, so the box touches the root
    // once per frame. The child's own body must stay out of it, or the contact would be reported twice and the root
    // pushed twice.
    [Fact]
    public void LinksetChild_PhysicalLinkset_TouchIsOnePairPerFrameUnderRootId()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor root = AddBox(scene, 721, new Vector3(128f, 128f, RestZ), physical: true);
            PhysicsActor child = AddBox(scene, 722, new Vector3(129f, 128f, RestZ), physical: true);
            Frame(scene);
            Link(root, child);
            RapierTestScene.Step(scene, 1f);
            PhysicsActor visitor = AddBox(scene, 725, new Vector3(129f, 128f, RestZ + Edge + 0.05f), physical: true);
            visitor.SubscribeEvents(50);
            RapierTestScene.Step(scene, 1.5f);

            Assert.Equal(0u, RapierNative.r3RigidBody_IsEnabled(BodyOf(scene, 722).Body));
            for (int ii = 0; ii < 15; ii++)
            {
                List<CollisionDesc> pairs = Frame(scene).Where(c => Involves(c, 725)).ToList();
                Assert.True(pairs.Count == 1, $"frame {ii}: {pairs.Count} pairs for the visitor: "
                            + string.Join(", ", pairs.Select(c => c.aID + "-" + c.bID)));
                Assert.True(IsPair(pairs[0], 721, 725), $"frame {ii}: the pair is {pairs[0].aID}-{pairs[0].bID}");
            }
            Assert.InRange(visitor.Position.Z, RestZ + Edge - 0.03f, RestZ + Edge + 0.03f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // BSLinksetCompound builds no compound for a linkset that is not physical (RecomputeLinksetCompound returns before
    // it does): every prim keeps its own body and is reported under its own ID, as with Bullet.
    [Fact]
    public void LinksetChild_StaticLinkset_TouchIsOnePairPerFrameUnderChildId()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor root = AddBox(scene, 731, new Vector3(128f, 128f, RestZ), physical: false);
            PhysicsActor child = AddBox(scene, 732, new Vector3(129f, 128f, RestZ), physical: false);
            Frame(scene);
            Link(root, child);
            RapierTestScene.Step(scene, 1f);
            PhysicsActor visitor = AddBox(scene, 735, new Vector3(129f, 128f, RestZ + Edge + 0.05f), physical: true);
            visitor.SubscribeEvents(50);
            RapierTestScene.Step(scene, 1.5f);

            Assert.Single(BodyOf(scene, 731).Colliders);
            for (int ii = 0; ii < 15; ii++)
            {
                List<CollisionDesc> pairs = Frame(scene).Where(c => Involves(c, 735)).ToList();
                Assert.True(pairs.Count == 1, $"frame {ii}: {pairs.Count} pairs for the visitor: "
                            + string.Join(", ", pairs.Select(c => c.aID + "-" + c.bID)));
                Assert.True(IsPair(pairs[0], 732, 735), $"frame {ii}: the pair is {pairs[0].aID}-{pairs[0].bID}");
            }
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // A static child in a physical linkset (a child whose physics flag has not caught up with the root's): its shape
    // goes into the root's compound, so its own fixed body has to go quiet as a dynamic child's does.
    [Fact]
    public void LinksetChild_StaticChildOfPhysicalRoot_TouchIsOnePairPerFrameUnderRootId()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor root = AddBox(scene, 741, new Vector3(128f, 128f, RestZ), physical: true);
            PhysicsActor child = AddBox(scene, 742, new Vector3(129f, 128f, RestZ), physical: false);
            Frame(scene);
            Link(root, child);
            RapierTestScene.Step(scene, 1f);
            PhysicsActor visitor = AddBox(scene, 745, new Vector3(129f, 128f, RestZ + Edge + 0.05f), physical: true);
            visitor.SubscribeEvents(50);
            RapierTestScene.Step(scene, 1.5f);

            Assert.Equal(2, BodyOf(scene, 741).Colliders.Count);
            Assert.Equal(0u, RapierNative.r3RigidBody_IsEnabled(BodyOf(scene, 742).Body));
            for (int ii = 0; ii < 15; ii++)
            {
                List<CollisionDesc> pairs = Frame(scene).Where(c => Involves(c, 745)).ToList();
                Assert.True(pairs.Count == 1, $"frame {ii}: {pairs.Count} pairs for the visitor: "
                            + string.Join(", ", pairs.Select(c => c.aID + "-" + c.bID)));
                Assert.True(IsPair(pairs[0], 741, 745), $"frame {ii}: the pair is {pairs[0].aID}-{pairs[0].bID}");
            }
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Linkset_UpdateChildTransform_MovesCollider()
    {
        using ErrorLog log = new ErrorLog();
        // No centre-of-mass offset: BSLinksetCompound.UpdateProperties gives UpdateChildTransform the child's offset from
        // the root, which is only where the child is when the compound is not shifted to the centre of mass.
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string>
        {
            { "Gravity", "0" }, { "LinksetOffsetCenterOfMass", "false" }
        });
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor root = AddBox(scene, 751, new Vector3(128f, 128f, 30f), physical: true);
            PhysicsActor child = AddBox(scene, 752, new Vector3(130f, 128f, 30f), physical: true);
            Frame(scene);
            Link(root, child);
            RapierTestScene.Step(scene, 0.5f);

            BSAPIRapier.BulletBodyRapier body = BodyOf(scene, 751);
            List<RapierNative.ColliderHandle> colliders = new List<RapierNative.ColliderHandle>(body.Colliders);
            Assert.Equal(2, colliders.Count);
            RaycastHit before = Ray(scene, new Vector3(130f, 128f, 35f), new Vector3(130f, 128f, 25f));
            Assert.True(before.hasHit(), "the child is not where it was linked");
            Assert.Equal(751u, before.ID);
            Assert.Equal(30.25f, before.Point.Z, 0.01f);

            child.Position = new Vector3(131f, 128f, 30f);
            RapierTestScene.Step(scene, 0.3f);

            // The same body and colliders: the child was moved, not the linkset rebuilt.
            Assert.Same(body, BodyOf(scene, 751));
            Assert.Equal(colliders, body.Colliders);
            RaycastHit after = Ray(scene, new Vector3(131f, 128f, 35f), new Vector3(131f, 128f, 25f));
            Assert.True(after.hasHit(), "nothing at the child's new place");
            Assert.Equal(751u, after.ID);
            Assert.False(Ray(scene, new Vector3(130f, 128f, 35f), new Vector3(130f, 128f, 25f)).hasHit(),
                        "something is still at the child's old place");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // BulletS's UseBulletSimRootOffsetHack (a private switch in BSLinksetCompound) is what sets
    // BS_RETURN_ROOT_COMPOUND_SHAPE: the body then reports the pose of compound child 0, the root prim.
    [Fact]
    public void Linkset_RootOffsetHack_ReportsRootChildPose()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "Gravity", "0" } });
        try
        {
            SetFlatTerrain(scene);
            Quaternion turn = AxisAngle(Vector3.UnitZ, 30f);
            PhysicsActor root = AddBox(scene, 761, new Vector3(128f, 128f, 30f), turn, physical: true);
            PhysicsActor child = AddBox(scene, 762, new Vector3(129f, 128.5f, 30f), turn, physical: true);
            Frame(scene);
            BSLinkset linkset = ((BSPrimLinkable)scene.PhysObjects[761]).Linkset;
            FieldInfo hack = typeof(BSLinksetCompound).GetField("UseBulletSimRootOffsetHack", BindingFlags.NonPublic | BindingFlags.Instance)!;
            hack.SetValue(linkset, true);
            Link(root, child);
            RapierTestScene.Step(scene, 0.5f);

            BSAPIRapier.BulletBodyRapier body = BodyOf(scene, 761);
            Assert.Equal(2, body.Colliders.Count);
            Assert.True((body.Flags & CollisionFlags.BS_RETURN_ROOT_COMPOUND_SHAPE) != 0);
            // Child 0 is the root prim, at the body's origin with the hack.
            Assert.True(body.HasRootChild);
            AssertNear(Vector3.Zero, body.RootChildPos, 0.001f, "root child position");
            AssertSameRotation(Quaternion.Identity, body.RootChildRot, "root child rotation");
            AssertNear(new Vector3(128f, 128f, 30f), root.Position, 0.01f, "root prim position");
            AssertSameRotation(turn, root.Orientation, "root prim rotation");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Linkset_Delink_RestoresChild()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor root = AddBox(scene, 771, new Vector3(128f, 128f, RestZ), physical: true);
            PhysicsActor child = AddBox(scene, 772, new Vector3(129f, 128f, RestZ), physical: true);
            Frame(scene);
            Link(root, child);
            RapierTestScene.Step(scene, 1f);
            Assert.Equal(2, BodyOf(scene, 771).Colliders.Count);

            child.delink();
            RapierTestScene.Step(scene, 1f);

            Assert.Single(BodyOf(scene, 771).Colliders);
            Assert.Single(BodyOf(scene, 772).Colliders);
            Assert.Equal(1u, RapierNative.r3RigidBody_IsEnabled(BodyOf(scene, 772).Body));
            AssertNear(new Vector3(129f, 128f, RestZ), child.Position, 0.05f, "child position");
            AssertNear(new Vector3(128f, 128f, RestZ), root.Position, 0.05f, "root position");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // ---- Compounds through the engine ----

    // A linkset child whose shape is a hull list (one native compound, one collider), turned a quarter about Z.
    [Fact]
    public void Linkset_NestedHullChild_Collides()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "Gravity", "-9.8" } });
        try
        {
            // Two unit cubes 2 m apart along the hull list's X; turned a quarter about Z they lie along Y.
            BulletShape hulls = scene.PE.CreateHullShape(scene.World, 2, HullList(
                (Vector3.Zero, CubeCorners(0.5f, new Vector3(-1f, 0f, 0f))),
                (Vector3.Zero, CubeCorners(0.5f, new Vector3(1f, 0f, 0f)))));
            BulletShape compound = scene.PE.CreateCompoundShape(scene.World, false);
            scene.PE.AddChildShapeToCompoundShape(compound, Box(scene, 1f), Vector3.Zero, Quaternion.Identity);
            scene.PE.AddChildShapeToCompoundShape(compound, hulls, new Vector3(3f, 0f, 0f), AxisAngle(Vector3.UnitZ, 90f));
            Assert.Equal(2, scene.PE.GetNumberOfCompoundChildren(compound));
            Assert.True(scene.PE.IsCompound(compound));
            BulletBody body = FixedBody(scene, compound, 9100, new Vector3(128f, 128f, 50f));
            Assert.Equal(2, ((BSAPIRapier.BulletBodyRapier)body).Colliders.Count);
            EngineStep(scene, 0.02f);

            foreach (Vector3 at in new[] { new Vector3(131f, 127f, 0f), new Vector3(131f, 129f, 0f), new Vector3(128f, 128f, 0f) })
            {
                RaycastHit hit = Ray(scene, new Vector3(at.X, at.Y, 60f), new Vector3(at.X, at.Y, 40f));
                Assert.True(hit.hasHit(), $"no hit at {at}");
                Assert.Equal(9100u, hit.ID);
                Assert.Equal(50.5f, hit.Point.Z, 0.01f);
            }
            // The gap between the hulls, and where they would be without the quarter turn.
            foreach (Vector3 at in new[] { new Vector3(131f, 128f, 0f), new Vector3(130f, 128f, 0f), new Vector3(132f, 128f, 0f) })
                Assert.False(Ray(scene, new Vector3(at.X, at.Y, 60f), new Vector3(at.X, at.Y, 40f)).hasHit(), $"a hit at {at}");

            // Something dropped on each hull rests on it.
            BulletBody first = DynamicBody(scene, Box(scene, 0.5f), 9201, new Vector3(131f, 127f, 52f), Quaternion.Identity);
            BulletBody second = DynamicBody(scene, Box(scene, 0.5f), 9202, new Vector3(131f, 129f, 52f), Quaternion.Identity);
            EngineStep(scene, 3f);
            Assert.InRange(scene.PE.GetPosition(first).Z, 50.75f - 0.03f, 50.75f + 0.03f);
            Assert.InRange(scene.PE.GetPosition(second).Z, 50.75f - 0.03f, 50.75f + 0.03f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // Parry allows no compound inside a compound, so a BulletS compound that holds another is flattened: one collider
    // per leaf shape, its pose the two child poses composed. Moving the outer child moves the leaves with it.
    [Fact]
    public void Compound_InsideCompound_IsFlattened()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            BulletShape inner = scene.PE.CreateCompoundShape(scene.World, false);
            scene.PE.AddChildShapeToCompoundShape(inner, Box(scene, 1f), new Vector3(1f, 0f, 0f), Quaternion.Identity);
            scene.PE.AddChildShapeToCompoundShape(inner, Box(scene, 1f), new Vector3(-1f, 0f, 0f), Quaternion.Identity);
            BulletShape outer = scene.PE.CreateCompoundShape(scene.World, false);
            scene.PE.AddChildShapeToCompoundShape(outer, Box(scene, 1f), Vector3.Zero, Quaternion.Identity);
            scene.PE.AddChildShapeToCompoundShape(outer, inner, new Vector3(5f, 0f, 0f), AxisAngle(Vector3.UnitZ, 90f));
            BSAPIRapier.BulletBodyRapier body = (BSAPIRapier.BulletBodyRapier)FixedBody(scene, outer, 9100, new Vector3(128f, 128f, 50f));
            Assert.Equal(3, body.Colliders.Count);
            EngineStep(scene, 0.02f);

            // The inner boxes at inner x = +-1, turned onto y, at outer x = 5.
            foreach (Vector3 at in new[] { new Vector3(133f, 129f, 0f), new Vector3(133f, 127f, 0f) })
                Assert.True(Ray(scene, new Vector3(at.X, at.Y, 60f), new Vector3(at.X, at.Y, 40f)).hasHit(), $"no hit at {at}");
            Assert.False(Ray(scene, new Vector3(134f, 128f, 60f), new Vector3(134f, 128f, 40f)).hasHit());

            scene.PE.UpdateChildTransform(outer, 1, new Vector3(8f, 0f, 0f), AxisAngle(Vector3.UnitZ, 90f), true);
            EngineStep(scene, 0.02f);
            Assert.True(Ray(scene, new Vector3(136f, 129f, 60f), new Vector3(136f, 129f, 40f)).hasHit());
            Assert.False(Ray(scene, new Vector3(133f, 129f, 60f), new Vector3(133f, 129f, 40f)).hasHit());
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // BS_RETURN_ROOT_COMPOUND_SHAPE: BulletSim reports worldTransform * childTransform(0). The child's rotation is not
    // the identity, so the order of the quaternion product is pinned.
    [Fact]
    public void Compound_RootChildPose_ReportedAsBodyTimesChild()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "Gravity", "0" } });
        try
        {
            Vector3 position = new Vector3(100f, 100f, 100f);
            Quaternion world = AxisAngle(Vector3.UnitZ, 30f);
            Quaternion childRot = AxisAngle(Vector3.UnitX, 90f);
            BulletShape compound = scene.PE.CreateCompoundShape(scene.World, false);
            scene.PE.AddChildShapeToCompoundShape(compound, Box(scene, 1f), new Vector3(1f, 0f, 0f), childRot);
            scene.PE.AddChildShapeToCompoundShape(compound, Box(scene, 1f), new Vector3(-1f, 0f, 0f), Quaternion.Identity);
            BulletBody body = DynamicBody(scene, compound, 9300, position, world);

            // Without the flag the body's own pose.
            EntityProperties plain = PushedUpdate(scene, body);
            AssertNear(position, plain.Position, 0.001f, "plain position");
            AssertSameRotation(world, plain.Rotation, "plain rotation");

            scene.PE.AddToCollisionFlags(body, CollisionFlags.BS_RETURN_ROOT_COMPOUND_SHAPE);
            EntityProperties root = PushedUpdate(scene, body);
            AssertNear(position + Turn(world, new Vector3(1f, 0f, 0f)), root.Position, 0.001f, "child 0 position");
            AssertSameRotation(Hamilton(world, childRot), root.Rotation, "child 0 rotation");

            // Moving child 0 moves what is reported.
            Quaternion movedRot = AxisAngle(Vector3.UnitY, 45f);
            scene.PE.UpdateChildTransform(compound, 0, new Vector3(0f, 2f, 0f), movedRot, true);
            EntityProperties moved = PushedUpdate(scene, body);
            AssertNear(position + Turn(world, new Vector3(0f, 2f, 0f)), moved.Position, 0.001f, "moved child 0 position");
            AssertSameRotation(Hamilton(world, movedRot), moved.Rotation, "moved child 0 rotation");

            // A plain shape on the body: no child 0 any more.
            scene.PE.SetCollisionShape(scene.World, body, Box(scene, 1f));
            Assert.Single(((BSAPIRapier.BulletBodyRapier)body).Colliders);
            EntityProperties single = PushedUpdate(scene, body);
            AssertNear(position, single.Position, 0.001f, "single shape position");
            AssertSameRotation(world, single.Rotation, "single shape rotation");

            // And a compound put on a body made from a plain shape brings it back.
            BulletShape again = scene.PE.CreateCompoundShape(scene.World, false);
            scene.PE.AddChildShapeToCompoundShape(again, Box(scene, 1f), new Vector3(0f, 0f, 1f), childRot);
            scene.PE.SetCollisionShape(scene.World, body, again);
            EntityProperties back = PushedUpdate(scene, body);
            AssertNear(position + Turn(world, new Vector3(0f, 0f, 1f)), back.Position, 0.001f, "compound again position");
            AssertSameRotation(Hamilton(world, childRot), back.Rotation, "compound again rotation");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // SetCollisionShape replaces the colliders. What the body had been told about (no contact response, collision
    // events, groups) holds for the new ones, and the old ones leave the tables.
    [Fact]
    public void SetCollisionShape_KeepsTheBodysSettings()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            BulletShape slab = Box(scene, 1f);
            scene.PE.SetLocalScaling(slab, new Vector3(40f, 40f, 1f));
            BulletBody floor = FixedBody(scene, slab, 9000, new Vector3(128f, 128f, 49.5f));
            BSAPIRapier.BulletBodyRapier body = (BSAPIRapier.BulletBodyRapier)DynamicBody(scene, Box(scene, 0.5f), 9401,
                                                    new Vector3(128f, 128f, 51f), Quaternion.Identity);
            scene.PE.AddToCollisionFlags(body, CollisionFlags.CF_NO_CONTACT_RESPONSE | CollisionFlags.BS_SUBSCRIBE_COLLISION_EVENTS);
            RapierNative.ColliderHandle old = body.Colliders.Single();

            BulletShape compound = scene.PE.CreateCompoundShape(scene.World, false);
            scene.PE.AddChildShapeToCompoundShape(compound, Box(scene, 0.5f), Vector3.Zero, Quaternion.Identity);
            scene.PE.RemoveObjectFromWorld(scene.World, body);
            scene.PE.SetCollisionShape(scene.World, body, compound);
            scene.PE.AddObjectToWorld(scene.World, body);

            RapierWorldState state = WorldOf(scene).State;
            RapierNative.ColliderHandle fresh = body.Colliders.Single();
            Assert.False(state.ColliderOwners.ContainsKey(old));
            Assert.DoesNotContain(old, state.WantsCollisions);
            Assert.False(state.ColliderShapes.ContainsKey(old));
            Assert.Same(body, state.ColliderOwners[fresh]);
            Assert.Contains(fresh, state.WantsCollisions);

            // It passes through the floor, but the touch is reported.
            bool reported = false;
            for (int ii = 0; ii < 60; ii++)
            {
                scene.PE.PhysicsStep(scene.World, 1f / 60f, 0, 1f / 60f, out int _, out int pairs);
                for (int jj = 0; jj < pairs; jj++)
                    reported |= IsPair(state.CollisionArray[jj], 9000, 9401);
            }
            Assert.True(reported, "the touch with the floor was not reported");
            Assert.True(scene.PE.GetPosition(body).Z < 49f, $"the body did not pass through: z={scene.PE.GetPosition(body).Z}");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Compound_RemoveChild_RemovesItsCollider()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            BulletShape left = Box(scene, 1f);
            BulletShape right = Box(scene, 1f);
            BulletShape compound = scene.PE.CreateCompoundShape(scene.World, false);
            scene.PE.AddChildShapeToCompoundShape(compound, left, new Vector3(-2f, 0f, 0f), Quaternion.Identity);
            scene.PE.AddChildShapeToCompoundShape(compound, right, new Vector3(2f, 0f, 0f), Quaternion.Identity);
            BSAPIRapier.BulletBodyRapier body = (BSAPIRapier.BulletBodyRapier)FixedBody(scene, compound, 9100, new Vector3(128f, 128f, 50f));
            Assert.Equal(2, body.Colliders.Count);
            Assert.True(scene.PE.GetChildShapeFromCompoundShapeIndex(compound, 1).ReferenceSame(right));

            BulletShape removed = scene.PE.RemoveChildShapeFromCompoundShapeIndex(compound, 0);
            Assert.True(removed.ReferenceSame(left));
            Assert.Equal(1, scene.PE.GetNumberOfCompoundChildren(compound));
            Assert.Single(body.Colliders);
            EngineStep(scene, 0.02f);
            Assert.False(Ray(scene, new Vector3(126f, 128f, 60f), new Vector3(126f, 128f, 40f)).hasHit());
            Assert.True(Ray(scene, new Vector3(130f, 128f, 60f), new Vector3(130f, 128f, 40f)).hasHit());

            // Added while the compound is on the body: a collider for it at once.
            scene.PE.AddChildShapeToCompoundShape(compound, left, new Vector3(0f, 3f, 0f), Quaternion.Identity);
            Assert.Equal(2, body.Colliders.Count);
            scene.PE.RemoveChildShapeFromCompoundShape(compound, right);
            Assert.Single(body.Colliders);
            EngineStep(scene, 0.02f);
            Assert.True(Ray(scene, new Vector3(128f, 131f, 60f), new Vector3(128f, 131f, 40f)).hasHit());
            Assert.False(Ray(scene, new Vector3(130f, 128f, 60f), new Vector3(130f, 128f, 40f)).hasHit());
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }
    // A plain cylinder linked as a child keeps its native cylinder in the root's compound: the child prim's shape is
    // SHAPE_CYLINDER, the linkset rests flat, and rays find the child's round edge, not a square one.
    [Fact]
    public void Linkset_NativeCylinderChild_IsRoundAndRests()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor root = AddBox(scene, 721, new Vector3(128f, 128f, 23f), physical: true);
            PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
            pbs.ProfileShape = ProfileShape.Circle;
            Vector3 size = new Vector3(Edge, Edge, Edge);
            pbs.Scale = size;
            PhysicsActor child = scene.AddPrimShape("cylinder722", pbs, new Vector3(128.6f, 128f, 23f), size, Quaternion.Identity, true, 722);
            Frame(scene);
            Assert.Equal(BSPhysicsShapeType.SHAPE_CYLINDER, scene.PhysObjects[722].PhysShape.ShapeType);
            Link(root, child);
            RapierTestScene.Step(scene, 4f);

            BSAPIRapier.BulletBodyRapier body = BodyOf(scene, 721);
            Assert.Equal(2, body.Colliders.Count);
            Assert.Equal(BSPhysicsShapeType.SHAPE_CYLINDER, scene.PhysObjects[722].PhysShape.ShapeType);
            foreach (RapierNative.ColliderHandle collider in body.Colliders)
                Assert.InRange(ColliderPosition(collider).Z, RestZ - 0.03f, RestZ + 0.03f);

            // The cylinder's axis stays upright: its collider carries the quarter turn from Rapier's Y axis to Z.
            Vector3 centre = ColliderPosition(body.Colliders[1]);
            Vector3 axis = Turn(ColliderRotation(body.Colliders[1]), Vector3.UnitY);
            Assert.True(MathF.Abs(axis.Z) > 0.999f, $"cylinder axis {axis}");
            // Inside the radius the top face is hit; in the corner of its bounding square, outside the radius, the ray
            // goes past it to the terrain.
            RaycastHit inside = Ray(scene, centre + new Vector3(0.18f, 0f, 5f), centre + new Vector3(0.18f, 0f, -5f));
            Assert.True(inside.hasHit());
            Assert.Equal(721u, inside.ID);
            Assert.Equal(RestZ + Edge / 2f, inside.Point.Z, 0.02f);
            RaycastHit corner = Ray(scene, centre + new Vector3(0.2f, 0.2f, 5f), centre + new Vector3(0.2f, 0.2f, -5f));
            Assert.True(corner.hasHit());
            Assert.Equal(BSScene.TERRAIN_ID, corner.ID);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }
}
