using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

/// <summary>
/// BulletS's avatar (BSCharacter and BSActorAvatarMove) on the Rapier backend (H10): a dynamic capsule (a box with AvatarShape=1) with
/// locked rotation that never sleeps, moved by impulses from BulletS's velocity motor, flying by reduced gravity (H6).
/// Every avatar comes from scene.AddAvatar, as the simulator makes them.
/// </summary>
public class AvatarTests
{
    private const float TerrainHeight = 21f;
    private const uint AvatarId = 1001;
    // What ScenePresence passes by default (AvatarAppearance.AvatarBoxSize).
    private static readonly Vector3 AvatarSize = new Vector3(0.45f, 0.6f, 1.9f);

    private readonly ITestOutputHelper _output;

    public AvatarTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static void SetFlatTerrain(BSScene scene)
    {
        float[] map = new float[256 * 256];
        for (int ii = 0; ii < map.Length; ii++)
            map[ii] = TerrainHeight;
        scene.SetTerrain(map);
        RapierTestScene.Step(scene, 0.3f);
    }

    private static PhysicsActor AddAvatar(BSScene scene, Vector3 position, bool flying)
    {
        // As ScenePresence.AddToPhysicalScene does: the avatar hears its collisions, which BSActorAvatarMove needs to
        // know it is standing and to find steps.
        PhysicsActor avatar = scene.AddAvatar(AvatarId, "avatar", position, AvatarSize, 0f, flying);
        Assert.NotNull(avatar);
        avatar.SubscribeEvents(100);
        return avatar;
    }

    private static BSAPIRapier.BulletBodyRapier BodyOf(BSScene scene, uint id)
    {
        return (BSAPIRapier.BulletBodyRapier)scene.PhysObjects[id].PhysBody;
    }

    // The avatar's bottom as BSCharacter sees it (BSActorAvatarMove.WalkUpStairs): its position less half its height.
    private static float Feet(PhysicsActor avatar)
    {
        return avatar.Position.Z - avatar.Size.Z / 2f;
    }

    private static float FeetAboveTerrain(BSScene scene, PhysicsActor avatar)
    {
        return Feet(avatar) - scene.TerrainManager.GetTerrainHeightAtXYZ(avatar.Position);
    }

    // How far the body's up axis has tipped from world Z, in radians.
    private static float Tilt(BSScene scene, uint id)
    {
        Quaternion rot = scene.PE.GetOrientation(BodyOf(scene, id));
        Vector3 up = Vector3.UnitZ * rot;
        return (float)Math.Acos(Math.Clamp(up.Z, -1f, 1f));
    }

    [Fact]
    public void Avatar_StandsOnTerrain()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor avatar = AddAvatar(scene, new Vector3(128f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.3f), false);
            RapierTestScene.Step(scene, 2.0f);

            float feet = FeetAboveTerrain(scene, avatar);
            _output.WriteLine($"feet above terrain after 2 s: {feet:F4} m (pos.Z {avatar.Position.Z:F4}, native body Z {scene.PE.GetPosition(BodyOf(scene, AvatarId)).Z:F4})");
            Assert.InRange(feet, -0.05f, 0.05f);

            BSAPIRapier.BulletBodyRapier body = BodyOf(scene, AvatarId);
            RapierWorldState state = ((BSAPIRapier.BulletWorldRapier)scene.World).State;
            Assert.Contains(body, state.NeverSleep);
            float elapsed = 0f;
            while (elapsed < 10f)
            {
                scene.Simulate(RapierTestScene.FrameSeconds);
                elapsed += RapierTestScene.FrameSeconds;
                Assert.True(RapierNative.r3RigidBody_IsSleeping(body.Body) == 0, $"the avatar slept at {elapsed:F2}s");
                Assert.Contains(body, state.NeverSleep);
            }
            feet = FeetAboveTerrain(scene, avatar);
            _output.WriteLine($"feet above terrain after 12 s: {feet:F4} m");
            Assert.InRange(feet, -0.05f, 0.05f);
            Assert.True(Tilt(scene, AvatarId) < 0.01f, $"the avatar tipped by {Tilt(scene, AvatarId)} rad");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Avatar_WalksForward()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor avatar = AddAvatar(scene, new Vector3(100f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.1f), false);
            RapierTestScene.Step(scene, 2.0f);
            float startX = avatar.Position.X;
            float startY = avatar.Position.Y;

