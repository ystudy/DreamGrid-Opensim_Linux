/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

using OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS.Rapier
{
    /// <summary>
    /// The only file that knows the layout of ThirdParty/rapier/include/rapier.h (3D, single precision, no FEM or
    /// robotics). Struct fields keep the header's order and types: R3Bool is uint32 (declared uint here, never bool),
    /// size_t is UIntPtr, and every handle is the header's 16-byte value struct. Functions are added as they are needed.
    /// </summary>
    internal static class RapierNative
    {
        public const string Lib = "rapier";

        // R3_ABI_VERSION, and R3_ABI_FEATURES for a build without RAPIER_FEM and RAPIER_ROBOTICS.
        private const uint AbiVersion = 1;
        private const uint AbiFeatures = 0;

        // R3_SHAPE_DESC_SHARED: a ColliderDesc whose shape is the SharedShape pointer in ShapeDesc.sharedShape.
        public const uint ShapeKindShared = 10;

        // R3_SHAPE_DESC_HEIGHTFIELD, and R3_HEIGHTFIELD_FIX_INTERNAL_EDGES for ShapeDesc.flags.
        public const uint ShapeKindHeightfield = 11;
        public const uint HeightfieldFixInternalEdges = 1;

        // R3_TRIMESH_MERGE_DUPLICATE_VERTICES and R3_TRIMESH_FIX_INTERNAL_EDGES (which includes the merge), for the flags of
        // r3TrimeshSharedShapeWithFlags.
        public const uint TrimeshMergeDuplicateVertices = 16;
        public const uint TrimeshFixInternalEdges = 144;

        // R3_NOT_FOUND: the status a query reports when nothing was hit (not an error).
        public const uint StatusNotFound = 7;

        // R3_LOCK_* bits for RigidBodyDesc.lockedAxes.
        public const byte LockTranslationX = 1;
        public const byte LockTranslationY = 2;
        public const byte LockTranslationZ = 4;
        public const byte LockRotationX = 8;
        public const byte LockRotationY = 16;
        public const byte LockRotationZ = 32;

        // R3_DYNAMIC, R3_FIXED and the two kinematic kinds: RigidBodyDesc.bodyType and r3RigidBody_SetBodyType.
        public const uint BodyDynamic = 0;
        public const uint BodyFixed = 1;
        public const uint BodyKinematicPositionBased = 2;
        public const uint BodyKinematicVelocityBased = 3;

        // R3_COLLISION_TYPES_*: which pairs of body types a collider takes part in (ColliderDesc.activeCollisionTypes).
        public const ushort CollisionTypesDefault = 15;
        public const ushort CollisionTypesKinematicFixed = 8704;
        public const ushort CollisionTypesFixedFixed = 32;

        // R3_MASS_*: how ColliderDesc turns its density, mass or mass properties into the body's mass.
        public const uint MassModeDensity = 0;

        // R3_COMBINE_MULTIPLY: Bullet multiplies the two bodies' friction and restitution.
        public const uint CombineMultiply = 2;

        // ---- Structs, copied from rapier.h ----

        [StructLayout(LayoutKind.Sequential)]
        public struct Vector { public float x, y, z; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rotation { public float x, y, z, w; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Pose { public Vector translation; public Rotation rotation; }

        [StructLayout(LayoutKind.Sequential)]
        public struct UserData { public ulong low, high; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Aabb { public Vector mins, maxs; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RigidBodyHandle { public IntPtr world; public uint index, generation; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ColliderHandle : IEquatable<ColliderHandle>
        {
            public IntPtr world; public uint index, generation;

            public bool Equals(ColliderHandle other) { return world == other.world && index == other.index && generation == other.generation; }
            public override bool Equals(object obj) { return obj is ColliderHandle other && Equals(other); }
            public override int GetHashCode() { return unchecked((int)(index * 397u ^ generation * 31u) ^ world.GetHashCode()); }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ImpulseJointHandle { public IntPtr world; public uint index, generation; }

        // ---- Joints ----

        // R3_JOINT_DOF_COUNT (6 in 3D): axes 0 to 2 are translations along the joint frame's X, Y and Z, 3 to 5 rotations
        // about them. A mask over the axes has bit n for axis n (the R3_LOCK_* bits).
        public const int JointDof = 6;

        [StructLayout(LayoutKind.Sequential)]
        public struct JointLimits { public float min, max; }

        // R3JointMotor. model: 0 acceleration-based, 1 force-based.
        [StructLayout(LayoutKind.Sequential)]
        public struct JointMotor
        {
            public float targetVel, targetPos, stiffness, damping, maxForce;
            public uint model;
        }

        [InlineArray(JointDof)]
        public struct JointLimitsArray { private JointLimits _element; }

        [InlineArray(JointDof)]
        public struct JointMotorArray { private JointMotor _element; }

        [InlineArray(JointDof)]
        public struct JointImpulseArray { private float _element; }

        // R3SpringCoefficients: Rapier's joint softness, a natural frequency in Hz and a damping ratio.
        [StructLayout(LayoutKind.Sequential)]
        public struct SpringCoefficients { public float naturalFrequency, dampingRatio; }

        [StructLayout(LayoutKind.Sequential)]
        public struct JointDesc
        {
            public Pose localFrame1;
            public Pose localFrame2;
            public byte lockedAxes;
            public byte limitAxes;
            public byte motorAxes;
            public byte coupledAxes;
            public JointLimitsArray limits;
            public JointMotorArray motors;
            public SpringCoefficients softness;
            public uint contactsEnabled;
            public uint enabled;
            public UserData userData;
        }

        // R3JointImpulses (3D: the angular impulse is a vector): what the solver applied in the last step, along the
        // joint frame's axes.
        [StructLayout(LayoutKind.Sequential)]
        public struct JointImpulses
        {
            public Vector linear;
            public Vector angular;
            public JointImpulseArray limits;
            public JointImpulseArray motors;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MassProperties
        {
            public Vector localCom;
            public float mass;
            public Vector principalInertia;
            public Rotation principalInertiaLocalFrame;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct InteractionGroups { public uint memberships, filter, testMode; }

        [StructLayout(LayoutKind.Sequential)]
        public struct QueryFilter
        {
            public uint flags;
            public uint useGroups;
            public InteractionGroups groups;
            public ColliderHandle excludeCollider;
            public RigidBodyHandle excludeRigidBody;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct QueryOptions
        {
            public QueryFilter filter;
            public IntPtr predicate;
            public IntPtr userData;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RayHit
        {
            public ColliderHandle collider;
            public float timeOfImpact;
            public Vector normal;
            public uint featureType;
            public uint featureId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RigidBodyDesc
        {
            public Pose position;
            public Vector linvel;
            public Vector angvel;
            public uint bodyType;
            public float gravityScale;
            public float linearDamping;
            public float angularDamping;
            public float additionalMass;
            public uint useAdditionalMassProperties;
            public MassProperties additionalMassProperties;
            public byte lockedAxes;
            public uint canSleep;
            public uint sleeping;
            public uint ccdEnabled;
            public float softCcdPrediction;
            public uint allowFastRotation;
            public uint enabled;
            public sbyte dominanceGroup;
            public UIntPtr additionalSolverIterations;
            public UIntPtr additionalPgsIterations;
            public uint gyroscopicForcesEnabled;
            public UserData userData;
        }

        // R3VectorView, R3TriangleView, R3EdgeView, R3RealView and R3CompoundShapeView: a borrowed pointer and an element count.
        [StructLayout(LayoutKind.Sequential)]
        public struct View { public IntPtr data; public UIntPtr count; }

        // R3Triangle: three vertex indices.
        [StructLayout(LayoutKind.Sequential)]
        public struct Triangle { public uint a, b, c; }

        // R3CompoundShapeDesc: a child's pose in the compound and its shape (here always a ShapeKindShared one).
        [StructLayout(LayoutKind.Sequential)]
        public struct CompoundShapeDesc
        {
            public Pose pose;
            public ShapeDesc shape;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ShapeDesc
        {
            public uint kind;
            public Vector a;
            public Vector b;
            public Vector c;
            public float radius;
            public float halfHeight;
            public float borderRadius;
            public View vertices;
            public View triangles;
            public View edges;
            public uint flags;
            public View heights;
            public UIntPtr rows;
            public UIntPtr columns;
            public Vector scale;
            public IntPtr sharedShape;
            public View children;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ColliderDesc
        {
            public ShapeDesc shape;
            public Pose position;
            public uint massMode;
            public float density;
            public float mass;
            public MassProperties massProperties;
            public float friction;
            public float restitution;
            public uint frictionCombineRule;
            public uint restitutionCombineRule;
            public uint isSensor;
            public uint enabled;
            public InteractionGroups collisionGroups;
            public InteractionGroups solverGroups;
            public ushort activeCollisionTypes;
            public uint activeHooks;
            public uint activeEvents;
            public float contactForceEventThreshold;
            public float contactSkin;
            public UserData userData;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RigidBodyState
        {
            public Pose position;
            public Vector linvel;
            public Vector angvel;
            public uint sleeping;
            public uint enabled;
            public UserData userData;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PodLayout
        {
            public UIntPtr rigidBodyDesc, colliderDesc, shapeDesc, jointDesc;
            public UIntPtr softBodyMaterial, integrationParameters, softBodyDesc;
            public UIntPtr softMeshBindingDesc, queryOptions, urdfLoaderOptions, mjcfLoaderOptions;
        }

        // R3ContactPair, R3IntersectionPair and R3ContactPoint (3D, single precision: the tangent impulses are two reals).
        [StructLayout(LayoutKind.Sequential)]
        public struct ContactPair
        {
            public ColliderHandle collider1, collider2;
            public uint hasAnyActiveContact;
            public Vector totalImpulse;
            public float totalImpulseMagnitude;
            public float maxImpulse;
            public Vector maxImpulseDirection;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IntersectionPair
        {
            public ColliderHandle collider1, collider2;
            public uint intersecting;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ContactPoint
        {
            public UIntPtr manifoldIndex;
            public Vector localP1, localP2;
            public Vector normal;
            public float distance;
            public float impulse;
            public float tangentImpulse0, tangentImpulse1;
        }

        // R3BuildFeatures: what the loaded library was built with.
        [StructLayout(LayoutKind.Sequential)]
        public struct BuildFeatures { public uint profiling, simdLanes, parallel, enhancedDeterminism; }

        // ---- Library, ABI and error reporting ----

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr r3Version();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern BuildFeatures r3BuildFeatures();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3CheckAbi(uint version, uint dimension, UIntPtr realSize, UIntPtr vectorSize, UIntPtr poseSize, uint features);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern PodLayout r3PodLayout();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr r3LastError();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3LastStatus();

        // ---- World ----

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr r3NewWorld();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3FreeWorld(IntPtr world);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3SetGravity(IntPtr world, Vector value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern Vector r3Gravity(IntPtr world);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3SetTimeStep(IntPtr world, float value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern float r3TimeStep(IntPtr world);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3SetNumSolverIterations(IntPtr world, UIntPtr value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern UIntPtr r3NumSolverIterations(IntPtr world);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3SetNumThreads(IntPtr world, UIntPtr numThreads);

        // hooks and events are R3PhysicsHooks* and R3EventCollector*; pass IntPtr.Zero for none.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3Step(IntPtr world, IntPtr hooks, IntPtr events);

        // ---- Rigid bodies ----

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern RigidBodyDesc r3DynamicRigidBodyDesc();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern RigidBodyDesc r3FixedRigidBodyDesc();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern RigidBodyDesc r3KinematicPositionBasedRigidBodyDesc();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern RigidBodyDesc r3KinematicVelocityBasedRigidBodyDesc();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern RigidBodyHandle r3InsertRigidBody(IntPtr world, in RigidBodyDesc desc);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RemoveRigidBody(RigidBodyHandle handle, uint removeAttachedColliders);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern Vector r3RigidBody_Translation(RigidBodyHandle handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern Rotation r3RigidBody_Rotation(RigidBodyHandle handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern Vector r3RigidBody_Linvel(RigidBodyHandle handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern Vector r3RigidBody_Angvel(RigidBodyHandle handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetTranslation(RigidBodyHandle handle, Vector value, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetRotation(RigidBodyHandle handle, Rotation value, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetLinvel(RigidBodyHandle handle, Vector value, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetAngvel(RigidBodyHandle handle, Vector value, uint wakeUp);

        // ---- Rigid body state, mass and flags ----

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetPosition(RigidBodyHandle handle, Pose value, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetGravityScale(RigidBodyHandle handle, float value, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetEnabled(RigidBodyHandle handle, uint value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_IsEnabled(RigidBodyHandle handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetBodyType(RigidBodyHandle handle, uint value, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_BodyType(RigidBodyHandle handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetAdditionalMassProperties(RigidBodyHandle handle, MassProperties properties, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetUserData(RigidBodyHandle handle, UserData value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern UserData r3RigidBody_UserData(RigidBodyHandle handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetLinearDamping(RigidBodyHandle handle, float value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetAngularDamping(RigidBodyHandle handle, float value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_WakeUp(RigidBodyHandle handle, uint strong);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_Sleep(RigidBodyHandle handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_IsSleeping(RigidBodyHandle handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_ResetForces(RigidBodyHandle handle, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_ResetTorques(RigidBodyHandle handle, uint wakeUp);

        // wakeUp is R3Bool: 1 wakes the body, 0 keeps its sleep state. The force and torque add to what the body has and
        // stay until r3RigidBody_ResetForces / r3RigidBody_ResetTorques; an impulse changes the velocity at once.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_AddForce(RigidBodyHandle handle, Vector value, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_AddTorque(RigidBodyHandle handle, Vector value, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_ApplyImpulse(RigidBodyHandle handle, Vector value, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_ApplyTorqueImpulse(RigidBodyHandle handle, Vector value, uint wakeUp);

        // The force and the point are both in world space.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_AddForceAtPoint(RigidBodyHandle handle, Vector value, Vector point, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_ApplyImpulseAtPoint(RigidBodyHandle handle, Vector value, Vector point, uint wakeUp);

        // What r3RigidBody_AddForce / _AddTorque (and the at-point forms) have accumulated since the last reset, in world space.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern Vector r3RigidBody_UserForce(RigidBodyHandle handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern Vector r3RigidBody_UserTorque(RigidBodyHandle handle);

        // The world-space centre of mass.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern Vector r3RigidBody_CenterOfMass(RigidBodyHandle handle);

        // axes is a mask of the R3_LOCK_* bits below.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetLockedAxes(RigidBodyHandle handle, byte axes, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetCcdEnabled(RigidBodyHandle handle, uint value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_IsCcdEnabled(RigidBodyHandle handle);

        // Fills buffer with up to capacity handles of the bodies that are awake and returns how many.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern UIntPtr r3ActiveRigidBodies(IntPtr world, [Out] RigidBodyHandle[] buffer, UIntPtr capacity);

        // Copies the states of handles[0..handleCount) in the same order. Null/0 for states/capacity is a size query.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern UIntPtr r3RigidBodyReadStates(IntPtr world, RigidBodyHandle[] handles, UIntPtr handleCount, [Out] RigidBodyState[] states, UIntPtr capacity);

        // ---- Colliders and shapes ----

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern ColliderDesc r3DefaultColliderDesc();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern ColliderDesc r3BallColliderDesc(float radius);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern ColliderDesc r3CuboidColliderDesc(Vector halfExtents);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern ColliderDesc r3CylinderColliderDesc(float halfHeight, float radius);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern ColliderDesc r3CapsuleColliderDesc(Vector a, Vector b, float radius);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern ColliderHandle r3InsertCollider(RigidBodyHandle parent, in ColliderDesc desc);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern ColliderHandle r3InsertColliderWithoutParent(IntPtr world, in ColliderDesc desc);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RemoveCollider(ColliderHandle handle, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3Collider_SetFriction(ColliderHandle handle, float value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3Collider_SetRestitution(ColliderHandle handle, float value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3Collider_SetCollisionGroups(ColliderHandle handle, InteractionGroups value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3Collider_SetSolverGroups(ColliderHandle handle, InteractionGroups value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3Collider_SetSensor(ColliderHandle handle, uint value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3Collider_SetActiveCollisionTypes(ColliderHandle handle, ushort value);

        // The collider's pose in the world.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern Pose r3Collider_Position(ColliderHandle handle);

        // The collider's pose relative to its parent body.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3Collider_SetPositionWrtParent(ColliderHandle handle, Pose value);

        // ---- Contacts and sensor overlaps ----
        // For all three the buffer takes up to capacity entries and the return value is how many there are in all:
        // when it is more than capacity, nothing useful was written and the call is made again with a bigger buffer.

        // The narrow-phase pairs the collider is in, with or without an active contact.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern UIntPtr r3Collider_ContactPairs(ColliderHandle handle, [Out] ContactPair[] buffer, UIntPtr capacity);

        // Every sensor overlap in the world.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern UIntPtr r3IntersectionPairs(IntPtr world, [Out] IntersectionPair[] buffer, UIntPtr capacity);

        // Contact points of a pair: collider-local points, world normal. Follow the pair's own collider order.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern UIntPtr r3ContactPoints(ColliderHandle collider1, ColliderHandle collider2, [Out] ContactPoint[] buffer, UIntPtr capacity);

        // A shared shape is an R3SharedShape*: set ColliderDesc.shape.kind = ShapeKindShared and shape.sharedShape to it,
        // and free it with r3FreeSharedShape when done with it.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr r3BallSharedShape(float radius);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr r3CuboidSharedShape(Vector halfExtents);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr r3CylinderSharedShape(float halfHeight, float radius);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr r3CapsuleSharedShape(Vector a, Vector b, float radius);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3FreeSharedShape(IntPtr shape);

        // The views are borrowed for the call: Rapier copies the geometry.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr r3TrimeshSharedShapeWithFlags(View vertices, View triangles, uint flags);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr r3ConvexHullSharedShape(View vertices);

        // A compound of convex parts. It takes no parameters.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr r3ConvexDecompositionSharedShape(View vertices, View triangles);

        // Child shapes are shared, not consumed: the compound holds its own references.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr r3CompoundSharedShape(View children);

        // A new wrapper sharing the same immutable geometry; free it with r3FreeSharedShape.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr r3SharedShape_Clone(IntPtr shape);

        // The collider shares the shape (the supplied wrapper is not consumed).
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3Collider_SetShape(ColliderHandle handle, IntPtr shape);

        // Builds a SharedShape from a ShapeDesc (the borrowed views are copied). Null on failure.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr r3ShapeDesc_Build(in ShapeDesc desc);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern UserData r3Collider_UserData(ColliderHandle handle);

        // ---- Impulse joints ----

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern JointDesc r3DefaultJointDesc();

        // Joins two bodies of one world. An invalid handle (and an error status) when it fails.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern ImpulseJointHandle r3InsertImpulseJoint(RigidBodyHandle body1, RigidBodyHandle body2, in JointDesc joint);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RemoveImpulseJoint(ImpulseJointHandle handle, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern JointDesc r3ImpulseJoint_Desc(ImpulseJointHandle handle);

        // Replaces the whole configuration and resets the cached limit and motor impulses.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3ImpulseJoint_SetDesc(ImpulseJointHandle handle, in JointDesc desc, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3ImpulseJoint_SetEnabled(ImpulseJointHandle handle, uint value, uint wakeUp);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern JointImpulses r3ImpulseJoint_Impulses(ImpulseJointHandle handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern float r3RigidBody_Mass(RigidBodyHandle handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBody_SetAdditionalSolverIterations(RigidBodyHandle handle, UIntPtr value);

        // ---- Queries ----

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern QueryFilter r3DefaultQueryFilter();

        // Closest hit of origin + direction * t for 0 <= t <= maxToi; a miss reports R3_NOT_FOUND in r3LastStatus.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern RayHit r3CastRay(IntPtr world, in QueryOptions options, Vector origin, Vector direction, float maxToi, uint solid);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern uint r3RigidBodyPropagateModifiedBodyPositionsToColliders(IntPtr world);

        // Local mass properties of the shape at the given density (mass = density * volume).
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern MassProperties r3SharedShape_MassProperties(IntPtr shape, float density);

        // The shape's axis-aligned bounds at the given pose.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern Aabb r3SharedShape_ComputeAabb(IntPtr shape, Pose pose);

        // ---- Status and ABI helpers ----

        /// <summary>Throws InvalidOperationException with Rapier's last error message when status is not R3_OK (0).</summary>
        public static void Check(uint status)
        {
            if (status != 0)
                throw new InvalidOperationException("Rapier: " + LastError());
        }

        public static string LastError()
        {
            return Marshal.PtrToStringUTF8(r3LastError()) ?? "unknown error";
        }

        public static string Version()
        {
            return Marshal.PtrToStringUTF8(r3Version()) ?? "";
        }

        /// <summary>
        /// Asks the loaded library whether it was built with the layout rapier.h describes here, and whether the structs
        /// in this file are the sizes it uses. Null when everything agrees; otherwise a message naming the first
        /// disagreement. Call it, and get null, before any other Rapier call.
        /// </summary>
        public static string VerifyAbi()
        {
            try
            {
                uint status = r3CheckAbi(AbiVersion, 3, (UIntPtr)sizeof(float), (UIntPtr)Marshal.SizeOf<Vector>(),
                    (UIntPtr)Marshal.SizeOf<Pose>(), AbiFeatures);
                if (status != 0)
                    return "Rapier r3CheckAbi failed: " + LastError();

                PodLayout native = r3PodLayout();
                return CompareLayout(new (string name, int expected, int actual)[]
                {
                    ("RigidBodyDesc", Marshal.SizeOf<RigidBodyDesc>(), (int)native.rigidBodyDesc),
                    ("ColliderDesc", Marshal.SizeOf<ColliderDesc>(), (int)native.colliderDesc),
                    ("ShapeDesc", Marshal.SizeOf<ShapeDesc>(), (int)native.shapeDesc),
                    ("JointDesc", Marshal.SizeOf<JointDesc>(), (int)native.jointDesc),
                });
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            {
                return "Rapier library is not usable: " + e.Message;
            }
        }

        /// <summary>
        /// Null when every struct is the size the native library says; otherwise the message for the first one that is not.
        /// expected is the size of this file's struct, actual the size the library reports.
        /// </summary>
        internal static string CompareLayout(IReadOnlyList<(string name, int expected, int actual)> sizes)
        {
            foreach (var (name, expected, actual) in sizes)
            {
                if (expected != actual)
                    return $"Rapier struct {name} is {actual} bytes in the native library but {expected} bytes in RapierNative.";
            }
            return null;
        }

        // ---- Loading ----

        /// <summary>When set (tests), the library is loaded from this path instead of the one in the BulletS .dll.config.</summary>
        internal static string LibraryPathOverride;

        private static readonly object s_loadLock = new object();
        private static IntPtr s_handle;
        private static bool s_resolverInstalled;

        /// <summary>
        /// Loads the Rapier library and makes the "rapier" DllImports use it. The file comes from LibraryPathOverride
        /// or, otherwise, from the dllmap line for "rapier" in the BulletS .dll.config, read by the same
        /// DllmapConfigHelper (and so the same os/cpu matching) that resolves the BulletSim and ODE libraries.
        /// Safe to call again; later calls reuse the first successful load.
        /// </summary>
        public static bool TryLoad(out string error)
        {
            lock (s_loadLock)
            {
                IntPtr handle;
                string path = LibraryPathOverride;
                if (!string.IsNullOrEmpty(path))
                {
                    if (!NativeLibrary.TryLoad(path, out handle))
                    {
                        error = $"Rapier library could not be loaded from {path}";
                        return false;
                    }
                }
                else if (s_handle != IntPtr.Zero)
                {
                    error = null;
                    return true;
                }
                else
                {
                    Assembly assembly = typeof(RapierNative).Assembly;
                    try
                    {
                        // The registration BSAPIUnman and the ODE module make. It reads the assembly's .dll.config, applies
                        // its os/cpu rules and loads every library the matching dllmap lines name; LoadDll returns the
                        // handle for one of them. (Its DllImport resolver only serves the public types of the assembly,
                        // which is why the handle is handed to the runtime below instead.)
                        DllmapConfigHelper.RegisterAssembly(assembly);
                        handle = DllmapConfigHelper.LoadDll(Lib);
                        if (handle == IntPtr.Zero)
                        {
                            error =$"{assembly.Location}.config has no dllmap line for \"{Lib}\" that fits this operating system and processor, or the library it names could not be loaded";
                            return false;
                        }
                    }
                    catch (Exception e)
                    {
                        error = $"Rapier library mapping in {assembly.Location}.config could not be used: {e.Message}";
                        return false;
                    }
                }

                s_handle = handle;
                if (!s_resolverInstalled)
                {
                    // ResolvingUnmanagedDll is the runtime's last try after its own probing, so it cooperates with the
                    // DllmapConfigHelper resolver on this assembly instead of competing for the one resolver slot.
                    AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveRapier;
                    s_resolverInstalled = true;
                }
                error = null;
                return true;
            }
        }

        private static IntPtr ResolveRapier(Assembly assembly, string libraryName)
        {
            if (libraryName == Lib && assembly == typeof(RapierNative).Assembly)
                return s_handle;
            return IntPtr.Zero;
        }
    }
}
