using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace GridGate.Physics.Rapier.Tests;

public class UpdateTests
{
    // BulletSim's tolerances, from SimMotionState::setWorldTransform in BulletSim.h (Misterblue/BulletSim). A body is
    // reported when any of position, rotation, velocity or angular velocity is not within the tolerance of what was last
    // sent, in every component (strictly inside +-tolerance counts as equal).
    private const float PositionTolerance = 0.05f;
    private const float VelocityTolerance = 0.001f;
    private const float RotationTolerance = 0.01f;
    private const float AngularVelocityTolerance = 0.01f;

    private static PhysicsActor AddBox(BSScene scene, uint localId, Vector3 position, bool physical)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        Vector3 size = new Vector3(0.5f, 0.5f, 0.5f);
        pbs.Scale = size;
        return scene.AddPrimShape("box" + localId, pbs, position, size, Quaternion.Identity, physical, localId);
    }

    private static void SetFlatTerrain(BSScene scene, float height)
    {
        float[] map = new float[256 * 256];
        Array.Fill(map, height);
        scene.SetTerrain(map);
        RapierTestScene.Step(scene, 0.3f);
    }

    /// <summary>One BSScene.Simulate call, and the entries of the update array it filled in. (The array is cleared beyond
    /// what a step used and no localID is 0, so the filled part is the entries with an ID.)</summary>
    private static List<EntityProperties> Frame(BSScene scene)
    {
        scene.Simulate(RapierTestScene.FrameSeconds);
        List<EntityProperties> updates = new List<EntityProperties>();
        foreach (EntityProperties e in scene.m_updateArray)
        {
            if (e.ID == 0)
                break;
            updates.Add(e);
        }
        return updates;
    }

    private static BulletBody BodyOf(BSScene scene, uint id)
    {
        return scene.PhysObjects[id].PhysBody;
    }

    [Fact]
    public void Updates_StillBodyStopsReportingAfterSleep()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene, 21f);
            AddBox(scene, 301, new Vector3(128f, 128f, 23f), physical: true);

            EntityProperties? last = null;
            double lastReportAt = -1;
            double now = 0;
            while (now < 8.0)
            {
                foreach (EntityProperties e in Frame(scene))
                {
                    if (e.ID == 301)
                    {
                        last = e;
                        lastReportAt = now;
                    }
                }
                now += RapierTestScene.FrameSeconds;
            }

            Assert.NotNull(last);
            Assert.True(lastReportAt < 5.0, $"still reporting at {lastReportAt:F1} s");
            Assert.Equal(Vector3.Zero, last!.Value.Velocity);
            Assert.Equal(Vector3.Zero, last.Value.RotationalVelocity);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Updates_BelowTolerance_NotReported()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "Gravity", "0" } });
        try
        {
            AddBox(scene, 302, new Vector3(100f, 100f, 100f), physical: true);
            Frame(scene);
            BulletBody body = BodyOf(scene, 302);
            Quaternion r0 = scene.PE.GetOrientation(body);

            // Put a known pose on record.
            Vector3 sent = new Vector3(100.1f, 100f, 100f);
            scene.PE.SetTranslation(body, sent, r0);
            Assert.Contains(Frame(scene), e => e.ID == 302);

            // Under the position tolerance: not reported.
            scene.PE.SetTranslation(body, sent + new Vector3(PositionTolerance * 0.8f, 0f, 0f), r0);
            Assert.DoesNotContain(Frame(scene), e => e.ID == 302);

            // A velocity under the tolerance: not reported.
            scene.PE.SetTranslation(body, sent, r0);
            scene.PE.SetLinearVelocity(body, new Vector3(VelocityTolerance * 0.4f, 0f, 0f));
            Assert.DoesNotContain(Frame(scene), e => e.ID == 302);

            // A rotation under the tolerance: not reported.
            scene.PE.SetLinearVelocity(body, Vector3.Zero);
            scene.PE.SetTranslation(body, sent, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, RotationTolerance * 1.5f));
            Assert.DoesNotContain(Frame(scene), e => e.ID == 302);

            // An angular velocity under the tolerance: not reported.
            scene.PE.SetTranslation(body, sent, r0);
            scene.PE.SetAngularVelocity(body, new Vector3(0f, 0f, AngularVelocityTolerance * 0.5f));
            Assert.DoesNotContain(Frame(scene), e => e.ID == 302);
            scene.PE.SetAngularVelocity(body, Vector3.Zero);

            // The same moves, past the tolerances, are reported (so the checks above are not vacuous).
            scene.PE.SetTranslation(body, sent + new Vector3(PositionTolerance * 1.5f, 0f, 0f), r0);
            Assert.Contains(Frame(scene), e => e.ID == 302);
            scene.PE.SetTranslation(body, sent, r0);
            Assert.Contains(Frame(scene), e => e.ID == 302);
            scene.PE.SetLinearVelocity(body, new Vector3(VelocityTolerance * 3f, 0f, 0f));
            Assert.Contains(Frame(scene), e => e.ID == 302);
            scene.PE.SetLinearVelocity(body, Vector3.Zero);
            Assert.Contains(Frame(scene), e => e.ID == 302);
            scene.PE.SetAngularVelocity(body, new Vector3(0f, 0f, AngularVelocityTolerance * 3f));
            Assert.Contains(Frame(scene), e => e.ID == 302);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Updates_PushUpdate_AlwaysReported()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            SetFlatTerrain(scene, 21f);
            AddBox(scene, 303, new Vector3(128f, 128f, 23f), physical: true);
            RapierTestScene.Step(scene, 6.0f);
            Assert.DoesNotContain(Frame(scene), e => e.ID == 303);

            scene.PE.PushUpdate(BodyOf(scene, 303));
            Assert.Contains(Frame(scene), e => e.ID == 303);
            Assert.DoesNotContain(Frame(scene), e => e.ID == 303);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Updates_MoreThanArray_TruncatesAndCarriesOver()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "MaxUpdatesPerFrame", "1000" } });
        try
        {
            // 55 x 55 boxes, 2 m apart and 200 m up, so they fall freely without touching each other.
            const int count = 3000;
            for (int i = 0; i < count; i++)
                AddBox(scene, (uint)(1000 + i), new Vector3(10f + (i % 55) * 2f, 10f + (i / 55) * 2f, 200f), physical: true);

            // The first Simulate runs the taints that create the bodies and then steps once, so it is the first step
            // with all 3,000 bodies awake and falling.
            List<EntityProperties> first = Frame(scene);
            Assert.Equal(count, ((BSAPIRapier.BulletWorldRapier)scene.World).State.Bodies.Count(b => b.Key >= 1000));
            Assert.Equal(1000, first.Count);

            HashSet<uint> seen = new HashSet<uint>(first.Select(e => e.ID));
            Assert.Equal(1000, seen.Count);
            for (int i = 0; i < 3; i++)
            {
                List<EntityProperties> frame = Frame(scene);
                Assert.True(frame.Count <= 1000);
                Assert.Equal(frame.Count, frame.Select(e => e.ID).Distinct().Count());
                foreach (EntityProperties e in frame)
                    seen.Add(e.ID);
            }
            Assert.Equal(count, seen.Count);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Updates_RemovedWhilePending_NeverReported()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "MaxUpdatesPerFrame", "10" } });
        try
        {
            Dictionary<uint, PhysicsActor> actors = new Dictionary<uint, PhysicsActor>();
            for (uint id = 2001; id <= 2030; id++)
                actors[id] = AddBox(scene, id, new Vector3(10f + (id - 2001) * 2f, 50f, 200f), physical: true);

            HashSet<uint> reported = new HashSet<uint>(Frame(scene).Select(e => e.ID));
            Assert.Equal(10, reported.Count);
            List<uint> pending = actors.Keys.Where(id => !reported.Contains(id)).ToList();
            uint doomed = pending[0];
            scene.RemovePrim(actors[doomed]);

            HashSet<uint> later = new HashSet<uint>();
            for (int i = 0; i < 6; i++)
            {
                foreach (EntityProperties e in Frame(scene))
                    later.Add(e.ID);
            }
            Assert.DoesNotContain(doomed, later);
            reported.UnionWith(later);
            Assert.Equal(29, reported.Count);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Updates_RootCompoundOffset_AppliedWhenFlagged()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "Gravity", "0" } });
        try
        {
            AddBox(scene, 304, new Vector3(100f, 100f, 100f), physical: true);
            Frame(scene);
            BSAPIRapier.BulletBodyRapier body = (BSAPIRapier.BulletBodyRapier)BodyOf(scene, 304);
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(Math.PI / 2));
            scene.PE.SetTranslation(body, new Vector3(100f, 100f, 100f), turn);
            body.RootChildPos = new Vector3(1f, 0f, 0f);
            body.RootChildRot = Quaternion.Identity;
            body.HasRootChild = true;

            // Without the flag the body's own pose is reported.
            scene.PE.PushUpdate(body);
            EntityProperties plain = Frame(scene).Single(e => e.ID == 304);
            Assert.Equal(100f, plain.Position.X, 0.001f);
            Assert.Equal(100f, plain.Position.Y, 0.001f);

            // With it, the pose of child 0: the body turned 90 degrees about Z puts the child's +X offset along +Y.
            scene.PE.AddToCollisionFlags(body, CollisionFlags.BS_RETURN_ROOT_COMPOUND_SHAPE);
            scene.PE.PushUpdate(body);
            EntityProperties root = Frame(scene).Single(e => e.ID == 304);
            Assert.Equal(100f, root.Position.X, 0.001f);
            Assert.Equal(101f, root.Position.Y, 0.001f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }
}
