using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

/// <summary>
/// Things moving across a mesh floor made of tiles do not catch on the seams between the tiles' triangles. BulletS builds
/// every mesh with FIX_INTERNAL_EDGES (flags 144), and DreamGrid's Parry patch (ThirdParty/rapier/patches) keeps the data
/// for it in 4 bytes per triangle instead of about 42. These tests guard the behaviour, so they pass with the patched and
/// the unpatched library alike. The *_WithoutTheFix tests build the same floor without FIX_INTERNAL_EDGES and check that
/// the thresholds do catch what the fix prevents.
///
/// On a flat floor every stored edge angle is 0, so those tests see whether the fix is on, not whether the angles are
/// right. The *_OnACurvedMeshFloor tests run on a wavy floor, where every seam has an angle, and compare with the
/// unpatched library's results (constants measured on 2026-11-08 with the libraries of b80d2bdf).
///
/// The scenarios were first run in Rapier alone (a Rust harness kept outside the repository): a flat tiled floor, a
/// ball, a sliding box, a box on a vertex, avatars, and a wavy floor.
/// </summary>
public class MeshSeamTests
{
    private const float FloorZ = 50f;
    private const int Tiles = 30;                       // 30 x 30 one-metre tiles centred on (128, 128)
    private const float Step = 1f / 60f;

    private readonly ITestOutputHelper _output;

    public MeshSeamTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Tiles of 1 m, each with its own four vertices (as meshers and sculpt maps emit them; the trimesh merges them),
    // their top at z = 0 in the shape's frame (or at Wave(x, y) when curved). The body puts it at FloorZ.
    private static void TiledFloor(out float[] vertices, out int[] indices, bool curved = false)
    {
        List<float> v = new List<float>();
        List<int> i = new List<int>();
        float o = -Tiles / 2f;
        for (int x = 0; x < Tiles; x++)
        {
            for (int y = 0; y < Tiles; y++)
            {
                int first = v.Count / 3;
                float x0 = o + x, y0 = o + y;
                foreach ((float px, float py) in new[] { (x0, y0), (x0 + 1f, y0), (x0 + 1f, y0 + 1f), (x0, y0 + 1f) })
                    v.AddRange(new[] { px, py, curved ? Wave(px, py) : 0f });
                i.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
            }
        }
        vertices = v.ToArray();
        indices = i.ToArray();
    }

    // The wavy floor: about +-0.3 m, so seams bend by a few degrees each, convex and concave.
    private static float Wave(float x, float y)
    {
        return 0.15f * (MathF.Sin(0.7f * x) + MathF.Cos(0.5f * y));
    }

    private static BSScene MakeScene()
    {
        BSScene scene = RapierTestScene.Create();
        // Terrain well below the floor, so only the mesh is touched.
        float[] map = new float[256 * 256];
        Array.Fill(map, 10f);
        scene.SetTerrain(map);
        RapierTestScene.Step(scene, 0.3f);
        return scene;
    }

    // The tiled floor as a static body, built with the flags BulletS uses unless withFix is false.
    private static BulletBody Floor(BSScene scene, float friction, bool withFix = true, bool curved = false)
    {
        TiledFloor(out float[] vertices, out int[] indices, curved);
        if (!withFix)
            BSAPIRapier.TrimeshFlagsOverrideForTests = RapierNative.TrimeshMergeDuplicateVertices;
        BulletShape mesh;
        try
        {
            mesh = scene.PE.CreateMeshShape(scene.World, indices.Length, indices, vertices.Length / 3, vertices);
        }
        finally
        {
            BSAPIRapier.TrimeshFlagsOverrideForTests = null;
        }
        Assert.True(mesh.HasPhysicalShape);
        BulletBody body = scene.PE.CreateBodyWithDefaultMotionState(mesh, 9000, new Vector3(128f, 128f, FloorZ), Quaternion.Identity);
        InWorld(scene, body);
        scene.PE.SetFriction(body, friction);
        return body;
    }

    private static void InWorld(BSScene scene, BulletBody body)
    {
        Assert.True(scene.PE.AddObjectToWorld(scene.World, body));
        Assert.True(scene.PE.SetCollisionGroupMask(body, 0xffff, 0xffff));
    }

