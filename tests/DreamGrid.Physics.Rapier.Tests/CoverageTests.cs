using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

/// <summary>
/// The members of the engine interface that BulletS calls rarely or never (map rows marked 'never'). There is no test per
/// member: one that has no logic of its own is covered by NoMemberThrowsNotImplemented, and the ones with some (a joint
/// type, a sweep, the getter and setter pairs) have a test each.
/// </summary>
public class CoverageTests
{
    // High enough that nothing lands during a test.
    private static readonly Vector3 Sky = new Vector3(128f, 128f, 200f);

    private static PhysicsActor AddBox(BSScene scene, uint localId, Vector3 position, bool physical, float edge = 0.5f)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        Vector3 size = new Vector3(edge, edge, edge);
        pbs.Scale = size;
        return scene.AddPrimShape("box" + localId, pbs, position, size, Quaternion.Identity, physical, localId);
    }

    private static BSAPIRapier.BulletBodyRapier BodyOf(BSScene scene, uint id)
    {
        return (BSAPIRapier.BulletBodyRapier)scene.PhysObjects[id].PhysBody;
    }

    private static Vector3 Position(BSAPIRapier.BulletBodyRapier body)
    {
        return BSAPIRapier.ToVector3(RapierNative.r3RigidBody_Translation(body.Body));
    }

    private static Quaternion Rotation(BSAPIRapier.BulletBodyRapier body)
    {
        return BSAPIRapier.ToQuaternion(RapierNative.r3RigidBody_Rotation(body.Body));
    }

    private static string? RepoRoot()
    {
        for (DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DreamGrid.sln")))
                return dir.FullName;
        }
        return null;
    }

    [Fact]
    public void NoMemberThrowsNotImplemented()
    {
        string? root = RepoRoot();
        Assert.True(root != null, "DreamGrid.sln was not found above " + AppContext.BaseDirectory);
        string folder = Path.Combine(root!, "OpenSim", "Region", "PhysicsModules", "BulletS", "Rapier");
        string[] files = Directory.GetFiles(folder, "BSAPIRapier*.cs");
        Assert.NotEmpty(files);
        List<string> offenders = files.Where(f => File.ReadAllText(f).Contains("NotImplementedException"))
                                      .Select(Path.GetFileName).Select(n => n!).ToList();
        Assert.True(offenders.Count == 0, "NotImplementedException is still in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Hinge_KeepsTwoBodiesOnTheAxis()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 701, Sky, physical: true);
            AddBox(scene, 702, Sky + new Vector3(1f, 0f, 0f), physical: true);
            RapierTestScene.Step(scene, 0.2f);
            BSAPIRapier.BulletBodyRapier a = BodyOf(scene, 701);
            BSAPIRapier.BulletBodyRapier b = BodyOf(scene, 702);

            // The pivot is the point half way between the two boxes; the hinge axis is Z in both.
            BulletConstraint hinge = scene.PE.CreateHingeConstraint(scene.World, a, b,
                        new Vector3(0.5f, 0f, 0f), new Vector3(-0.5f, 0f, 0f), Vector3.UnitZ, Vector3.UnitZ, true, true);
            Assert.True(hinge.HasPhysicalConstraint);

            // Spin B about X (which the hinge refuses) and about Z (which it allows).
            scene.PE.SetAngularVelocity(b, new Vector3(3f, 0f, 3f));
            RapierTestScene.Step(scene, 1f);

            Quaternion rotationA = Rotation(a), rotationB = Rotation(b);
            Vector3 axisA = BSAPIRapier.Rotate(rotationA, Vector3.UnitZ);
            Vector3 axisB = BSAPIRapier.Rotate(rotationB, Vector3.UnitZ);
            Assert.True(Vector3.Dot(axisA, axisB) > 0.99f, "the hinge axes parted: " + Vector3.Dot(axisA, axisB));

            Vector3 pivotA = Position(a) + BSAPIRapier.Rotate(rotationA, new Vector3(0.5f, 0f, 0f));
            Vector3 pivotB = Position(b) + BSAPIRapier.Rotate(rotationB, new Vector3(-0.5f, 0f, 0f));
            Assert.True((pivotA - pivotB).Length() < 0.05f, "the pivots parted by " + (pivotA - pivotB).Length());

            // The free rotation did happen: B turned about the axis relative to A.
            Quaternion relative = new Quaternion(-rotationA.X, -rotationA.Y, -rotationA.Z, rotationA.W) * rotationB;
            float angle = 2f * MathF.Atan2(MathF.Sqrt(relative.X * relative.X + relative.Y * relative.Y + relative.Z * relative.Z), MathF.Abs(relative.W));
            Assert.True(angle > 0.3f, "B did not turn about the hinge: " + angle);

            // Limiting the hinge to nothing stops that turn.
            Assert.True(scene.PE.HingeSetLimits(hinge, 0f, 0f, BSAPITemplate.HINGE_NOT_SPECIFIED, 0f, 0f));
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void OtherJointTypes_AreMadeAndConfigured()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 711, Sky, physical: true);
            AddBox(scene, 712, Sky + new Vector3(1f, 0f, 0f), physical: true);
            RapierTestScene.Step(scene, 0.2f);
            BSAPIRapier.BulletBodyRapier a = BodyOf(scene, 711);
            BSAPIRapier.BulletBodyRapier b = BodyOf(scene, 712);
            Vector3 pivotA = new Vector3(0.5f, 0f, 0f), pivotB = new Vector3(-0.5f, 0f, 0f);

            BulletConstraint slider = scene.PE.CreateSliderConstraint(scene.World, a, b, pivotA, Quaternion.Identity,
                        pivotB, Quaternion.Identity, true, true);
            Assert.True(slider.HasPhysicalConstraint);
            Assert.True(scene.PE.SliderSetLimits(slider, BSAPITemplate.SLIDER_LOWER_LIMIT, BSAPITemplate.SLIDER_LINEAR, -0.5f));
            Assert.True(scene.PE.SliderSetLimits(slider, BSAPITemplate.SLIDER_UPPER_LIMIT, BSAPITemplate.SLIDER_LINEAR, 0.5f));
            Assert.True(scene.PE.SliderMotor(slider, BSAPITemplate.SLIDER_MOTOR_VELOCITY, BSAPITemplate.SLIDER_LINEAR, 1f));
            Assert.True(scene.PE.SliderMotor(slider, BSAPITemplate.SLIDER_MAX_MOTOR_FORCE, BSAPITemplate.SLIDER_LINEAR, 10f));
            Assert.True(scene.PE.SliderMotorEnable(slider, BSAPITemplate.SLIDER_LINEAR, 1f));

            BulletConstraint ball = scene.PE.CreatePoint2PointConstraint(scene.World, a, b, pivotA, pivotB, true);
            Assert.True(ball.HasPhysicalConstraint);
            BulletConstraint cone = scene.PE.CreateConeTwistConstraint(scene.World, a, b, pivotA, Quaternion.Identity,
                        pivotB, Quaternion.Identity, true);
            Assert.True(cone.HasPhysicalConstraint);

            RapierTestScene.Step(scene, 0.5f);
            Assert.True(scene.PE.DestroyConstraint(scene.World, slider));
            Assert.True(scene.PE.DestroyConstraint(scene.World, ball));
            Assert.True(scene.PE.DestroyConstraint(scene.World, cone));
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void ConvexSweepTest2_HitsABox()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 721, new Vector3(128f, 128f, 100f), physical: false);
            AddBox(scene, 722, new Vector3(50f, 50f, 200f), physical: false);
            RapierTestScene.Step(scene, 0.3f);

            // A 0.5 box dropped from 110 to 90 onto a box whose top is at 100.25: it touches when its bottom, 0.25 below its
            // centre, is there, at a centre height of 100.5, which is 9.5 of the 20 metres.
            SweepHit hit = scene.PE.ConvexSweepTest2(scene.World, BodyOf(scene, 722),
                        new Vector3(128f, 128f, 110f), new Vector3(128f, 128f, 90f), 0f);
            Assert.True(hit.hasHit());
            Assert.Equal(721u, hit.ID);
            Assert.InRange(hit.Fraction, 0.46f, 0.49f);
            Assert.True(hit.Normal.Z > 0.9f, "normal " + hit.Normal);

            // Nothing is in the way of a sweep somewhere else.
            SweepHit miss = scene.PE.ConvexSweepTest2(scene.World, BodyOf(scene, 722),
                        new Vector3(60f, 60f, 110f), new Vector3(60f, 60f, 90f), 0f);
            Assert.False(miss.hasHit());
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void GettersReturnWhatTheSettersGave()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            AddBox(scene, 731, Sky, physical: true);
            AddBox(scene, 732, Sky + new Vector3(20f, 0f, 0f), physical: false);
            RapierTestScene.Step(scene, 0.2f);
            BSAPIRapier.BulletBodyRapier body = BodyOf(scene, 731);
            BSAPIRapier.BulletBodyRapier fixedBody = BodyOf(scene, 732);
            BSAPITemplate pe = scene.PE;

            pe.SetLinearDamping(body, 0.3f);
            pe.SetAngularDamping(body, 0.6f);
            Assert.InRange(pe.GetLinearDamping(body), 0.299f, 0.301f);
            Assert.InRange(pe.GetAngularDamping(body), 0.599f, 0.601f);
            pe.SetDamping(body, 0.1f, 0.2f);
            Assert.InRange(pe.GetLinearDamping(body), 0.099f, 0.101f);
            Assert.InRange(pe.GetAngularDamping(body), 0.199f, 0.201f);

            pe.SetLinearFactor(body, new Vector3(1f, 0f, 1f));
            Assert.Equal(new Vector3(1f, 0f, 1f), pe.GetLinearFactor(body));
            pe.SetAngularFactorV(body, new Vector3(0f, 0f, 1f));
            Assert.Equal(new Vector3(0f, 0f, 1f), pe.GetAngularFactor(body));

            Vector3 world = ((BSAPIRapier.BulletWorldRapier)scene.World).State.Gravity;
            Assert.Equal(world, pe.GetGravity(body));
            pe.SetGravity(body, new Vector3(0f, 0f, -3f));
            Assert.Equal(new Vector3(0f, 0f, -3f), pe.GetGravity(body));

            BulletShape shape = pe.GetCollisionShape(body);
            Assert.True(shape.HasPhysicalShape);
            pe.SetMargin(shape, 0.04f);
            Assert.Equal(0.04f, pe.GetMargin(shape));
            Assert.True(pe.IsConvex(shape));
            Assert.False(pe.IsConcave(shape));
            Assert.False(pe.IsSoftBody(shape));
            Assert.True(pe.GetAngularMotionDisc(shape) > 0.3f);

            pe.SetFriction(body, 0.7f);
            pe.SetRestitution(body, 0.4f);
            Assert.InRange(pe.GetFriction(body), 0.699f, 0.701f);
            Assert.InRange(pe.GetRestitution(body), 0.399f, 0.401f);

            // The pointer is kept apart from the localID, which the engine reads back from the same user data.
            pe.SetUserPointer(body, new IntPtr(1234));
            Assert.Equal(new IntPtr(1234), pe.GetUserPointer(body));
            Assert.Equal(731ul, RapierNative.r3RigidBody_UserData(body.Body).low);

            Assert.False(pe.IsStaticObject(body));
            Assert.True(pe.IsStaticObject(fixedBody));
            Assert.True(pe.IsStaticOrKinematicObject(fixedBody));
            Assert.False(pe.IsKinematicObject(fixedBody));

            pe.SetLinearVelocity(body, new Vector3(0f, 0f, 0f));
            Vector3 before = pe.GetPosition(body);
            pe.Translate(body, new Vector3(1f, 2f, 3f));
            Assert.True((pe.GetPosition(body) - before - new Vector3(1f, 2f, 3f)).Length() < 0.001f);

            pe.ForceActivationState(body, ActivationState.DISABLE_DEACTIVATION);
            Assert.Equal((int)ActivationState.DISABLE_DEACTIVATION, pe.GetActivationState(body));
            Assert.True(pe.IsActive(body));

            // The members Rapier has no counterpart for log once and give back a neutral value.
            pe.SetSleepingThresholds(body, 0.5f, 0.6f);
            pe.SetDeactivationTime(body, 3f);
            Assert.Equal(0.5f, pe.GetLinearSleepingThreshold(body));
            Assert.Equal(3f, pe.GetDeactivationTime(body));
            BulletConstraint gear = pe.CreateGearConstraint(scene.World, body, fixedBody, Vector3.UnitZ, Vector3.UnitZ, 2f, true);
            Assert.False(gear.HasPhysicalConstraint);
            Assert.Equal(Vector3.One, pe.SetAnisotripicFriction(gear, new Vector3(1f, 2f, 3f)));
            Assert.False(pe.HasAnisotripicFriction(gear));
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }
}
