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
    /// 2026-11-06-rapier-bsapi-map.md maps each one. A member no task has implemented yet throws NotImplementedException
    /// naming itself.
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

            // Set for a body that owns its shape (terrain and the ground plane), whose shape goes when the body does.
            public BulletShapeRapier OwnedShape;

            // The local pose of compound child 0, for BS_RETURN_ROOT_COMPOUND_SHAPE (H1). Set when the body is built on a
            // compound shape.
            public bool HasRootChild;

            // Whether the body is enabled (Bullet: added to the world). Only these are reported by PhysicsStep.
            public bool InWorld;
            public Vector3 RootChildPos;
            public Quaternion RootChildRot = Quaternion.Identity;

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
            // A compound shape is only this list until it is put on a body (H5a).
            public List<(BulletShapeRapier child, Vector3 pos, Quaternion rot)> Children;
            // The mesh a hull was built from, kept so a hull can be rebuilt (H9).
            public float[] MeshVerts;
            public int[] MeshIndices;

            public BulletShapeRapier(IntPtr shape, BSPhysicsShapeType type, Vector3 scale)
                : base()
            {
                Shape = shape;
                shapeType = type;
                Scale = scale;
            }

            public override bool HasPhysicalShape
            {
                get { return Shape != IntPtr.Zero; }
            }

            public override void Clear()
            {
                Shape = IntPtr.Zero;
            }

            public override BulletShape Clone()
            {
                BulletShapeRapier ret = new BulletShapeRapier(Shape, shapeType, Scale);
                ret.shapeKey = shapeKey;
                ret.isNativeShape = isNativeShape;
                ret.LocalPos = LocalPos;
                ret.LocalRot = LocalRot;
                ret.Children = Children;
                ret.MeshVerts = MeshVerts;
                ret.MeshIndices = MeshIndices;
                return ret;
            }

            public override bool ReferenceSame(BulletShape other)
            {
                BulletShapeRapier otherRapier = other as BulletShapeRapier;
                return (otherRapier != null) && (this.Shape == otherRapier.Shape);
            }

            public override string AddrString
            {
                get { return "RapierShape" + Shape.ToString("X"); }
            }
        }

        internal sealed class BulletConstraintRapier : BulletConstraint
        {
            // The impulse joint.
            public RapierNative.ImpulseJointHandle Joint;
            private bool m_valid;

            public BulletConstraintRapier(RapierNative.ImpulseJointHandle joint)
                : base()
            {
                Joint = joint;
                m_valid = true;
            }

            public override bool HasPhysicalConstraint
            {
                get { return m_valid; }
            }

            public override void Clear()
            {
                m_valid = false;
            }

            public override string AddrString
            {
                get { return "RapierJoint"; }
            }
        }

        // ---- Engine identity ----

        private BSScene PhysicsScene { get; set; }

        public override string BulletEngineName { get { return "Rapier"; } }
        public override string BulletEngineVersion { get; protected set; }

        public BSAPIRapier(string paramName, BSScene physScene)
        {
            PhysicsScene = physScene;
            string version = RapierNative.Version();
            if (RapierNative.r3BuildFeatures().parallel != 0)
                version += "+parallel";
            BulletEngineVersion = version;
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
                RapierNative.Check(RapierNative.r3SetGravity(world, ToNative(state.Gravity)));

                int iterations = Math.Max(1, (int)BSParam.RapierSolverIterations);
                RapierNative.Check(RapierNative.r3SetNumSolverIterations(world, (UIntPtr)iterations));

                int threads = Math.Max(1, (int)BSParam.RapierNumThreads);
                if (RapierNative.r3SetNumThreads(world, (UIntPtr)threads) != 0)
                {
                    PhysicsScene.Logger.WarnFormat("{0} Could not set {1} Rapier threads, using Rapier's default: {2}",
                                LogHeader, threads, RapierNative.LastError());
                }
            }
            catch
            {
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
                RapierNative.r3FreeWorld(world.World);
                world.World = IntPtr.Zero;
            }
            // Shapes BulletS never released. A collider holds its own reference, so they can go after the world.
            foreach (IntPtr shape in world.State.Shapes)
                RapierNative.r3FreeSharedShape(shape);
            world.State.Shapes.Clear();
        }

        // Rapier refreshes the broad phase from the bodies' poses when it steps (map row 8).
        public override void UpdateSingleAabb(BulletWorld world, BulletBody obj)
        {
        }

        public override void UpdateAabbs(BulletWorld world)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(UpdateAabbs));
        }

        public override bool GetForceUpdateAllAabbs(BulletWorld world)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetForceUpdateAllAabbs));
        }

        public override void SetForceUpdateAllAabbs(BulletWorld world, bool force)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetForceUpdateAllAabbs));
        }
    }
}