    private static BulletBody Dynamic(BSScene scene, BSPhysicsShapeType type, Vector3 size, Vector3 position, float friction)
    {
        BulletShape shape = scene.PE.BuildNativeShape(scene.World, new ShapeData { Type = type, Scale = size });
        BulletBody body = scene.PE.CreateBodyFromShape(scene.World, shape, 9101, position, Quaternion.Identity);
        scene.PE.SetMassProps(body, 1f, scene.PE.CalculateLocalInertia(shape, 1f));
        InWorld(scene, body);
        scene.PE.SetFriction(body, friction);
        scene.PE.ForceActivationState(body, ActivationState.DISABLE_DEACTIVATION);
        return body;
    }

    private struct Run
    {
        public float MaxVz;         // largest vertical speed
        public float MaxRise;       // highest the body got above where it settled
        public float MaxSpin;       // largest angular speed
        public Vector3 Start;
        public Vector3 End;
    }

    // Lets the body settle for 1 s, then gives it this velocity and follows it for this long.
    private static Run Follow(BSScene scene, BulletBody body, Vector3 velocity, float seconds)
    {
        for (float t = 0f; t < 1f; t += Step)
            scene.PE.PhysicsStep(scene.World, Step, 0, Step, out int _, out int _);
        Run run = new Run { Start = scene.PE.GetPosition(body) };
        scene.PE.SetLinearVelocity(body, velocity);
        for (float t = 0f; t < seconds; t += Step)
        {
            scene.PE.PhysicsStep(scene.World, Step, 0, Step, out int _, out int _);
            run.MaxVz = Math.Max(run.MaxVz, Math.Abs(scene.PE.GetLinearVelocity(body).Z));
            run.MaxRise = Math.Max(run.MaxRise, scene.PE.GetPosition(body).Z - run.Start.Z);
            run.MaxSpin = Math.Max(run.MaxSpin, scene.PE.GetAngularVelocity(body).Length());
        }
        run.End = scene.PE.GetPosition(body);
        return run;
    }

    private void Report(string what, Run run)
    {
        _output.WriteLine($"{what}: max |vz| {run.MaxVz:F4} m/s, max rise {run.MaxRise:F4} m, max |w| {run.MaxSpin:F4} rad/s, "
                          + $"moved {Vector3.Distance(run.Start, run.End):F3} m, end ({run.End.X:F4}, {run.End.Y:F4}, {run.End.Z:F4})");
    }

    // Where the scenarios start: over the floor's south-west part, a little above the curved floor's highest point.
    private static Vector3 StartAt(float aboveFloor, bool curved)
    {
        return new Vector3(117.3f, 120.37f, FloorZ + aboveFloor + (curved ? 0.35f : 0f));
    }

    private static string Name(string what, bool withFix, bool curved)
    {
        return what + (curved ? ", curved floor" : "") + (withFix ? "" : ", without the fix");
    }

    // A ball of 0.5 m radius rolling diagonally across the seams at 5 m/s.
    private Run RollBall(bool withFix, bool curved = false)
    {
        BSScene scene = MakeScene();
        try
        {
            Floor(scene, 0.5f, withFix, curved);
            BulletBody ball = Dynamic(scene, BSPhysicsShapeType.SHAPE_SPHERE, Vector3.One, StartAt(0.5f, curved), 0.5f);
            Run run = Follow(scene, ball, new Vector3(5f, 1.7f, 0f), 3f);
            Report(Name("ball", withFix, curved), run);
            return run;
        }
        finally
        {
            scene.Dispose();
        }
    }

