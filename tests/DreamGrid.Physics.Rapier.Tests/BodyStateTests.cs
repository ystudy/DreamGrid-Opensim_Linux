using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

/// <summary>Activation states, filter groups, materials, damping, factors, CCD and forces (H3, H6, H7, H11).</summary>
public class BodyStateTests
{
    private const float TerrainHeight = 21f;

    private static PhysicsActor AddBox(BSScene scene, uint localId, Vector3 position, bool physical, float edge = 0.5f)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        Vector3 size = new Vector3(edge, edge, edge);
        pbs.Scale = size;
        return scene.AddPrimShape("box" + localId, pbs, position, size, Quaternion.Identity, physical, localId);
    }

    private static void SetTerrain(BSScene scene, Func<int, int, float> height)
    {
        float[] map = new float[256 * 256];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                map[y * 256 + x] = height(x, y);
        scene.SetTerrain(map);
        RapierTestScene.Step(scene, 0.3f);
    }

    private static void SetFlatTerrain(BSScene scene)
    {
        SetTerrain(scene, (x, y) => TerrainHeight);
    }

    private static BSAPIRapier.BulletBodyRapier BodyOf(BSScene scene, uint id)
    {
        return (BSAPIRapier.BulletBodyRapier)scene.PhysObjects[id].PhysBody;
    }

    private static bool Sleeping(BSAPIRapier.BulletBodyRapier body)
    {
        return RapierNative.r3RigidBody_IsSleeping(body.Body) != 0;
    }

    private static List<CollisionDesc> Frame(BSScene scene, out List<uint> updated)
    {
        scene.Simulate(RapierTestScene.FrameSeconds);
        int count = ((BSAPIRapier.BulletWorldRapier)scene.World).State.LastCollisionCount;
        List<CollisionDesc> pairs = new List<CollisionDesc>();
        for (int ii = 0; ii < count; ii++)
            pairs.Add(scene.m_collisionArray[ii]);
        updated = new List<uint>();
        foreach (EntityProperties e in scene.m_updateArray)
        {
            if (e.ID == 0)
                break;
            updated.Add(e.ID);
        }
        return pairs;
    }

    private static bool Involves(CollisionDesc c, uint id)
    {
        return c.aID == id || c.bID == id;
    }

    /// <summary>Steps the engine directly with variable-length steps of exactly this long (no accumulation).</summary>
    private static void EngineStep(BSScene scene, float seconds, int times)
    {
        for (int ii = 0; ii < times; ii++)
            scene.PE.PhysicsStep(scene.World, seconds, 0, seconds, out int _, out int _);
    }

    [Fact]
    public void DisableDeactivation_BodyNeverSleeps()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            AddBox(scene, 501, new Vector3(120f, 128f, 21.4f), physical: true);
            AddBox(scene, 502, new Vector3(136f, 128f, 21.4f), physical: true);
            RapierTestScene.Step(scene, 0.3f);
            scene.PE.ForceActivationState(BodyOf(scene, 501), ActivationState.DISABLE_DEACTIVATION);

            // Without the state the control box goes to sleep; with it the other stays awake for 10 s.
            bool controlSlept = false;
            float elapsed = 0f;
            while (elapsed < 10f)
            {
                scene.Simulate(RapierTestScene.FrameSeconds);
                elapsed += RapierTestScene.FrameSeconds;
                controlSlept |= Sleeping(BodyOf(scene, 502));
                Assert.False(Sleeping(BodyOf(scene, 501)), $"slept at {elapsed}s");
            }
            Assert.True(controlSlept, "the control box never went to sleep, so the test shows nothing");

            // Another state ends it: the body may sleep again.
            scene.PE.ForceActivationState(BodyOf(scene, 501), ActivationState.ACTIVE_TAG);
            Assert.DoesNotContain(BodyOf(scene, 501), ((BSAPIRapier.BulletWorldRapier)scene.World).State.NeverSleep);
            RapierTestScene.Step(scene, 6f);
            Assert.True(Sleeping(BodyOf(scene, 501)));
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void DisableDeactivation_DestroyedBody_LeavesTheSet()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor box = AddBox(scene, 503, new Vector3(128f, 128f, 21.4f), physical: true);
            RapierTestScene.Step(scene, 0.3f);
            RapierWorldState state = ((BSAPIRapier.BulletWorldRapier)scene.World).State;
            scene.PE.ForceActivationState(BodyOf(scene, 503), ActivationState.DISABLE_DEACTIVATION);
            Assert.Single(state.NeverSleep);

            scene.RemovePrim(box);
            RapierTestScene.Step(scene, 0.5f);
            Assert.Empty(state.NeverSleep);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void GroupMask_ExcludedPairDoesNotCollide()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            // Control: the upper box lands on the lower one.
            PhysicsActor lowerC = AddBox(scene, 510, new Vector3(100f, 128f, 25f), physical: true);
            PhysicsActor upperC = AddBox(scene, 511, new Vector3(100f, 128f, 26f), physical: true);
            // Excluded: neither mask lets the other's group through.
            PhysicsActor lower = AddBox(scene, 512, new Vector3(150f, 128f, 25f), physical: true);
            PhysicsActor upper = AddBox(scene, 513, new Vector3(150f, 128f, 26f), physical: true);
            RapierTestScene.Step(scene, 0.1f);

            uint group = (uint)CollisionFilterGroups.BSolidGroup;
            uint mask = (uint)(CollisionFilterGroups.BAllGroup & ~CollisionFilterGroups.BSolidGroup);
            Assert.True(scene.PE.SetCollisionGroupMask(BodyOf(scene, 512), group, mask));
            Assert.True(scene.PE.SetCollisionGroupMask(BodyOf(scene, 513), group, mask));
            RapierTestScene.Step(scene, 3f);

            Assert.True(upperC.Position.Z - lowerC.Position.Z > 0.45f, $"control: upper {upperC.Position.Z} lower {lowerC.Position.Z}");
            // They fell through each other and both rest on the terrain.
            Assert.InRange(upper.Position.Z, TerrainHeight + 0.2f, TerrainHeight + 0.3f);
            Assert.InRange(lower.Position.Z, TerrainHeight + 0.2f, TerrainHeight + 0.3f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Friction_Zero_Slides_One_Stops()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { ["TerrainFriction"] = "1" });
        try
        {
            const float slope = 0.36397f;   // tan 20 degrees
            SetTerrain(scene, (x, y) => TerrainHeight + slope * x);

            // Boxes lying flat on the slope, a hair above it. The surface normal leans back toward -X.
            Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -20f * MathF.PI / 180f);
            Vector3 normal = new Vector3(-MathF.Sin(20f * MathF.PI / 180f), 0f, MathF.Cos(20f * MathF.PI / 180f));
            Vector3 PlaceOn(float y)
            {
                float x = 128f;
                return new Vector3(x, y, TerrainHeight + slope * x) + normal * 0.26f;
            }
            PhysicsActor slick = scene.AddPrimShape("slick", BoxShape(), PlaceOn(100f), new Vector3(0.5f, 0.5f, 0.5f), tilt, true, 520);
            PhysicsActor grippy = scene.AddPrimShape("grippy", BoxShape(), PlaceOn(150f), new Vector3(0.5f, 0.5f, 0.5f), tilt, true, 521);
            ((BSPrim)slick).Friction = 0f;
            ((BSPrim)grippy).Friction = 1f;
            RapierTestScene.Step(scene, 0.5f);

            Vector3 slickStart = slick.Position;
            Vector3 grippyStart = grippy.Position;
            RapierTestScene.Step(scene, 2f);

            float slid = (slick.Position - slickStart).Length();
            float held = (grippy.Position - grippyStart).Length();
            Assert.True(slid > 2f, $"friction 0 slid {slid} m");
            Assert.True(held < 0.1f, $"friction 1 slid {held} m");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    private static PrimitiveBaseShape BoxShape()
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.Scale = new Vector3(0.5f, 0.5f, 0.5f);
        return pbs;
    }

    [Fact]
    public void LinearDamping_MatchesBulletDecay()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 530, new Vector3(128f, 128f, 100f), physical: true);
            RapierTestScene.Step(scene, 0.2f);
            BulletBody body = BodyOf(scene, 530);

            scene.PE.SetGravity(body, Vector3.Zero);
            scene.PE.SetDamping(body, 0.5f, 0f);
            scene.PE.SetLinearVelocity(body, new Vector3(10f, 0f, 0f));
            // One second in 60 steps of 1/60 s, as the engine will step it.
            EngineStep(scene, 1f / 60f, 60);

            // Bullet: v * (1 - 0.5)^1.
            float vx = scene.PE.GetLinearVelocity(body).X;
            Assert.InRange(vx, 5f * 0.97f, 5f * 1.03f);
            // No damping of the other kind, and none on the angular side.
            Assert.Equal(0f, scene.PE.GetLinearVelocity(body).Z, 3);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void AngularFactorZero_DoesNotRotate()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 540, new Vector3(100f, 128f, 100f), physical: true);
            AddBox(scene, 541, new Vector3(110f, 128f, 100f), physical: true);
            RapierTestScene.Step(scene, 0.2f);
            BulletBody locked = BodyOf(scene, 540);
            BulletBody free = BodyOf(scene, 541);
            scene.PE.SetGravity(locked, Vector3.Zero);
            scene.PE.SetGravity(free, Vector3.Zero);
            scene.PE.SetAngularFactor(locked, 0f);
            Quaternion before = scene.PE.GetOrientation(locked);

            scene.PE.ApplyTorqueImpulse(locked, new Vector3(0.3f, 0.2f, 0.4f));
            scene.PE.ApplyTorqueImpulse(free, new Vector3(0.3f, 0.2f, 0.4f));
            RapierTestScene.Step(scene, 1f);

            Assert.True(scene.PE.GetAngularVelocity(free).Length() > 0.01f, "the control box did not turn, so the torque impulse does nothing");
            Assert.Equal(0f, scene.PE.GetAngularVelocity(locked).Length(), 4);
            Quaternion after = scene.PE.GetOrientation(locked);
            Assert.Equal(before.X, after.X, 4);
            Assert.Equal(before.Y, after.Y, 4);
            Assert.Equal(before.Z, after.Z, 4);
            Assert.Equal(before.W, after.W, 4);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void LinearFactor_LocksAxes()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 545, new Vector3(100f, 128f, 100f), physical: true);
            RapierTestScene.Step(scene, 0.2f);
            BulletBody body = BodyOf(scene, 545);
            scene.PE.SetLinearFactor(body, new Vector3(1f, 0f, 1f));
            scene.PE.SetGravity(body, Vector3.Zero);
            scene.PE.SetLinearVelocity(body, Vector3.Zero);
            Vector3 start = scene.PE.GetPosition(body);

            // An impulse along X and Y moves the body along X only. (A velocity set directly is not filtered, in Rapier
            // as in Bullet: the factor limits what forces and impulses do.)
            scene.PE.ApplyCentralImpulse(body, new Vector3(100f, 100f, 0f));
            RapierTestScene.Step(scene, 0.5f);

            Vector3 moved = scene.PE.GetPosition(body) - start;
            Assert.True(moved.X > 0.05f, $"moved {moved}");
            Assert.Equal(0f, moved.Y, 3);
            Assert.Equal(new Vector3(1f, 0f, 1f), scene.PE.GetLinearFactor(body));
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void FractionalFactor_IsLoggedOncePerBody()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 546, new Vector3(100f, 128f, 100f), physical: true);
            AddBox(scene, 547, new Vector3(110f, 128f, 100f), physical: true);
            RapierTestScene.Step(scene, 0.2f);
            HashSet<string> logged = ((BSAPIRapier.BulletWorldRapier)scene.World).State.LoggedOnce;
            int before = logged.Count(k => k.StartsWith("factor:"));

            scene.PE.SetLinearFactor(BodyOf(scene, 546), new Vector3(0.5f, 1f, 1f));
            scene.PE.SetLinearFactor(BodyOf(scene, 546), new Vector3(0.25f, 1f, 1f));
            scene.PE.SetAngularFactor(BodyOf(scene, 546), 0.5f);
            Assert.Equal(before + 1, logged.Count(k => k.StartsWith("factor:")));
            scene.PE.SetAngularFactorV(BodyOf(scene, 547), new Vector3(1f, 0.5f, 1f));
            Assert.Equal(before + 2, logged.Count(k => k.StartsWith("factor:")));

            // Exactly 0 and 1 are not gaps.
            scene.PE.SetAngularFactorV(BodyOf(scene, 546), new Vector3(0f, 1f, 0f));
            Assert.Equal(before + 2, logged.Count(k => k.StartsWith("factor:")));
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Buoyancy_GravityScale()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 550, new Vector3(100f, 128f, 200f), physical: true);
            AddBox(scene, 551, new Vector3(110f, 128f, 200f), physical: true);
            RapierTestScene.Step(scene, 0.1f);
            BulletBody floaty = BodyOf(scene, 550);
            BulletBody normal = BodyOf(scene, 551);
            float vz0 = scene.PE.GetLinearVelocity(normal).Z;
            Assert.Equal(scene.PE.GetLinearVelocity(floaty).Z, vz0, 3);

            scene.PE.SetGravity(floaty, new Vector3(0f, 0f, -4.9f));
            // Compare with the box in full gravity, so the step quantisation cancels out.
            RapierTestScene.Step(scene, 1f);
            float dvFloaty = scene.PE.GetLinearVelocity(floaty).Z - vz0;
            float dvNormal = scene.PE.GetLinearVelocity(normal).Z - vz0;
            Assert.True(dvNormal < -8f, $"the control box gained {dvNormal}");
            Assert.InRange(dvFloaty / dvNormal, 0.49f, 0.51f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Forces_ClearedAfterStep()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 560, new Vector3(128f, 128f, 100f), physical: true);
            RapierTestScene.Step(scene, 0.2f);
            BulletBody body = BodyOf(scene, 560);
            scene.PE.SetGravity(body, Vector3.Zero);
            scene.PE.SetLinearVelocity(body, Vector3.Zero);
            scene.PE.SetAngularVelocity(body, Vector3.Zero);

            scene.PE.ApplyCentralForce(body, new Vector3(50f, 0f, 0f));
            scene.PE.ApplyTorque(body, new Vector3(0f, 0f, 5f));
            EngineStep(scene, 0.1f, 1);
            Vector3 v1 = scene.PE.GetLinearVelocity(body);
            Vector3 w1 = scene.PE.GetAngularVelocity(body);
            EngineStep(scene, 0.1f, 1);
            Vector3 v2 = scene.PE.GetLinearVelocity(body);
            Vector3 w2 = scene.PE.GetAngularVelocity(body);

            Assert.True(v1.X > 0.01f, $"the force did nothing: {v1}");
            Assert.True(w1.Z > 0.001f, $"the torque did nothing: {w1}");
            Assert.Equal(v1.X, v2.X, 4);
            Assert.Equal(w1.Z, w2.Z, 4);

            // A frame with no whole step in it clears the forces too (Bullet's stepSimulation does).
            scene.PE.ApplyCentralForce(body, new Vector3(50f, 0f, 0f));
            scene.PE.PhysicsStep(scene.World, 0.001f, 1, 1f / 60f, out int _, out int _);
            scene.PE.PhysicsStep(scene.World, 0.1f, 1, 1f / 60f, out int _, out int _);
            Assert.Equal(v1.X, scene.PE.GetLinearVelocity(body).X, 3);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Impulse_ChangesVelocityAtOnce()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 565, new Vector3(128f, 128f, 100f), physical: true);
            RapierTestScene.Step(scene, 0.2f);
            BulletBody body = BodyOf(scene, 565);
            scene.PE.SetGravity(body, Vector3.Zero);
            scene.PE.SetLinearVelocity(body, Vector3.Zero);
            scene.PE.ApplyCentralImpulse(body, new Vector3(0f, 2f, 0f));
            Assert.True(scene.PE.GetLinearVelocity(body).Y > 0.01f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // A force added and then the body taken out (RemoveObjectFromWorld) or switched off (DISABLE_SIMULATION) before the
    // step must not stay on the native body for when it comes back: Bullet drops it at the end of the next step.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Forces_DroppedWhenBodyLeavesTheStep(bool disableSimulation)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 566, new Vector3(128f, 128f, 100f), physical: true);
            RapierTestScene.Step(scene, 0.2f);
            BSAPIRapier.BulletBodyRapier body = BodyOf(scene, 566);
            scene.PE.SetGravity(body, Vector3.Zero);
            scene.PE.SetLinearVelocity(body, Vector3.Zero);
            scene.PE.SetAngularVelocity(body, Vector3.Zero);

            scene.PE.ApplyCentralForce(body, new Vector3(50f, 0f, 0f));
            scene.PE.ApplyTorque(body, new Vector3(0f, 0f, 5f));
            if (disableSimulation)
                scene.PE.ForceActivationState(body, ActivationState.DISABLE_SIMULATION);
            else
                scene.PE.RemoveObjectFromWorld(scene.World, body);
            EngineStep(scene, 0.1f, 2);
            if (disableSimulation)
                scene.PE.ForceActivationState(body, ActivationState.ACTIVE_TAG);
            else
                scene.PE.AddObjectToWorld(scene.World, body);

            Assert.Equal(Vector3.Zero, BSAPIRapier.ToVector3(RapierNative.r3RigidBody_UserForce(body.Body)));
            Assert.Equal(Vector3.Zero, BSAPIRapier.ToVector3(RapierNative.r3RigidBody_UserTorque(body.Body)));
            EngineStep(scene, 0.1f, 3);
            Assert.Equal(0f, scene.PE.GetLinearVelocity(body).X, 4);
            Assert.Equal(0f, scene.PE.GetAngularVelocity(body).Z, 4);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // What BSCharacter calls: ClearForces drops what was added, SetObjectForce replaces it (map rows 107 and 118).
    [Fact]
    public void ClearForces_And_SetObjectForce()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 567, new Vector3(128f, 128f, 100f), physical: true);
            RapierTestScene.Step(scene, 0.2f);
            BSAPIRapier.BulletBodyRapier body = BodyOf(scene, 567);

            scene.PE.ApplyCentralForce(body, new Vector3(50f, 0f, 0f));
            scene.PE.ApplyTorque(body, new Vector3(0f, 0f, 5f));
            scene.PE.ClearForces(body);
            Assert.Equal(Vector3.Zero, scene.PE.GetTotalForce(body));
            Assert.Equal(Vector3.Zero, scene.PE.GetTotalTorque(body));

            scene.PE.ApplyCentralForce(body, new Vector3(50f, 0f, 0f));
            scene.PE.SetObjectForce(body, new Vector3(0f, 7f, 0f));
            Assert.Equal(new Vector3(0f, 7f, 0f), scene.PE.GetTotalForce(body));
            // It is still a force of this step only.
            EngineStep(scene, 0.1f, 1);
            Assert.Equal(Vector3.Zero, scene.PE.GetTotalForce(body));
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // Bullet's point is relative to the centre of mass (map rows 114 and 117): off centre the push also turns the body.
    [Fact]
    public void ForceAndImpulseAtPoint_TurnTheBody()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 568, new Vector3(128f, 128f, 100f), physical: true);
            RapierTestScene.Step(scene, 0.2f);
            BSAPIRapier.BulletBodyRapier body = BodyOf(scene, 568);
            scene.PE.SetGravity(body, Vector3.Zero);
            scene.PE.SetLinearVelocity(body, Vector3.Zero);
            scene.PE.SetAngularVelocity(body, Vector3.Zero);

            // +X at +Y from the centre turns the body about -Z.
            scene.PE.ApplyImpulse(body, new Vector3(1f, 0f, 0f), new Vector3(0f, 0.25f, 0f));
            Assert.True(scene.PE.GetLinearVelocity(body).X > 0.01f);
            Assert.True(scene.PE.GetAngularVelocity(body).Z < -0.01f);

            scene.PE.SetLinearVelocity(body, Vector3.Zero);
            scene.PE.SetAngularVelocity(body, Vector3.Zero);
            scene.PE.ApplyForce(body, new Vector3(0f, 10f, 0f), new Vector3(0.25f, 0f, 0f));
            Assert.Equal(new Vector3(0f, 10f, 0f), scene.PE.GetTotalForce(body));
            Assert.True(scene.PE.GetTotalTorque(body).Z > 0.1f);
            EngineStep(scene, 0.1f, 1);
            Assert.True(scene.PE.GetLinearVelocity(body).Y > 0.01f);
            Assert.True(scene.PE.GetAngularVelocity(body).Z > 0.01f);
            Assert.Equal(Vector3.Zero, scene.PE.GetTotalForce(body));
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // Rapier has no collision margins (map rows 24, 40 and 41): setting one changes nothing and reads back.
    [Fact]
    public void Margins_AreStoredOnly()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 569, new Vector3(128f, 128f, 100f), physical: true);
            RapierTestScene.Step(scene, 0.2f);
            BulletShape shape = scene.PhysObjects[569].PhysShape.physShapeInfo;
            scene.PE.SetMargin(shape, 0.04f);
            Assert.Equal(0.04f, scene.PE.GetMargin(shape));
            scene.PE.SetShapeCollisionMargin(shape, 0.02f);
            Assert.Equal(0.02f, scene.PE.GetMargin(shape));
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Ccd_ThresholdTurnsItOn_AndIsKept()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 570, new Vector3(128f, 128f, 100f), physical: true);
            RapierTestScene.Step(scene, 0.2f);
            BSAPIRapier.BulletBodyRapier body = BodyOf(scene, 570);
            Assert.Equal(0u, RapierNative.r3RigidBody_IsCcdEnabled(body.Body));

            scene.PE.SetCcdMotionThreshold(body, 0.3f);
            scene.PE.SetCcdSweptSphereRadius(body, 0.2f);
            Assert.Equal(1u, RapierNative.r3RigidBody_IsCcdEnabled(body.Body));
            Assert.Equal(0.3f, scene.PE.GetCcdMotionThreshold(body));
            Assert.Equal(0.2f, scene.PE.GetCcdSweptSphereRadius(body));

            scene.PE.SetCcdMotionThreshold(body, 0f);
            Assert.Equal(0u, RapierNative.r3RigidBody_IsCcdEnabled(body.Body));
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Ccd_OnStaticBody_IsHarmless()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { ["CcdMotionThreshold"] = "0.1" });
        try
        {
            SetFlatTerrain(scene);
            // BSPrim sets the threshold on static prims too (MakeDynamic), and on physical ones.
            AddBox(scene, 571, new Vector3(128f, 128f, 22f), physical: false);
            AddBox(scene, 572, new Vector3(140f, 128f, 22f), physical: true);
            RapierTestScene.Step(scene, 0.5f);
            Assert.Equal(1u, RapierNative.r3RigidBody_IsCcdEnabled(BodyOf(scene, 572).Body));
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void PhysicalToStaticAndBack_WhileSleeping_KeepsHandleAndWakes()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            // A box on a static support, which is later removed.
            PhysicsActor support = AddBox(scene, 580, new Vector3(128f, 128f, 21.25f), physical: false);
            PhysicsActor box = AddBox(scene, 581, new Vector3(128f, 128f, 21.8f), physical: true);
            RapierTestScene.Step(scene, 0.2f);   // the bodies are made on the first step

            float elapsed = 0f;
            while (elapsed < 15f && !Sleeping(BodyOf(scene, 581)))
            {
                scene.Simulate(RapierTestScene.FrameSeconds);
                elapsed += RapierTestScene.FrameSeconds;
            }
            Assert.True(Sleeping(BodyOf(scene, 581)), "the box never went to sleep");
            Assert.InRange(box.Position.Z, 21.7f, 21.8f);

            BulletBody handle = scene.PhysObjects[581].PhysBody;
            Vector3 restAt = box.Position;

            // Physical to static, through BSPrim.MakeDynamic.
            box.IsPhysical = false;
            RapierTestScene.Step(scene, 1f);
            Assert.Same(handle, scene.PhysObjects[581].PhysBody);
            Assert.True(handle.HasPhysicalBody);
            Assert.Equal(RapierNative.BodyFixed, RapierNative.r3RigidBody_BodyType(((BSAPIRapier.BulletBodyRapier)handle).Body));
            Assert.Equal(restAt.Z, box.Position.Z, 3);
            Assert.Equal(Vector3.Zero, scene.PE.GetLinearVelocity(handle));

            // Back to physical, with its support removed in the meantime.
            scene.RemovePrim(support);
            RapierTestScene.Step(scene, 0.2f);
            Assert.Equal(restAt.Z, box.Position.Z, 3);   // still static
            box.IsPhysical = true;
            RapierTestScene.Step(scene, 2f);

            Assert.Same(handle, scene.PhysObjects[581].PhysBody);
            Assert.True(handle.HasPhysicalBody);
            Assert.Equal(RapierNative.BodyDynamic, RapierNative.r3RigidBody_BodyType(((BSAPIRapier.BulletBodyRapier)handle).Body));
            Assert.InRange(box.Position.Z, TerrainHeight + 0.2f, TerrainHeight + 0.3f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void DisableSimulation_DynamicBody_ReportsNoPairsAndNoUpdates()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor under = AddBox(scene, 590, new Vector3(128f, 128f, 21.4f), physical: true);
            PhysicsActor over = AddBox(scene, 591, new Vector3(128f, 128f, 21.9f), physical: true);
            under.SubscribeEvents(50);
            RapierTestScene.Step(scene, 1.5f);
            BSAPIRapier.BulletBodyRapier body = BodyOf(scene, 591);

            // They touch and the lower one listens.
            bool pairSeen = false;
            for (int ii = 0; ii < 5; ii++)
                pairSeen |= Frame(scene, out _).Any(c => c.aID == 590 && c.bID == 591);
            Assert.True(pairSeen, "the boxes were not reported touching before the test began");

            scene.PE.ForceActivationState(body, ActivationState.DISABLE_SIMULATION);
            Assert.Equal(0u, RapierNative.r3RigidBody_IsEnabled(body.Body));
            // For BulletS the body is still in the world.
            Assert.True(scene.PE.IsInWorld(scene.World, body));
            scene.PE.PushUpdate(body);

            for (int ii = 0; ii < 15; ii++)
            {
                List<CollisionDesc> pairs = Frame(scene, out List<uint> updated);
                Assert.DoesNotContain(pairs, c => Involves(c, 591));
                Assert.DoesNotContain(591u, updated);
            }

            // ACTIVE_TAG puts it back.
            scene.PE.ForceActivationState(body, ActivationState.ACTIVE_TAG);
            Assert.Equal(1u, RapierNative.r3RigidBody_IsEnabled(body.Body));
            bool pairBack = false;
            for (int ii = 0; ii < 15; ii++)
                pairBack |= Frame(scene, out _).Any(c => c.aID == 590 && c.bID == 591);
            Assert.True(pairBack, "no pair after the body was enabled again");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void DisableSimulation_FallingBody_StopsReportingAndAddObjectToWorldEnablesIt()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 595, new Vector3(128f, 128f, 200f), physical: true);
            RapierTestScene.Step(scene, 0.3f);
            BSAPIRapier.BulletBodyRapier body = BodyOf(scene, 595);
            Frame(scene, out List<uint> falling);
            Assert.Contains(595u, falling);

            scene.PE.ForceActivationState(body, ActivationState.DISABLE_SIMULATION);
            float z = scene.PE.GetPosition(body).Z;
            for (int ii = 0; ii < 6; ii++)
            {
                Frame(scene, out List<uint> updated);
                Assert.DoesNotContain(595u, updated);
            }
            Assert.Equal(z, scene.PE.GetPosition(body).Z, 3);

            // Taking it out of the world and putting it back enables it.
            scene.PE.RemoveObjectFromWorld(scene.World, body);
            Assert.False(scene.PE.IsInWorld(scene.World, body));
            scene.PE.AddObjectToWorld(scene.World, body);
            Assert.True(scene.PE.IsInWorld(scene.World, body));
            Assert.Equal(1u, RapierNative.r3RigidBody_IsEnabled(body.Body));
            RapierTestScene.Step(scene, 0.3f);
            Assert.True(scene.PE.GetPosition(body).Z < z - 0.1f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }
}
