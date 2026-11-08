using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

/// <summary>
/// Physical objects under Rapier feel like ubODE's (the owner's choice, 2026-11-07): a wood box on a slope, a ball rolling.
/// The ubODE values are constants measured with the same scripts on ubODE, the way AvatarFeelTests' summary describes
/// (the harness is kept outside the repository; see
/// .superpowers/sdd/2026-11-06-rapier-opensim-integration/avatar-feel-report.md, section "Objects"). The prims are
/// added with Building = false, as SceneObjectPart does; ubODE also needs a moment for its mesh worker to build them.
/// </summary>
public class ObjectFeelTests
{
    private readonly ITestOutputHelper _output;

    public ObjectFeelTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // A terrain plane rising along the azimuth by tan(slope) per metre, flat (at 21 m) 60 m before the centre of the
    // region and flat again 60 m after it.
    private static float Hill(float x, float y, float tan, Vector2 dir)
    {
        float u = Math.Clamp((x - 128f) * dir.X + (y - 128f) * dir.Y, -60f, 60f);
        return 21f + (u + 60f) * tan;
    }

    private static void SetHill(BSScene scene, float slopeDegrees, float azimuthDegrees)
    {
        float tan = MathF.Tan(slopeDegrees * MathF.PI / 180f);
        Vector2 dir = new Vector2(MathF.Cos(azimuthDegrees * MathF.PI / 180f), MathF.Sin(azimuthDegrees * MathF.PI / 180f));
        float[] map = new float[256 * 256];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                map[y * 256 + x] = Hill(x, y, tan, dir);
        scene.SetTerrain(map);
        RapierTestScene.Step(scene, 0.3f);
    }

    // ---- The terrain height BulletS looks up (BSTerrainHeightmap.GetTerrainHeightAtXYZ) ----