            avatar.TargetVelocity = new Vector3(1.5f, 0f, 0f);
            float elapsed = 0f;
            float worstFeet = 0f;
            float worstTilt = 0f;
            while (elapsed < 3f - 0.0001f)
            {
                scene.Simulate(RapierTestScene.FrameSeconds);
                elapsed += RapierTestScene.FrameSeconds;
                float feet = FeetAboveTerrain(scene, avatar);
                if (Math.Abs(feet) > Math.Abs(worstFeet))
                    worstFeet = feet;
                worstTilt = Math.Max(worstTilt, Tilt(scene, AvatarId));
            }
            float moved = avatar.Position.X - startX;
            _output.WriteLine($"moved {moved:F3} m in X, {avatar.Position.Y - startY:F3} m in Y; worst feet {worstFeet:F4} m; worst tilt {worstTilt:F4} rad");
            // 1.5 m/s for 3 s, at Rapier's ubODE-like speed (BSAPITemplate.AvatarSpeedFactor, 1/1.3).
            float expected = 1.5f * 3f * (1f / 1.3f);
            Assert.InRange(moved, expected - 0.7f, expected + 0.7f);
            Assert.InRange(worstFeet, -0.1f, 0.1f);
            Assert.InRange(avatar.Position.Y - startY, -0.1f, 0.1f);
            Assert.True(worstTilt < 0.01f, $"the avatar tipped by {worstTilt} rad");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Avatar_ClimbsStep()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            // A static 0.3 m step whose near face is at x = 127, wide and deep enough to end on.
            PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
            Vector3 stepSize = new Vector3(8f, 8f, 0.3f);
            pbs.Scale = stepSize;
            scene.AddPrimShape("step", pbs, new Vector3(131f, 128f, TerrainHeight + stepSize.Z / 2f), stepSize,
                                Quaternion.Identity, false, 1002);

            PhysicsActor avatar = AddAvatar(scene, new Vector3(124f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.1f), false);
            RapierTestScene.Step(scene, 2.0f);
            float startZ = avatar.Position.Z;

            avatar.TargetVelocity = new Vector3(1.5f, 0f, 0f);
            RapierTestScene.Step(scene, 4.0f);
            avatar.TargetVelocity = Vector3.Zero;
            RapierTestScene.Step(scene, 1.5f);

