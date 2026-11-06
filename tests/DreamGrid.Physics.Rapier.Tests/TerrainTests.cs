using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

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
        return scene.PE.RayTest2(scene.World, new Vector3(x, y, 400f), new Vector3(x, y, -10f), group, mask);
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
                Assert.Equal(20f + 0.1f * x + 0.05f * y, hit.Point.Z, 0.05f);
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
                Assert.Equal(20f + 0.1f * x + 0.05f * y, hit.Point.Z, 0.05f);
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
}