    // On a planar slope in any direction, the height BulletS looks up is the surface the engine collides with (a ray
    // straight down), between the samples too; on Rapier that is the plane the heightmap describes. Shared
    // BulletS code: the same lookup serves the Bullet engines.
    [Theory]
    [InlineData("Rapier", 30f, 0f)]
    [InlineData("Rapier", 30f, 90f)]
    [InlineData("Rapier", 30f, 180f)]
    [InlineData("Rapier", 30f, 225f)]
    [InlineData("Rapier", 10f, 300f)]
    [InlineData("BulletUnmanaged", 30f, 180f)]
    [InlineData("BulletUnmanaged", 30f, 225f)]
    [InlineData("BulletUnmanaged", 30f, 0f)]
    public void TerrainHeight_OnASlope_IsThePlanesHeight(string engine, float slope, float azimuth)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "BulletEngine", engine } });
        try
        {
            SetHill(scene, slope, azimuth);
            float tan = MathF.Tan(slope * MathF.PI / 180f);
            Vector2 dir = new Vector2(MathF.Cos(azimuth * MathF.PI / 180f), MathF.Sin(azimuth * MathF.PI / 180f));
            Random random = new Random(11);
            float worst = 0f;
            for (int ii = 0; ii < 400; ii++)
            {
                // Inside the sloped band, away from its creases.
                float u = (float)(random.NextDouble() * 100.0 - 50.0);
                float v = (float)(random.NextDouble() * 100.0 - 50.0);
                float x = 128f + dir.X * u - dir.Y * v;
                float y = 128f + dir.Y * u + dir.X * v;
                if (x < 1f || y < 1f || x > 254f || y > 254f)
                    continue;
                float looked = scene.TerrainManager.GetTerrainHeightAtXYZ(new Vector3(x, y, 0f));
                // The surface the engine itself collides with, straight below the point.
                RaycastHit hit = scene.PE.RayTest2(scene.World, new Vector3(x, y, 300f), new Vector3(x, y, -10f), 0xffff, 0xffff);
                Assert.True(hit.hasHit(), $"no terrain under ({x}, {y})");
                worst = Math.Max(worst, Math.Abs(looked - hit.Point.Z));
                if (engine == "Rapier")
                    worst = Math.Max(worst, Math.Abs(looked - Hill(x, y, tan, dir)));
            }
            _output.WriteLine($"{engine}, {slope} degrees towards {azimuth}: worst height error {worst:F4} m");
            Assert.True(worst < 0.01f, $"the looked-up terrain height is off by up to {worst} m");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // Rough terrain: 30 m plus up to 0.3 m of noise on every sample (seeded), so no cell is planar.
    private static float[] SetRoughTerrain(BSScene scene, float roughness, int seed)
    {
        Random random = new Random(seed);
        float[] map = new float[256 * 256];
        for (int ii = 0; ii < map.Length; ii++)
            map[ii] = 30f + (float)(random.NextDouble() * roughness);
        scene.SetTerrain(map);
        RapierTestScene.Step(scene, 0.3f);
        return map;
    }

    // On rough terrain the height BulletS looks up is the surface the engine collides with: each engine splits a cell
    // into two triangles its own way (BSAPITemplate.TerrainTriangleRule), and a bilinear blend reads up to 0.1 m off.
    [Theory]
    [InlineData("Rapier")]
    [InlineData("BulletUnmanaged")]
    public void TerrainHeight_OnRoughTerrain_IsTheEnginesSurface(string engine)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "BulletEngine", engine } });
        try
        {
            SetRoughTerrain(scene, 0.3f, 5);
            Random random = new Random(17);
            float worst = 0f;
            for (int ii = 0; ii < 2000; ii++)
            {
                float x = 20f + (float)random.NextDouble() * 200f;
                float y = 20f + (float)random.NextDouble() * 200f;
                float looked = scene.TerrainManager.GetTerrainHeightAtXYZ(new Vector3(x, y, 0f));
                RaycastHit hit = scene.PE.RayTest2(scene.World, new Vector3(x, y, 100f), new Vector3(x, y, 0f), 0xffff, 0xffff);
                Assert.True(hit.hasHit(), $"no terrain under ({x}, {y})");
                worst = Math.Max(worst, Math.Abs(looked - hit.Point.Z));
            }
            _output.WriteLine($"{engine} ({scene.PE.BulletEngineVersion}), 0.3 m rough terrain: worst height error {worst:F4} m");
            Assert.True(worst < 0.001f, $"the looked-up terrain height is off by up to {worst} m");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // Small prims resting on rough terrain are never lifted by PositionSanityCheck: forty 5 x 5 x 2.5 cm cubes set down
    // on 0.3 m roughness and watched for 10 s never rise. (A resting cube does not rise by itself; PositionSanityCheck
    // moving one up to the looked-up height does.) Rapier only: on Bullet these 2.5 cm cubes fall into its heightfield
    // (its 4 cm collision margins) every other frame, and PositionSanityCheck rightly brings them back up, with an exact
    // lookup too (measured: about 2,800 rescues in 10 s, the cube 1-3 cm below the surface each time).
    [Theory]
    [InlineData("Rapier")]
    public void SmallCubes_OnRoughTerrain_AreNotLifted(string engine)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "BulletEngine", engine } });
        try
        {
            SetRoughTerrain(scene, 0.3f, 7);
            Random random = new Random(23);
            List<PhysicsActor> cubes = new List<PhysicsActor>();
            Vector3 size = new Vector3(0.05f, 0.05f, 0.025f);
            for (int ii = 0; ii < 40; ii++)
            {
                float x = 60f + (float)random.NextDouble() * 120f;
                float y = 60f + (float)random.NextDouble() * 120f;
                RaycastHit hit = scene.PE.RayTest2(scene.World, new Vector3(x, y, 100f), new Vector3(x, y, 0f), 0xffff, 0xffff);
                cubes.Add(AddPrim(scene, (uint)(3000 + ii), PrimitiveBaseShape.CreateBox(), new Vector3(x, y, hit.Point.Z + 0.03f),
                                    size, Quaternion.Identity, true));
            }
            RapierTestScene.Step(scene, 1f);
            List<float> lastZ = cubes.Select(c => c.Position.Z).ToList();
            int rises = 0;
            HashSet<int> risen = new HashSet<int>();
            int frames = (int)Math.Round(10f / RapierTestScene.FrameSeconds);
            for (int frame = 0; frame < frames; frame++)
            {
                scene.Simulate(RapierTestScene.FrameSeconds);
                for (int ii = 0; ii < cubes.Count; ii++)
                {
                    float z = cubes[ii].Position.Z;
                    if (z - lastZ[ii] > 0.003f)
                    {
                        rises++;
                        risen.Add(ii);
                    }
                    lastZ[ii] = z;
                }
            }
            _output.WriteLine($"{engine}: {rises} rises on {risen.Count} of {cubes.Count} cubes in 10 s");
            Assert.Equal(0, rises);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // ---- Shared helpers for the object scripts ----

    private const uint BoxId = 2001;
    private const uint SlopeId = 2002;

    internal sealed class Watched
    {
        public List<Vector3> Positions = new List<Vector3>();
        public List<Vector3> Velocities = new List<Vector3>();
        public Vector3 Start;

        public float Distance { get { return Vector3.Distance(Positions[Positions.Count - 1], Start); } }

        // Seconds until the object moves less than 1 mm a frame for good; -1 if it is still moving at the end.
        public float RestSeconds
        {
            get
            {
                int rest = Positions.Count - 1;
                while (rest > 0 && Vector3.Distance(Positions[rest], Positions[rest - 1]) <= 0.001f)
                    rest--;
                return (rest >= Positions.Count - 2) ? -1f : (rest + 1) * RapierTestScene.FrameSeconds;
            }
        }
    }

    private static PhysicsActor AddPrim(BSScene scene, uint id, PrimitiveBaseShape pbs, Vector3 position, Vector3 size,
                                        Quaternion rotation, bool physical)
    {
        pbs.Scale = size;
        PhysicsActor prim = scene.AddPrimShape("prim" + id, pbs, position, size, rotation, physical, id);
        Assert.NotNull(prim);
        return prim;
    }

    private static Watched Watch(BSScene scene, PhysicsActor prim, Vector3 start, float seconds)
    {
        Watched watched = new Watched { Start = start };
        int frames = (int)Math.Round(seconds / RapierTestScene.FrameSeconds);
        for (int ii = 0; ii < frames; ii++)
        {
            scene.Simulate(RapierTestScene.FrameSeconds);
            watched.Positions.Add(prim.Position);
            watched.Velocities.Add(prim.Velocity);
        }
        return watched;
    }

    // A wood 0.5 m cube released at rest, sitting flat on a slope rising towards the azimuth: on the terrain hill 40 m up
    // from its foot, or on a 60 m long static box tilted to the slope (its centre 60 m up, over flat terrain).
    private static Watched BoxOnSlope(string engine, float slope, float azimuth, bool primSlope, float seconds = 15f,
                                      Dictionary<string, string>? ini = null)
    {
        Dictionary<string, string> config = new Dictionary<string, string> { { "BulletEngine", engine } };
        if (ini != null)
            foreach (KeyValuePair<string, string> kvp in ini)
                config[kvp.Key] = kvp.Value;
        BSScene scene = RapierTestScene.Create(config);
        try
        {
            float rad = slope * MathF.PI / 180f;
            Vector2 dir = new Vector2(MathF.Cos(azimuth * MathF.PI / 180f), MathF.Sin(azimuth * MathF.PI / 180f));
            Vector3 normal = new Vector3(-MathF.Sin(rad) * dir.X, -MathF.Sin(rad) * dir.Y, MathF.Cos(rad));
            Quaternion tilt = Quaternion.CreateFromAxisAngle(new Vector3(-dir.Y, dir.X, 0f), -rad);
            Vector3 surface;
            if (primSlope)
            {
                SetHill(scene, 0f, azimuth);
                Vector3 centre = new Vector3(128f, 128f, 60f);
                AddPrim(scene, SlopeId, PrimitiveBaseShape.CreateBox(), centre, new Vector3(60f, 20f, 1f), tilt, false);
                Vector3 upSlope = new Vector3(dir.X * MathF.Cos(rad), dir.Y * MathF.Cos(rad), MathF.Sin(rad));
                surface = centre + normal * 0.5f + upSlope * 10f;
            }
            else
            {
                SetHill(scene, slope, azimuth);
                float x = 128f + dir.X * -20f + 0.37f, y = 128f + dir.Y * -20f + 0.41f;
                surface = new Vector3(x, y, Hill(x, y, MathF.Tan(rad), dir));
            }
            Vector3 start = surface + normal * 0.26f;
            PhysicsActor box = AddPrim(scene, BoxId, PrimitiveBaseShape.CreateBox(), start, new Vector3(0.5f, 0.5f, 0.5f), tilt, true);
            return Watch(scene, box, start, seconds);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // ---- The box on the hill (symptom 1) ----

    // A box on a 30 degree hill that falls away towards +X (the lookup's worst case): with the old height lookup,
    // PositionSanityCheck found it below ground every few frames, lifted it and stopped it, so it hung up on the hill and
    // never came to rest. It slides to the foot of the hill and stops there.
    [Fact]
    public void Box_OnATerrainHill_IsNotHungUpByTheHeightLookup()
    {
        using ErrorLog log = new ErrorLog();
        Watched box = BoxOnSlope("Rapier", 30f, 180f, false);
        Vector3 end = box.Positions[box.Positions.Count - 1];
        _output.WriteLine($"Rapier, 30 degree hill falling to +X: slid {box.Distance:F2} m to x {end.X:F2}, at rest after {box.RestSeconds:F2} s");
        Assert.True(box.RestSeconds > 0f, "the box never came to rest");
        // The foot of the hill is at x = 188 on this side.
        Assert.True(end.X > 188f, $"the box stopped at x {end.X}, on the hill");
        Assert.Empty(log.Errors);
    }

    // ubODE, measured as above with the same script (a wood 0.5 m box released at rest, watched for 15 s): on the
    // terrain hill it stays put at 10 and 20 degrees (0.00 and 0.04 m) and slides down at 30 and 40 degrees; on a wood
    // prim slope it stays put up to 30 degrees (0.00, 0.02, 0.21 m) and slides at 40. ubODE combines two frictions as
    // sqrt(mu1 * mu2) (ODEScene's collision callback): wood on terrain sqrt(0.6 x 0.3) = 0.42, wood on wood 0.6.
    // BulletS multiplies them (0.18 and 0.36), so on Rapier the box slid at 20 degrees on both.
    [Theory]
    [InlineData(10f, false, false)]
    [InlineData(20f, false, false)]
    [InlineData(30f, false, true)]
    [InlineData(40f, false, true)]
    [InlineData(10f, true, false)]
    [InlineData(20f, true, false)]
    [InlineData(30f, true, false)]
    [InlineData(40f, true, true)]
    public void Box_OnASlope_StaysOrSlidesAsOnUbode(float slope, bool primSlope, bool slides)
    {
        using ErrorLog log = new ErrorLog();
        Watched box = BoxOnSlope("Rapier", slope, 0f, primSlope);
        _output.WriteLine($"Rapier, box on a {slope} degree {(primSlope ? "prim" : "terrain")} slope: slid {box.Distance:F2} m, at rest after {box.RestSeconds:F2} s");
        if (slides)
        {
            Assert.True(box.Distance > 10f, $"the box slid only {box.Distance} m");
        }
        else
        {
            Assert.True(box.Distance < 0.3f, $"the box slid {box.Distance} m");
            Assert.InRange(box.RestSeconds, 0f, 2f);
        }
        Assert.Empty(log.Errors);
    }

    // Bullet's box is unchanged by the Rapier friction rule: it still slides at 20 degrees on the terrain (55.4 m, measured
    // on this branch at 07881f6c).
    [Fact]
    public void Box_OnBullet_SlidesAsBefore()
    {
        using ErrorLog log = new ErrorLog();
        Watched box = BoxOnSlope("BulletUnmanaged", 20f, 0f, false);
        _output.WriteLine($"Bullet, box on a 20 degree terrain slope: slid {box.Distance:F2} m");
        Assert.True(box.Distance > 10f, $"the box slid only {box.Distance} m");
        Assert.Empty(log.Errors);
    }

    // FrictionCombineGeometric=false in the INI wins over Rapier's default: the product is back and the box slides at
    // 20 degrees again.
    [Fact]
    public void Box_FrictionRuleFromTheIni_WinsOverRapiersDefault()
    {
        using ErrorLog log = new ErrorLog();
        Watched box = BoxOnSlope("Rapier", 20f, 0f, false, 15f, new Dictionary<string, string> { { "FrictionCombineGeometric", "false" } });
        _output.WriteLine($"Rapier with the product rule: slid {box.Distance:F2} m");
        Assert.True(box.Distance > 10f, $"the box slid only {box.Distance} m");
        Assert.False(BSParam.FrictionCombineGeometric);
        Assert.Empty(log.Errors);
    }

    // ---- The rolling ball (symptom 2) ----

    // A wood 0.5 m sphere: released 40 m up a terrain hill (rising along +X), or on flat terrain given 3 m/s along +X.
    // Returns its speed and the distance it covered 2 s after it starts to move, and the distance and speed after 40 s.
    private static (float speed2, float distance2, float distance40, float speed40) Ball(string engine, float slope,
                                    Dictionary<string, string>? ini = null)
    {
        Dictionary<string, string> config = new Dictionary<string, string> { { "BulletEngine", engine } };
        if (ini != null)
            foreach (KeyValuePair<string, string> kvp in ini)
                config[kvp.Key] = kvp.Value;
        BSScene scene = RapierTestScene.Create(config);
        try
        {
            float rad = slope * MathF.PI / 180f;
            Vector2 dir = new Vector2(1f, 0f);
            SetHill(scene, slope, 0f);
            float x = slope > 0f ? 108f : 40f;
            Vector3 normal = new Vector3(-MathF.Sin(rad), 0f, MathF.Cos(rad));
            Vector3 start = new Vector3(x, 128.5f, Hill(x, 128.5f, MathF.Tan(rad), dir)) + normal * 0.26f;
            PhysicsActor ball = AddPrim(scene, BoxId, PrimitiveBaseShape.CreateSphere(), start, new Vector3(0.5f, 0.5f, 0.5f),
                                        Quaternion.Identity, true);
            if (slope == 0f)
            {
                RapierTestScene.Step(scene, 0.3f);
                start = ball.Position;
                ball.Velocity = new Vector3(3f, 0f, 0f);
            }
            Watched watched = Watch(scene, ball, start, 40f);
            int t0 = Math.Max(0, watched.Positions.FindIndex(p => Vector3.Distance(p, start) > 0.005f) - 1);
            Vector3 from = t0 > 0 ? watched.Positions[t0] : start;
            int i2 = t0 + (int)Math.Round(2f / RapierTestScene.FrameSeconds);
            return (watched.Velocities[i2].Length(), Vector3.Distance(watched.Positions[i2], from), watched.Distance,
                    watched.Velocities[watched.Velocities.Count - 1].Length());
        }
        finally
        {
            scene.Dispose();
        }
    }

    // ubODE, measured as above: 2 s after it starts to roll, the ball goes 1.90 m/s on the 10 degree hill and 3.79 m/s on
    // the 20 degree one; pushed at 3 m/s on flat ground it goes 1.57 m/s after 2 s and 24.5 m in 40 s, slowing to
    // 0.11 m/s. ubODE gives every prim the inertia of its bounding box (ODEPrim.UpdatePrimBodyData, MassSetBoxTotal), so
    // a ball rolls down at 0.6 g sin(slope), not a solid sphere's 5/7 g sin(slope), and damps every body by 0.2% of its
    // linear and 0.05% of its angular speed per 0.02 s step (BodySetDamping(0.002, 0.0005)), about 9.5% and 2.5% a second.
    private const float UbodeBallSpeed10 = 1.90f;
    private const float UbodeBallSpeed20 = 3.79f;
    private const float UbodePushedSpeed2 = 1.57f;
    private const float UbodePushedDistance40 = 24.46f;

    [Theory]
    [InlineData(10f)]
    [InlineData(20f)]
    public void Ball_RollsDownAHillAsOnUbode(float slope)
    {
        using ErrorLog log = new ErrorLog();
        float ubode = slope == 10f ? UbodeBallSpeed10 : UbodeBallSpeed20;
        (float speed2, float distance2, _, _) = Ball("Rapier", slope);
        _output.WriteLine($"Rapier, ball on a {slope} degree hill: {speed2:F2} m/s and {distance2:F2} m 2 s after it starts (ubODE {ubode:F2} m/s, {(speed2 / ubode - 1f) * 100f:F0}%)");
        Assert.InRange(speed2, ubode * 0.9f, ubode * 1.1f);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Ball_PushedOnFlatGround_SlowsAsOnUbode()
    {
        using ErrorLog log = new ErrorLog();
        (float speed2, _, float distance40, float speed40) = Ball("Rapier", 0f);
        _output.WriteLine($"Rapier, ball pushed at 3 m/s: {speed2:F2} m/s after 2 s, {distance40:F2} m in 40 s, then {speed40:F2} m/s (ubODE {UbodePushedSpeed2:F2}, {UbodePushedDistance40:F2} m, 0.11 m/s)");
        Assert.InRange(speed2, UbodePushedSpeed2 * 0.9f, UbodePushedSpeed2 * 1.1f);
        Assert.InRange(distance40, UbodePushedDistance40 * 0.8f, UbodePushedDistance40 * 1.2f);
        Assert.True(speed40 < 0.3f, $"still rolling at {speed40} m/s after 40 s");
        Assert.Empty(log.Errors);
    }

    // Bullet's ball is unchanged: pushed at 3 m/s it goes 2.10 m/s after 2 s and 58.1 m in 40 s on Windows (BulletSim
    // 3.26), 2.08 m/s and 61.3 m on Linux (Bullet 2.84, the file named 2.86); both measured before these changes, at
    // e6744974. Keyed on the library's version (LoadedBullet).
    [Fact]
    public void Ball_OnBullet_RollsAsBefore()
    {
        using ErrorLog log = new ErrorLog();
        (float speed2, _, float distance40, _) = Ball("BulletUnmanaged", 0f);
        _output.WriteLine($"Bullet, ball pushed at 3 m/s: {speed2:F3} m/s after 2 s, {distance40:F2} m in 40 s");
        float beforeSpeed = LoadedBullet.Pick(new Dictionary<string, float> { { "1.3,3.26", 2.097f }, { "1.0,2.84", 2.081f } });
        float beforeDistance = LoadedBullet.Pick(new Dictionary<string, float> { { "1.3,3.26", 58.06f }, { "1.0,2.84", 61.30f } });
        Assert.InRange(speed2, beforeSpeed - 0.03f, beforeSpeed + 0.03f);
        Assert.InRange(distance40, beforeDistance - 1f, beforeDistance + 1f);
        Assert.Empty(log.Errors);
    }

    // A body in free flight keeps ubODE's damping: thrown at 5 m/s, high above the ground, it keeps 0.998^50 = 90.5% of
    // its horizontal speed a second (ubODE's BodySetDamping(0.002, ...) per 0.02 s step). Bullet, with BulletS's
    // LinearDamping of 0, keeps it all.
    [Theory]
    [InlineData("Rapier", 5f * 0.9047f)]
    [InlineData("BulletUnmanaged", 5f)]
    public void Ball_InFreeFlight_LosesSpeedAsOnUbode(string engine, float expected)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "BulletEngine", engine } });
        try
        {
            SetHill(scene, 0f, 0f);
            PhysicsActor ball = AddPrim(scene, BoxId, PrimitiveBaseShape.CreateSphere(), new Vector3(60f, 128f, 200f),
                                        new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity, true);
            RapierTestScene.Step(scene, 0.1f);
            ball.Velocity = new Vector3(5f, 0f, 0f);
            RapierTestScene.Step(scene, 0.0909f);
            float x0 = ball.Position.X;
            float v0 = ball.Velocity.X;
            Watch(scene, ball, ball.Position, 11 * RapierTestScene.FrameSeconds);
            float ratio = ball.Velocity.X / v0;
            _output.WriteLine($"{engine}: horizontal speed {v0:F3} -> {ball.Velocity.X:F3} m/s over 11 frames ({ratio:F4} of it)");
            Assert.InRange(ball.Velocity.X / v0, expected / 5f - 0.01f, expected / 5f + 0.01f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // PrimInertiaFromBoundingBox=false in the INI wins over Rapier's default: the ball has a solid sphere's inertia again
    // and rolls down the 10 degree hill faster (about 2.25 m/s with the damping).
    [Fact]
    public void Ball_InertiaRuleFromTheIni_WinsOverRapiersDefault()
    {
        using ErrorLog log = new ErrorLog();
        (float speed2, _, _, _) = Ball("Rapier", 10f, new Dictionary<string, string> { { "PrimInertiaFromBoundingBox", "false" } });
        _output.WriteLine($"Rapier with the sphere's own inertia: {speed2:F2} m/s 2 s after it starts");
        Assert.True(speed2 > UbodeBallSpeed10 * 1.1f, $"the ball rolled at {speed2} m/s");
        Assert.False(BSParam.PrimInertiaFromBoundingBox);
        Assert.Empty(log.Errors);
    }
}

/// <summary>
/// The BulletSim library this run loaded, for the tests that pin Bullet's behaviour to what it was before a change. The
/// dllmap loads a different BulletSim on each platform (Bullet 3.26 on Windows, 2.84 in the Linux build), and their
/// numbers differ; keying the values on the version makes a library update fail clearly rather than look like a change.
/// </summary>
internal static class LoadedBullet
{
    private static string? s_version;

    public static string Version
    {
        get
        {
            if (s_version == null)
            {
                BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "BulletEngine", "BulletUnmanaged" } });
                try
                {
                    s_version = scene.PE.BulletEngineVersion;
                }
                finally
                {
                    scene.Dispose();
                }
            }
            return s_version;
        }
    }

    // The value measured for the loaded library; an unknown library fails the test with its version.
    public static T Pick<T>(Dictionary<string, T> byVersion)
    {
        Assert.True(byVersion.TryGetValue(Version, out T? value),
                    $"no value was measured for BulletSim '{Version}' (known: {string.Join(", ", byVersion.Keys)})");
        return value!;
    }
}
