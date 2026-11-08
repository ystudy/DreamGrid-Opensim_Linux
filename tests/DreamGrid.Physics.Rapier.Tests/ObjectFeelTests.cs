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
}