    // A 1 x 1 x 0.5 m box sliding diagonally across the seams at 6 m/s, friction 0.1.
    private Run SlideBox(bool withFix, bool curved = false)
    {
        BSScene scene = MakeScene();
        try
        {
            Floor(scene, 0.1f, withFix, curved);
            BulletBody box = Dynamic(scene, BSPhysicsShapeType.SHAPE_BOX, new Vector3(1f, 1f, 0.5f), StartAt(0.25f, curved), 0.1f);
            Run run = Follow(scene, box, new Vector3(6f, 2.1f, 0f), 3f);
            Report(Name("box", withFix, curved), run);
            return run;
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Ball_RollsAcrossTiledMeshFloor_WithoutHopping()
    {
        using ErrorLog log = new ErrorLog();
        Run run = RollBall(true);
        Assert.True(run.MaxVz < 0.3f, $"the ball hopped: max |vz| {run.MaxVz}");
        Assert.True(run.MaxRise < 0.01f, $"the ball rose {run.MaxRise} m");
        // It keeps rolling: 10.0 m in 3 s (9.5 m on a floor without the fix, which slows it at the seams).
        Assert.True(Vector3.Distance(run.Start, run.End) > 9f, $"the ball moved only {Vector3.Distance(run.Start, run.End)} m");
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Box_SlidesAcrossTiledMeshFloor_WithoutCatchingOrTumbling()
    {
        using ErrorLog log = new ErrorLog();
        Run run = SlideBox(true);
        Assert.True(run.MaxSpin < 0.1f, $"the box tumbled: max |w| {run.MaxSpin}");
        Assert.True(run.MaxRise < 0.01f, $"the box rose {run.MaxRise} m");
        Assert.True(run.MaxVz < 0.1f, $"the box hopped: max |vz| {run.MaxVz}");
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Ball_Box_And_Avatar_WithoutTheFix_DoCatchOnTheSeams()
    {
        // The same runs on a floor built without FIX_INTERNAL_EDGES fail the thresholds of the other tests: they can see
        // catching. Measured: the ball hops at 0.55 m/s, the box rises 9 cm and spins at 3.9 rad/s, the avatar hops at
        // 0.89 m/s (0.08, 0.0006 and 0 with the fix).
        using ErrorLog log = new ErrorLog();
        Run ball = RollBall(false);
        Assert.True(ball.MaxVz >= 0.3f || ball.MaxRise >= 0.01f, $"the ball did not hop: max |vz| {ball.MaxVz}, rise {ball.MaxRise}");
        Run box = SlideBox(false);
        Assert.True(box.MaxSpin >= 0.1f || box.MaxRise >= 0.01f, $"the box did not catch: max |w| {box.MaxSpin}, rise {box.MaxRise}");
        (float maxVz, float zRange, _) = Walk(false);
        Assert.True(maxVz >= 0.1f || zRange >= 0.01f, $"the avatar did not hop: max |vz| {maxVz}, height range {zRange}");
        Assert.Empty(log.Errors);
    }

    // A resting check only: in Rapier alone the box also stays still on a floor without the fix, so this test guards
    // against a regression (a box creeping or turning at rest) and does not tell the fix's builds apart.
    [Fact]
    public void Box_RestingOnASeamVertex_StaysPut()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = MakeScene();
        try
        {
            Floor(scene, 0.5f);
            // Its centre is over the vertex four tiles share.
            BulletBody box = Dynamic(scene, BSPhysicsShapeType.SHAPE_BOX, new Vector3(1f, 1f, 0.5f), new Vector3(128f, 128f, FloorZ + 0.25f), 0.5f);
            Run run = Follow(scene, box, Vector3.Zero, 10f);
            Report("box on a vertex", run);
            Assert.True(Vector3.Distance(run.Start, run.End) < 0.001f, $"the box drifted {Vector3.Distance(run.Start, run.End)} m");
            Assert.True(run.MaxSpin < 0.01f, $"the box turned: max |w| {run.MaxSpin}");
            Assert.InRange(run.End.Z, FloorZ + 0.25f - 0.02f, FloorZ + 0.25f + 0.02f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // An avatar walking diagonally across the tiles as ScenePresence drives it: TargetVelocity once, then frames. Returns
    // the largest vertical speed while walking, how far its height strayed, and its ground speed over the last second.
    private (float maxVz, float zRange, float speed) Walk(bool withFix)
    {
        BSScene scene = MakeScene();
        try
        {
            Floor(scene, 0.5f, withFix);
            Vector3 size = new Vector3(0.45f, 0.6f, 1.9f);
            PhysicsActor avatar = scene.AddAvatar(1001, "avatar", new Vector3(117.3f, 120.37f, FloorZ + size.Z / 2f + 0.05f), size, 0f, false);
            Assert.NotNull(avatar);
            avatar.SubscribeEvents(100);
            RapierTestScene.Step(scene, 2f);

            avatar.TargetVelocity = new Vector3(4.096f, 1.4f, 0f);
            const int frames = 44;      // 4 s
            float maxVz = 0f, zMin = float.MaxValue, zMax = float.MinValue;
            List<Vector3> positions = new List<Vector3>();
            for (int ii = 0; ii < frames; ii++)
            {
                scene.Simulate(RapierTestScene.FrameSeconds);
                positions.Add(avatar.Position);
                if (ii >= 11)       // after the first second, once it is up to speed
                {
                    maxVz = Math.Max(maxVz, Math.Abs(avatar.Velocity.Z));
                    zMin = Math.Min(zMin, avatar.Position.Z);
                    zMax = Math.Max(zMax, avatar.Position.Z);
                }
            }
            Vector3 d = positions[frames - 1] - positions[frames - 12];
            float speed = new Vector2(d.X, d.Y).Length() / (11 * RapierTestScene.FrameSeconds);
            _output.WriteLine($"avatar{(withFix ? "" : ", without the fix")}: max |vz| {maxVz:F4} m/s, height range {zMax - zMin:F4} m, "
                              + $"ground speed {speed:F3} m/s, end {positions[frames - 1]}");
            return (maxVz, zMax - zMin, speed);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Avatar_WalksAcrossTheSeams_WithoutHoppingOrStopping()
    {
        using ErrorLog log = new ErrorLog();
        (float maxVz, float zRange, float speed) = Walk(true);
        Assert.True(maxVz < 0.1f, $"the avatar hopped: max |vz| {maxVz}");
        Assert.True(zRange < 0.01f, $"the avatar's height varied by {zRange} m");
        // ubODE's walking speed is 3.149 m/s along a 4.096 m/s target (AvatarFeelTests); this target is a little longer.
        Assert.True(speed > 3f, $"the avatar slowed to {speed} m/s");
        Assert.Empty(log.Errors);
    }

    // The unpatched library on the curved floor (b80d2bdf's libraries, Windows and Linux alike): where the ball and the
    // box end after 3 s, and the box's largest spin. The thresholds below were set by also running a deliberately broken
    // patch (the edge angle's sign flipped, or doubled) through these tests:
    //   patched:      ball 3 mm off, box 6.5 cm off, box spin -9 %
    //   wrong sign:   ball 21 mm off, box 26 cm off, box spin -27 %
    //   doubled:      ball 25 mm off, box 13 cm off, box spin -28 %
    // so a wrong angle fails the ball's 1 cm and the spin's 15 %, and the box's 10 cm in both cases tried.
    private static readonly Vector3 UnpatchedBallEnd = new Vector3(125.1920f, 123.9518f, 50.3012f);
    private static readonly Vector3 UnpatchedBoxEnd = new Vector3(130.3282f, 124.9736f, 50.4054f);
    private const float UnpatchedBoxSpin = 1.0967f;

    [Fact]
    public void Ball_RollsOnACurvedMeshFloor_AsWithParrysOwnPseudoNormals()
    {
        using ErrorLog log = new ErrorLog();
        Run run = RollBall(true, curved: true);
        float off = Vector3.Distance(run.End, UnpatchedBallEnd);
        Assert.True(off < 0.01f, $"the ball ended {off} m from where it did before the patch ({run.End} vs {UnpatchedBallEnd})");
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Box_SlidesOnACurvedMeshFloor_AsWithParrysOwnPseudoNormals()
    {
        using ErrorLog log = new ErrorLog();
        Run run = SlideBox(true, curved: true);
        float off = Vector3.Distance(run.End, UnpatchedBoxEnd);
        Assert.True(off < 0.1f, $"the box ended {off} m from where it did before the patch ({run.End} vs {UnpatchedBoxEnd})");
        Assert.InRange(run.MaxSpin, UnpatchedBoxSpin * 0.85f, UnpatchedBoxSpin * 1.15f);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Ball_And_Box_OnACurvedMeshFloor_WithoutTheFix_EndElsewhere()
    {
        // The comparison above can see a change: without the fix the same runs end well away from those points.
        using ErrorLog log = new ErrorLog();
        Run ball = RollBall(false, curved: true);
        Assert.True(Vector3.Distance(ball.End, UnpatchedBallEnd) > 0.2f, $"the ball ended at {ball.End}");
        Run box = SlideBox(false, curved: true);
        Assert.True(Vector3.Distance(box.End, UnpatchedBoxEnd) > 0.2f, $"the box ended at {box.End}");
        Assert.Empty(log.Errors);
    }
}
