using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

public class CollisionTests
{
    private const float TerrainHeight = 21f;

    private static PhysicsActor AddBox(BSScene scene, uint localId, Vector3 position, bool physical, float edge = 0.5f)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        Vector3 size = new Vector3(edge, edge, edge);
        pbs.Scale = size;
        return scene.AddPrimShape("box" + localId, pbs, position, size, Quaternion.Identity, physical, localId);
    }

    private static void SetFlatTerrain(BSScene scene)
    {
        float[] map = new float[256 * 256];
        Array.Fill(map, TerrainHeight);
        scene.SetTerrain(map);
        RapierTestScene.Step(scene, 0.3f);
    }

    /// <summary>The pairs one BSScene.Simulate call put in the collision array (what BSScene reads).</summary>
    private static List<CollisionDesc> Frame(BSScene scene)
    {
        scene.Simulate(RapierTestScene.FrameSeconds);
        int count = ((BSAPIRapier.BulletWorldRapier)scene.World).State.LastCollisionCount;
        List<CollisionDesc> pairs = new List<CollisionDesc>();
        for (int ii = 0; ii < count; ii++)
            pairs.Add(scene.m_collisionArray[ii]);
        return pairs;
    }

    private static bool IsPair(CollisionDesc c, uint a, uint b)
    {
        return c.aID == Math.Min(a, b) && c.bID == Math.Max(a, b);
    }

    private static void Taint(BSScene scene, uint id, Action action)
    {
        scene.TaintedObject(id, "CollisionTests", () => action());
    }

    [Fact]
    public void Collisions_SubscribedPrimOnTerrain_Reported()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor box = AddBox(scene, 401, new Vector3(128f, 128f, 23f), physical: true);
            box.SubscribeEvents(50);

            CollisionDesc? found = null;
            bool reachedBSScene = false;
            for (int ii = 0; ii < 40 && found == null; ii++)
            {
                foreach (CollisionDesc c in Frame(scene))
                {
                    if (IsPair(c, 0, 401))
                        found = c;
                }
                if (found != null)
                    reachedBSScene = scene.PhysObjects[401].CollisionsLastTick.m_objCollisionList.ContainsKey(BSScene.TERRAIN_ID);
            }

            Assert.NotNull(found);
            // BulletSim stores the lower ID first, and the normal points from A to B: terrain to box, up.
            Assert.Equal(0u, found!.Value.aID);
            Assert.Equal(401u, found.Value.bID);
            Assert.True(found.Value.normal.Z > 0.9f, $"normal {found.Value.normal}");
            // BulletSim's penetration is the signed distance: about 0 at rest, negative when sunk in.
            Assert.InRange(found.Value.penetration, -0.1f, 0.1f);
            Assert.InRange(found.Value.point.Z, TerrainHeight - 0.1f, TerrainHeight + 0.4f);
            Assert.InRange(found.Value.point.X, 127f, 129f);
            // BSScene sends the pair both ways, and the box hears it under the terrain's ID.
            Assert.True(reachedBSScene, "BSScene did not deliver the terrain collision to the box");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Collisions_SleepingRestingBody_ReportsEveryFrame()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor box = AddBox(scene, 405, new Vector3(128f, 128f, 21.4f), physical: true);
            box.SubscribeEvents(50);
            Frame(scene);   // the body is made on the first step
            BSAPIRapier.BulletBodyRapier body = (BSAPIRapier.BulletBodyRapier)scene.PhysObjects[405].PhysBody;

            // Until Rapier puts it to sleep.
            float elapsed = 0f;
            while (elapsed < 20f && RapierNative.r3RigidBody_IsSleeping(body.Body) == 0)
            {
                Frame(scene);
                elapsed += RapierTestScene.FrameSeconds;
            }
            Assert.True(RapierNative.r3RigidBody_IsSleeping(body.Body) != 0, "the box never went to sleep");

            // Bullet reports a sleeping manifold every frame, and scripts rely on it to avoid a collision_end.
            int frames = (int)MathF.Ceiling(2f / RapierTestScene.FrameSeconds) + 1;
            for (int ii = 0; ii < frames; ii++)
            {
                List<CollisionDesc> pairs = Frame(scene);
                Assert.True(RapierNative.r3RigidBody_IsSleeping(body.Body) != 0, $"woke at frame {ii}");
                Assert.True(pairs.Any(c => IsPair(c, 0, 405)), $"no terrain pair at frame {ii}");
            }
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Collisions_UnsubscribedPair_NotReported()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor lower = AddBox(scene, 420, new Vector3(128f, 128f, 21.5f), physical: true);
            PhysicsActor upper = AddBox(scene, 421, new Vector3(128f, 128f, 22.1f), physical: true);

            int reported = 0;
            for (int ii = 0; ii < 30; ii++)
                reported += Frame(scene).Count;

            // They did touch (the upper box rests on the lower), but nobody listens.
            Assert.True(upper.Position.Z - lower.Position.Z > 0.45f, $"upper {upper.Position.Z} lower {lower.Position.Z}");
            Assert.True(upper.Position.Z - lower.Position.Z < 0.6f, $"upper {upper.Position.Z} lower {lower.Position.Z}");
            Assert.Equal(0, reported);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Theory]
    [InlineData(430u, 431u, 1f)]   // the lower ID underneath: A is below B, so the normal points up
    [InlineData(441u, 440u, -1f)]  // the lower ID on top: the normal points down
    public void Collisions_BoxOnBox_NormalPointsFromAToB(uint bottomId, uint topId, float expectedSignZ)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor bottom = AddBox(scene, bottomId, new Vector3(128f, 128f, 21.5f), physical: true);
            PhysicsActor top = AddBox(scene, topId, new Vector3(128f, 128f, 22.1f), physical: true);
            bottom.SubscribeEvents(50);
            top.SubscribeEvents(50);

            CollisionDesc? found = null;
            for (int ii = 0; ii < 30; ii++)
            {
                foreach (CollisionDesc c in Frame(scene))
                {
                    if (IsPair(c, bottomId, topId))
                        found = c;
                }
            }

            Assert.NotNull(found);
            Assert.Equal(Math.Min(bottomId, topId), found!.Value.aID);
            Assert.True(found.Value.normal.Z * expectedSignZ > 0.9f, $"normal {found.Value.normal}");
            Assert.InRange(found.Value.point.Z, 21.3f, 22.5f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Collisions_NoContactResponse_PassesThroughButReports()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            // A static box spanning z 25 to 27, and a box falling onto it from 30.
            AddBox(scene, 402, new Vector3(128f, 128f, 26f), physical: false, edge: 2f);
            PhysicsActor box = AddBox(scene, 401, new Vector3(128f, 128f, 30f), physical: true);
            box.SubscribeEvents(50);
            RapierTestScene.Step(scene, 0.1f);
            Taint(scene, 401, () => scene.PE.AddToCollisionFlags(scene.PhysObjects[401].PhysBody, CollisionFlags.CF_NO_CONTACT_RESPONSE));

            bool reported = false;
            float lowest = float.MaxValue;
            for (int ii = 0; ii < 12; ii++)
            {
                foreach (CollisionDesc c in Frame(scene))
                {
                    if (IsPair(c, 401, 402))
                        reported = true;
                }
                lowest = Math.Min(lowest, box.Position.Z);
            }

            // Solid, the box would rest on top at 27.25. It went through the middle of the static box and has not reached the terrain.
            Assert.True(lowest < 26f, $"lowest z {lowest}");
            Assert.True(box.Position.Z > TerrainHeight + 0.25f, $"z {box.Position.Z}");
            Assert.True(reported, "no pair with the static box was reported");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void VolumeDetect_SensorReportsOverlap_NoForce()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor sensor = AddBox(scene, 400, new Vector3(128f, 128f, 26f), physical: false, edge: 2f);
            sensor.SetVolumeDetect(1);
            sensor.SubscribeEvents(50);
            PhysicsActor box = AddBox(scene, 401, new Vector3(128f, 128f, 30f), physical: true);
            RapierTestScene.Step(scene, 0.1f);

            BulletBody boxBody = scene.PhysObjects[401].PhysBody;
            float previousVz = scene.PE.GetLinearVelocity(boxBody).Z;
            bool reported = false;
            bool reachedBSScene = false;
            int framesInside = 0;
            for (int ii = 0; ii < 12; ii++)
            {
                List<CollisionDesc> pairs = Frame(scene);
                CollisionDesc? pair = pairs.Where(c => IsPair(c, 400, 401)).Select(c => (CollisionDesc?)c).FirstOrDefault();
                Vector3 v = scene.PE.GetLinearVelocity(boxBody);
                if (pair != null)
                {
                    reported = true;
                    framesInside++;
                    Assert.Equal(0f, pair.Value.penetration);
                    reachedBSScene |= scene.PhysObjects[400].CollisionsLastTick.m_objCollisionList.ContainsKey(401);
                }
                // Free fall: the vertical speed only grows, and nothing pushes sideways.
                Assert.True(v.Z <= previousVz + 0.001f, $"vz went from {previousVz} to {v.Z} at frame {ii}");
                Assert.InRange(v.X, -0.01f, 0.01f);
                Assert.InRange(v.Y, -0.01f, 0.01f);
                previousVz = v.Z;
            }

            Assert.True(reported, "the sensor reported nothing");
            Assert.True(framesInside >= 2, $"reported in {framesInside} frames");
            Assert.True(reachedBSScene, "BSScene did not deliver the volume-detect collision");
            Assert.True(box.Position.Z > TerrainHeight + 0.25f && box.Position.Z < 26f, $"z {box.Position.Z}");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void RemoveTouchingSubscribedPrim_NextStepIsClean()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor lower = AddBox(scene, 410, new Vector3(128f, 128f, 21.5f), physical: true);
            PhysicsActor upper = AddBox(scene, 411, new Vector3(128f, 128f, 22.1f), physical: true);
            lower.SubscribeEvents(50);
            upper.SubscribeEvents(50);

            bool touching = false;
            for (int ii = 0; ii < 30; ii++)
            {
                foreach (CollisionDesc c in Frame(scene))
                    touching |= IsPair(c, 410, 411);
            }
            Assert.True(touching, "the boxes never reported touching");

            scene.RemovePrim(upper);
            for (int ii = 0; ii < 12; ii++)
            {
                foreach (CollisionDesc c in Frame(scene))
                    Assert.False(c.aID == 411 || c.bID == 411, $"pair with the removed prim: {c.aID},{c.bID}");
            }
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Collisions_MoreThanArray_Truncates()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "MaxCollisionsPerFrame", "100" } });
        try
        {
            SetFlatTerrain(scene);
            Assert.Equal(100, scene.m_collisionArray.Length);
            uint id = 600;
            for (int ix = 0; ix < 20; ix++)
            {
                for (int iy = 0; iy < 20; iy++)
                {
                    PhysicsActor box = AddBox(scene, id++, new Vector3(100f + ix * 0.6f, 100f + iy * 0.6f, 21.3f), physical: true);
                    box.SubscribeEvents(50);
                }
            }

            int most = 0;
            for (int ii = 0; ii < 30; ii++)
            {
                int count = Frame(scene).Count;
                Assert.True(count <= 100, $"{count} pairs for an array of 100");
                most = Math.Max(most, count);
            }
            Assert.Equal(100, most);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }
}
