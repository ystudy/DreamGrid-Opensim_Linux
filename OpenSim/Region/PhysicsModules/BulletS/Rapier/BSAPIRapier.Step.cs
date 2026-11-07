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
    public sealed partial class BSAPIRapier
    {
        // Step the world and report what moved (H1). BSScene calls this once per simulator frame, with the time since
        // the last one. Like Bullet's stepSimulation it takes whole fixedTimeStep steps out of the accumulated time,
        // at most maxSubSteps of them, and returns how many it took.
        //
        // The entity updates go into the array BSScene gave Initialize, from the start, and updatedEntityCount says how
        // many entries are valid.
        public override int PhysicsStep(BulletWorld pWorld, float timeStep, int maxSubSteps, float fixedTimeStep,
                                out int updatedEntityCount, out int collidersCount)
        {
            BulletWorldRapier world = (BulletWorldRapier)pWorld;
            RapierWorldState state = world.State;

            // Pairs are written once per call, across all its substeps (BulletSim's m_collidersThisFrame).
            state.PairsThisFrame.Clear();
            state.LastCollisionCount = 0;

            int steps = state.PlanSteps(timeStep, maxSubSteps, fixedTimeStep, out float stepSeconds);
            if (steps > 0 && stepSeconds != state.TimeStep)
            {
                RapierNative.Check(RapierNative.r3SetTimeStep(world.World, stepSeconds));
                state.TimeStep = stepSeconds;
            }
            if (steps > 0)
                WakeNeverSleeping(state);
            for (int ii = 0; ii < steps; ii++)
            {
                RapierNative.Check(RapierNative.r3Step(world.World, IntPtr.Zero, IntPtr.Zero));
                // Collisions are gathered after every substep, as in BulletSim: something can touch and bounce off
                // within one frame.
                GatherCollisions(world);
            }
            ResetAppliedForces(state);

            collidersCount = state.LastCollisionCount;
            updatedEntityCount = GatherUpdates(world);
            return steps;
        }

        // DISABLE_DEACTIVATION (H3): rapier.h can only allow sleeping when a body is created, so the bodies BulletS asked
        // never to sleep are woken before each step. A strong wake-up also restarts the body's rest timer, so it is
        // still awake when the step ends. A body that is out of the world, simulation-disabled or no longer dynamic is
        // skipped (it keeps its place in the set; BulletS forces the state again if it wants it back).
        private static void WakeNeverSleeping(RapierWorldState state)
        {
            foreach (BulletBodyRapier body in state.NeverSleep)
            {
                if (body.Reporting && BodyTypeFor(body.Flags) == RapierNative.BodyDynamic)
                    RapierNative.Check(RapierNative.r3RigidBody_WakeUp(body.Body, 1));
            }
        }

        // Rapier keeps a force or torque until it is reset, while Bullet's stepSimulation clears them at its end, whether
        // or not it took a step. BulletS applies forces in the taints before PhysicsStep, so what was added since the last
        // call has now been used (or, with no whole step in this frame, is dropped as Bullet drops it).
        private static void ResetAppliedForces(RapierWorldState state)
        {
            if (state.ForcedBodies.Count == 0)
                return;
            foreach (BulletBodyRapier body in state.ForcedBodies)
            {
                if (!body.HasPhysicalBody)
                    continue;
                RapierNative.Check(RapierNative.r3RigidBody_ResetForces(body.Body, 0));
                RapierNative.Check(RapierNative.r3RigidBody_ResetTorques(body.Body, 0));
            }
            state.ForcedBodies.Clear();
        }

        // Collision reporting (H2, H4), after one substep. The conventions are BulletSim's (BulletSim.cpp,
        // SubstepCollisionCallback and RecordCollision):
        //  - a pair is kept only if one side wants collisions (BS_SUBSCRIBE_COLLISION_EVENTS or BS_VEHICLE_COLLISIONS);
        //  - one entry per pair per frame: aID is the lower localID, bID the higher;
        //  - one contact point per pair (here the deepest), in world space;
        //  - the normal points from A to B;
        //  - the penetration is the signed distance, negative when the two have sunk into each other.
        // Rapier lists a contact pair under either of its colliders, so the pairs are found from the colliders that want
        // collisions and a pair seen from both sides is written once. At most the array's length is written.
        private void GatherCollisions(BulletWorldRapier world)
        {
            RapierWorldState state = world.State;
            CollisionDesc[] array = state.CollisionArray;
            if (array == null)
                return;

            foreach (RapierNative.ColliderHandle collider in state.WantsCollisions)
            {
                if (state.LastCollisionCount >= array.Length)
                    return;
                if (!state.ColliderOwners.TryGetValue(collider, out BulletBodyRapier owner) || !owner.Reporting)
                    continue;

                int pairCount = (int)RapierNative.r3Collider_ContactPairs(collider, state.PairBuffer, (UIntPtr)state.PairBuffer.Length);
                if (pairCount > state.PairBuffer.Length)
                {
                    state.PairBuffer = new RapierNative.ContactPair[pairCount + pairCount / 2];
                    pairCount = (int)RapierNative.r3Collider_ContactPairs(collider, state.PairBuffer, (UIntPtr)state.PairBuffer.Length);
                }
                for (int ii = 0; ii < pairCount && state.LastCollisionCount < array.Length; ii++)
                    RecordContactPair(state, array, in state.PairBuffer[ii]);
            }

            if (state.Sensors.Count > 0 && state.LastCollisionCount < array.Length)
                GatherSensorOverlaps(world, array);
        }

        private static void RecordContactPair(RapierWorldState state, CollisionDesc[] array, in RapierNative.ContactPair pair)
        {
            if (pair.hasAnyActiveContact == 0)
                return;
            if (!state.ColliderOwners.TryGetValue(pair.collider1, out BulletBodyRapier body1) || !body1.Reporting
                    || !state.ColliderOwners.TryGetValue(pair.collider2, out BulletBodyRapier body2) || !body2.Reporting)
                return;
            ulong key = PairKey(body1.ID, body2.ID);
            if (state.PairsThisFrame.Contains(key))
                return;

            // The deepest point of the pair.
            int pointCount = (int)RapierNative.r3ContactPoints(pair.collider1, pair.collider2, state.PointBuffer, (UIntPtr)state.PointBuffer.Length);
            if (pointCount > state.PointBuffer.Length)
            {
                state.PointBuffer = new RapierNative.ContactPoint[pointCount + pointCount / 2];
                pointCount = (int)RapierNative.r3ContactPoints(pair.collider1, pair.collider2, state.PointBuffer, (UIntPtr)state.PointBuffer.Length);
            }
            if (pointCount == 0)
                return;
            int deepest = 0;
            for (int ii = 1; ii < pointCount; ii++)
            {
                if (state.PointBuffer[ii].distance < state.PointBuffer[deepest].distance)
                    deepest = ii;
            }
            ref RapierNative.ContactPoint contact = ref state.PointBuffer[deepest];

            // The point is local to collider 2 (BulletSim: the position on B); the normal runs from collider 1 to collider 2.
            RapierNative.Pose pose = RapierNative.r3Collider_Position(pair.collider2);
            Vector3 point = ToVector3(pose.translation) + ToVector3(contact.localP2) * ToQuaternion(pose.rotation);
            Vector3 normal = ToVector3(contact.normal);

            state.PairsThisFrame.Add(key);
            WriteCollision(state, array, body1.ID, body2.ID, point, normal, contact.distance);
        }

        // Sensor overlaps (volume detect). There is no contact point, so the point is the position of the body the sensor
        // detected, the normal runs from the lower ID's body to the higher's, and the penetration is zero.
        private static void GatherSensorOverlaps(BulletWorldRapier world, CollisionDesc[] array)
        {
            RapierWorldState state = world.State;
            int count = (int)RapierNative.r3IntersectionPairs(world.World, state.SensorBuffer, (UIntPtr)state.SensorBuffer.Length);
            if (count > state.SensorBuffer.Length)
            {
                state.SensorBuffer = new RapierNative.IntersectionPair[count + count / 2];
                count = (int)RapierNative.r3IntersectionPairs(world.World, state.SensorBuffer, (UIntPtr)state.SensorBuffer.Length);
            }
            for (int ii = 0; ii < count && state.LastCollisionCount < array.Length; ii++)
            {
                ref RapierNative.IntersectionPair pair = ref state.SensorBuffer[ii];
                if (pair.intersecting == 0)
                    continue;
                if (!state.ColliderOwners.TryGetValue(pair.collider1, out BulletBodyRapier body1) || !body1.Reporting
                        || !state.ColliderOwners.TryGetValue(pair.collider2, out BulletBodyRapier body2) || !body2.Reporting)
                    continue;
                if (((body1.Flags | body2.Flags) & WantsCollisionsFlags) == 0)
                    continue;
                ulong key = PairKey(body1.ID, body2.ID);
                if (state.PairsThisFrame.Contains(key))
                    continue;
                state.PairsThisFrame.Add(key);

                BulletBodyRapier detected = (state.Sensors.Contains(pair.collider2) && !state.Sensors.Contains(pair.collider1)) ? body1 : body2;
                Vector3 point = ToVector3(RapierNative.r3RigidBody_Translation(detected.Body));
                Vector3 position1 = ToVector3(RapierNative.r3RigidBody_Translation(body1.Body));
                Vector3 position2 = ToVector3(RapierNative.r3RigidBody_Translation(body2.Body));
                Vector3 normal = position2 - position1;
                float lengthSquared = normal.LengthSquared();
                normal = (lengthSquared > 1e-12f) ? normal * (1f / MathF.Sqrt(lengthSquared)) : Vector3.UnitZ;
                WriteCollision(state, array, body1.ID, body2.ID, point, normal, 0f);
            }
        }

        private static ulong PairKey(uint idA, uint idB)
        {
            return (idA <= idB) ? (((ulong)idA << 32) | idB) : (((ulong)idB << 32) | idA);
        }

        // BulletSim's RecordCollision after its keep-the-pair tests: the lower ID is A, and swapping flips the normal.
        // The normal given runs from the first body to the second.
        private static void WriteCollision(RapierWorldState state, CollisionDesc[] array, uint id1, uint id2, Vector3 point,
                                    Vector3 normal1To2, float distance)
        {
            if (id1 > id2)
            {
                uint swap = id1;
                id1 = id2;
                id2 = swap;
                normal1To2 = -normal1To2;
            }
            array[state.LastCollisionCount++] = new CollisionDesc
            {
                aID = id1,
                bID = id2,
                point = point,
                normal = normal1To2,
                penetration = distance
            };
        }

        // Decide what to report this step and fill the update array with it (H1). As with BulletSim's SimMotionState, a
        // body is reported when it has moved past the tolerances since it was last reported. On top of that:
        //  - a body that is no longer awake, after a report with a non-zero velocity, gets one more with zero velocities
        //    (BulletSim gets that from its patch to Bullet);
        //  - the bodies PushUpdate named are reported whatever their state;
        //  - what does not fit in the array waits in PendingUpdates and goes first next time, so a crowd of moving bodies
        //    is reported in turns, not the same ones every step.
        // Returns the number of entries filled.
        private int GatherUpdates(BulletWorldRapier world)
        {
            RapierWorldState state = world.State;
            EntityProperties[] updates = state.UpdateArray;

            // Only the entries used last time can hold anything; leave the rest of the array as it is.
            Array.Clear(updates, 0, Math.Min(state.LastUpdateCount, updates.Length));
            state.LastUpdateCount = 0;

            Dictionary<uint, EntityProperties> current = state.CurrentUpdates;
            List<uint> candidates = state.CandidateUpdates;
            current.Clear();
            candidates.Clear();

            // The awake bodies. A buffer that is too small gets the needed count back and nothing written.
            int active = (int)RapierNative.r3ActiveRigidBodies(world.World, state.ActiveBodies, (UIntPtr)state.ActiveBodies.Length);
            if (active > state.ActiveBodies.Length)
            {
                state.ActiveBodies = new RapierNative.RigidBodyHandle[active + active / 2];
                state.ActiveStates = new RapierNative.RigidBodyState[state.ActiveBodies.Length];
                active = (int)RapierNative.r3ActiveRigidBodies(world.World, state.ActiveBodies, (UIntPtr)state.ActiveBodies.Length);
            }
            if (active > 0)
            {
                int read = (int)RapierNative.r3RigidBodyReadStates(world.World, state.ActiveBodies, (UIntPtr)active,
                                                                    state.ActiveStates, (UIntPtr)state.ActiveStates.Length);
                RapierNative.Check(RapierNative.r3LastStatus());
                for (int ii = 0; ii < read; ii++)
                {
                    uint id = (uint)state.ActiveStates[ii].userData.low;
                    if (!state.Bodies.TryGetValue(id, out BulletBodyRapier body) || !body.Reporting)
                        continue;
                    EntityProperties props = ToEntityProperties(body, in state.ActiveStates[ii], false);
                    current[id] = props;
                    if (state.NeedsReport(props))
                        candidates.Add(id);
                }
            }

            // Bodies last reported moving that are not awake now: one final report with the velocities at zero.
            foreach (uint id in state.MovingSent)
            {
                if (current.ContainsKey(id))
                    continue;
                if (TryReadCurrent(world, id, true, out EntityProperties props))
                {
                    current[id] = props;
                    candidates.Add(id);
                }
            }

            // PushUpdate.
            foreach (uint id in state.ForcedUpdates)
            {
                if (!current.ContainsKey(id) && TryReadCurrent(world, id, false, out EntityProperties props))
                    current[id] = props;
                if (current.ContainsKey(id))
                    candidates.Add(id);
            }
            state.ForcedUpdates.Clear();

            // Fill the array: what was left over last time first, then this step's.
            HashSet<uint> reported = state.ReportedUpdates;
            reported.Clear();
            int count = 0;
            while (count < updates.Length && state.PendingUpdates.Count > 0)
            {
                uint id = state.PendingUpdates.Dequeue();
                if (!state.PendingSet.Remove(id))
                    continue;   // removed since it was queued, or queued twice
                if (!current.TryGetValue(id, out EntityProperties props) && !TryReadCurrent(world, id, false, out props))
                    continue;
                count = Report(state, updates, count, props, reported);
            }
            for (int ii = 0; ii < candidates.Count; ii++)
            {
                uint id = candidates[ii];
                if (reported.Contains(id))
                    continue;
                if (count < updates.Length)
                    count = Report(state, updates, count, current[id], reported);
                else if (state.PendingSet.Add(id))
                    state.PendingUpdates.Enqueue(id);
            }

            state.LastUpdateCount = count;
            return count;
        }

        private static int Report(RapierWorldState state, EntityProperties[] updates, int count, EntityProperties props,
                                    HashSet<uint> reported)
        {
            updates[count] = props;
            reported.Add(props.ID);
            state.LastSent[props.ID] = props;
            if (props.Velocity != Vector3.Zero || props.RotationalVelocity != Vector3.Zero)
                state.MovingSent.Add(props.ID);
            else
                state.MovingSent.Remove(props.ID);
            return count + 1;
        }

        // The current state of one body that this step has not read already (one native read). False for a body that is
        // gone or not in the world. A stopped body is reported with zero velocities.
        private static bool TryReadCurrent(BulletWorldRapier world, uint id, bool stopped, out EntityProperties props)
        {
            RapierWorldState state = world.State;
            props = default(EntityProperties);
            if (!state.Bodies.TryGetValue(id, out BulletBodyRapier body) || !body.Reporting || !body.HasPhysicalBody)
                return false;
            state.OneBody[0] = body.Body;
            int read = (int)RapierNative.r3RigidBodyReadStates(world.World, state.OneBody, (UIntPtr)1, state.OneState, (UIntPtr)1);
            if (read < 1)
                return false;
            props = ToEntityProperties(body, in state.OneState[0], stopped);
            return true;
        }

        // The EntityProperties BulletS wants for a body. With BS_RETURN_ROOT_COMPOUND_SHAPE the pose is that of compound
        // child 0 (BulletSim: worldTrans * getChildTransform(0), so the rotation is world rotation times child rotation
        // in btQuaternion's order); the angular and linear velocities stay the body's.
        private static EntityProperties ToEntityProperties(BulletBodyRapier body, in RapierNative.RigidBodyState bodyState, bool stopped)
        {
            EntityProperties update = new EntityProperties();
            update.ID = (uint)bodyState.userData.low;
            Vector3 position = ToVector3(bodyState.position.translation);
            Quaternion rotation = ToQuaternion(bodyState.position.rotation);
            if (body.HasRootChild && (body.Flags & CollisionFlags.BS_RETURN_ROOT_COMPOUND_SHAPE) != 0)
            {
                Compose(position, rotation, body.RootChildPos, body.RootChildRot, out position, out rotation);
            }
            update.Position = position;
            update.Rotation = rotation;
            update.Velocity = stopped ? Vector3.Zero : ToVector3(bodyState.linvel);
            update.Acceleration = Vector3.Zero;
            update.RotationalVelocity = stopped ? Vector3.Zero : ToVector3(bodyState.angvel);
            return update;
        }

        // Report this body on the next step even if it has not moved: BulletS calls it when something that must reach
        // the viewer changed without the body moving.
        public override bool PushUpdate(BulletBody obj)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (!body.HasPhysicalBody)
                return false;
            body.State.ForcedUpdates.Add(body.ID);
            return true;
        }
    }
}
