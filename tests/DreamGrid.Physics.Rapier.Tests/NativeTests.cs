using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace DreamGrid.Physics.Rapier.Tests;

// Runs against the shipped library, found through the BulletS .dll.config mapping like the product finds it.
public class NativeTests
{
    private static void Load()
    {
        Assert.True(RapierNative.TryLoad(out string error), error);
    }

    [Fact]
    public void Version_Is_0_36_1()
    {
        Load();
        string? version = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(RapierNative.r3Version());
        Assert.NotNull(version);
        Assert.StartsWith("0.36.1", version);
    }

    [Fact]
    public void VerifyAbi_PassesWithShippedLibrary()
    {
        Load();
        Assert.Null(RapierNative.VerifyAbi());
    }

    [Fact]
    public void VerifyAbi_ReportsMismatch()
    {
        string? message = RapierNative.CompareLayout(new (string name, int expected, int actual)[]
        {
            ("RigidBodyDesc", 160, 160),
            ("ColliderDesc", 321, 320),
            ("ShapeDesc", 128, 128),
        });
        Assert.NotNull(message);
        Assert.Contains("ColliderDesc", message);
        Assert.Contains("321", message);
        Assert.Contains("320", message);
        Assert.DoesNotContain("RigidBodyDesc", message);
    }

    [Fact]
    public void BoxFallsUnderGravity()
    {
        Load();
        Assert.Null(RapierNative.VerifyAbi());

        IntPtr world = RapierNative.r3NewWorld();
        RapierNative.Check(RapierNative.r3LastStatus());
        try
        {
            RapierNative.Check(RapierNative.r3SetGravity(world, new RapierNative.Vector { x = 0, y = 0, z = -9.8f }));
            RapierNative.Check(RapierNative.r3SetTimeStep(world, 0.02f));

            RapierNative.RigidBodyDesc bodyDesc = RapierNative.r3DynamicRigidBodyDesc();
            bodyDesc.position.translation = new RapierNative.Vector { x = 3, y = 4, z = 100 };
            RapierNative.RigidBodyHandle body = RapierNative.r3InsertRigidBody(world, in bodyDesc);
            RapierNative.Check(RapierNative.r3LastStatus());

            RapierNative.ColliderDesc colliderDesc = RapierNative.r3CuboidColliderDesc(new RapierNative.Vector { x = 0.5f, y = 0.5f, z = 0.5f });
            RapierNative.r3InsertCollider(body, in colliderDesc);
            RapierNative.Check(RapierNative.r3LastStatus());

            for (int i = 0; i < 50; i++)
                RapierNative.Check(RapierNative.r3Step(world, IntPtr.Zero, IntPtr.Zero));

            RapierNative.Vector position = RapierNative.r3RigidBody_Translation(body);
            double expectedZ = 100 - 0.5 * 9.8 * 1.0 * 1.0;
            Assert.InRange(position.z, expectedZ * 0.98, expectedZ * 1.02);
            Assert.Equal(3f, position.x);
            Assert.Equal(4f, position.y);
        }
        finally
        {
            RapierNative.r3FreeWorld(world);
        }
    }

    [Fact]
    public void TryLoad_ReportsAMissingOverride()
    {
        string? previous = RapierNative.LibraryPathOverride;
        RapierNative.LibraryPathOverride = Path.Combine(Path.GetTempPath(), "no-such-dir", "no-such-rapier");
        try
        {
            Assert.False(RapierNative.TryLoad(out string error));
            Assert.Contains("no-such-rapier", error);
        }
        finally
        {
            RapierNative.LibraryPathOverride = previous;
        }
    }
}
