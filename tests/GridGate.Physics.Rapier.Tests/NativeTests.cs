using OpenSim.Region.PhysicsModule.BulletS.Rapier;
using Xunit;

namespace GridGate.Physics.Rapier.Tests;

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

    // The structs VerifyAbi does not size-check, because r3PodLayout does not report them: their sizes from rapier.h
    // (3D, single precision, 64-bit: a handle is a pointer and two uint32, 16 bytes, and size_t is 8), so a header bump
    // that changes one fails here. Each literal is the field sum plus the padding to the struct's alignment.

    [Fact]
    public void RigidBodyState_MatchesRapierH()
    {
        // Pose 28 + linvel 12 + angvel 12 + sleeping 4 + enabled 4 = 60, padded to 64 for the 8-aligned UserData 16.
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(80, System.Runtime.InteropServices.Marshal.SizeOf<RapierNative.RigidBodyState>());
    }

    [Fact]
    public void ContactPair_MatchesRapierH()
    {
        // Two handles 32 + has_any_active_contact 4 + total_impulse 12 + two reals 8 + max_impulse_direction 12 = 68 -> 72.
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(72, System.Runtime.InteropServices.Marshal.SizeOf<RapierNative.ContactPair>());
    }

    [Fact]
    public void ContactPoint_MatchesRapierH()
    {
        // manifold_index 8 + local_p1, local_p2, normal 36 + distance, impulse 8 + tangent_impulse[2] 8 = 60 -> 64.
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(64, System.Runtime.InteropServices.Marshal.SizeOf<RapierNative.ContactPoint>());
    }

    [Fact]
    public void MassProperties_MatchesRapierH()
    {
        // local_com 12 + mass 4 + principal_inertia 12 + principal_inertia_local_frame 16 = 44, all 4-aligned.
        Assert.Equal(44, System.Runtime.InteropServices.Marshal.SizeOf<RapierNative.MassProperties>());
    }

    [Fact]
    public void IntersectionPair_MatchesRapierH()
    {
        // Two handles 32 + intersecting 4 = 36 -> 40.
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(40, System.Runtime.InteropServices.Marshal.SizeOf<RapierNative.IntersectionPair>());
    }

    [Fact]
    public void CollisionEvent_MatchesRapierH()
    {
        // Two handles 32 + started 4 + flags 4 = 40.
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(40, System.Runtime.InteropServices.Marshal.SizeOf<RapierNative.CollisionEvent>());
    }

    [Fact]
    public void ShapeCastHit_MatchesRapierH()
    {
        // Handle 16 + time_of_impact 4 + witness1, witness2, normal1, normal2 48 + status 4 = 72.
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(72, System.Runtime.InteropServices.Marshal.SizeOf<RapierNative.ShapeCastHit>());
    }

    [Fact]
    public void JointImpulses_MatchesRapierH()
    {
        // linear 12 + angular 12 + limits[6] 24 + motors[6] 24 = 72, all 4-aligned.
        Assert.Equal(72, System.Runtime.InteropServices.Marshal.SizeOf<RapierNative.JointImpulses>());
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
