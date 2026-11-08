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

using OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS.Rapier
{
    /// <summary>
    /// BulletS's physics engine interface (BSAPITemplate) on top of Rapier (0.36 C bindings through RapierNative).
    /// BSScene holds one of these and calls it from the thread that runs its taints and its step, so every Rapier call
    /// happens on that thread. The members are spread over partial files by subject; docs/superpowers/specs/
    /// 2026-11-06-rapier-bsapi-map.md maps each one. Members Rapier has no counterpart for (the map's gaps) log once
    /// and return a neutral value.
    /// </summary>
    public sealed partial class BSAPIRapier : BSAPITemplate
    {
        private static readonly string LogHeader = "[BULLETS RAPIER]";

        // ---- Wrappers around Rapier handles, as BSAPIXNA wraps BulletXNA objects ----

        internal sealed class BulletWorldRapier : BulletWorld
        {
            public IntPtr World;
            public RapierWorldState State;

            public BulletWorldRapier(uint id, BSScene physScene, IntPtr world, RapierWorldState state)
                : base(id, physScene)
            {
                World = world;
                State = state;
            }
        }

        internal sealed class BulletBodyRapier : BulletBody
        {
            public RapierNative.RigidBodyHandle Body;
            public List<RapierNative.ColliderHandle> Colliders = new List<RapierNative.ColliderHandle>();
            public readonly RapierWorldState State;

            // The Bullet collision flags last given to the body (H3). Rapier has no such field, so they are kept here.
            public CollisionFlags Flags;

            // The value last given to SetContactProcessingThreshold (Rapier has no such setting; map rows 142 and 143).
            public float ContactProcessingThreshold;

            // A volume-detect body, made by CreateGhostFromShape; its collider is a sensor.
            public bool IsGhost;

            // Set for a body that owns its shape (terrain and the ground plane), whose shape goes when the body does.
            public BulletShapeRapier OwnedShape;

            // The local pose of compound child 0, for BS_RETURN_ROOT_COMPOUND_SHAPE (H1): kept equal to the pose of child 0
            // of the compound on the body (RootChildPos, RootChildRot), and cleared when the body's shape is not a
            // compound.
            public bool HasRootChild;

            // The compound shape whose children are this body's colliders, or null (H5a).
            public CompoundState Compound;

            // What BulletS last set on the body that lives in the colliders, so colliders added later (SetCollisionShape,
            // a child added to the compound) get it too.
            public bool HasGroups;
            public RapierNative.InteractionGroups Groups;
            public float? Friction;
            public float? Restitution;

            // Whether BulletS has the body in the world (AddObjectToWorld, until RemoveObjectFromWorld). IsInWorld answers
            // this, whatever the activation state.
            public bool InWorld;

            // The body (not terrain) was forced to DISABLE_SIMULATION, so Rapier has it disabled although BulletS still
            // has it in the world (H3). Only AddObjectToWorld or another activation state ends it.
            public bool SimulationDisabled;

            // Whether PhysicsStep may report the body: in the world and simulated. A body Rapier has disabled makes no
            // contact pairs and no updates.
            public bool Reporting
            {
                get { return InWorld && !SimulationDisabled; }
            }

            // The linear and angular factors last set, as BulletS reads them back, and the locked-axes bits they made.
            public Vector3 LinearFactor = Vector3.One;
            public Vector3 AngularFactor = Vector3.One;
            public byte LockedAxes;

            // The CCD settings last set (H7). Rapier sweeps the real shape, so the radius is only kept.
            public float CcdMotionThreshold;
            public float CcdSweptSphereRadius;
            public Vector3 RootChildPos;
            public Quaternion RootChildRot = Quaternion.Identity;

            // The pose and velocities last read from Rapier or set, which the getters return for a body that is gone or
            // a read Rapier refuses: the creation pose and zero velocities until then.
            public Vector3 KnownPosition;
            public Quaternion KnownRotation = Quaternion.Identity;
            public Vector3 KnownVelocity;
            public Vector3 KnownAngularVelocity;

            // The shape last put on the body (CreateBodyFromShape, SetCollisionShape), for GetCollisionShape and sweeps.
            public BulletShapeRapier Shape;

            // Values Rapier has no place for, kept for the getters (map rows 60, 84, 94, 100, 105, 110, 128).
            // The gravity last set, or none (the world's applies).
            public Vector3? Gravity;
            public float HitFraction = 1f;
            public float LinearSleepingThreshold = 0.8f;
            public float AngularSleepingThreshold = 1f;
            public float DeactivationTime = 2f;
            // Bullet's activation state number (ActivationState); 1 is ACTIVE_TAG.
            public int Activation = 1;
            // What SetMassProps and SetCenterOfMassByPosRot last gave the additional mass properties.
            public float Mass;
            public Vector3 Inertia;
            public Vector3 CenterOfMass;
            public Quaternion InertiaFrame = Quaternion.Identity;
            // The constraints BulletS told the body about (AddConstraintRef); not the joints Rapier holds.
            public readonly List<BulletConstraint> ConstraintRefs = new List<BulletConstraint>();

            // The constraints this body is joined by; they die with it.
            private List<BulletConstraintRapier> m_joints;

            public void AddJoint(BulletConstraintRapier joint)
            {
                if (m_joints == null)
                    m_joints = new List<BulletConstraintRapier>();
                m_joints.Add(joint);
            }

            public void RemoveJoint(BulletConstraintRapier joint)
            {
                m_joints?.Remove(joint);
            }

            /// <summary>Rapier removes a body's joints with it: mark them dead so nothing calls Rapier with their handles.</summary>
            public void KillJoints()
            {
                if (m_joints == null)
                    return;
                foreach (BulletConstraintRapier joint in m_joints.ToArray())
                    joint.MarkDead();
                m_joints = null;
            }

            private bool m_valid;

            public BulletBodyRapier(uint id, RapierNative.RigidBodyHandle body, RapierWorldState state)
                : base(id)
            {
                Body = body;
                State = state;
                m_valid = true;
            }

            public override bool HasPhysicalBody
            {
                get { return m_valid; }
            }

            public override void Clear()
            {
                m_valid = false;
                Colliders.Clear();
                OwnedShape = null;
                Compound = null;
                Shape = null;
                HasRootChild = false;
                ConstraintRefs.Clear();
            }

            public override string AddrString
            {
                get { return "RapierBody" + Body.index + "." + Body.generation; }
            }
        }

        internal sealed class BulletShapeRapier : BulletShape
        {
            // The SharedShape. Rapier shapes cannot be resized, so it is built at the final size (H9).
            public IntPtr Shape;
            // The size this shape was built for; BulletS compares it with the prim's scale to decide on a rebuild.
            public Vector3 Scale;
            // Where the collider sits on the body. Rapier cylinders and capsules run along Y, BulletS's along Z, so
            // those get a quarter turn about X.
            public Vector3 LocalPos = Vector3.Zero;
            public Quaternion LocalRot = Quaternion.Identity;
            // Set for a compound shape (H5a), which has no native shape: it is a list of children until it is put on a
            // body, and then one collider per child on that body.
            public CompoundState Compound;

            // The compound's children, or null for a shape that is not a compound.
            public List<(BulletShapeRapier child, Vector3 pos, Quaternion rot)> Children
            {
                get { return Compound?.Children; }
            }
            // The mesh a hull was built from, kept so a hull can be rebuilt (H9).
            public float[] MeshVerts;
            public int[] MeshIndices;
            // How SetLocalScaling rebuilds the shape (H9). A native shape (box, sphere...) is built again at the new size;
            // one made from geometry is built again from MeshVerts after they are scaled; a hull list keeps its
            // BulletSim layout in MeshVerts, so scaling must leave the counts alone.
            public Func<Vector3, IntPtr> BuildNativeAt;
            public Func<float[], IntPtr> BuildFromVerts;
            public bool VertsAreHullList;
            // Bullet's collision margin, kept for GetMargin only: Rapier shapes have none (map rows 24, 40 and 41).
            public float Margin;

            public BulletShapeRapier(IntPtr shape, BSPhysicsShapeType type, Vector3 scale)
                : base()
            {
                Shape = shape;
                shapeType = type;
                Scale = scale;
            }

            public override bool HasPhysicalShape
            {
                get { return Shape != IntPtr.Zero || Compound != null; }
            }

            public override void Clear()
            {
                Shape = IntPtr.Zero;
                Compound = null;
            }

            public override BulletShape Clone()
            {
                BulletShapeRapier ret = new BulletShapeRapier(Shape, shapeType, Scale);
                ret.shapeKey = shapeKey;
                ret.isNativeShape = isNativeShape;
                ret.LocalPos = LocalPos;
                ret.LocalRot = LocalRot;
                // The same compound: a clone is another handle on it, as a copied btCompoundShape pointer would be.
                ret.Compound = Compound;
                ret.MeshVerts = MeshVerts;
                ret.MeshIndices = MeshIndices;
                ret.BuildNativeAt = BuildNativeAt;
                ret.BuildFromVerts = BuildFromVerts;
                ret.VertsAreHullList = VertsAreHullList;
                return ret;
            }

            public override bool ReferenceSame(BulletShape other)
            {
                BulletShapeRapier otherRapier = other as BulletShapeRapier;
                if (otherRapier == null)
                    return false;
                // Compounds have no native pointer, so they are the same when they are the same compound.
                if (this.Compound != null || otherRapier.Compound != null)
                    return ReferenceEquals(this.Compound, otherRapier.Compound);
                return this.Shape == otherRapier.Shape;
            }

            // BSShapeCompound keeps its compounds in a table by this string, so each compound needs its own.
            public override string AddrString
            {
                get { return (Compound != null) ? "RapierCompound" + Compound.Id : "RapierShape" + Shape.ToString("X"); }
            }
        }

        /// <summary>
        /// A compound shape (H5a, option b). Parry allows no composite shape inside a compound, and BulletS changes
        /// single children of a linkset's compound in place, so a BulletS compound is not a native shape: it is this
        /// list of children, and on a body each child is one or more colliders of that body (a child that is itself a
        /// BulletS compound is flattened into one collider per shape it holds). A hull list is not a BulletS compound:
        /// it is one native compound of convex hulls, so one collider.
        /// </summary>
        internal sealed class CompoundState
        {
            private static long s_nextId;

            public readonly long Id = System.Threading.Interlocked.Increment(ref s_nextId);

            public readonly List<(BulletShapeRapier child, Vector3 pos, Quaternion rot)> Children =
                        new List<(BulletShapeRapier child, Vector3 pos, Quaternion rot)>();

            // The body the compound is on, or null.
            public BulletBodyRapier Body;

            // While on a body: for each child, by the same index, its colliders and the pose of each within the child.
            public readonly List<List<ChildCollider>> Colliders = new List<List<ChildCollider>>();
        }

        internal struct ChildCollider
        {
            public RapierNative.ColliderHandle Collider;
            // The collider's pose relative to its compound child: the shape's own offset (LocalPos, LocalRot), composed
            // with the poses inside a nested compound.
            public Vector3 Pos;
            public Quaternion Rot;
        }

        /// <summary>
        /// A 6-DOF constraint (H5b) as an impulse joint. Bullet's btGeneric6DofConstraint keeps its limits per axis and
        /// BulletS sets them in several calls, so the per-axis state lives here and Desc, the joint's whole configuration,
        /// is rebuilt from it and given to Rapier on every change. A constraint is dead once it is destroyed or one of its
        /// bodies is: Rapier removes a body's joints with it, so the handle must not be used again.
        /// </summary>
        internal sealed class BulletConstraintRapier : BulletConstraint
        {
            public RapierNative.ImpulseJointHandle Joint;
            public readonly RapierWorldState State;
            // The bodies; Body1 is null for a constraint to the world, which is the state's anchor body.
            public readonly BulletBodyRapier Body1;
            public readonly BulletBodyRapier Body2;

            // The joint's configuration as last given to Rapier.
            public RapierNative.JointDesc Desc;

            // Bullet's per-axis limits (low == high locks, low < high limits, low > high frees) and 6-DOF defaults: the
            // linear axes locked and the angular axes free.
            public Vector3 LinearLow = Vector3.Zero;
            public Vector3 LinearHigh = Vector3.Zero;
            public Vector3 AngularLow = Vector3.One;
            public Vector3 AngularHigh = -Vector3.One;

            // The translational limit motor (X axis only, as BulletSim's TranslationalLimitMotor drives it).
            public bool TransMotor;
            public float TransMotorVelocity;
            public float TransMotorForce;

            // The springs, one per axis (0 to 2 linear, 3 to 5 angular).
            public readonly bool[] SpringOn = new bool[RapierNative.JointDof];
            public readonly float[] Stiffness = new float[RapierNative.JointDof];
            public readonly float[] Damping = new float[RapierNative.JointDof];
            public readonly float[] Equilibrium = new float[RapierNative.JointDof];

            // ERP and CFM as last set (Bullet's defaults until then).
            public float Erp = 0.2f;
            public float Cfm;

            // The slider's motors, linear (0) and angular (1); they drive the slider axis, X of the frame.
            public readonly bool[] SliderMotorOn = new bool[2];
            public readonly float[] SliderMotorVelocity = new float[2];
            public readonly float[] SliderMotorForce = new float[2];

            // The anisotropic friction last given (SetAnisotripicFriction is a gap, map row 76); Bullet's default is 1.
            public Vector3 AnisotropicFriction = Vector3.One;

            // The impulse above which the joint breaks, and whether it has.
            public float BreakingThreshold = float.PositiveInfinity;
            public bool Broken;

            private bool m_valid;

            public BulletConstraintRapier(RapierWorldState state, BulletBodyRapier body1, BulletBodyRapier body2)
                : base()
            {
                State = state;
                Body1 = body1;
                Body2 = body2;
            }

            public void Activate(RapierNative.ImpulseJointHandle joint)
            {
                Joint = joint;
                m_valid = true;
                State.Joints.Add(this);
                Body1?.AddJoint(this);
                Body2?.AddJoint(this);
            }

            public override bool HasPhysicalConstraint
            {
                get { return m_valid; }
            }

            /// <summary>Forget the joint without touching Rapier: it has been removed natively, or the world is going.</summary>
            public void MarkDead()
            {
                if (!m_valid)
                    return;
                m_valid = false;
                State.Joints.Remove(this);
                State.Breakable.Remove(this);
                Body1?.RemoveJoint(this);
                Body2?.RemoveJoint(this);
            }

            public override void Clear()
            {
                MarkDead();
            }

            public override string AddrString
            {
                get { return "RapierJoint" + Joint.index + "." + Joint.generation; }
            }
        }

        // ---- Engine identity ----

        private BSScene PhysicsScene { get; set; }

        public override string BulletEngineName { get { return "Rapier"; } }
        public override string BulletEngineVersion { get; protected set; }

        // Rapier's cylinder is exact and cheaper than a hull of the prim's mesh.
        public override bool NativeCylinderPrims { get { return true; } }

        // Rapier steps 0.02 s by default (BSScene.RapierFixedTimeStep); the jump keeps Bullet's tuning.
        public override bool JumpUsesBulletStepLength { get { return true; } }

        // CreateTerrainShape puts heightmap sample (i, j) at (i, j), as the simulator and ubODE have it.
        public override float TerrainSampleOffset { get { return 0f; } }

        // Objects slide like ubODE's: ubODE combines two frictions as sqrt(mu1 x mu2), so a wood box gets 0.42 on the
        // terrain and 0.6 on wood, where the product gave 0.18 and 0.36 and the box slid down a 20 degree hill.
        public override bool FrictionCombineGeometric { get { return true; } }

        // And slow down like ubODE's: ubODE damps every body by 0.2% of its linear and 0.05% of its angular speed each
        // 0.02 s step (ODEPrim, BodySetDamping(0.002, 0.0005)), 1 - 0.998^50 = 9.5% and 1 - 0.9995^50 = 2.5% a second.
        // With none, a pushed ball rolled on for good.
        public override float DefaultLinearDamping { get { return 0.0953f; } }
        public override float DefaultAngularDamping { get { return 0.0247f; } }

        // And roll like ubODE's: ubODE gives every prim the inertia of its bounding box (ODEPrim.UpdatePrimBodyData,
        // MassSetBoxTotal), so a 0.5 m ball rolls down at 0.6 g sin(slope); a solid sphere's inertia gave 5/7 g sin(slope),
        // and with it the ball rolled 28-31% faster than on ubODE.
        public override bool PrimInertiaFromBoundingBox { get { return true; } }

        // 50 walking avatars step 18% faster as capsules than as boxes (Speed Step 2, standalone).
        public override bool CapsuleAvatars { get { return true; } }

        // SetTranslation is a teleport here: 50 walking avatars step 11% faster without the one per frame.
        public override bool SkipsUnchangedAvatarYaw { get { return true; } }

        // Avatars feel like ubODE's (the region's default engine): ubODE divides the horizontal target by its
        // av_movement_divisor_walk, 1.3, so it walks at 3.15 m/s and flies at 12.6. Rapier reports the body's own pose,
        // so without this it would cover the full 4.07 and 16.39 m/s.
        public override float AvatarSpeedFactor { get { return 1f / 1.3f; } }

        // And start and stop like ubODE's, at any frame time (the launcher offers 5 to 55 fps). Starting, the motor closes
        // 1 - exp(-dt/0.0565 s) of the gap a frame: 80% at the default 0.0909 s frame (ubODE 79%). Stopping, the speed
        // decays with a 0.019 s time constant and each frame moves its average, so a walking avatar slides about
        // 3.1 m/s x 0.019 s = 0.06 m whatever the frame time; ubODE slides 0.060 m at every rate from 5 to 55 fps. The
        // 0.7 m/s threshold ends the stop in the next frame. (Bullet's linear 0.2 s timescale and 0.45 m/s slide 0.43 m
        // in 0.36 s at the default frame.)
        public override float AvatarMotorTimeConstant { get { return 0.0565f; } }
        public override float AvatarStopTimeConstant { get { return 0.019f; } }
        public override float AvatarStopZeroThreshold { get { return 0.7f; } }

        // A capsule touches a wall near the feet, which WalkUpStairs took for a step: walking into a wall lifted the
        // avatar by up to 0.8 m. ubODE never does. Only a contact with a walkable top is a step here.
        public override bool StepsNeedAWalkableTop { get { return true; } }

        // With a walkable top required, the 0.999 m step height made every flat top up to 0.9 m above the feet a step: a
        // low wall, a bench, a table or a counter was climbed. Stairs are 0.15 to 0.3 m. 0.5 m puts the top of the range
        // 0.4 m above the feet (less AvatarStepGroundFudge, 0.1 m), so a 0.3 m stair is still climbed and a 0.5 m block is
        // not. ubODE has no step-up at all.
        public override float AvatarStepHeight { get { return 0.5f; } }

        // Running like ubODE's: it multiplies the horizontal target by 1 / av_movement_divisor_run = 1.25, flying or not
        // (ODECharacter's m_alwaysRun branch has no flying check): 5.12 m/s on the ground, 20.5 m/s flying.
        public override float AvatarAlwaysRunFactor { get { return 1.25f; } }
        public override bool AvatarRunFactorWhenFlying { get { return true; } }

        public BSAPIRapier(string paramName, BSScene physScene)
        {
            PhysicsScene = physScene;
            string version = RapierNative.Version();
            if (RapierNative.r3BuildFeatures().parallel != 0)
                version += "+parallel";
            BulletEngineVersion = version;

            // Which file is in use, as the log should say: the runtime's own probing can find a copy before the dllmap
            // target, and then the DllImports use that copy.
            string bound = RapierNative.BoundLibraryPath();
            string loaded = RapierNative.LoadedLibraryPath();
            PhysicsScene.Logger.InfoFormat("{0} Rapier {1} native library: {2}", LogHeader, version, bound ?? loaded ?? "(path unknown)");
            if (bound != null && loaded != null && !string.Equals(bound, loaded, StringComparison.Ordinal))
                PhysicsScene.Logger.WarnFormat("{0} Rapier was loaded from {1} but the calls go to {2}, which was found first",
                            LogHeader, loaded, bound);
        }

        // Log a message the first time this key is seen for the world; later calls do nothing.
        internal void LogOnce(BulletWorldRapier world, bool warn, string key, string message)
        {
            if (!world.State.LoggedOnce.Add(key))
                return;
            if (warn)
                PhysicsScene.Logger.WarnFormat("{0} {1}", LogHeader, message);
            else
                PhysicsScene.Logger.DebugFormat("{0} {1}", LogHeader, message);
        }

        // Same for a constraint, which knows its world's state but not the world.
        internal void LogOnce(RapierWorldState state, string key, string message)
        {
            if (state.LoggedOnce.Add(key))
                PhysicsScene.Logger.DebugFormat("{0} {1}", LogHeader, message);
        }

        // Same for a body, which knows its world's state but not the world.
        internal void LogOnce(BulletBodyRapier body, string key, string message)
        {
            if (body.State.LoggedOnce.Add(key))
                PhysicsScene.Logger.DebugFormat("{0} {1}", LogHeader, message);
        }

        // ---- Conversions between OpenMetaverse's and Rapier's value types ----

        internal static RapierNative.Vector ToNative(Vector3 v)
        {
            return new RapierNative.Vector { x = v.X, y = v.Y, z = v.Z };
        }

        internal static RapierNative.Rotation ToNative(Quaternion q)
        {
            return new RapierNative.Rotation { x = q.X, y = q.Y, z = q.Z, w = q.W };
        }

        internal static Vector3 ToVector3(RapierNative.Vector v)
        {
            return new Vector3(v.x, v.y, v.z);
        }

        internal static Quaternion ToQuaternion(RapierNative.Rotation r)
        {
            return new Quaternion(r.x, r.y, r.z, r.w);
        }

        // ---- World lifetime ----

        public override BulletWorld Initialize(Vector3 maxPosition, ConfigurationParameters parms,
                                                int maxCollisions, ref CollisionDesc[] collisionArray,
                                                int maxUpdates, ref EntityProperties[] updateArray)
        {
            IntPtr world = RapierNative.r3NewWorld();
            if (world == IntPtr.Zero)
                RapierNative.Check(RapierNative.r3LastStatus());

            RapierWorldState state = new RapierWorldState();
            // The arrays BSScene allocated; PhysicsStep fills them and returns how many entries it used.
            state.UpdateArray = updateArray;
            state.CollisionArray = collisionArray;
            try
            {
                // BulletS gives the strength of gravity along Z; it is the same vector for the whole world.
                state.Gravity = new Vector3(0f, 0f, parms.gravity);
                state.ConfiguredTimeStep = PhysicsScene.m_fixedTimeStep;
                RapierNative.Check(RapierNative.r3SetGravity(world, ToNative(state.Gravity)));

                // Collision events (start and stop) from the colliders that want collisions, read after each substep.
                state.Events = RapierNative.r3NewEventCollector();
                // Without one every step would fail, so a world without a collector is not made at all.
                if (state.Events == IntPtr.Zero)
                    throw new InvalidOperationException("Rapier: could not make the world's collision event collector: "
                                + RapierNative.LastError());

                int iterations = Math.Max(1, (int)BSParam.RapierSolverIterations);
                RapierNative.Check(RapierNative.r3SetNumSolverIterations(world, (UIntPtr)iterations));

                // Only the parallel build ("rapier_mt") has a pool to size. The single-threaded build steps on the
                // calling thread; r3SetNumThreads would only answer that it is not available. A parallel build is
                // always given a pool, even of one thread (a region that asked for 1 after another loaded rapier_mt):
                // without one its parallel parts would use rayon's global pool, one thread per core.
                int threads = Math.Max(1, (int)BSParam.RapierNumThreads);
                if (RapierNative.r3BuildFeatures().parallel != 0
                        && RapierNative.r3SetNumThreads(world, (UIntPtr)threads) != 0)
                {
                    PhysicsScene.Logger.WarnFormat("{0} Could not set {1} Rapier threads, using Rapier's default: {2}",
                                LogHeader, threads, RapierNative.LastError());
                }
            }
            catch
            {
                RapierNative.r3FreeEventCollector(state.Events);
                state.Events = IntPtr.Zero;
                RapierNative.r3FreeWorld(world);
                throw;
            }

            return new BulletWorldRapier(1, PhysicsScene, world, state);
        }

        public override bool UpdateParameter(BulletWorld world, UInt32 localID, String parm, float value)
        {
            return false;
        }

        public override void Shutdown(BulletWorld pWorld)
        {
            BulletWorldRapier world = (BulletWorldRapier)pWorld;
            if (world.World != IntPtr.Zero)
            {
                // Constraints BulletS still holds must not reach into the world once it is gone.
                foreach (BulletConstraintRapier joint in new List<BulletConstraintRapier>(world.State.Joints))
                    joint.MarkDead();
                world.State.HasWorldAnchor = false;
                RapierNative.r3FreeWorld(world.World);
                world.World = IntPtr.Zero;
            }
            if (world.State.Events != IntPtr.Zero)
            {
                RapierNative.r3FreeEventCollector(world.State.Events);
                world.State.Events = IntPtr.Zero;
            }
            // Shapes BulletS never released. A collider holds its own reference, so they can go after the world. The
            // ones BulletS's mesh and hull caches still hold are kept instead (see KeptForBulletSCaches).
            HashSet<IntPtr> cached = ShapesInBulletSCaches();
            foreach (IntPtr shape in world.State.Shapes)
            {
                if (cached.Contains(shape))
                {
                    lock (KeptForBulletSCaches)
                        KeptForBulletSCaches.Add(shape);
                }
                else
                    RapierNative.r3FreeSharedShape(shape);
            }
            world.State.Shapes.Clear();
        }

        // BSShapeMesh.Meshes, BSShapeHull.Hulls, BSShapeConvexHull.ConvexHulls and BSShapeGImpact.GImpacts are static:
        // one cache for the whole process, never emptied, so a second BSScene (several regions in one simulator, or a
        // region restarted in-process) is handed the shape another world built. A Rapier shape handle belongs to no
        // world (an owned SharedShape, an Arc'd geometry that r3FreeWorld does not touch), so it stays valid after its
        // world is freed, and Shutdown must not free one these caches hold: the next scene would get a freed pointer.
        // Those shapes move here and live as long as the cache entry does, which is the life of the process, as with
        // Bullet's native backend (BSAPIUnman never frees a cached shape either). The set is only for freeing them once:
        // if BulletS ever deletes or rescales one in a later world, ReleaseShape frees it from here. Other backends do
        // not use it, and the caches themselves are unchanged.
        internal static readonly HashSet<IntPtr> KeptForBulletSCaches = new HashSet<IntPtr>();

        // Every Rapier shape handle in BulletS's static caches. Each cache is locked on its own, as BulletS locks them,
        // so no lock order is imposed on BulletS.
        private static HashSet<IntPtr> ShapesInBulletSCaches()
        {
            HashSet<IntPtr> found = new HashSet<IntPtr>();
            void Add(IEnumerable<BSShape> shapes)
            {
                foreach (BSShape cached in shapes)
                {
                    if (cached.physShapeInfo is BulletShapeRapier rapier && rapier.Shape != IntPtr.Zero)
                        found.Add(rapier.Shape);
                }
            }
            lock (BSShapeMesh.Meshes)
                Add(BSShapeMesh.Meshes.Values);
            lock (BSShapeHull.Hulls)
                Add(BSShapeHull.Hulls.Values);
            lock (BSShapeConvexHull.ConvexHulls)
                Add(BSShapeConvexHull.ConvexHulls.Values);
            lock (BSShapeGImpact.GImpacts)
                Add(BSShapeGImpact.GImpacts.Values);
            return found;
        }

        // Frees a shape this world built, or one an earlier world left to BulletS's caches; any other is not ours to
        // free (already freed, or still owned by another world) and is left alone.
        private static void ReleaseShape(BulletWorldRapier world, IntPtr shape)
        {
            bool ours = world.State.Shapes.Remove(shape);
            if (!ours)
            {
                lock (KeptForBulletSCaches)
                    ours = KeptForBulletSCaches.Remove(shape);
            }
            if (ours)
                RapierNative.r3FreeSharedShape(shape);
        }

        // Rapier refreshes the broad phase from the bodies' poses when it steps (map row 8).
        public override void UpdateSingleAabb(BulletWorld world, BulletBody obj)
        {
        }

        // Map row 9: pass the poses of bodies that moved on to their colliders.
        public override void UpdateAabbs(BulletWorld world)
        {
            RapierNative.Check(RapierNative.r3RigidBodyPropagateModifiedBodyPositionsToColliders(((BulletWorldRapier)world).World));
            ((BulletWorldRapier)world).State.CollidersStale = false;
        }

        // Map rows 10 and 11: the flag is kept; there is nothing to set in Rapier.
        public override bool GetForceUpdateAllAabbs(BulletWorld world)
        {
            return ((BulletWorldRapier)world).State.ForceUpdateAllAabbs;
        }

        public override void SetForceUpdateAllAabbs(BulletWorld world, bool force)
        {
            ((BulletWorldRapier)world).State.ForceUpdateAllAabbs = force;
        }
    }
}
