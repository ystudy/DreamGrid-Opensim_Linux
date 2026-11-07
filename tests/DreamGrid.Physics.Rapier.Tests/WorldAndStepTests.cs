using System.Runtime.InteropServices;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

public class WorldAndStepTests
{
    private static PhysicsActor AddBox(BSScene scene, uint localId, Vector3 position, bool physical)
    {
        PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
        Vector3 size = new Vector3(0.5f, 0.5f, 0.5f);
        pbs.Scale = size;
        return scene.AddPrimShape("box" + localId, pbs, position, size, Quaternion.Identity, physical, localId);
    }

    [Fact]
    public void SelectsRapier()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            Assert.Equal("Rapier", scene.PE.BulletEngineName);
            Assert.Contains("0.36.1", scene.EngineName);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void FixedTimeStep_DefaultsTo002ForRapier()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            Assert.Equal(0.02f, scene.m_fixedTimeStep);
            // One frame of about 0.091 s is four or five 0.02 s substeps, and Rapier is told the length.
            scene.Simulate(RapierTestScene.FrameSeconds);
            Assert.Equal(0.02f, ((BSAPIRapier.BulletWorldRapier)scene.World).State.TimeStep);
            Assert.Equal(0.02f, RapierNative.r3TimeStep(((BSAPIRapier.BulletWorldRapier)scene.World).World), 6);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Theory]
    [InlineData("0.016666667", 1f / 60f)]   // Bullet's default, set explicitly
    [InlineData("0.01", 0.01f)]
    public void FixedTimeStep_SetInIni_IsKeptForRapier(string configured, float expected)
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string> { { "FixedTimeStep", configured } });
        try
        {
            Assert.Equal(expected, scene.m_fixedTimeStep, 6);
            scene.Simulate(RapierTestScene.FrameSeconds);
            Assert.Equal(expected, ((BSAPIRapier.BulletWorldRapier)scene.World).State.TimeStep, 6);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void FixedTimeStep_BulletDefaultUnchanged()
    {
        // The parameter's own default, which the Bullet engines keep: only Rapier's selection replaces it.
        Assert.True(BSParam.TryGetParameter("FixedTimeStep", out BSParam.ParameterDefnBase defn));
        BSScene scene = new BSScene();
        defn.AssignDefault(scene);
        Assert.Equal(1f / 60f, scene.m_fixedTimeStep);
    }

    [Fact]
    public void PhysicalBoxFreeFalls()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            PhysicsActor box = AddBox(scene, 101, new Vector3(128f, 128f, 200f), physical: true);
            RapierTestScene.Step(scene, 1.0f);

            // The plan asks for the position to be 200 - 4.9 within 3%; read literally that is 196.1 +- 5.9, which a body
            // that never moved would also satisfy, so this checks the drop itself. The 11 frames of one second take
            // whole fixed steps, which stop a little short of it (0.98 s of 0.02 s steps), so the drop and speed are
            // checked for the time stepped; semi-implicit Euler lands a little off the exact value, which the 3% allows.
            const int frames = 11;   // what RapierTestScene.Step takes for one second
            double stepped = Math.Floor(frames * RapierTestScene.FrameSeconds / scene.m_fixedTimeStep) * scene.m_fixedTimeStep;
            double drop = 200.0 - box.Position.Z;
            double exact = 0.5 * 9.8 * stepped * stepped;
            Assert.InRange(drop, exact * 0.97, exact * 1.03);
            Assert.InRange(box.Velocity.Z, -9.8 * stepped * 1.03, -9.8 * stepped * 0.97);
            Assert.Equal(128f, box.Position.X, 0.01f);
            Assert.Equal(128f, box.Position.Y, 0.01f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void StaticBoxDoesNotMove()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            PhysicsActor box = AddBox(scene, 102, new Vector3(128f, 128f, 200f), physical: false);
            RapierTestScene.Step(scene, 1.0f);

            Assert.Equal(200f, box.Position.Z, 0.01f);
            Assert.Equal(0f, box.Velocity.Z, 0.01f);
            Assert.Empty(log.Errors);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void ParametersAreRegisteredWithTheirDefaults()
    {
        BSScene scene = RapierTestScene.Create();
        try
        {
            Assert.True(BSParam.TryGetParameter("RapierNumThreads", out BSParam.ParameterDefnBase threads));
            Assert.Equal("1", threads.GetValue(scene));
            Assert.True(BSParam.TryGetParameter("RapierSolverIterations", out BSParam.ParameterDefnBase iterations));
            Assert.Equal("4", iterations.GetValue(scene));
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void ParametersFromTheIniReachTheWorld()
    {
        BSScene scene = RapierTestScene.Create(new Dictionary<string, string>
        {
            { "RapierSolverIterations", "7" },
            { "RapierNumThreads", "2" },
        });
        try
        {
            Assert.Equal(7f, BSParam.RapierSolverIterations);
            Assert.Equal(2f, BSParam.RapierNumThreads);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void MissingLibrary_SelectEngineLogsAndReturnsNull()
    {
        MemoryAppender appender = new MemoryAppender();
        Hierarchy repository = (Hierarchy)log4net.LogManager.GetRepository(typeof(BSScene).Assembly);
        repository.Root.AddAppender(appender);
        Level? previousLevel = repository.Root.Level;
        repository.Root.Level = Level.All;
        repository.Configured = true;

        string? previousOverride = RapierNative.LibraryPathOverride;
        RapierNative.LibraryPathOverride = Path.Combine(Path.GetTempPath(), "no-such-dir", "no-such-rapier");
        try
        {
            BSAPITemplate? engine = BSScene.SelectEngineForTest("Rapier");

            Assert.Null(engine);
            Assert.Contains(appender.GetEvents(), e => e.RenderedMessage.Contains("Rapier native library"));
        }
        finally
        {
            RapierNative.LibraryPathOverride = previousOverride;
            repository.Root.Level = previousLevel;
            repository.Root.RemoveAppender(appender);
        }
    }

    [Fact]
    public void Capabilities_RapierAsksForThem_BulletDoesNot()
    {
        BSAPITemplate? rapier = BSScene.SelectEngineForTest("Rapier");
        Assert.NotNull(rapier);
        Assert.True(rapier!.NativeCylinderPrims);
        Assert.True(rapier.JumpUsesBulletStepLength);
        // BulletXNA is managed, so it can be made here; it keeps the template's defaults, as BSAPIUnman does.
        BSAPITemplate xna = new BSAPIXNA("BulletXNA", new BSScene());
        Assert.False(xna.NativeCylinderPrims);
        Assert.False(xna.JumpUsesBulletStepLength);
    }

    [Fact]
    public void SelectEngine_HyphenSuffixKeepsTheWholeName()
    {
        // "Rapier-0.36" must select "rapier", not "rapie".
        BSAPITemplate? engine = BSScene.SelectEngineForTest("Rapier-0.36");
        Assert.NotNull(engine);
        Assert.Equal("Rapier", engine!.BulletEngineName);
    }

    [Fact]
    public void StateStructs_HaveTheSizesRapierHImplies()
    {
        // rapier.h: R3RigidBodyState is a pose (7 floats), two vectors, two R3Bool and an 8-aligned 16-byte user value;
        // R3MassProperties is a vector, a float, a vector and a rotation; R3InteractionGroups is three uint32.
        Assert.Equal(80, Marshal.SizeOf<RapierNative.RigidBodyState>());
        Assert.Equal(44, Marshal.SizeOf<RapierNative.MassProperties>());
        Assert.Equal(12, Marshal.SizeOf<RapierNative.InteractionGroups>());
    }

    [Fact]
    public void ReadStates_RoundTripsEveryField()
    {
        Assert.True(RapierNative.TryLoad(out string error), error);
        IntPtr world = RapierNative.r3NewWorld();
        try
        {
            RapierNative.RigidBodyDesc desc = RapierNative.r3DynamicRigidBodyDesc();
            desc.position.translation = new RapierNative.Vector { x = 1f, y = 2f, z = 3f };
            desc.position.rotation = new RapierNative.Rotation { x = 0f, y = 0f, z = 0.70710677f, w = 0.70710677f };
            desc.linvel = new RapierNative.Vector { x = 4f, y = 5f, z = 6f };
            desc.angvel = new RapierNative.Vector { x = 7f, y = 8f, z = 9f };
            desc.userData = new RapierNative.UserData { low = 0x1122334455667788UL, high = 99UL };
            desc.enabled = 1;
            RapierNative.RigidBodyHandle body = RapierNative.r3InsertRigidBody(world, in desc);
            RapierNative.Check(RapierNative.r3LastStatus());

            // Two bodies, so the order of the handles is visible in the order of the states.
            RapierNative.RigidBodyDesc other = RapierNative.r3FixedRigidBodyDesc();
            other.position.translation = new RapierNative.Vector { x = -1f, y = -2f, z = -3f };
            other.userData = new RapierNative.UserData { low = 5UL, high = 6UL };
            other.enabled = 0;
            RapierNative.RigidBodyHandle otherBody = RapierNative.r3InsertRigidBody(world, in other);
            RapierNative.Check(RapierNative.r3LastStatus());

            RapierNative.RigidBodyHandle[] handles = { otherBody, body };
            RapierNative.RigidBodyState[] states = new RapierNative.RigidBodyState[2];
            UIntPtr count = RapierNative.r3RigidBodyReadStates(world, handles, (UIntPtr)2, states, (UIntPtr)2);
            RapierNative.Check(RapierNative.r3LastStatus());
            Assert.Equal(2, (int)count);

            Assert.Equal(-1f, states[0].position.translation.x);
            Assert.Equal(-2f, states[0].position.translation.y);
            Assert.Equal(-3f, states[0].position.translation.z);
            Assert.Equal(5UL, states[0].userData.low);
            Assert.Equal(6UL, states[0].userData.high);
            Assert.Equal(0u, states[0].enabled);

            RapierNative.RigidBodyState s = states[1];
            Assert.Equal(1f, s.position.translation.x);
            Assert.Equal(2f, s.position.translation.y);
            Assert.Equal(3f, s.position.translation.z);
            Assert.Equal(0.70710677f, s.position.rotation.z, 1e-5f);
            Assert.Equal(0.70710677f, s.position.rotation.w, 1e-5f);
            Assert.Equal(4f, s.linvel.x);
            Assert.Equal(5f, s.linvel.y);
            Assert.Equal(6f, s.linvel.z);
            Assert.Equal(7f, s.angvel.x);
            Assert.Equal(8f, s.angvel.y);
            Assert.Equal(9f, s.angvel.z);
            Assert.Equal(1u, s.enabled);
            Assert.Equal(0x1122334455667788UL, s.userData.low);
            Assert.Equal(99UL, s.userData.high);
        }
        finally
        {
            RapierNative.r3FreeWorld(world);
        }
    }

    [Fact]
    public void MassProperties_FieldsLandWhereTheLibraryPutsThem()
    {
        Assert.True(RapierNative.TryLoad(out string error), error);
        // A unit-density 2 x 4 x 6 box: mass 48, inertia about each axis (m/12)(b^2 + c^2), centre of mass at the origin.
        IntPtr shape = RapierNative.r3CuboidSharedShape(new RapierNative.Vector { x = 1f, y = 2f, z = 3f });
        try
        {
            RapierNative.MassProperties props = RapierNative.r3SharedShape_MassProperties(shape, 1f);
            Assert.Equal(48f, props.mass, 1e-3f);
            Assert.Equal(0f, props.localCom.x, 1e-5f);
            Assert.Equal(4f * (16f + 36f), props.principalInertia.x, 1e-2f);
            Assert.Equal(4f * (4f + 36f), props.principalInertia.y, 1e-2f);
            Assert.Equal(4f * (4f + 16f), props.principalInertia.z, 1e-2f);
        }
        finally
        {
            RapierNative.r3FreeSharedShape(shape);
        }
    }

    [Fact]
    public void PlanSteps_AccumulatesLikeBulletsStepSimulation()
    {
        RapierWorldState state = new RapierWorldState();
        float fixedStep = 1f / 60f;

        // 0.0909 s is 5.45 steps: five now, the 0.45 left over is carried.
        Assert.Equal(5, state.PlanSteps(0.0909f, 10, fixedStep, out float seconds));
        Assert.Equal(fixedStep, seconds);
        Assert.Equal(0.0909f - 5 * fixedStep, state.Accumulator, 1e-5f);

        // Less than one step of time takes no step and keeps the time.
        RapierWorldState small = new RapierWorldState();
        Assert.Equal(0, small.PlanSteps(0.005f, 10, fixedStep, out _));
        Assert.Equal(0.005f, small.Accumulator, 1e-6f);
        Assert.Equal(1, small.PlanSteps(0.0125f, 10, fixedStep, out _));

        // More steps than maxSubSteps are cut to maxSubSteps and the time beyond them is dropped.
        RapierWorldState capped = new RapierWorldState();
        Assert.Equal(3, capped.PlanSteps(1f, 3, fixedStep, out _));
        Assert.True(capped.Accumulator < fixedStep);

        // maxSubSteps of 0 is one variable-length step of the whole time.
        RapierWorldState variable = new RapierWorldState();
        Assert.Equal(1, variable.PlanSteps(0.05f, 0, fixedStep, out seconds));
        Assert.Equal(0.05f, seconds);
        Assert.Equal(0f, variable.Accumulator);
    }

    private static BSAPIRapier.BulletWorldRapier NewWorld(out BSAPIRapier api)
    {
        Assert.True(RapierNative.TryLoad(out string error), error);
        Assert.Null(RapierNative.VerifyAbi());
        api = new BSAPIRapier("Rapier", new BSScene());
        CollisionDesc[] collisions = new CollisionDesc[10];
        EntityProperties[] updates = new EntityProperties[10];
        ConfigurationParameters parms = new ConfigurationParameters { gravity = -9.8f };
        return (BSAPIRapier.BulletWorldRapier)api.Initialize(new Vector3(256f, 256f, 4096f), parms, 10, ref collisions, 10, ref updates);
    }

    private static ShapeData NativeShape(BSPhysicsShapeType type, Vector3 scale)
    {
        return new ShapeData { Type = type, Scale = scale, Size = scale };
    }

    [Fact]
    public void NativeBox_IsBuiltAtItsFinalSizeAndHasTheBoxInertia()
    {
        BSAPIRapier api;
        BSAPIRapier.BulletWorldRapier world = NewWorld(out api);
        try
        {
            Vector3 size = new Vector3(2f, 4f, 6f);
            BulletShape shape = api.BuildNativeShape(world, NativeShape(BSPhysicsShapeType.SHAPE_BOX, size));

            Assert.True(shape.HasPhysicalShape);
            Assert.True(api.IsNativeShape(shape));
            Assert.Equal(size, api.GetLocalScaling(shape));
            // (m / 12) (b^2 + c^2) about each axis, for a 2 x 4 x 6 box of mass 12.
            Vector3 inertia = api.CalculateLocalInertia(shape, 12f);
            Assert.Equal(16f + 36f, inertia.X, 1e-2f);
            Assert.Equal(4f + 36f, inertia.Y, 1e-2f);
            Assert.Equal(4f + 16f, inertia.Z, 1e-2f);
        }
        finally
        {
            api.Shutdown(world);
        }
    }

    [Fact]
    public void NativeCylinder_RunsAlongZ()
    {
        BSAPIRapier api;
        BSAPIRapier.BulletWorldRapier world = NewWorld(out api);
        try
        {
            // Radius 1 (Scale.X / 2), height 6 (Scale.Z), mass 12. Rapier builds it along Y, so the collider is turned to
            // make Z the axis: Izz = m r^2 / 2, Ixx = Iyy = m (3 r^2 + h^2) / 12.
            BulletShape shape = api.BuildNativeShape(world, NativeShape(BSPhysicsShapeType.SHAPE_CYLINDER, new Vector3(2f, 2f, 6f)));

            Vector3 inertia = api.CalculateLocalInertia(shape, 12f);
            Assert.Equal(12f * 1f / 2f, inertia.Z, 1e-2f);
            Assert.Equal(12f * (3f + 36f) / 12f, inertia.X, 1e-2f);
            Assert.Equal(12f * (3f + 36f) / 12f, inertia.Y, 1e-2f);
        }
        finally
        {
            api.Shutdown(world);
        }
    }

    [Fact]
    public void DeleteCollisionShape_IsSafeToRepeat_AndShutdownFreesTheRest()
    {
        BSAPIRapier api;
        BSAPIRapier.BulletWorldRapier world = NewWorld(out api);
        BulletShape deleted = api.BuildNativeShape(world, NativeShape(BSPhysicsShapeType.SHAPE_SPHERE, new Vector3(1f, 1f, 1f)));
        BulletShape copy = deleted.Clone();
        api.BuildNativeShape(world, NativeShape(BSPhysicsShapeType.SHAPE_BOX, new Vector3(1f, 1f, 1f)));
        Assert.Equal(2, world.State.Shapes.Count);

        Assert.True(api.DeleteCollisionShape(world, deleted));
        Assert.False(deleted.HasPhysicalShape);
        // The clone pointed at the same native shape; deleting that again must not free it twice.
        Assert.True(api.DeleteCollisionShape(world, copy));
        Assert.Single(world.State.Shapes);

        api.Shutdown(world);
        Assert.Empty(world.State.Shapes);
        // BSScene's Dispose could reach Shutdown more than once; the second must not touch the freed world.
        api.Shutdown(world);
    }

    [Fact]
    public void MissingLibrary_CreatingTheSceneFailsWithAClearException()
    {
        string? previousOverride = RapierNative.LibraryPathOverride;
        RapierNative.LibraryPathOverride = Path.Combine(Path.GetTempPath(), "no-such-dir", "no-such-rapier");
        try
        {
            InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => RapierTestScene.Create());
            Assert.Contains("Rapier", e.Message);
            Assert.Contains("could not be started", e.Message);
        }
        finally
        {
            RapierNative.LibraryPathOverride = previousOverride;
        }
    }

    [Fact]
    public void ActivationStatesAndFlags_AreHandled_AndNothingIsLogged()
    {
        BSScene scene = RapierTestScene.Create();
        try
        {
            BSAPITemplate pe = scene.PE;
            BulletBody body = pe.CreateBodyFromShape(scene.World,
                pe.BuildNativeShape(scene.World, new ShapeData { Type = BSPhysicsShapeType.SHAPE_BOX, Scale = Vector3.One, Size = Vector3.One }),
                7, Vector3.Zero, Quaternion.Identity);
            // The flags that do nothing in Rapier by design (H3) are not worth a log line.
            pe.AddToCollisionFlags(body, CollisionFlags.CF_CHARACTER_OBJECT | CollisionFlags.BS_FLOATS_ON_WATER);
            pe.AddToCollisionFlags(body, CollisionFlags.CF_NO_CONTACT_RESPONSE);
            // Every activation state BulletS uses is acted on now (H3), so none is reported as ignored.
            foreach (ActivationState state in Enum.GetValues<ActivationState>())
                pe.ForceActivationState(body, state);

            HashSet<string> logged = ((BSAPIRapier.BulletBodyRapier)body).State.LoggedOnce;
            Assert.DoesNotContain(logged, k => k.StartsWith("state:"));
            Assert.DoesNotContain(logged, k => k.StartsWith("flags:"));
            Assert.DoesNotContain("terrain-standin", logged);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void Loading_LogsTheNativeLibraryPath()
    {
        using ErrorLog log = new ErrorLog();
        BSScene scene = RapierTestScene.Create();
        try
        {
            string line = Assert.Single(log.Infos, i => i.Contains("native library:"));
            string path = RapierNative.BoundLibraryPath()!;
            Assert.NotNull(path);
            Assert.True(File.Exists(path), path);
            Assert.Contains(path, line);
            Assert.Equal(path, RapierNative.LoadedLibraryPath());
            Assert.DoesNotContain(log.Warnings, w => w.Contains("calls go to"));
        }
        finally
        {
            scene.Dispose();
        }
    }
}
