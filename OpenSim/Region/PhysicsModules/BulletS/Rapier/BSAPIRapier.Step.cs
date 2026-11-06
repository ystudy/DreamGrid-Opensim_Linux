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

            // No collision reporting yet.
            collidersCount = 0;

            int steps = state.PlanSteps(timeStep, maxSubSteps, fixedTimeStep, out float stepSeconds);
            if (steps > 0 && stepSeconds != state.TimeStep)
            {
                RapierNative.Check(RapierNative.r3SetTimeStep(world.World, stepSeconds));
                state.TimeStep = stepSeconds;
            }
            for (int ii = 0; ii < steps; ii++)
                RapierNative.Check(RapierNative.r3Step(world.World, IntPtr.Zero, IntPtr.Zero));

            updatedEntityCount = GatherUpdates(world);
            return steps;
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
                    if (!state.Bodies.TryGetValue(id, out BulletBodyRapier body) || !body.InWorld)
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
            if (!state.Bodies.TryGetValue(id, out BulletBodyRapier body) || !body.InWorld || !body.HasPhysicalBody)
                return false;
            state.OneBody[0] = body.Body;
            int read = (int)RapierNative.r3RigidBodyReadStates(world.World, state.OneBody, (UIntPtr)1, state.OneState, (UIntPtr)1);
            if (read < 1)
                return false;
            props = ToEntityProperties(body, in state.OneState[0], stopped);
            return true;
        }

        // The EntityProperties BulletS wants for a body. With BS_RETURN_ROOT_COMPOUND_SHAPE the pose is that of compound
        // child 0 (BulletSim: worldTrans * getChildTransform(0)); the angular and linear velocities stay the body's.
        private static EntityProperties ToEntityProperties(BulletBodyRapier body, in RapierNative.RigidBodyState bodyState, bool stopped)
        {
            EntityProperties update = new EntityProperties();
            update.ID = (uint)bodyState.userData.low;
            Vector3 position = ToVector3(bodyState.position.translation);
            Quaternion rotation = ToQuaternion(bodyState.position.rotation);
            if (body.HasRootChild && (body.Flags & CollisionFlags.BS_RETURN_ROOT_COMPOUND_SHAPE) != 0)
            {
                position += body.RootChildPos * rotation;
                rotation = rotation * body.RootChildRot;
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
