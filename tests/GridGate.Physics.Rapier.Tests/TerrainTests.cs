using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using Xunit;

namespace GridGate.Physics.Rapier.Tests;

public class TerrainTests
{
    private static PhysicsActor AddBox(BSScene scene, uint localId, Vector3 position, bool physical)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        Vector3 size = new Vector3(0.5f, 0.5f, 0.5f);
        pbs.Scale = size;
        return scene.AddPrimShape("box" + localId, pbs, position, size, Quaternion.Identity, physical, localId);
    }

    // OpenSim's heightmap: index = y * size + x, so sample (x, y) is the point at world (x, y).
    private static float[] MakeMap(int size, Func<int, int, float> height)
    {
        float[] map = new float[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                map[y * size + x] = height(x, y);
        return map;
    }

    private static void SetTerrain(BSScene scene, float[] map)
    {
        scene.SetTerrain(map);
        RapierTestScene.Step(scene, 0.3f);
    }

    private static RaycastHit RayDown(BSScene scene, float x, float y, uint group = 0xffff, uint mask = 0xffff)
    {
        return scene.PE.RayTest2(scene.World, new Vector3(x, y, 1000f), new Vector3(x, y, -10f), group, mask);
    }

    [Fact]
    public void Terrain_Flat_BoxRestsOnSurface()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetTerrain(scene, MakeMap(256, (x, y) => 21f));
            PhysicsActor box = AddBox(scene, 201, new Vector3(128f, 128f, 30f), physical: true);
            RapierTestScene.Step(scene, 3.0f);

            Assert.InRange(box.Position.Z, 21.25f - 0.05f, 21.25f + 0.05f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Terrain_Sloped_RaysHitHeightmap()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetTerrain(scene, MakeMap(256, (x, y) => 20f + 0.1f * x + 0.05f * y));
            foreach ((float x, float y) in new[] { (10f, 10f), (128f, 128f), (250f, 200f) })
            {
                RaycastHit hit = RayDown(scene, x, y);
                Assert.True(hit.hasHit(), $"no hit at {x},{y}");
                Assert.Equal(20f + 0.1f * x + 0.05f * y, hit.Point.Z, 0.01f);
                Assert.Equal(BSScene.TERRAIN_ID, hit.ID);
            }
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Terrain_Varregion512_RaysHitHeightmapAtCorners()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(regionSize: 512);
        try
        {
            SetTerrain(scene, MakeMap(512, (x, y) => 20f + 0.1f * x + 0.05f * y));
            foreach ((float x, float y) in new[] { (1f, 1f), (510f, 1f), (1f, 510f), (510f, 510f), (256f, 256f) })
            {
                RaycastHit hit = RayDown(scene, x, y);
                Assert.True(hit.hasHit(), $"no hit at {x},{y}");
                Assert.Equal(20f + 0.1f * x + 0.05f * y, hit.Point.Z, 0.01f);
            }
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    // Steep and curved, so a half-metre shift, a swapped axis or a size/size-1 mix-up moves the hit by far more than
    // the tolerance (0.5 m/m in X, 0.3 plus a 5 m sine in Y).
    [Fact]
    public void Terrain_Varregion512_SteepCurvedMapPinsPlacement()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(regionSize: 512);
        try
        {
            Func<float, float, float> h = (x, y) => 20f + 0.5f * x + 0.3f * y + 5f * MathF.Sin(y / 40f);
            SetTerrain(scene, MakeMap(512, (x, y) => h(x, y)));
            foreach ((float x, float y) in new[] { (1f, 1f), (510f, 1f), (1f, 510f), (510f, 510f), (256f, 256f) })
            {
                RaycastHit hit = RayDown(scene, x, y);
                Assert.True(hit.hasHit(), $"no hit at {x},{y}");
                Assert.Equal(h(x, y), hit.Point.Z, 0.01f);
            }
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Terrain_RegionEdges_RaysHit()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetTerrain(scene, MakeMap(256, (x, y) => 20f + 0.1f * x + 0.05f * y));
            foreach ((float x, float y) in new[] { (0f, 0f), (0f, 128f), (128f, 0f), (255f, 255f), (255f, 0f), (0f, 255f) })
            {
                RaycastHit hit = RayDown(scene, x, y);
                Assert.True(hit.hasHit(), $"no hit at {x},{y}");
                Assert.Equal(20f + 0.1f * x + 0.05f * y, hit.Point.Z, 0.01f);
            }
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Ray_Filter_IgnoresMaskedGroup()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetTerrain(scene, MakeMap(256, (x, y) => 21f));
            AddBox(scene, 202, new Vector3(128f, 128f, 50f), physical: false);
            RapierTestScene.Step(scene, 0.3f);

            RaycastHit all = RayDown(scene, 128f, 128f);
            Assert.True(all.hasHit());
            Assert.Equal(202u, all.ID);
            Assert.Equal(50.25f, all.Point.Z, 0.05f);

            uint noStatic = 0xffff & ~(uint)CollisionFilterGroups.BStaticGroup;
            RaycastHit through = RayDown(scene, 128f, 128f, 0xffff, noStatic);
            Assert.True(through.hasHit());
            Assert.Equal(BSScene.TERRAIN_ID, through.ID);
            Assert.Equal(21f, through.Point.Z, 0.05f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }
    // BulletS teleports a body in a taint and can cast a ray in the same taint pass, before any step. Rapier's broad
    // phase only learns a collider's new bounding box in a step, so until then a ray finds the body only through the box
    // it had at the last step; what the backend must do is pass the new pose on to the colliders, so the ray tests the
    // body where it now is, and not where it was. After the next step the ray finds it anywhere.
    [Theory]
    [InlineData(true)]    // a resting physical box
    [InlineData(false)]   // a static box
    public void Ray_RightAfterATeleportInTheSameFrame_TestsTheNewPose(bool physical)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetTerrain(scene, MakeMap(256, (x, y) => 21f));
            AddBox(scene, 203, new Vector3(128f, 128f, physical ? 21.3f : 30f), physical);
            RapierTestScene.Step(scene, 2f);
            float oldTop = physical ? 21.5f : 30.25f;

            RaycastHit beforeOld = default, afterMove = default, secondOld = default;
            scene.TaintedObject(203, "TerrainTests", () =>
            {
                BulletBody body = scene.PhysObjects[203].PhysBody;
                // A ray first, so whatever was waiting to reach the colliders has been passed on.
                beforeOld = RayDown(scene, 128f, 128f);
                // Up, and a little along, so this ray still crosses the old bounding box and the new place.
                scene.PE.SetTranslation(body, new Vector3(128.2f, 128f, 40f), Quaternion.Identity);
                afterMove = RayDown(scene, 128f, 128f);
                // A second move with nothing else between, then a ray over the place it left.
                scene.PE.SetTranslation(body, new Vector3(140f, 128f, 45f), Quaternion.Identity);
                secondOld = RayDown(scene, 128.2f, 128f);
            });
            scene.Simulate(RapierTestScene.FrameSeconds);

            Assert.True(beforeOld.hasHit());
            Assert.Equal(203u, beforeOld.ID);
            Assert.Equal(oldTop, beforeOld.Point.Z, 0.05f);
            // The box is hit at its new height, not its old one.
            Assert.True(afterMove.hasHit());
            Assert.Equal(203u, afterMove.ID);
            Assert.Equal(40.25f, afterMove.Point.Z, 0.05f);
            // And once it has gone, the ray over the place it left reaches the terrain.
            Assert.True(secondOld.hasHit());
            Assert.Equal(BSScene.TERRAIN_ID, secondOld.ID);

            // A step later the ray finds it where it went (a static box stays there; a physical one has begun to fall).
            RaycastHit now = RayDown(scene, 140f, 128f);
            Assert.True(now.hasHit());
            Assert.Equal(203u, now.ID);
            Assert.InRange(now.Point.Z, 44f, 45.3f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }
}
