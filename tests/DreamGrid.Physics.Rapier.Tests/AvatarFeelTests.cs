using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

/// <summary>
/// Avatars under Rapier feel like ubODE's, the region's default engine (the owner's choice, 2026-11-07). Each test drives
/// the avatar as ScenePresence does in-world: TargetVelocity once, and the viewer's body rotation set every frame (a yaw
/// of float noise, as HandleAgentUpdate passes it while a key is held).
///
/// The ubODE values are constants. They were measured on 2026-11-07 by running the same script on ubODE (ubOdeModule and
/// its ubODEMeshmerizer, at the simulator's 0.0909 s frame) with a harness kept outside the repository; see
/// .superpowers/sdd/2026-11-06-rapier-opensim-integration/avatar-feel-report.md. ubODE is not referenced by this project.
/// Bullet (BulletUnmanaged, the shipped BulletSim library, which loads here through the same dllmap) runs the same script
/// live, to show that its behaviour has not changed.
/// </summary>
public class AvatarFeelTests
{
    // ubODE, measured as above: ground speed over the last 1 s of a 2 s walk (4.096 m/s asked) and flight (16.384 m/s
    // asked, 30 m up). ubODE divides the horizontal target by av_movement_divisor_walk = 1.3.
    private const float UbodeWalkSpeed = 3.149f;
    private const float UbodeFlySpeed = 12.594f;

    // Bullet, measured on this branch before the change (HEAD 65112051) with the same in-world script: its avatar covers
    // 8.7% less ground than its velocity, because each orientation set puts it back to its trailing interpolated pose.
    private const float BulletWalkSpeed = 3.710f;
    private const float BulletFlySpeed = 14.968f;

    private const float TerrainHeight = 21f;
    private const uint AvatarId = 1001;
    private static readonly Vector3 AvatarSize = new Vector3(0.45f, 0.6f, 1.9f);
    private const float WalkSpeed = 4.096f;         // ScenePresence.AgentControlNormalVel
    private const float FlySpeed = 4f * 4.096f;     // AddNewMovement's factor for flying

    private readonly ITestOutputHelper _output;

    public AvatarFeelTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static BSScene MakeScene(string engine, Dictionary<string, string>? ini)
    {
        Dictionary<string, string> config = new Dictionary<string, string> { { "BulletEngine", engine } };
        if (ini != null)
        {
            foreach (KeyValuePair<string, string> kvp in ini)
                config[kvp.Key] = kvp.Value;
        }
        BSScene scene = RapierTestScene.Create(config);
        float[] map = new float[256 * 256];
        Array.Fill(map, TerrainHeight);
        scene.SetTerrain(map);
        RapierTestScene.Step(scene, 0.3f);
        return scene;
    }

    private static PhysicsActor AddAvatar(BSScene scene, Vector3 position, bool flying)
    {
        PhysicsActor avatar = scene.AddAvatar(AvatarId, "avatar", position, AvatarSize, 0f, flying);
        Assert.NotNull(avatar);
        avatar.SubscribeEvents(100);
        return avatar;
    }

    // One simulator frame, with the viewer's rotation set first as ScenePresence sets it.
    private static void Frame(BSScene scene, PhysicsActor avatar, int frame, bool keyHeld)
    {
        if (keyHeld)
        {
            float yaw = (frame % 2 == 0) ? 1e-5f : -1e-5f;
            avatar.Orientation = new Quaternion(0f, 0f, MathF.Sin(yaw / 2f), MathF.Cos(yaw / 2f));
        }
        scene.Simulate(RapierTestScene.FrameSeconds);
    }

