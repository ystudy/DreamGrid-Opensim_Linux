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
    /// The C#-side state of one Rapier world: everything the BSAPITemplate emulation needs that Rapier itself does not
    /// keep. One owner, shared by the partial files of BSAPIRapier. Used only from the thread that runs BSScene's
    /// taints and step.
    /// </summary>
    internal sealed class RapierWorldState
    {
        // The world's gravity, as given when it was created. Per-body gravity is a scale of this vector's Z (H6).
        public Vector3 Gravity;

        // The step length last given to r3SetTimeStep, so it is only set again when BSScene changes it.
        public float TimeStep;

        // Simulated time BSScene handed over but PhysicsStep has not stepped yet (Bullet's m_localTime).
        public float Accumulator;

        // The Z offset (world units) the terrain collider has from its body: BSTerrainHeightmap puts the body at
        // (minZ + maxZ) / 2 because Bullet centres its heightfield between min and max, while Rapier's heights are
        // absolute, so the collider sits this far below (the negative of that centre).
        public float TerrainZOffset;

        // The arrays BSScene gave Initialize. PhysicsStep fills them and reports how many entries it used.
        public EntityProperties[] UpdateArray;
        public CollisionDesc[] CollisionArray;
        public int LastUpdateCount;
        // How many entries of CollisionArray the last PhysicsStep filled (also what it returned in collidersCount).
        public int LastCollisionCount;

        // Scratch buffers for r3ActiveRigidBodies and r3RigidBodyReadStates, grown as needed and reused.
        public RapierNative.RigidBodyHandle[] ActiveBodies = new RapierNative.RigidBodyHandle[256];
        public RapierNative.RigidBodyState[] ActiveStates = new RapierNative.RigidBodyState[256];

        // Every body that has been created and not destroyed, by localID, so an update can find the body's flags.
        public readonly Dictionary<uint, BSAPIRapier.BulletBodyRapier> Bodies = new Dictionary<uint, BSAPIRapier.BulletBodyRapier>();

        // ---- Update reporting (H1) ----

        // BulletSim's tolerances, from SimMotionState::setWorldTransform in BulletSim.h (Misterblue/BulletSim): a body is
        // reported when a component of its position, rotation, velocity or angular velocity is not strictly within
        // +-tolerance of what was last sent (Vector3::AlmostEqual and Quaternion::AlmostEqual in APIData.h).
        public const float PositionTolerance = 0.05f;
        public const float VelocityTolerance = 0.001f;
        public const float RotationTolerance = 0.01f;
        public const float AngularVelocityTolerance = 0.01f;

        // What was last reported for each body, by localID.
        public readonly Dictionary<uint, EntityProperties> LastSent = new Dictionary<uint, EntityProperties>();

        // Bodies whose last report had a non-zero velocity or angular velocity. One that is no longer awake gets a final
        // report with both zero (BulletSim gets that from its patch to Bullet).
        public readonly HashSet<uint> MovingSent = new HashSet<uint>();

        // Bodies to report on the next step whatever their state (PushUpdate).
        public readonly HashSet<uint> ForcedUpdates = new HashSet<uint>();

        // Updates that did not fit in the array, reported first on the next step. The set says which queue entries are
        // still wanted; an entry whose id is not in it (the body was removed, or the id is queued twice) is skipped.
        public readonly Queue<uint> PendingUpdates = new Queue<uint>();
        public readonly HashSet<uint> PendingSet = new HashSet<uint>();

        // Per-step scratch, kept to avoid allocating every frame.
        public readonly Dictionary<uint, EntityProperties> CurrentUpdates = new Dictionary<uint, EntityProperties>();
        public readonly List<uint> CandidateUpdates = new List<uint>();
        public readonly HashSet<uint> ReportedUpdates = new HashSet<uint>();
        public readonly RapierNative.RigidBodyHandle[] OneBody = new RapierNative.RigidBodyHandle[1];
        public readonly RapierNative.RigidBodyState[] OneState = new RapierNative.RigidBodyState[1];

        /// <summary>Forget everything about reporting this body: it has left the world or been destroyed.</summary>
        public void ForgetUpdates(uint id)
        {
            LastSent.Remove(id);
            MovingSent.Remove(id);
            ForcedUpdates.Remove(id);
            PendingSet.Remove(id);
        }

        public void ForgetBody(BSAPIRapier.BulletBodyRapier body)
        {
            ForgetUpdates(body.ID);
            // Before the colliders are removed natively, so no later step asks Rapier about a collider that is gone.
            foreach (RapierNative.ColliderHandle collider in body.Colliders)
                ForgetCollider(collider);
            if (Bodies.TryGetValue(body.ID, out BSAPIRapier.BulletBodyRapier known) && ReferenceEquals(known, body))
                Bodies.Remove(body.ID);
        }

        /// <summary>BulletSim's test for whether a body has changed enough to report (SimMotionState::setWorldTransform).</summary>
        public bool NeedsReport(EntityProperties now)
        {
            if (!LastSent.TryGetValue(now.ID, out EntityProperties last))
                return true;
            return !AlmostEqual(now.Position, last.Position, PositionTolerance)
                || !AlmostEqual(now.Rotation, last.Rotation, RotationTolerance)
                // Both velocities have just become zero: make sure the zeros reach the viewer.
                || ((now.Velocity == Vector3.Zero && now.RotationalVelocity == Vector3.Zero)
                    && (now.Velocity != last.Velocity || now.RotationalVelocity != last.RotationalVelocity))
                || !AlmostEqual(now.Velocity, last.Velocity, VelocityTolerance)
                || !AlmostEqual(now.RotationalVelocity, last.RotationalVelocity, AngularVelocityTolerance);
        }

        private static bool AlmostEqual(Vector3 a, Vector3 b, float eps)
        {
            return (b.X - eps < a.X && a.X < b.X + eps)
                && (b.Y - eps < a.Y && a.Y < b.Y + eps)
                && (b.Z - eps < a.Z && a.Z < b.Z + eps);
        }

        private static bool AlmostEqual(Quaternion a, Quaternion b, float eps)
        {
            return (b.X - eps < a.X && a.X < b.X + eps)
                && (b.Y - eps < a.Y && a.Y < b.Y + eps)
                && (b.Z - eps < a.Z && a.Z < b.Z + eps)
                && (b.W - eps < a.W && a.W < b.W + eps);
        }

        // ---- Collision reporting (H2, H4) ----

        // The colliders of bodies that have BS_SUBSCRIBE_COLLISION_EVENTS or BS_VEHICLE_COLLISIONS (BulletSim's
        // BS_WANTS_COLLISIONS). Only their contact pairs are looked at after a substep.
        public readonly HashSet<RapierNative.ColliderHandle> WantsCollisions = new HashSet<RapierNative.ColliderHandle>();

        // Sensor colliders (volume detect), whose overlaps are read from the world's intersection pairs.
        public readonly HashSet<RapierNative.ColliderHandle> Sensors = new HashSet<RapierNative.ColliderHandle>();

        // The body each collider belongs to, to turn the colliders of a contact pair into localIDs and flags.
        public readonly Dictionary<RapierNative.ColliderHandle, BSAPIRapier.BulletBodyRapier> ColliderOwners =
                    new Dictionary<RapierNative.ColliderHandle, BSAPIRapier.BulletBodyRapier>();

        // The pairs (low localID << 32 | high localID) already written since the step began: one entry per pair per
        // BSScene.Simulate call, however many substeps it took.
        public readonly HashSet<ulong> PairsThisFrame = new HashSet<ulong>();

        // Scratch buffers for the contact queries, grown as needed and reused.
        public RapierNative.ContactPair[] PairBuffer = new RapierNative.ContactPair[64];
        public RapierNative.ContactPoint[] PointBuffer = new RapierNative.ContactPoint[32];
        public RapierNative.IntersectionPair[] SensorBuffer = new RapierNative.IntersectionPair[64];

        public void ForgetCollider(RapierNative.ColliderHandle collider)
        {
            WantsCollisions.Remove(collider);
            Sensors.Remove(collider);
            ColliderOwners.Remove(collider);
        }

        // Every native shape that has been built and not freed, so Shutdown frees the ones BulletS never released and
        // a shape is never freed twice.
        public readonly HashSet<IntPtr> Shapes = new HashSet<IntPtr>();

        // Messages already logged once for this world (ignored flags and the like), so a repeated call logs nothing.
        public readonly HashSet<string> LoggedOnce = new HashSet<string>();

        /// <summary>
        /// Bullet's fixed-step accumulation (btDiscreteDynamicsWorld::stepSimulation): add timeStep to the time not yet
        /// simulated, take as many whole fixedTimeStep steps as fit, and do at most maxSubSteps of them. Time beyond
        /// maxSubSteps is dropped, as Bullet drops it. With maxSubSteps of 0 the step is one variable-length step of
        /// timeStep. Returns how many steps to take; stepSeconds is how long each one is.
        /// </summary>
        public int PlanSteps(float timeStep, int maxSubSteps, float fixedTimeStep, out float stepSeconds)
        {
            int steps = 0;
            stepSeconds = fixedTimeStep;
            if (maxSubSteps > 0 && fixedTimeStep > 0f)
            {
                Accumulator += timeStep;
                if (Accumulator >= fixedTimeStep)
                {
                    steps = (int)(Accumulator / fixedTimeStep);
                    Accumulator -= steps * fixedTimeStep;
                }
                if (steps > maxSubSteps)
                    steps = maxSubSteps;
            }
            else if (timeStep > 0f)
            {
                stepSeconds = timeStep;
                Accumulator = 0f;
                steps = 1;
            }
            return steps;
        }
    }
}
