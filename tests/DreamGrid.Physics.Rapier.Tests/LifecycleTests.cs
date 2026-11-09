using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

/// <summary>
/// Lifecycle leaks: prims and linksets made, changed in the ways that rebuild bodies and shapes (relink, rescale, a
/// physical/static flip, a constraint) and removed again, all through BSScene, must leave Rapier's world and the C#
/// tables as they found them.
/// </summary>
public class LifecycleTests
{
    private const float TerrainHeight = 21f;
    private const float Edge = 0.5f;

    private readonly ITestOutputHelper _output;

    public LifecycleTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static PhysicsActor AddBox(BSScene scene, uint localId, Vector3 position, bool physical, bool hollow = false)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        Vector3 size = new Vector3(Edge, Edge, Edge);
        pbs.Scale = size;
        // A hollow box is not a native shape: BulletS meshes it (a trimesh when static, a hull when physical).
        if (hollow)
            pbs.ProfileHollow = 20000;
        return scene.AddPrimShape("box" + localId, pbs, position, size, Quaternion.Identity, physical, localId);
    }

    private static BSAPIRapier.BulletWorldRapier WorldOf(BSScene scene)
    {
        return (BSAPIRapier.BulletWorldRapier)scene.World;
    }

    private static BSAPIRapier.BulletBodyRapier BodyOf(BSScene scene, uint id)
    {
        return (BSAPIRapier.BulletBodyRapier)scene.PhysObjects[id].PhysBody;
    }

    /// <summary>The counts that must come back: Rapier's own, and every C# table that follows a body, collider or shape.</summary>
    private static SortedDictionary<string, long> Counts(BSAPIRapier.BulletWorldRapier world)
    {
        RapierWorldState s = world.State;
        return new SortedDictionary<string, long>
        {
            ["native rigid bodies"] = (long)RapierNative.r3RigidBodyCount(world.World),
            ["native colliders"] = (long)RapierNative.r3ColliderCount(world.World),
            ["native impulse joints"] = (long)RapierNative.r3ImpulseJointCount(world.World),
            ["Bodies"] = s.Bodies.Count,
            // Less the ones BulletS's caches keep (CachedByBulletS), which the test takes off.
            ["Shapes"] = s.Shapes.Count,
            ["ShapeUsers"] = s.ShapeUsers.Count,
            ["ColliderShapes"] = s.ColliderShapes.Count,
            ["ColliderOwners"] = s.ColliderOwners.Count,
            ["WantsCollisions"] = s.WantsCollisions.Count,
            ["Sensors"] = s.Sensors.Count,
            ["LastSent"] = s.LastSent.Count,
            ["MovingSent"] = s.MovingSent.Count,
            ["ForcedUpdates"] = s.ForcedUpdates.Count,
            ["PendingSet"] = s.PendingSet.Count,
            ["NeverSleep"] = s.NeverSleep.Count,
            ["ForcedBodies"] = s.ForcedBodies.Count,
            ["Joints"] = s.Joints.Count,
            ["Breakable"] = s.Breakable.Count,
        };
    }

    /// <summary>
    /// The native shapes of this world that BulletS's mesh and hull caches hold. BulletS keeps those for the life of the
    /// process and never deletes one (BSShapeMesh.Dereference and the others only count down), so they are not the
    /// backend's to free and are not a leak here.
    /// </summary>
    private static int CachedByBulletS(BSAPIRapier.BulletWorldRapier world)
    {
        IEnumerable<BSShape> cached;
        lock (BSShapeMesh.Meshes) lock (BSShapeHull.Hulls) lock (BSShapeConvexHull.ConvexHulls) lock (BSShapeGImpact.GImpacts)
        {
            cached = BSShapeMesh.Meshes.Values.Cast<BSShape>()
                .Concat(BSShapeHull.Hulls.Values)
                .Concat(BSShapeConvexHull.ConvexHulls.Values)
                .Concat(BSShapeGImpact.GImpacts.Values)
                .ToList();
        }
        return cached.Select(c => c.physShapeInfo as BSAPIRapier.BulletShapeRapier)
            .Where(c => c != null && world.State.Shapes.Contains(c.Shape))
            .Select(c => c!.Shape)
            .Distinct()
            .Count();
    }

    [Fact]
    public void ManyPrimsAndLinksets_CreatedChangedAndRemoved_LeaveNothingBehind()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            float[] map = new float[256 * 256];
            Array.Fill(map, TerrainHeight);
            scene.SetTerrain(map);
            RapierTestScene.Step(scene, 0.3f);
            BSAPIRapier.BulletWorldRapier world = WorldOf(scene);
            SortedDictionary<string, long> baseline = Counts(world);

            const int Rounds = 25;
            uint nextId = 1000;
            int made = 0;
            int peakColliders = 0;
            int cachedPeak = 0;
            Assert.Equal(0, CachedByBulletS(world));
            for (int round = 0; round < Rounds; round++)
            {
                List<PhysicsActor> prims = new List<PhysicsActor>();
                float y = 20f + (round % 5) * 40f;
                float z = TerrainHeight + 0.3f + Edge / 2f;

                // Single prims, physical and static, two of them meshed.
                for (int ii = 0; ii < 6; ii++)
                    prims.Add(AddBox(scene, nextId++, new Vector3(20f + ii * 3f, y, z), physical: ii % 2 == 0, hollow: ii >= 4));

                // Two linksets of three, the second meshed.
                PhysicsActor[] roots = new PhysicsActor[2];
                PhysicsActor[] lastChildren = new PhysicsActor[2];
                for (int set = 0; set < 2; set++)
                {
                    float x = 60f + set * 20f;
                    PhysicsActor root = AddBox(scene, nextId++, new Vector3(x, y, z), physical: true, hollow: set == 1);
                    PhysicsActor child1 = AddBox(scene, nextId++, new Vector3(x + 1f, y, z), physical: true, hollow: set == 1);
                    PhysicsActor child2 = AddBox(scene, nextId++, new Vector3(x + 2f, y, z), physical: true);
                    prims.Add(root);
                    prims.Add(child1);
                    prims.Add(child2);
                    child1.link(root);
                    child2.link(root);
                    roots[set] = root;
                    lastChildren[set] = child2;
                }
                made += prims.Count;
                RapierTestScene.Step(scene, 0.3f);

                // A constraint between two physical single prims, breakable so it is in Breakable too.
                BSConstraint6Dof joint = new BSConstraint6Dof(scene.World, BodyOf(scene, prims[0].LocalID), BodyOf(scene, prims[2].LocalID),
                            prims[0].Position + new Vector3(3f, 0f, 0f), true, true);
                joint.SetLinearLimits(Vector3.Zero, Vector3.Zero);
                joint.SetAngularLimits(Vector3.Zero, Vector3.Zero);
                joint.SetBreakingImpulseThreshold(1000f);
                Assert.True(joint.Constraint.HasPhysicalConstraint);
                Assert.Equal(baseline["native impulse joints"] + 1, (long)RapierNative.r3ImpulseJointCount(world.World));

                // Relink: one child of each linkset leaves and comes back.
                foreach (PhysicsActor child in lastChildren)
                    child.delink();
                RapierTestScene.Step(scene, 0.2f);
                for (int set = 0; set < 2; set++)
                    lastChildren[set].link(roots[set]);
                RapierTestScene.Step(scene, 0.2f);

                // Rescale a single prim (native and meshed) and a linkset root.
                prims[1].Size = new Vector3(0.8f, 0.6f, 0.4f);
                prims[4].Size = new Vector3(0.7f, 0.7f, 0.7f);
                roots[0].Size = new Vector3(0.9f, 0.5f, 0.5f);
                RapierTestScene.Step(scene, 0.2f);

                // Physical/static flips, single and linkset, there and back.
                prims[0].IsPhysical = false;
                prims[5].IsPhysical = true;
                roots[1].IsPhysical = false;
                RapierTestScene.Step(scene, 0.2f);
                prims[0].IsPhysical = true;
                prims[5].IsPhysical = false;
                roots[1].IsPhysical = true;
                RapierTestScene.Step(scene, 0.2f);

                peakColliders = Math.Max(peakColliders, (int)RapierNative.r3ColliderCount(world.World));

                // Half the rounds destroy the constraint as BulletS does; the others leave it to go with its bodies.
                if (round % 2 == 0)
                    joint.Dispose();

                // Everything goes: children before their roots, as a deleted object's parts are removed.
                for (int ii = prims.Count - 1; ii >= 0; ii--)
                    scene.RemovePrim(prims[ii]);
                RapierTestScene.Step(scene, 0.2f);

                SortedDictionary<string, long> now = Counts(world);
                int cached = CachedByBulletS(world);
                cachedPeak = Math.Max(cachedPeak, cached);
                now["Shapes"] -= cached;
                foreach (KeyValuePair<string, long> entry in baseline)
                    Assert.True(entry.Value == now[entry.Key], $"round {round}: {entry.Key} is {now[entry.Key]}, was {entry.Value} before");
            }

            _output.WriteLine($"{made} prims made and removed over {Rounds} rounds; at most {peakColliders} native colliders at once; "
                        + $"{cachedPeak} shapes held by BulletS's mesh and hull caches");
            foreach (KeyValuePair<string, long> entry in baseline)
                _output.WriteLine($"{entry.Key}: {entry.Value}");
            Assert.True(made >= 300, "fewer prims than the test means to make: " + made);
            Assert.True(peakColliders > (int)baseline["native colliders"] + 10, "the prims never reached Rapier");
            Assert.True(cachedPeak > 0, "no meshed prim was made, so the trimesh and hull paths went untested");
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }
}