    // The ground speed over the last second of a 2 s walk (or flight, 30 m up) along +X, and the highest speed the body
    // reached on the way.
    private static (float speed, float peak) GroundSpeed(string engine, bool flying, Dictionary<string, string>? ini = null)
    {
        BSScene scene = MakeScene(engine, ini);
        try
        {
            Vector3 start = flying ? new Vector3(60f, 128f, TerrainHeight + 30f)
                                   : new Vector3(100f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.05f);
            PhysicsActor avatar = AddAvatar(scene, start, flying);
            RapierTestScene.Step(scene, flying ? 1f : 2f);

            avatar.TargetVelocity = new Vector3(flying ? FlySpeed : WalkSpeed, 0f, 0f);
            const int frames = 22;      // 2 s
            List<float> x = new List<float>();
            float peak = 0f;
            for (int ii = 0; ii < frames; ii++)
            {
                Frame(scene, avatar, ii, true);
                x.Add(avatar.Position.X);
                peak = Math.Max(peak, avatar.Velocity.X);
            }
            return ((x[frames - 1] - x[frames - 12]) / (11 * RapierTestScene.FrameSeconds), peak);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // Symptoms 2 (walking) and 4 (flying): the ground the avatar covers.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Avatar_CoversGroundAtUbodesSpeed(bool flying)
    {
        using ErrorLog log = new ErrorLog();
        float ubode = flying ? UbodeFlySpeed : UbodeWalkSpeed;
        (float rapier, float peak) = GroundSpeed("Rapier", flying);
        _output.WriteLine($"{(flying ? "flying" : "walking")}: Rapier {rapier:F3} m/s (peak {peak:F3}), ubODE {ubode:F3} ({(rapier / ubode - 1f) * 100f:F1}%)");
        Assert.InRange(rapier, ubode * 0.97f, ubode * 1.03f);
        // It gets there without going past it.
        Assert.True(peak <= ubode * 1.03f, $"peak speed {peak}");
        Assert.Empty(log.Errors);
    }

    // AvatarSpeedFactor in the INI wins over Rapier's default: 1 gives back the speed asked for.
    [Fact]
    public void Avatar_SpeedFactorFromTheIni_WinsOverRapiersDefault()
    {
        using ErrorLog log = new ErrorLog();
        (float rapier, _) = GroundSpeed("Rapier", false, new Dictionary<string, string> { { "AvatarSpeedFactor", "1" } });
        _output.WriteLine($"walking with AvatarSpeedFactor=1: {rapier:F3} m/s");
        Assert.InRange(rapier, WalkSpeed * 0.97f, WalkSpeed * 1.01f);
        Assert.Empty(log.Errors);
    }

    // Bullet's avatar covers the same ground as before: its speed factor stays 1.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Avatar_OnBullet_CoversTheSameGroundAsBefore(bool flying)
    {
        using ErrorLog log = new ErrorLog();
        float before = flying ? BulletFlySpeed : BulletWalkSpeed;
        (float bullet, _) = GroundSpeed("BulletUnmanaged", flying);
        _output.WriteLine($"{(flying ? "flying" : "walking")}: Bullet {bullet:F3} m/s, before {before:F3}");
        Assert.InRange(bullet, before * 0.995f, before * 1.005f);
        Assert.Empty(log.Errors);
    }

    // ---- Stopping (symptom 1) ----

    // ubODE, measured as above: walking at full speed, then the key released (the rotation sets stop) and the target
    // set to zero, the avatar slides 0.061 m and is at rest one frame later. Starting from standing, it reaches 79% of its
    // speed in the first frame and 99% in the second.
    private const float UbodeSlide = 0.061f;
    private const float UbodeRestSeconds = 0.091f;
    private const float UbodeStartFirstFrame = 0.79f;

    // Bullet, the same script measured on this branch before the stop change (8f947116).
    private const float BulletSlide = 0.4283f;
    private const float BulletRestSeconds = 0.3636f;

    private sealed class StopRun
    {
        public float Slide;
        public float RestSeconds;
        public List<float> StartSpeeds = new List<float>();
        public float Target;
    }

    // Walk 2 s along +X with the key held, then release: the rotation sets stop and the target goes to zero (ScenePresence
    // does that 250 ms later; that time is the same on every engine and is left out). Watched for 2 s after.
    private static StopRun WalkThenStop(string engine, Dictionary<string, string>? ini = null)
    {
        BSScene scene = MakeScene(engine, ini);
        try
        {
            PhysicsActor avatar = AddAvatar(scene, new Vector3(100f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.05f), false);
            RapierTestScene.Step(scene, 2f);
            StopRun run = new StopRun();
            avatar.TargetVelocity = new Vector3(WalkSpeed, 0f, 0f);
            run.Target = avatar.TargetVelocity.X;
            for (int ii = 0; ii < 22; ii++)
            {
                Frame(scene, avatar, ii, true);
                if (ii < 5)
                    run.StartSpeeds.Add(avatar.Velocity.X);
            }
            avatar.TargetVelocity = Vector3.Zero;
            List<float> x = new List<float> { avatar.Position.X };
            for (int ii = 0; ii < 22; ii++)
            {
                Frame(scene, avatar, ii, false);
                x.Add(avatar.Position.X);
            }
            run.Slide = x[x.Count - 1] - x[0];
            // The frame after which it moves less than 1 mm a frame for good.
            int rest = x.Count - 1;
            while (rest > 0 && Math.Abs(x[rest] - x[rest - 1]) <= 0.001f)
                rest--;
            run.RestSeconds = rest * RapierTestScene.FrameSeconds;
            return run;
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Avatar_StopsLikeUbode()
    {
        using ErrorLog log = new ErrorLog();
        StopRun run = WalkThenStop("Rapier");
        _output.WriteLine($"Rapier: slide {run.Slide:F3} m, at rest after {run.RestSeconds:F3} s; start {string.Join(", ", run.StartSpeeds.Select(v => (v / run.Target).ToString("F2")))} of {run.Target:F3}");
        Assert.InRange(run.Slide, 0f, UbodeSlide + 0.04f);
        Assert.InRange(run.RestSeconds, 0f, UbodeRestSeconds + 0.01f);
        Assert.Empty(log.Errors);
    }

    // Walking start-up stays smooth: no faster than ubODE's first frame by much, and never past the target.
    [Fact]
    public void Avatar_StartsWalkingLikeUbode()
    {
        using ErrorLog log = new ErrorLog();
        StopRun run = WalkThenStop("Rapier");
        _output.WriteLine($"Rapier start: {string.Join(", ", run.StartSpeeds.Select(v => (v / run.Target).ToString("F2")))} of {run.Target:F3}");
        Assert.InRange(run.StartSpeeds[0] / run.Target, UbodeStartFirstFrame - 0.15f, UbodeStartFirstFrame + 0.15f);
        Assert.True(run.StartSpeeds[2] / run.Target > 0.95f, $"third frame {run.StartSpeeds[2]}");
        Assert.True(run.StartSpeeds.Max() / run.Target < 1.02f, $"peak {run.StartSpeeds.Max()}");
        Assert.Empty(log.Errors);
    }

    // The INI wins over Rapier's stop defaults: Bullet's 0.2 s timescale and 0.45 m/s threshold give back its long slide.
    [Fact]
    public void Avatar_StopTuningFromTheIni_WinsOverRapiersDefault()
    {
        using ErrorLog log = new ErrorLog();
        StopRun run = WalkThenStop("Rapier", new Dictionary<string, string>
                    { { "AvatarMotorTimeScale", "0.2" }, { "AvatarStopZeroThreshold", "0.45" } });
        _output.WriteLine($"Rapier with Bullet's stop tuning: slide {run.Slide:F3} m, at rest after {run.RestSeconds:F3} s");
        Assert.True(run.Slide > 0.25f, $"slide {run.Slide}");
        Assert.True(run.RestSeconds > 0.3f, $"rest {run.RestSeconds}");
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Avatar_OnBullet_StopsAsBefore()
    {
        using ErrorLog log = new ErrorLog();
        StopRun run = WalkThenStop("BulletUnmanaged");
        _output.WriteLine($"Bullet: slide {run.Slide:F4} m, at rest after {run.RestSeconds:F4} s; start {string.Join(", ", run.StartSpeeds.Select(v => (v / run.Target).ToString("F3")))}");
        Assert.InRange(run.Slide, BulletSlide - 0.005f, BulletSlide + 0.005f);
        Assert.InRange(run.RestSeconds, BulletRestSeconds - 0.01f, BulletRestSeconds + 0.01f);
        Assert.Empty(log.Errors);
    }
}
