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
/// The ubODE values are constants. They were measured on 2026-11-07 by running the same script on ubODE with a harness
/// kept outside the repository (see .superpowers/sdd/2026-11-06-rapier-opensim-integration/avatar-feel-report.md).
/// ubODE is not referenced by this project. To measure them again:
/// <list type="number">
/// <item>Add ProjectReferences to OpenSim/Region/PhysicsModules/ubOde and ubOdeMeshing to this project, and copy
/// bin/OpenSim.Region.PhysicsModule.ubOde.dll.config beside the test assembly (a None item with CopyToOutputDirectory),
/// so its dllmap finds lib64/ubode.dll or libubode-x86_64.so.</item>
/// <item>Build the scene as RapierTestScene does, with [Startup] physics = ubODE and meshing = ubODEMeshmerizer, an empty
/// [Mesh] section with MeshFileCache = false (ubMeshmerizer enables itself only with one), and an [ODEPhysicsSettings]
/// section. Initialise, AddRegion and RegionLoaded a ubMeshmerizer, then the internal ubOdeModule (made by reflection,
/// type OpenSim.Region.PhysicsModule.ubOde.ubOdeModule), and take the scene from
/// scene.RequestModuleInterface&lt;PhysicsScene&gt;().</item>
/// <item>ubODE steps by wall-clock time since its last call, not by the time passed to Simulate: before each Simulate,
/// set the private field ODEScene.m_lastframe to Util.GetTimeStamp() minus the frame time.</item>
/// <item>Run the scripts below unchanged (flat terrain at 21 m, the same avatar, AddAvatar, SubscribeEvents, rotation
/// noise every frame while a key is held) and read avatar.Position and avatar.Velocity each frame.</item>
/// </list>
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
    private static void Frame(BSScene scene, PhysicsActor avatar, int frame, bool keyHeld,
                                float frameSeconds = RapierTestScene.FrameSeconds)
    {
        if (keyHeld)
        {
            float yaw = (frame % 2 == 0) ? 1e-5f : -1e-5f;
            avatar.Orientation = new Quaternion(0f, 0f, MathF.Sin(yaw / 2f), MathF.Cos(yaw / 2f));
        }
        scene.Simulate(frameSeconds);
    }

    // The ground speed over the last second of a 2 s walk (or flight, 30 m up) along +X, and the highest speed the body
    // reached on the way.
    private static (float speed, float peak) GroundSpeed(string engine, bool flying, Dictionary<string, string>? ini = null,
                                                          bool alwaysRun = false)
    {
        BSScene scene = MakeScene(engine, ini);
        try
        {
            Vector3 start = flying ? new Vector3(60f, 128f, TerrainHeight + 30f)
                                   : new Vector3(100f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.05f);
            PhysicsActor avatar = AddAvatar(scene, start, flying);
            RapierTestScene.Step(scene, flying ? 1f : 2f);

            avatar.SetAlwaysRun = alwaysRun;
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

    // ubODE with always-run, measured as above: 5.116 m/s on the ground and 20.465 m/s flying. ubODE multiplies the
    // horizontal target by 1 / av_movement_divisor_run = 1.25 when running, flying or not (ODECharacter.cs, the
    // m_alwaysRun branch, which has no flying check).
    private const float UbodeRunSpeed = 5.116f;
    private const float UbodeFlyRunSpeed = 20.465f;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Avatar_RunsAtUbodesSpeed(bool flying)
    {
        using ErrorLog log = new ErrorLog();
        float ubode = flying ? UbodeFlyRunSpeed : UbodeRunSpeed;
        (float rapier, float peak) = GroundSpeed("Rapier", flying, null, alwaysRun: true);
        _output.WriteLine($"running{(flying ? ", flying" : "")}: Rapier {rapier:F3} m/s (peak {peak:F3}), ubODE {ubode:F3} ({(rapier / ubode - 1f) * 100f:F1}%)");
        Assert.InRange(rapier, ubode * 0.97f, ubode * 1.03f);
        Assert.True(peak <= ubode * 1.03f, $"peak speed {peak}");
        Assert.Empty(log.Errors);
    }

    // Bullet, measured before this change (at 0c065d57) with the same script: running is AvatarAlwaysRunFactor 1.3 on
    // the ground, less what each orientation set takes back; flying ignores always-run.
    private const float BulletRunSpeed = 4.831f;
    private const float BulletFlyRunSpeed = 14.968f;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Avatar_OnBullet_RunsAsBefore(bool flying)
    {
        using ErrorLog log = new ErrorLog();
        float before = flying ? BulletFlyRunSpeed : BulletRunSpeed;
        (float bullet, _) = GroundSpeed("BulletUnmanaged", flying, null, alwaysRun: true);
        _output.WriteLine($"Bullet running{(flying ? ", flying" : "")}: {bullet:F3} m/s, before {before:F3}");
        Assert.InRange(bullet, before * 0.995f, before * 1.005f);
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

    // ubODE at other frame times (the launcher offers 5 to 55 fps), measured the same way with the frame passed to
    // Simulate (and ubODE's wall clock moved on by it): the slide is 0.060 m at 0.2, 0.125, 0.0909, 0.05 and 1/55 s,
    // and it is at rest after 0.200, 0.125, 0.091, 0.050 and 0.036 s, with no reversal.
    private const float UbodeSlideAtAnyFrameRate = 0.060f;

    // Bullet, the same script measured on this branch before the stop change (8f947116).
    private const float BulletSlide = 0.4283f;
    private const float BulletRestSeconds = 0.3636f;

    private sealed class StopRun
    {
        public float Slide;
        public float RestSeconds;
        public List<float> StartSpeeds = new List<float>();
        public float Target;
        // The largest step backwards in X while stopping (a reversal), and the largest speed after the first stopping
        // frame relative to the speed before it (ringing makes it grow again).
        public float WorstReversal;
        public bool SpeedRoseWhileStopping;
    }

    // Walk 2 s along +X with the key held, then release: the rotation sets stop and the target goes to zero (ScenePresence
    // does that 250 ms later; that time is the same on every engine and is left out). Watched for 2 s after.
    private static StopRun WalkThenStop(string engine, Dictionary<string, string>? ini = null,
                                        float frameSeconds = RapierTestScene.FrameSeconds)
    {
        BSScene scene = MakeScene(engine, ini);
        try
        {
            PhysicsActor avatar = AddAvatar(scene, new Vector3(100f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.05f), false);
            RapierTestScene.Step(scene, 2f);
            StopRun run = new StopRun();
            avatar.TargetVelocity = new Vector3(WalkSpeed, 0f, 0f);
            run.Target = avatar.TargetVelocity.X;
            int frames = (int)Math.Round(2f / frameSeconds);
            for (int ii = 0; ii < frames; ii++)
            {
                Frame(scene, avatar, ii, true, frameSeconds);
                if (ii < 5)
                    run.StartSpeeds.Add(avatar.Velocity.X);
            }
            avatar.TargetVelocity = Vector3.Zero;
            List<float> x = new List<float> { avatar.Position.X };
            float lastSpeed = avatar.Velocity.X;
            for (int ii = 0; ii < frames; ii++)
            {
                Frame(scene, avatar, ii, false, frameSeconds);
                x.Add(avatar.Position.X);
                float speed = avatar.Velocity.X;
                if (speed > lastSpeed + 0.01f || speed < -0.01f)
                    run.SpeedRoseWhileStopping = true;
                lastSpeed = speed;
                run.WorstReversal = Math.Max(run.WorstReversal, x[x.Count - 2] - x[x.Count - 1]);
            }
            run.Slide = x[x.Count - 1] - x[0];
            // The frame after which it moves less than 1 mm a frame for good.
            int rest = x.Count - 1;
            while (rest > 0 && Math.Abs(x[rest] - x[rest - 1]) <= 0.001f)
                rest--;
            run.RestSeconds = rest * frameSeconds;
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

    // The stop holds at the launcher's slowest and fastest frame rates (5 and 55 fps) as well as the default one: no
    // ringing on the way up or down, no step backwards, a slide no longer than ubODE's at that rate, and at rest within
    // one frame or 0.1 s, whichever is longer.
    [Theory]
    [InlineData(0.2f)]
    [InlineData(RapierTestScene.FrameSeconds)]
    [InlineData(1f / 55f)]
    public void Avatar_StopsLikeUbode_AtEveryFrameRate(float frameSeconds)
    {
        using ErrorLog log = new ErrorLog();
        StopRun run = WalkThenStop("Rapier", null, frameSeconds);
        string start = string.Join(", ", run.StartSpeeds.Select(v => (v / run.Target).ToString("F2")));
        _output.WriteLine($"Rapier at {frameSeconds:F4} s frames: slide {run.Slide:F3} m, at rest after {run.RestSeconds:F3} s, "
                    + $"worst reversal {run.WorstReversal:F4} m, speed rose {run.SpeedRoseWhileStopping}; start {start}");
        Assert.InRange(run.Slide, 0f, UbodeSlideAtAnyFrameRate + 0.01f);
        Assert.True(run.WorstReversal < 0.001f, $"the avatar stepped back {run.WorstReversal} m while stopping");
        Assert.False(run.SpeedRoseWhileStopping, "the speed rose again, or reversed, while stopping");
        for (int ii = 0; ii < run.StartSpeeds.Count; ii++)
        {
            Assert.True(run.StartSpeeds[ii] / run.Target < 1.02f, $"start frame {ii}: {run.StartSpeeds[ii]}, past the target");
            if (ii > 0)
                Assert.True(run.StartSpeeds[ii] >= run.StartSpeeds[ii - 1] - 0.05f, $"start frame {ii}: the speed fell back");
        }
        Assert.InRange(run.RestSeconds, 0f, Math.Max(frameSeconds, 0.1f) + 0.01f);
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

    // Setting AvatarMotorTimeConstant to 0 while the region runs (physics set) hands the motor back to
    // AvatarMotorTimeScale at once, not to the timescale the time constant last worked out.
    [Fact]
    public void Avatar_MotorTimeConstantSetToZeroAtRunTime_FallsBackToTheTimeScale()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = MakeScene("Rapier", null);
        try
        {
            PhysicsActor avatar = AddAvatar(scene, new Vector3(100f, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.05f), false);
            RapierTestScene.Step(scene, 2f);
            avatar.TargetVelocity = new Vector3(WalkSpeed, 0f, 0f);
            for (int ii = 0; ii < 22; ii++)
                Frame(scene, avatar, ii, true);
            Assert.True(BSParam.TryGetParameter("AvatarMotorTimeConstant", out BSParam.ParameterDefnBase defn));
            defn.SetValue(scene, "0");
            avatar.TargetVelocity = Vector3.Zero;
            float x0 = avatar.Position.X;
            for (int ii = 0; ii < 22; ii++)
                Frame(scene, avatar, ii, false);
            float slide = avatar.Position.X - x0;
            _output.WriteLine($"Rapier, time constant set to 0 at run time: slide {slide:F3} m");
            // The linear 0.2 s timescale slides about 0.285 m (Avatar_MotorTimeConstantFromTheIni_WinsOverRapiersDefault);
            // the timescale the time constant last left behind (about 0.114 s) would slide about 0.05 m.
            Assert.True(slide > 0.2f, $"slide {slide}");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // The INI wins over Rapier's motor default on its own: no time constant brings back the linear 0.2 s timescale and
    // its long slide, while the threshold keeps Rapier's 0.7.
    [Fact]
    public void Avatar_MotorTimeConstantFromTheIni_WinsOverRapiersDefault()
    {
        using ErrorLog log = new ErrorLog();
        StopRun run = WalkThenStop("Rapier", new Dictionary<string, string> { { "AvatarMotorTimeConstant", "0" } });
        _output.WriteLine($"Rapier without the time constant: slide {run.Slide:F3} m, at rest after {run.RestSeconds:F3} s; "
                    + $"threshold {BSParam.AvatarStopZeroThreshold}");
        Assert.True(run.Slide > 0.2f, $"slide {run.Slide}");
        Assert.True(run.RestSeconds > 0.15f, $"rest {run.RestSeconds}");
        Assert.Equal(0.7f, BSParam.AvatarStopZeroThreshold);
        Assert.Empty(log.Errors);
    }

    // Each Rapier avatar default, set alone in the INI, wins; the others keep Rapier's own. Values read after the scene
    // is made (BSParam is process-wide, and the scene made last sets it).
    [Theory]
    [InlineData("AvatarSpeedFactor", "0.9")]
    [InlineData("AvatarMotorTimeScale", "0.3")]
    [InlineData("AvatarMotorTimeConstant", "0.08")]
    [InlineData("AvatarStopTimeConstant", "0.05")]
    [InlineData("AvatarStopZeroThreshold", "0.45")]
    [InlineData("AvatarStepHeight", "0.7")]
    [InlineData("AvatarAlwaysRunFactor", "1.4")]
    [InlineData("AvatarRunFactorWhenFlying", "false")]
    [InlineData("AvatarStepsNeedAWalkableTop", "false")]
    public void Avatar_EachRapierDefault_LosesToTheIniOnItsOwn(string name, string value)
    {
        using ErrorLog log = new ErrorLog();
        Dictionary<string, string> rapier = new Dictionary<string, string>
        {
            { "AvatarSpeedFactor", (1f / 1.3f).ToString("R") },
            { "AvatarMotorTimeScale", 0.2f.ToString("R") },
            { "AvatarMotorTimeConstant", 0.0565f.ToString("R") },
            { "AvatarStopTimeConstant", 0.019f.ToString("R") },
            { "AvatarStopZeroThreshold", 0.7f.ToString("R") },
            { "AvatarStepHeight", 0.5f.ToString("R") },
            { "AvatarAlwaysRunFactor", 1.25f.ToString("R") },
            { "AvatarRunFactorWhenFlying", true.ToString() },
            { "AvatarStepsNeedAWalkableTop", true.ToString() },
        };
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { name, value } });
        try
        {
            foreach (KeyValuePair<string, string> kvp in rapier)
            {
                Assert.True(BSParam.TryGetParameter(kvp.Key, out BSParam.ParameterDefnBase defn), kvp.Key);
                string expected = kvp.Key == name ? value : kvp.Value;
                string actual = defn.GetValue(scene);
                if (bool.TryParse(expected, out bool expectedBool))
                    Assert.Equal(expectedBool, bool.Parse(actual));
                else
                    Assert.Equal(float.Parse(expected), float.Parse(actual), 5);
            }
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
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

    // ---- Walls and steps (symptom 3) ----

    // ubODE, measured as above: walking into a wall from ten approach distances, the avatar never rose more than 4 mm.
    private const float UbodeWallRise = 0.004f;

    // Bullet, the same script (approach from x = 106) measured before the wall change (at 1d49bc91, the commit before
    // these): its box avatar climbs the wall by BSActorAvatarMove.WalkUpStairs, as it always has. The dllmap loads a
    // different BulletSim on each platform (Bullet 3.26 on Windows, 2.84 on Linux), and they climb by different amounts;
    // the value is keyed on the loaded library's version (LoadedBullet).
    private static float BulletWallRise
    {
        get { return LoadedBullet.Pick(new Dictionary<string, float> { { "1.3,3.26", 0.4332f }, { "1.0,2.84", 1.5412f } }); }
    }

    private const uint WallId = 1002;
    private const uint StepId = 1003;

    private static void AddBox(BSScene scene, uint id, Vector3 center, Vector3 size)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        pbs.Scale = size;
        scene.AddPrimShape("box" + id, pbs, center, size, Quaternion.Identity, false, id);
    }

    // Walk along +X from startX for the given time with the key held. Returns the highest rise of the avatar over where
    // it stood, its rise at the end, and where it ended in X.
    private static (float maxRise, float endRise, float endX) WalkInto(BSScene scene, float startX, float seconds)
    {
        PhysicsActor avatar = AddAvatar(scene, new Vector3(startX, 128f, TerrainHeight + AvatarSize.Z / 2f + 0.05f), false);
        RapierTestScene.Step(scene, 2f);
        float startZ = avatar.Position.Z;
        float maxRise = 0f;
        avatar.TargetVelocity = new Vector3(WalkSpeed, 0f, 0f);
        int frames = (int)Math.Round(seconds / RapierTestScene.FrameSeconds);
        for (int ii = 0; ii < frames; ii++)
        {
            Frame(scene, avatar, ii, true);
            maxRise = Math.Max(maxRise, avatar.Position.Z - startZ);
        }
        return (maxRise, avatar.Position.Z - startZ, avatar.Position.X);
    }

    // A 6 m high, 2 m thick wall whose near face is at x = 110, walked into from ten distances (so the touch falls at
    // different points of the frame): it is not a step.
    private static float WorstWallRise(string engine, int approaches, float thickness = 2f,
                                        Dictionary<string, string>? ini = null, float ledgeDepth = 0f, float ledgeHeight = 0f)
    {
        float worst = 0f;
        for (int ii = 0; ii < approaches; ii++)
        {
            BSScene scene = MakeScene(engine, ini);
            try
            {
                AddBox(scene, WallId, new Vector3(110f + thickness / 2f, 128f, TerrainHeight + 3f), new Vector3(thickness, 20f, 6f));
                if (ledgeDepth > 0f)
                    AddBox(scene, StepId, new Vector3(110f - ledgeDepth / 2f, 128f, TerrainHeight + ledgeHeight / 2f),
                            new Vector3(ledgeDepth, 20f, ledgeHeight));
                (float rise, _, float endX) = WalkInto(scene, 106f + ii * 0.037f, 3f);
                Assert.True(endX > 109f - ledgeDepth, $"the avatar stopped at x {endX}, short of the wall");
                worst = Math.Max(worst, rise);
            }
            finally
            {
                scene.Dispose();
            }
        }
        return worst;
    }

    // A 0.25 m step turned 30 degrees to the path, and one that is a physical (dynamic) box, are still climbed with the
    // room-to-stand ray.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Avatar_ClimbsARotatedOrPhysicalStep(bool physical)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = MakeScene("Rapier", null);
        try
        {
            const float height = 0.25f;
            Vector3 size = new Vector3(3f, 8f, height);
            PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
            pbs.Scale = size;
            Quaternion rotation = physical ? Quaternion.Identity : Quaternion.CreateFromEulers(0f, 0f, 30f * MathF.PI / 180f);
            scene.AddPrimShape("step", pbs, new Vector3(128.5f + (physical ? 0f : 0.5f), 128f, TerrainHeight + height / 2f),
                                size, rotation, physical, StepId);
            RapierTestScene.Step(scene, 0.5f);
            (float maxRise, _, float endX) = WalkInto(scene, 124f, 3f);
            _output.WriteLine($"{(physical ? "physical" : "rotated")} step: ended at x {endX:F3}, highest {maxRise:F3} m");
            Assert.True(endX > 127.5f, $"the avatar stopped at x {endX}, before the step");
            Assert.InRange(maxRise, height - 0.05f, height + 0.1f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // A wall with a shallow ledge in front of it (a skirting board, a plinth or a kerb strip, 1 to 4 cm deep and 0.25 or
    // 0.3 m high, a separate prim against the wall) is not a step: there is no room to stand on the ledge.
    [Theory]
    [InlineData(0.01f, 0.25f)]
    [InlineData(0.03f, 0.25f)]
    [InlineData(0.04f, 0.25f)]
    [InlineData(0.01f, 0.3f)]
    [InlineData(0.03f, 0.3f)]
    [InlineData(0.04f, 0.3f)]
    public void Avatar_WalkingIntoAWallWithALedge_DoesNotClimbIt(float ledgeDepth, float ledgeHeight)
    {
        using ErrorLog log = new ErrorLog();
        float worst = WorstWallRise("Rapier", 5, 2f, null, ledgeDepth, ledgeHeight);
        _output.WriteLine($"Rapier: worst rise walking into the wall with a {ledgeHeight} m x {ledgeDepth} m ledge {worst:F3} m");
        Assert.True(worst < 0.05f, $"the avatar rose {worst} m at the ledge");
        Assert.Empty(log.Errors);
    }

    // A thin wall (3 cm, a glass pane or a partition) is not a step either, for all that the probe looks only 1 cm past
    // the face.
    [Fact]
    public void Avatar_WalkingIntoAThinWall_DoesNotClimbIt()
    {
        using ErrorLog log = new ErrorLog();
        float worst = WorstWallRise("Rapier", 5, 0.03f);
        _output.WriteLine($"Rapier: worst rise walking into the 3 cm wall {worst:F3} m");
        Assert.True(worst < 0.05f, $"the avatar rose {worst} m walking into the thin wall");
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Avatar_WalkingIntoAWall_DoesNotClimbIt()
    {
        using ErrorLog log = new ErrorLog();
        float worst = WorstWallRise("Rapier", 10);
        _output.WriteLine($"Rapier: worst rise walking into the wall {worst:F3} m (ubODE {UbodeWallRise:F3})");
        Assert.True(worst < 0.05f, $"the avatar rose {worst} m walking into the wall");
        Assert.Empty(log.Errors);
    }

    // AvatarStepsNeedAWalkableTop=false in the INI wins over Rapier's default: the old step test is back, and with the
    // old 0.999 m step height the avatar climbs the wall again.
    [Fact]
    public void Avatar_WallFixFromTheIni_CanBeTurnedOff()
    {
        using ErrorLog log = new ErrorLog();
        float worst = WorstWallRise("Rapier", 10, 2f, new Dictionary<string, string>
                    { { "AvatarStepsNeedAWalkableTop", "false" }, { "AvatarStepHeight", "0.999" } });
        _output.WriteLine($"Rapier without the wall fix: worst rise {worst:F3} m");
        Assert.True(worst > 0.1f, $"the avatar rose only {worst} m");
        Assert.Empty(log.Errors);
    }

    // A volume-detect prim (a sensor: a trigger zone, a door's detector) over the edge of a 0.3 m step does not hide the
    // step's top from the walkable-top ray: the step is still climbed.
    [Fact]
    public void Avatar_ClimbsAStepUnderAVolumeDetectPrim()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = MakeScene("Rapier", null);
        try
        {
            AddBox(scene, StepId, new Vector3(129f, 128f, TerrainHeight + 0.15f), new Vector3(4f, 8f, 0.3f));
            const uint sensorId = 1004;
            AddBox(scene, sensorId, new Vector3(127.5f, 128f, TerrainHeight + 0.3f + 0.5f), new Vector3(1f, 4f, 1f));
            scene.PhysObjects[sensorId].SetVolumeDetect(1);
            RapierTestScene.Step(scene, 0.2f);
            (float maxRise, _, float endX) = WalkInto(scene, 124f, 2f);
            _output.WriteLine($"Rapier: ended at x {endX:F3}, highest {maxRise:F3} m");
            Assert.True(endX > 127.5f, $"the avatar stopped at x {endX}, before the step");
            Assert.InRange(maxRise, 0.3f - 0.05f, 0.3f + 0.1f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // A real 0.3 m step, 4 m deep, with the same wall standing on the ground right behind it: the step is climbed, and
    // the wall above it is not.
    [Fact]
    public void Avatar_ClimbsAStepNextToAWall_ButNotTheWall()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = MakeScene("Rapier", null);
        try
        {
            AddBox(scene, StepId, new Vector3(129f, 128f, TerrainHeight + 0.15f), new Vector3(4f, 8f, 0.3f));
            AddBox(scene, WallId, new Vector3(132f, 128f, TerrainHeight + 3f), new Vector3(2f, 20f, 6f));
            (float maxRise, float endRise, float endX) = WalkInto(scene, 124f, 4f);
            _output.WriteLine($"Rapier: ended at x {endX:F3}, rise {endRise:F3} m, highest {maxRise:F3} m");
            Assert.True(endX > 128f, $"the avatar stopped at x {endX}, before the step");
            Assert.InRange(endRise, 0.3f - 0.05f, 0.3f + 0.05f);
            Assert.True(maxRise < 0.3f + 0.1f, $"the avatar rose {maxRise} m, above the step");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // Low obstacles, 2 m deep, whose near face is at x = 127, walked into from x = 124 for 4 s. Stairs (0.15 to 0.3 m, as
    // a box or as a cylinder, the round meshed step) are climbed to their top. A 0.5 m block (a low wall, a bench) and a
    // 0.8 m one (a table, a counter) are not: the avatar stays in front of them on the ground.
    [Theory]
    [InlineData(0.15f, false, true)]
    [InlineData(0.2f, false, true)]
    [InlineData(0.25f, false, true)]
    [InlineData(0.3f, false, true)]
    [InlineData(0.3f, true, true)]
    // A board 0.3 m high and only 4 cm or 2 cm deep, as a door sill or a kerb made of a thin prim, is a step too.
    [InlineData(0.3f, false, true, 0.04f)]
    [InlineData(0.3f, false, true, 0.02f)]
    [InlineData(0.5f, false, false)]
    [InlineData(0.8f, false, false)]
    public void Avatar_ClimbsStairsButNotLowBlocks(float height, bool cylinder, bool climbs, float depth = 2f)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = MakeScene("Rapier", null);
        try
        {
            PrimitiveBaseShape pbs = cylinder ? PrimitiveBaseShape.CreateCylinder() : PrimitiveBaseShape.CreateBox();
            // The cylinder is 4 m across, so its round face meets the avatar's path at x = 127 too.
            Vector3 size = cylinder ? new Vector3(4f, 4f, height) : new Vector3(depth, 8f, height);
            pbs.Scale = size;
            scene.AddPrimShape("obstacle", pbs, new Vector3(127f + size.X / 2f, 128f, TerrainHeight + height / 2f), size,
                                Quaternion.Identity, false, StepId);
            (float maxRise, float endRise, float endX) = WalkInto(scene, 124f, 4f);
            _output.WriteLine($"{(cylinder ? "cylinder" : "box")} {height} m ({size.X} m deep): ended at x {endX:F3}, rise {endRise:F3} m, highest {maxRise:F3} m");
            if (climbs)
            {
                Assert.True(endX > 127.5f, $"the avatar stopped at x {endX}, before the step");
                // On top, and no higher than a little over it (it walks off the far side later, so the end height is 0).
                Assert.InRange(maxRise, height - 0.05f, height + 0.1f);
            }
            else
            {
                Assert.True(endX < 127f, $"the avatar went on to x {endX}, onto or over the block");
                Assert.True(maxRise < 0.05f, $"the avatar rose {maxRise} m at the block");
            }
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Avatar_OnBullet_MeetsAWallAsBefore()
    {
        using ErrorLog log = new ErrorLog();
        float rise = WorstWallRise("BulletUnmanaged", 1);
        _output.WriteLine($"Bullet: rise walking into the wall {rise:F4} m");
        Assert.InRange(rise, BulletWallRise - 0.01f, BulletWallRise + 0.01f);
        Assert.Empty(log.Errors);
    }
}
