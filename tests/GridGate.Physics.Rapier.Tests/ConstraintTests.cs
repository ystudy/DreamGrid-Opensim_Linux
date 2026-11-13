using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace GridGate.Physics.Rapier.Tests;

/// <summary>
/// 6-DOF constraints, axis locks and springs (H5b). They are driven through the BulletS classes that call the engine
/// (BSActorLockAxis by way of the prim's axis-lock extension, BSConstraint6Dof, BSConstraintSpring) rather than through
/// the engine members directly, so the frames, limits and the order of calls are the ones BulletS makes.
/// </summary>
public class ConstraintTests
{
    private const float Edge = 0.5f;
    // High enough that nothing lands during a test.
    private static readonly Vector3 Sky = new Vector3(128f, 128f, 200f);

    private static PhysicsActor AddBox(BSScene scene, uint localId, Vector3 position, bool physical)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        Vector3 size = new Vector3(Edge, Edge, Edge);
        pbs.Scale = size;
        return scene.AddPrimShape("box" + localId, pbs, position, size, Quaternion.Identity, physical, localId);
    }

    private static BSAPIRapier.BulletBodyRapier BodyOf(BSScene scene, uint id)
    {
        return (BSAPIRapier.BulletBodyRapier)scene.PhysObjects[id].PhysBody;
    }

    private static float MassOf(BSAPIRapier.BulletBodyRapier body)
    {
        return RapierNative.r3RigidBody_Mass(body.Body);
    }

    private static Vector3 Position(BSAPIRapier.BulletBodyRapier body)
    {
        return BSAPIRapier.ToVector3(RapierNative.r3RigidBody_Translation(body.Body));
    }

    private static bool JointEnabled(BSConstraint constraint)
    {
        BSAPIRapier.BulletConstraintRapier joint = (BSAPIRapier.BulletConstraintRapier)constraint.Constraint;
        return RapierNative.r3ImpulseJoint_Desc(joint.Joint).enabled != 0;
    }

    [Fact]
    public void LockAxis_XTranslationLocked()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        PhysicsActor prim = AddBox(scene, 1, Sky, true);
        RapierTestScene.Step(scene, 0.3f);

        // physAxisLock-style locking: the prim's extension sets the flags and BSActorLockAxis builds the constraint to the
        // world before the next step. A script unlocks everything first: BSPrim starts the limits of the axes it was not
        // told about at 0 to 0, which Bullet reads as locked.
        object result = prim.Extension(ExtendedPhysics.PhysFunctAxisLockLimits, prim, null,
                    ExtendedPhysics.PHYS_AXIS_UNLOCK, ExtendedPhysics.PHYS_AXIS_LOCK_LINEAR_X);
        Assert.NotNull(result);
        RapierTestScene.Step(scene, 0.3f);

        BSAPIRapier.BulletBodyRapier body = BodyOf(scene, 1);
        RapierWorldState state = ((BSAPIRapier.BulletWorldRapier)scene.World).State;
        Assert.Single(state.Joints);
        Assert.True(state.HasWorldAnchor);
        float mass = MassOf(body);
        Vector3 start = Position(body);

        // 5 m/s along X, which the lock must refuse.
        scene.PE.ApplyCentralImpulse(body, new Vector3(5f * mass, 0f, 0f));
        RapierTestScene.Step(scene, 1f);
        Assert.True(MathF.Abs(Position(body).X - start.X) < 0.001f, "X moved by " + (Position(body).X - start.X));

        // The other horizontal axis is free.
        scene.PE.ApplyCentralImpulse(body, new Vector3(0f, 5f * mass, 0f));
        RapierTestScene.Step(scene, 1f);
        Assert.True(Position(body).Y - start.Y > 1f, "Y moved by " + (Position(body).Y - start.Y));
        Assert.True(MathF.Abs(Position(body).X - start.X) < 0.001f);

        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Spring_ReturnsToEquilibrium()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        Vector3 anchorAt = Sky;
        Vector3 bobAt = Sky + new Vector3(3f, 0f, 0f);
        AddBox(scene, 1, anchorAt, false);
        AddBox(scene, 2, bobAt, true);
        RapierTestScene.Step(scene, 0.3f);

        BSAPIRapier.BulletBodyRapier anchor = BodyOf(scene, 1);
        BSAPIRapier.BulletBodyRapier bob = BodyOf(scene, 2);
        // A known mass, and no gravity so the equilibrium is the one asked for.
        scene.PE.SetMassProps(bob, 1f, new Vector3(0.1f, 0.1f, 0.1f));
        scene.PE.SetGravity(bob, Vector3.Zero);

        // The anchor point is where the bob starts: 3 m along the anchor's X. Equilibrium 0 is that point.
        BSConstraintSpring spring = new BSConstraintSpring(scene.World, anchor, bob,
                    new Vector3(3f, 0f, 0f), Quaternion.Identity, Vector3.Zero, Quaternion.Identity, true, true);
        // Free along X (Bullet's convention: low below high is limited, here by a wide range), locked elsewhere.
        Assert.True(spring.SetLinearLimits(new Vector3(-10f, 0f, 0f), new Vector3(10f, 0f, 0f)));
        Assert.True(spring.SetAngularLimits(Vector3.Zero, Vector3.Zero));
        spring.SetEquilibriumPoint(Vector3.Zero, Vector3.Zero);
        spring.SetAxisEnable(0, true);
        spring.SetStiffness(0, 50f);
        spring.SetDamping(0, 10f);

        // Pull it 1 m off.
        scene.PE.SetTranslation(bob, bobAt + new Vector3(1f, 0f, 0f), Quaternion.Identity);
        scene.PE.Activate(bob, true);
        Assert.True(MathF.Abs(Position(bob).X - bobAt.X) > 0.9f);

        RapierTestScene.Step(scene, 5f);

        Assert.True(MathF.Abs(Position(bob).X - bobAt.X) < 0.1f, "bob is " + (Position(bob).X - bobAt.X) + " m from equilibrium");
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Fixed_TwoBodiesStayTogether()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        AddBox(scene, 1, Sky, true);
        AddBox(scene, 2, Sky + new Vector3(2f, 0f, 0f), true);
        RapierTestScene.Step(scene, 0.3f);

        BSAPIRapier.BulletBodyRapier a = BodyOf(scene, 1);
        BSAPIRapier.BulletBodyRapier b = BodyOf(scene, 2);
        Vector3 offset = Position(b) - Position(a);

        // The join point is half way between them; every axis locked.
        BSConstraint6Dof joint = new BSConstraint6Dof(scene.World, a, b,
                    new Vector3(1f, 0f, 0f), Quaternion.Identity, new Vector3(-1f, 0f, 0f), Quaternion.Identity, true, true);
        Assert.True(joint.SetLinearLimits(Vector3.Zero, Vector3.Zero));
        Assert.True(joint.SetAngularLimits(Vector3.Zero, Vector3.Zero));

        // They fall, and are knocked and spun while they do.
        scene.PE.ApplyCentralImpulse(a, new Vector3(0f, 3f * MassOf(a), 0f));
        scene.PE.ApplyTorqueImpulse(a, new Vector3(0f, 0f, 2f * MassOf(a)));
        for (int ii = 0; ii < 11; ii++)
        {
            scene.Simulate(RapierTestScene.FrameSeconds);
            Vector3 now = Position(b) - Position(a);
            // The offset turns with the pair, so its length is what has to stay.
            Assert.True(MathF.Abs(now.Length() - offset.Length()) < 0.01f, "offset length " + now.Length() + " at frame " + ii);
        }
        Assert.True(Position(a).Z < Sky.Z - 1f, "the pair did not fall");
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Breaking_ImpulseThreshold_Breaks()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        AddBox(scene, 1, Sky, true);
        AddBox(scene, 2, Sky + new Vector3(2f, 0f, 0f), true);
        RapierTestScene.Step(scene, 0.3f);

        BSAPIRapier.BulletBodyRapier a = BodyOf(scene, 1);
        BSAPIRapier.BulletBodyRapier b = BodyOf(scene, 2);
        BSConstraint6Dof joint = new BSConstraint6Dof(scene.World, a, b,
                    new Vector3(1f, 0f, 0f), Quaternion.Identity, new Vector3(-1f, 0f, 0f), Quaternion.Identity, true, true);
        joint.SetLinearLimits(Vector3.Zero, Vector3.Zero);
        joint.SetAngularLimits(Vector3.Zero, Vector3.Zero);
        Assert.True(joint.SetBreakingImpulseThreshold(1f));

        // Falling together asks nothing of the joint.
        RapierTestScene.Step(scene, 0.5f);
        Assert.True(JointEnabled(joint));

        scene.PE.ApplyCentralImpulse(a, new Vector3(0f, 100f, 0f));
        RapierTestScene.Step(scene, 0.3f);
        Assert.False(JointEnabled(joint));
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Breaking_BelowThreshold_Holds()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        AddBox(scene, 1, Sky, true);
        AddBox(scene, 2, Sky + new Vector3(2f, 0f, 0f), true);
        RapierTestScene.Step(scene, 0.3f);

        BSAPIRapier.BulletBodyRapier a = BodyOf(scene, 1);
        BSAPIRapier.BulletBodyRapier b = BodyOf(scene, 2);
        BSConstraint6Dof joint = new BSConstraint6Dof(scene.World, a, b,
                    new Vector3(1f, 0f, 0f), Quaternion.Identity, new Vector3(-1f, 0f, 0f), Quaternion.Identity, true, true);
        joint.SetLinearLimits(Vector3.Zero, Vector3.Zero);
        joint.SetAngularLimits(Vector3.Zero, Vector3.Zero);
        joint.SetBreakingImpulseThreshold(1e9f);

        scene.PE.ApplyCentralImpulse(a, new Vector3(0f, 100f, 0f));
        RapierTestScene.Step(scene, 0.5f);
        Assert.True(JointEnabled(joint));
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void WorldAnchor_IsNeverReportedAndIsMadeOnce()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        AddBox(scene, 1, Sky, true);
        AddBox(scene, 2, Sky + new Vector3(3f, 0f, 0f), true);
        RapierTestScene.Step(scene, 0.3f);
        RapierWorldState state = ((BSAPIRapier.BulletWorldRapier)scene.World).State;
        Assert.False(state.HasWorldAnchor);

        BSConstraint6Dof first = new BSConstraint6Dof(scene.World, BodyOf(scene, 1), Vector3.Zero, Quaternion.Identity, false, true);
        first.SetLinearLimits(Vector3.Zero, Vector3.Zero);
        first.SetAngularLimits(Vector3.Zero, Vector3.Zero);
        RapierNative.RigidBodyHandle anchor = state.WorldAnchor;
        BSConstraint6Dof second = new BSConstraint6Dof(scene.World, BodyOf(scene, 2), Vector3.Zero, Quaternion.Identity, false, true);
        Assert.Equal(anchor, state.WorldAnchor);
        Assert.Equal(2, state.Joints.Count);

        // Pinned in the sky, nothing moves, so there is nothing to report; and the anchor never is.
        for (int ii = 0; ii < 12; ii++)
        {
            scene.Simulate(RapierTestScene.FrameSeconds);
            foreach (EntityProperties update in scene.m_updateArray)
            {
                if (update.ID == 0)
                    break;
                Assert.True(update.ID == 1 || update.ID == 2, "update for " + update.ID);
            }
            Assert.Equal(0, state.LastCollisionCount);
        }
        Assert.Equal(RapierNative.BodyFixed, RapierNative.r3RigidBody_BodyType(anchor));
        Assert.Empty(log.Errors);

        // Shutting the world down leaves the constraints dead, not pointing into a freed world.
        scene.PE.Shutdown(scene.World);
        Assert.False(first.Constraint.HasPhysicalConstraint);
        Assert.False(second.Constraint.HasPhysicalConstraint);
        Assert.False(state.HasWorldAnchor);
    }

    [Fact]
    public void LockAxis_BodyAtRestStillSleeps()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        float[] map = new float[256 * 256];
        Array.Fill(map, 21f);
        scene.SetTerrain(map);
        PhysicsActor prim = AddBox(scene, 1, new Vector3(128f, 128f, 22f), true);
        RapierTestScene.Step(scene, 0.3f);
        prim.Extension(ExtendedPhysics.PhysFunctAxisLockLimits, prim, null,
                    ExtendedPhysics.PHYS_AXIS_UNLOCK, ExtendedPhysics.PHYS_AXIS_LOCK_LINEAR_X);

        // Rapier does not join the islands of a body and a fixed one, so the joint to the anchor does not keep it awake.
        RapierTestScene.Step(scene, 8f);
        Assert.Single(((BSAPIRapier.BulletWorldRapier)scene.World).State.Joints);
        Assert.NotEqual(0u, RapierNative.r3RigidBody_IsSleeping(BodyOf(scene, 1).Body));
        Assert.Empty(log.Errors);
    }

    // A CFM set before the world has ever stepped (the first frame's taints) is worked out for the step the scene will
    // take: a near-rigid CFM is capped at a quarter of the step rate, 12.5 Hz at Rapier's 0.02 s, not 1/60's 15 Hz.
    [Fact]
    public void Softness_BeforeTheFirstStep_UsesTheSceneStep()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            Assert.Equal(0f, ((BSAPIRapier.BulletWorldRapier)scene.World).State.TimeStep);
            ShapeData data = new ShapeData { Type = BSPhysicsShapeType.SHAPE_BOX, Scale = Vector3.One };
            BulletBody a = scene.PE.CreateBodyFromShape(scene.World, scene.PE.BuildNativeShape(scene.World, data), 9101, Sky, Quaternion.Identity);
            BulletBody b = scene.PE.CreateBodyFromShape(scene.World, scene.PE.BuildNativeShape(scene.World, data), 9102,
                        Sky + new Vector3(2f, 0f, 0f), Quaternion.Identity);
            scene.PE.SetMassProps(a, 1f, Vector3.One);
            scene.PE.SetMassProps(b, 1f, Vector3.One);

            BSConstraint6Dof joint = new BSConstraint6Dof(scene.World, a, b, Sky + new Vector3(1f, 0f, 0f), true, true);
            Assert.True(joint.SetCFMAndERP(1e-6f, 1f));
            RapierNative.JointDesc soft = RapierNative.r3ImpulseJoint_Desc(((BSAPIRapier.BulletConstraintRapier)joint.Constraint).Joint);
            Assert.Equal(0.25f / scene.m_fixedTimeStep, soft.softness.naturalFrequency, 3);
            Assert.Equal(12.5f, soft.softness.naturalFrequency, 3);
            joint.Dispose();
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void ToPointJoint_ErpCfmSoftness_AndSolverIterations()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        AddBox(scene, 1, Sky, true);
        AddBox(scene, 2, Sky + new Vector3(2f, 0f, 0f), true);
        RapierTestScene.Step(scene, 0.3f);
        BSAPIRapier.BulletBodyRapier a = BodyOf(scene, 1);
        BSAPIRapier.BulletBodyRapier b = BodyOf(scene, 2);

        // The linkset-constraint sequence: join at a point between them, limits, then CFM, ERP and solver iterations.
        BSConstraint6Dof joint = new BSConstraint6Dof(scene.World, a, b, Sky + new Vector3(1f, 0f, 0f), true, true);
        Assert.True(joint.Constraint.HasPhysicalConstraint);
        joint.SetLinearLimits(Vector3.Zero, Vector3.Zero);
        joint.SetAngularLimits(Vector3.Zero, Vector3.Zero);
        RapierNative.JointDesc rigid = RapierNative.r3ImpulseJoint_Desc(((BSAPIRapier.BulletConstraintRapier)joint.Constraint).Joint);
        Assert.True(joint.SetCFMAndERP(0.1f, 0.2f));
        Assert.True(joint.SetSolverIterations(40f));
        Assert.True(joint.RecomputeConstraintVariables(MassOf(a)));

        RapierNative.JointDesc soft = RapierNative.r3ImpulseJoint_Desc(((BSAPIRapier.BulletConstraintRapier)joint.Constraint).Joint);
        Assert.True(soft.softness.naturalFrequency > 0f && soft.softness.naturalFrequency < rigid.softness.naturalFrequency);
        Assert.True(soft.softness.dampingRatio > 0f);
        // The frames meet at the join point: 1 m along A's X, 1 m back along B's.
        Assert.Equal(1f, soft.localFrame1.translation.x, 3);
        Assert.Equal(-1f, soft.localFrame2.translation.x, 3);

        // A CFM of zero is rigid again.
        joint.SetCFMAndERP(0f, 0.2f);
        RapierNative.JointDesc hard = RapierNative.r3ImpulseJoint_Desc(((BSAPIRapier.BulletConstraintRapier)joint.Constraint).Joint);
        Assert.Equal(rigid.softness.naturalFrequency, hard.softness.naturalFrequency);

        RapierTestScene.Step(scene, 1f);
        Assert.True(MathF.Abs((Position(b) - Position(a)).Length() - 2f) < 0.2f);
        joint.Dispose();
        RapierTestScene.Step(scene, 0.3f);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void DestroyedBody_ConstraintCallsAreSafe()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        AddBox(scene, 1, Sky, true);
        AddBox(scene, 2, Sky + new Vector3(2f, 0f, 0f), true);
        RapierTestScene.Step(scene, 0.3f);

        BSAPIRapier.BulletBodyRapier a = BodyOf(scene, 1);
        BSAPIRapier.BulletBodyRapier b = BodyOf(scene, 2);
        BSConstraint6Dof joint = new BSConstraint6Dof(scene.World, a, b,
                    new Vector3(1f, 0f, 0f), Quaternion.Identity, new Vector3(-1f, 0f, 0f), Quaternion.Identity, true, true);
        joint.SetBreakingImpulseThreshold(1f);

        // The body goes, and Rapier takes its joints with it.
        scene.PE.DestroyObject(scene.World, a);
        Assert.False(joint.Constraint.HasPhysicalConstraint);

        // Nothing here may throw or log an error; BulletS can still hold the constraint.
        joint.SetLinearLimits(Vector3.Zero, Vector3.Zero);
        joint.SetAngularLimits(Vector3.Zero, Vector3.Zero);
        joint.SetFrames(Vector3.Zero, Quaternion.Identity, Vector3.Zero, Quaternion.Identity);
        joint.SetBreakingImpulseThreshold(2f);
        joint.TranslationalLimitMotor(true, 1f, 1f);
        joint.SetCFMAndERP(0.1f, 0.2f);
        joint.UseFrameOffset(true);
        joint.Dispose();
        RapierTestScene.Step(scene, 0.5f);
        Assert.Empty(log.Errors);
    }
}
