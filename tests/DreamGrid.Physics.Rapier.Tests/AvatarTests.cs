using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

/// <summary>
/// BulletS's avatar (BSCharacter and BSActorAvatarMove, unchanged) on the Rapier backend (H10): a dynamic box with
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
            Assert.InRange(moved, 4.5f - 0.7f, 4.5f + 0.7f);
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
}