            float rise = avatar.Position.Z - startZ;
            _output.WriteLine($"ended at x {avatar.Position.X:F3}, z rose {rise:F4} m; feet {Feet(avatar):F4} against step top {TerrainHeight + stepSize.Z:F4}");
            Assert.True(avatar.Position.X > 127.5f, $"the avatar stopped at x {avatar.Position.X}, before the step");
            Assert.InRange(rise, 0.3f - 0.05f, 0.3f + 0.05f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // Made flying (AddAvatar's isFlying), or made walking and then switched through the Flying property as ScenePresence
    // does when the viewer asks to fly.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Avatar_Flies(bool madeFlying)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            // Higher than AvatarFlyingGroundMargin (5 m), so the hover push stays out of it.
            PhysicsActor avatar = AddAvatar(scene, new Vector3(128f, 128f, TerrainHeight + 20f), madeFlying);
            if (!madeFlying)
                avatar.Flying = true;
            RapierTestScene.Step(scene, 0.5f);
            float startZ = avatar.Position.Z;

            float elapsed = 0f;
            float worst = 0f;
            while (elapsed < 3f - 0.0001f)
            {
                scene.Simulate(RapierTestScene.FrameSeconds);
                elapsed += RapierTestScene.FrameSeconds;
                float drift = avatar.Position.Z - startZ;
                if (Math.Abs(drift) > Math.Abs(worst))
                    worst = drift;
            }
            _output.WriteLine($"worst height drift over 3 s: {worst:F4} m");
            Assert.InRange(worst, -0.2f, 0.2f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }
    // BulletS's jump is a force of 0.22 m impulseZ / step that stays on for every substep of the frame. Rapier divides
    // by Bullet's 1/60 s, whatever its step, so one frame's jump adds 0.22 impulseZ 60 (n dt) to Vz, n dt being the
    // time the frame's substeps cover: the same as Bullet at its 1/60 default, and the same at any FixedTimeStep.
    [Theory]
    [InlineData(null)]            // Rapier's default, 0.02 s
    [InlineData("0.016666667")]
    [InlineData("0.01")]
    public void Avatar_JumpImpulse_FollowsTheFrameTimeNotTheStep(string? fixedTimeStep)
    {
        const float impulseZ = 9.4f;   // what ScenePresenceAnimator asks for
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(fixedTimeStep == null ? null
                    : new Dictionary<string, string> { { "FixedTimeStep", fixedTimeStep } });
        try
        {
            SetFlatTerrain(scene);
            // High in the air, so only gravity and the jump act on it.
            PhysicsActor avatar = AddAvatar(scene, new Vector3(128f, 128f, 150f), false);
            RapierTestScene.Step(scene, 0.3f);
            RapierWorldState state = ((BSAPIRapier.BulletWorldRapier)scene.World).State;
            float dt = scene.m_fixedTimeStep;
            Func<int> nextSubsteps = () => (int)((state.Accumulator + RapierTestScene.FrameSeconds) / dt);
            Func<float> vz = () => scene.PE.GetLinearVelocity(BodyOf(scene, AvatarId)).Z;

            // One frame of falling, for the pull of gravity per substep.
            int n0 = nextSubsteps();
            float v0 = vz();
            scene.Simulate(RapierTestScene.FrameSeconds);
            float gravityPerSubstep = (vz() - v0) / n0;
            Assert.True(gravityPerSubstep < 0f, $"gravity per substep {gravityPerSubstep}");

            int n1 = nextSubsteps();
            float v1 = vz();
            avatar.AvatarJump(impulseZ);
            scene.Simulate(RapierTestScene.FrameSeconds);
            float jump = vz() - v1 - gravityPerSubstep * n1;

            float expected = 0.22f * impulseZ * 60f * n1 * dt;
            _output.WriteLine($"step {dt}: n {n1}, jump dVz {jump}, expected {expected}");
            Assert.InRange(jump, expected * 0.95f, expected * 1.05f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // ---- Capsule avatars (BSAPITemplate.CapsuleAvatars) ----

    [Theory]
    [InlineData(null, BSPhysicsShapeType.SHAPE_CAPSULE)]
    [InlineData("0", BSPhysicsShapeType.SHAPE_CAPSULE)]
    [InlineData("1", BSPhysicsShapeType.SHAPE_BOX)]
    public void Avatar_IsACapsuleUnlessTheIniSaysOtherwise(string? avatarShape, BSPhysicsShapeType expected)
    {
        Dictionary<string, string>? ini = avatarShape == null ? null : new Dictionary<string, string> { { "AvatarShape", avatarShape } };
        BSScene scene = RapierTestScene.Create(ini);
        try
        {
            Assert.True(scene.PE.CapsuleAvatars);
            SetFlatTerrain(scene);
            AddAvatar(scene, new Vector3(128f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.3f), false);
            RapierTestScene.Step(scene, 0.2f);
            Assert.Equal(expected, scene.PhysObjects[AvatarId].PhysShape.physShapeInfo.shapeType);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // Where the avatar settles on flat terrain and its mass, as a box (AvatarShape=1) or as the default capsule.
    private (float z, float mass, RapierNative.Aabb bounds) Settle(string avatarShape, Vector3 size)
    {
        Dictionary<string, string>? ini = avatarShape == "" ? null : new Dictionary<string, string> { { "AvatarShape", avatarShape } };
        BSScene scene = RapierTestScene.Create(ini);
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor avatar = scene.AddAvatar(AvatarId, "avatar", new Vector3(128f, 128f, TerrainHeight + size.Z / 2f + 0.3f),
                                                  size, 0f, false);
            avatar.SubscribeEvents(100);
            RapierTestScene.Step(scene, 2.0f);
            IntPtr shape = ((BSAPIRapier.BulletShapeRapier)scene.PhysObjects[AvatarId].PhysShape.physShapeInfo).Shape;
            RapierNative.Pose identity = new RapierNative.Pose { rotation = new RapierNative.Rotation { w = 1f } };
            return (avatar.Position.Z, avatar.Mass, RapierNative.r3SharedShape_ComputeAabb(shape, identity));
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Theory]
    [InlineData(1.9f)]
    [InlineData(1.4f)]
    [InlineData(2.4f)]
    public void Avatar_CapsuleIsAsTallAsTheBox_AndStandsAtTheSameHeight(float height)
    {
        Vector3 size = new Vector3(AvatarSize.X, AvatarSize.Y, height);
        var box = Settle("1", size);
        var capsule = Settle("", size);
        _output.WriteLine($"height {height}: box Z {box.z:F4}, capsule Z {capsule.z:F4}; mass box {box.mass:F3}, capsule {capsule.mass:F3}");

        float boxHeight = box.bounds.maxs.z - box.bounds.mins.z;
        Assert.Equal(boxHeight, capsule.bounds.maxs.z - capsule.bounds.mins.z, 3);
        // Never wider than the box: its radius is half the smaller side.
        Assert.Equal(Math.Min(size.X, size.Y), capsule.bounds.maxs.x - capsule.bounds.mins.x, 3);
        Assert.Equal(Math.Min(size.X, size.Y), capsule.bounds.maxs.y - capsule.bounds.mins.y, 3);
        Assert.InRange(capsule.z - box.z, -0.01f, 0.01f);
        Assert.Equal(box.mass, capsule.mass, 3);
    }

    [Fact]
    public void Avatar_SitsAndStandsUp_AsACapsule()
    {
        // Sitting takes the avatar out of physics (ScenePresence.RemoveFromPhysicalScene); standing up adds a new one
        // where the seat was, a little above the ground.
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor avatar = AddAvatar(scene, new Vector3(128f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.3f), false);
            RapierTestScene.Step(scene, 1.0f);
            scene.RemoveAvatar(avatar);
            RapierTestScene.Step(scene, 1.0f);
            Assert.False(scene.PhysObjects.ContainsKey(AvatarId));

            avatar = AddAvatar(scene, new Vector3(130f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.5f), false);
            RapierTestScene.Step(scene, 2.0f);
            Assert.Equal(BSPhysicsShapeType.SHAPE_CAPSULE, scene.PhysObjects[AvatarId].PhysShape.physShapeInfo.shapeType);
            float feet = FeetAboveTerrain(scene, avatar);
            _output.WriteLine($"feet above terrain after standing up: {feet:F4} m");
            Assert.InRange(feet, -0.05f, 0.05f);
            Assert.True(Tilt(scene, AvatarId) < 0.01f, $"the avatar tipped by {Tilt(scene, AvatarId)} rad");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // ---- The orientation the simulator sets every frame (BSAPITemplate.SkipsUnchangedAvatarYaw) ----

    private static float BodyYaw(BSScene scene)
    {
        scene.PE.GetOrientation(BodyOf(scene, AvatarId)).GetEulerAngles(out _, out _, out float yaw);
        return yaw;
    }

    [Fact]
    public void Avatar_OrientationWithTheSameYaw_IsNotSetAgain()
    {
        BSScene scene = RapierTestScene.Create();
        try
        {
            Assert.True(scene.PE.SkipsUnchangedAvatarYaw);
            SetFlatTerrain(scene);
            PhysicsActor avatar = AddAvatar(scene, new Vector3(128f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.3f), false);
            avatar.Orientation = Quaternion.CreateFromEulers(0f, 0f, 0.5f);
            RapierTestScene.Step(scene, 1.0f);
            Quaternion before = scene.PE.GetOrientation(BodyOf(scene, AvatarId));
            Assert.Equal(0.5f, BodyYaw(scene), 4);

            // What the simulator does each frame: the same yaw back, off by float noise (and with the tilt the viewer
            // may add, which BSCharacter drops). The body is left exactly as it was.
            for (int frame = 0; frame < 20; frame++)
            {
                float noise = (frame % 2 == 0 ? 1f : -1f) * 2e-5f * frame / 20f;
                avatar.Orientation = Quaternion.CreateFromEulers(0.01f * noise, 0f, 0.5f + noise);
                scene.Simulate(RapierTestScene.FrameSeconds);
                Assert.Equal(before, scene.PE.GetOrientation(BodyOf(scene, AvatarId)));
            }
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Avatar_TiltedOrientationWithTheSameYaw_ThenAPositionSet_StaysUpright()
    {
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor avatar = AddAvatar(scene, new Vector3(128f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.3f), false);
            avatar.Orientation = Quaternion.CreateFromEulers(0f, 0f, 0.5f);
            RapierTestScene.Step(scene, 1.0f);

            // The same yaw with a tilt (the skip leaves the body alone), then a position set in the same frame: its
            // SetTranslation uses RawOrientation, which must not carry the tilt the yaw-only path drops.
            avatar.Orientation = Quaternion.CreateFromEulers(0.3f, 0.2f, 0.5f);
            avatar.Position = new Vector3(129f, 128f, avatar.Position.Z);
            scene.Simulate(RapierTestScene.FrameSeconds);
            Assert.True(Tilt(scene, AvatarId) < 0.001f, $"the avatar tipped by {Tilt(scene, AvatarId)} rad");
            RapierTestScene.Step(scene, 0.5f);
            Assert.True(Tilt(scene, AvatarId) < 0.001f, $"the avatar tipped by {Tilt(scene, AvatarId)} rad");
            Assert.Equal(0.5f, BodyYaw(scene), 3);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Theory]
    [InlineData(0.0011f)]    // just over the tolerance
    [InlineData(0.0175f)]    // one degree
    [InlineData(3.0f)]       // turning round
    public void Avatar_TurnBeyondTheTolerance_IsAppliedTheSameFrame(float turn)
    {
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor avatar = AddAvatar(scene, new Vector3(128f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.3f), false);
            avatar.Orientation = Quaternion.CreateFromEulers(0f, 0f, 0.5f);
            RapierTestScene.Step(scene, 1.0f);

            avatar.Orientation = Quaternion.CreateFromEulers(0f, 0f, 0.5f + turn);
            scene.Simulate(RapierTestScene.FrameSeconds);
            float expected = (float)Math.IEEERemainder(0.5f + turn, 2.0 * Math.PI);
            Assert.Equal(expected, BodyYaw(scene), 4);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Avatar_SlowTurnInStepsBelowTheTolerance_NeverFallsFurtherBehind()
    {
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene);
            PhysicsActor avatar = AddAvatar(scene, new Vector3(128f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.3f), false);
            // Away from 0, where Quaternion.GetEulerAngles (which BSCharacter trims the orientation with) reads small
            // yaws as 0.
            avatar.Orientation = Quaternion.CreateFromEulers(0f, 0f, 0.5f);
            RapierTestScene.Step(scene, 1.0f);

            // 0.0004 rad a frame: each step alone is under the tolerance, but the body is turned once it is behind.
            float yaw = 0.5f;
            for (int frame = 0; frame < 50; frame++)
            {
                yaw += 0.0004f;
                avatar.Orientation = Quaternion.CreateFromEulers(0f, 0f, yaw);
                scene.Simulate(RapierTestScene.FrameSeconds);
                Assert.True(Math.Abs(yaw - BodyYaw(scene)) < BSCharacter.AvatarYawTolerance + 1e-5f,
                            $"frame {frame}: asked {yaw}, body {BodyYaw(scene)}");
            }
            Assert.True(BodyYaw(scene) > 0.518f, $"the body turned only to {BodyYaw(scene)}");
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Theory]
    [InlineData(0.5f, 0.5f, true)]
    [InlineData(0.5f, 0.5009f, true)]
    [InlineData(0.5f, 0.5011f, false)]
    [InlineData(3.1414f, -3.1414f, true)]    // the same direction across +-pi
    [InlineData(0f, 0.0175f, false)]
    public void YawWithinTolerance_ComparesTheYawOnly(float current, float asked, bool within)
    {
        Quaternion body = Quaternion.CreateFromEulers(0f, 0f, current);
        Assert.Equal(within, BSCharacter.YawWithinTolerance(body, asked));
    }
}
