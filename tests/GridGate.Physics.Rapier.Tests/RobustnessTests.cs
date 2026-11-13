using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace GridGate.Physics.Rapier.Tests;

/// <summary>
/// What the backend does when Rapier refuses a call: the C# side and the native side fall out of step (here forced by
/// removing a body or joint natively behind BulletS's back), or Rapier panics. One bad body must not stop the region's
/// physics, and no table may go on claiming what Rapier no longer has.
/// </summary>
public class RobustnessTests
{
    private const float Edge = 0.5f;
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

    private static RapierWorldState StateOf(BSScene scene)
    {
        return ((BSAPIRapier.BulletWorldRapier)scene.World).State;
    }

    private static float NativeZ(BSAPIRapier.BulletBodyRapier body)
    {
        return RapierNative.r3RigidBody_Translation(body.Body).z;
    }

    // Rapier loses the body; C# still has it. This is the desync the step must survive.
    private static void LoseNatively(BSAPIRapier.BulletBodyRapier body)
    {
        RapierNative.Check(RapierNative.r3RemoveRigidBody(body.Body, 1));
    }

    private static bool StepThrew(ErrorLog log)
    {
        return log.Warnings.Any(w => w.Contains("PhysicsStep Exception"));
    }

    // ---- The step ----

    [Fact]
    public void NeverSleepBody_RapierWillNotWake_IsLoggedOnceAndDropped_AndTheStepGoesOn()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 1, Sky, physical: true);
            AddBox(scene, 2, Sky + new Vector3(3f, 0f, 0f), physical: true);
            RapierTestScene.Step(scene, 0.3f);
            BSAPIRapier.BulletBodyRapier stale = BodyOf(scene, 1);
            BSAPIRapier.BulletBodyRapier healthy = BodyOf(scene, 2);
            scene.PE.ForceActivationState(stale, ActivationState.DISABLE_DEACTIVATION);
            Assert.Contains(stale, StateOf(scene).NeverSleep);

            LoseNatively(stale);
            float before = NativeZ(healthy);
            RapierTestScene.Step(scene, 1f);

            Assert.False(StepThrew(log), string.Join(Environment.NewLine, log.Warnings));
            Assert.DoesNotContain(stale, StateOf(scene).NeverSleep);
            Assert.Single(log.Warnings, w => w.Contains("Could not wake body 1"));
            Assert.True(NativeZ(healthy) < before - 1f, "the healthy box did not fall, so the step stopped");
            Assert.True(scene.PhysObjects[2].Position.Z < Sky.Z - 1f, "no update reached BulletS");
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void ForcedBody_ResetFails_IsLoggedOnce_AndTheSetIsEmptied()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 1, Sky, physical: true);
            AddBox(scene, 2, Sky + new Vector3(3f, 0f, 0f), physical: true);
            RapierTestScene.Step(scene, 0.3f);
            BSAPIRapier.BulletBodyRapier stale = BodyOf(scene, 1);
            BSAPIRapier.BulletBodyRapier healthy = BodyOf(scene, 2);

            scene.PE.ApplyCentralForce(stale, new Vector3(1f, 0f, 0f));
            Assert.Contains(stale, StateOf(scene).ForcedBodies);
            LoseNatively(stale);
            float before = NativeZ(healthy);
            scene.Simulate(RapierTestScene.FrameSeconds);

            Assert.False(StepThrew(log), string.Join(Environment.NewLine, log.Warnings));
            Assert.Empty(StateOf(scene).ForcedBodies);
            Assert.Single(log.Warnings, w => w.Contains("Could not reset the forces on body 1"));

            // The next frames run normally: nothing is retried, nothing more is logged.
            RapierTestScene.Step(scene, 1f);
            Assert.False(StepThrew(log));
            Assert.Single(log.Warnings, w => w.Contains("Could not reset the forces"));
            Assert.True(NativeZ(healthy) < before - 1f, "the healthy box did not fall, so the step stopped");
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void ForcedBodies_AreClearedEvenWhenTheStepThrows()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 1, Sky, physical: true);
            RapierTestScene.Step(scene, 0.3f);
            BSAPIRapier.BulletBodyRapier body = BodyOf(scene, 1);
            scene.PE.ApplyCentralForce(body, new Vector3(1f, 0f, 0f));
            Assert.NotEmpty(StateOf(scene).ForcedBodies);

            // A world pointer Rapier rejects makes r3SetTimeStep fail, which still throws, inside the step.
            BSAPIRapier.BulletWorldRapier world = (BSAPIRapier.BulletWorldRapier)scene.World;
            IntPtr real = world.World;
            world.State.TimeStep = -1f;
            world.World = IntPtr.Zero;
            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                    scene.PE.PhysicsStep(scene.World, RapierTestScene.FrameSeconds, 10, 1f / 55f, out int _, out int _));
            }
            finally
            {
                world.World = real;
            }
            Assert.Empty(StateOf(scene).ForcedBodies);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void StepPanic_PoisonsTheWorld_OneError_AndNoMoreSteps()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 1, Sky, physical: true);
            RapierTestScene.Step(scene, 0.3f);
            BSAPIRapier api = (BSAPIRapier)scene.PE;
            RapierWorldState state = StateOf(scene);
            BSAPIRapier.BulletBodyRapier body = BodyOf(scene, 1);

            // What each status means. Rapier cannot be made to panic from here, so R3_PANIC is handed to the same check
            // PhysicsStep gives r3Step's status.
            Assert.False(api.StepFailed(state, 0));
            Assert.Throws<InvalidOperationException>(() => api.StepFailed(state, 2));
            Assert.False(state.Poisoned);
            Assert.True(api.StepFailed(state, RapierNative.StatusPanic));
            Assert.True(state.Poisoned);
            Assert.Single(log.Errors);
            Assert.Contains("panicked", log.Errors[0]);

            // A poisoned world is not stepped: nothing moves, nothing is reported, and no more errors are logged.
            scene.PE.ApplyCentralForce(body, new Vector3(1f, 0f, 0f));
            float z = NativeZ(body);
            int steps = scene.PE.PhysicsStep(scene.World, 1f, 10, 1f / 55f, out int updates, out int colliders);
            Assert.Equal(0, steps);
            Assert.Equal(0, updates);
            Assert.Equal(0, colliders);
            Assert.Empty(state.ForcedBodies);
            RapierTestScene.Step(scene, 1f);
            Assert.Equal(z, NativeZ(body));
            Assert.Single(log.Errors);
            Assert.False(StepThrew(log));
        }
        finally
        {
            scene.Dispose();
        }
    }

    // ---- Attach (all or nothing) ----

    [Fact]
    public void CompoundChild_RapierRefusesTheCollider_LeavesNoColliderClaimed()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            BSAPITemplate pe = scene.PE;
            RapierWorldState state = StateOf(scene);
            ShapeData box = new ShapeData { Type = BSPhysicsShapeType.SHAPE_BOX, Scale = Vector3.One, Size = Vector3.One };
            BulletShape compound = pe.CreateCompoundShape(scene.World, false);
            pe.AddChildShapeToCompoundShape(compound, pe.BuildNativeShape(scene.World, box), Vector3.Zero, Quaternion.Identity);
            BSAPIRapier.BulletBodyRapier body = (BSAPIRapier.BulletBodyRapier)pe.CreateBodyFromShape(scene.World, compound, 77, Sky, Quaternion.Identity);
            BSAPIRapier.CompoundState children = ((BSAPIRapier.BulletShapeRapier)compound).Compound;
            Assert.Single(body.Colliders);
            int owners = state.ColliderOwners.Count;
            int users = state.ColliderShapes.Count;

            // Rapier loses the body, so the new child's collider has nowhere to go.
            LoseNatively(body);
            BulletShape second = pe.BuildNativeShape(scene.World, box);
            Assert.Throws<InvalidOperationException>(() =>
                pe.AddChildShapeToCompoundShape(compound, second, new Vector3(1f, 0f, 0f), Quaternion.Identity));

            // The child is in the compound (BulletS counts on its index) with no collider, and nothing claims one.
            Assert.Equal(2, children.Children.Count);
            Assert.Equal(children.Children.Count, children.Colliders.Count);
            Assert.Empty(children.Colliders[1]);
            Assert.Single(body.Colliders);
            Assert.Equal(owners, state.ColliderOwners.Count);
            Assert.Equal(users, state.ColliderShapes.Count);

            // Removing that child later works on the indexes as they are.
            pe.RemoveChildShapeFromCompoundShapeIndex(compound, 1);
            Assert.Equal(children.Children.Count, children.Colliders.Count);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void SetCollisionShape_RapierRefuses_LeavesTheBodyWithNoShape()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            BSAPITemplate pe = scene.PE;
            RapierWorldState state = StateOf(scene);
            ShapeData box = new ShapeData { Type = BSPhysicsShapeType.SHAPE_BOX, Scale = Vector3.One, Size = Vector3.One };
            BSAPIRapier.BulletBodyRapier body = (BSAPIRapier.BulletBodyRapier)pe.CreateBodyFromShape(scene.World,
                        pe.BuildNativeShape(scene.World, box), 78, Sky, Quaternion.Identity);
            // The old collider goes the way SetCollisionShape takes it out, so only the new one can fail.
            pe.RemoveObjectFromWorld(scene.World, body);
            RapierNative.ColliderHandle old = body.Colliders[0];
            state.ForgetCollider(old);
            body.Colliders.Clear();
            RapierNative.Check(RapierNative.r3RemoveCollider(old, 0));
            int owners = state.ColliderOwners.Count;

            LoseNatively(body);
            BulletShape compound = pe.CreateCompoundShape(scene.World, false);
            pe.AddChildShapeToCompoundShape(compound, pe.BuildNativeShape(scene.World, box), Vector3.Zero, Quaternion.Identity);
            pe.AddChildShapeToCompoundShape(compound, pe.BuildNativeShape(scene.World, box), new Vector3(1f, 0f, 0f), Quaternion.Identity);
            Assert.Throws<InvalidOperationException>(() => pe.SetCollisionShape(scene.World, body, compound));

            Assert.Empty(body.Colliders);
            Assert.Null(body.Shape);
            Assert.Null(body.Compound);
            Assert.False(body.HasRootChild);
            Assert.Null(((BSAPIRapier.BulletShapeRapier)compound).Compound.Body);
            Assert.Equal(owners, state.ColliderOwners.Count);
            Assert.False(pe.GetCollisionShape(body).HasPhysicalShape);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // ---- Removal ----

    [Fact]
    public void DestroyObject_RapierRefuses_IsLogged_AndTheBodyIsForgotten()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            BSAPITemplate pe = scene.PE;
            RapierWorldState state = StateOf(scene);
            BulletBody body = pe.CreateBodyFromShape(scene.World,
                        pe.BuildNativeShape(scene.World, new ShapeData { Type = BSPhysicsShapeType.SHAPE_BOX, Scale = Vector3.One, Size = Vector3.One }),
                        79, Sky, Quaternion.Identity);
            LoseNatively((BSAPIRapier.BulletBodyRapier)body);

            pe.DestroyObject(scene.World, body);

            Assert.Single(log.Warnings, w => w.Contains("Could not remove the body of 79"));
            Assert.False(body.HasPhysicalBody);
            Assert.False(state.Bodies.ContainsKey(79));
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void DestroyConstraint_RapierRefuses_TheConstraintStillLeavesJointsAndBreakable()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 1, Sky, physical: true);
            AddBox(scene, 2, Sky + new Vector3(2f, 0f, 0f), physical: true);
            RapierTestScene.Step(scene, 0.3f);
            RapierWorldState state = StateOf(scene);
            BSConstraint6Dof joint = new BSConstraint6Dof(scene.World, BodyOf(scene, 1), BodyOf(scene, 2),
                        Sky + new Vector3(1f, 0f, 0f), true, true);
            joint.SetBreakingImpulseThreshold(100f);
            BSAPIRapier.BulletConstraintRapier rapierJoint = (BSAPIRapier.BulletConstraintRapier)joint.Constraint;
            Assert.Contains(rapierJoint, state.Joints);
            Assert.Contains(rapierJoint, state.Breakable);

            // Rapier loses the joint; C# still has it.
            RapierNative.Check(RapierNative.r3RemoveImpulseJoint(rapierJoint.Joint, 1));
            Assert.Throws<InvalidOperationException>(() => scene.PE.DestroyConstraint(scene.World, rapierJoint));

            Assert.False(rapierJoint.HasPhysicalConstraint);
            Assert.DoesNotContain(rapierJoint, state.Joints);
            Assert.DoesNotContain(rapierJoint, state.Breakable);
            RapierTestScene.Step(scene, 0.5f);
            Assert.False(StepThrew(log));
        }
        finally
        {
            scene.Dispose();
        }
    }

    // ---- Getters ----

    [Fact]
    public void PoseAndVelocityGetters_NeverQueryAStaleHandle_AndReturnTheLastKnownValues()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            BSAPITemplate pe = scene.PE;
            Quaternion turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
            BulletBody body = pe.CreateBodyFromShape(scene.World,
                        pe.BuildNativeShape(scene.World, new ShapeData { Type = BSPhysicsShapeType.SHAPE_BOX, Scale = Vector3.One, Size = Vector3.One }),
                        80, Sky, turned);

            // Before any read: the creation pose and zero velocities.
            pe.SetLinearVelocity(body, new Vector3(1f, 2f, 3f));
            pe.SetAngularVelocity(body, new Vector3(0f, 0f, 0.25f));
            Vector3 position = pe.GetPosition(body);
            Quaternion rotation = pe.GetOrientation(body);
            Assert.Equal(Sky, position);

            // Rapier loses the body: a refused read is not taken as zeros.
            LoseNatively((BSAPIRapier.BulletBodyRapier)body);
            Assert.Equal(position, pe.GetPosition(body));
            Assert.Equal(rotation, pe.GetOrientation(body));
            Assert.Equal(new Vector3(1f, 2f, 3f), pe.GetLinearVelocity(body));
            Assert.Equal(new Vector3(0f, 0f, 0.25f), pe.GetAngularVelocity(body));

            // A destroyed body is not asked at all.
            pe.DestroyObject(scene.World, body);
            Assert.False(body.HasPhysicalBody);
            Assert.Equal(position, pe.GetPosition(body));
            Assert.Equal(rotation, pe.GetOrientation(body));
            Assert.Equal(new Vector3(1f, 2f, 3f), pe.GetLinearVelocity(body));
            Assert.Equal(new Vector3(0f, 0f, 0.25f), pe.GetAngularVelocity(body));

            // A body that never was read or set gives its creation pose and zero velocities.
            BulletBody fresh = pe.CreateBodyFromShape(scene.World,
                        pe.BuildNativeShape(scene.World, new ShapeData { Type = BSPhysicsShapeType.SHAPE_BOX, Scale = Vector3.One, Size = Vector3.One }),
                        81, Sky + Vector3.UnitX, turned);
            pe.DestroyObject(scene.World, fresh);
            Assert.Equal(Sky + Vector3.UnitX, pe.GetPosition(fresh));
            Assert.Equal(turned, pe.GetOrientation(fresh));
            Assert.Equal(Vector3.Zero, pe.GetLinearVelocity(fresh));
            Assert.Equal(Vector3.Zero, pe.GetAngularVelocity(fresh));
        }
        finally
        {
            scene.Dispose();
        }
    }
}
