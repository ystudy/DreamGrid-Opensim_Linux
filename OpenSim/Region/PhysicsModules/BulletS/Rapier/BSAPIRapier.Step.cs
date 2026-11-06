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
        // many entries are valid. Every active body is reported for now; the tolerances, the carry-over of updates
        // that do not fit and the final stopped-body update come with the update reporting.
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

        // Fill the update array from the bodies that are awake. Returns the number of entries filled.
        private int GatherUpdates(BulletWorldRapier world)
        {
            RapierWorldState state = world.State;
            EntityProperties[] updates = state.UpdateArray;

            // Only the entries used last time can hold anything; leave the rest of the array as it is.
            Array.Clear(updates, 0, Math.Min(state.LastUpdateCount, updates.Length));
            state.LastUpdateCount = 0;

            // The active set. A buffer that is too small gets the needed count back and nothing written.
            int active = (int)RapierNative.r3ActiveRigidBodies(world.World, state.ActiveBodies, (UIntPtr)state.ActiveBodies.Length);
            if (active > state.ActiveBodies.Length)
            {
                state.ActiveBodies = new RapierNative.RigidBodyHandle[active + active / 2];
                state.ActiveStates = new RapierNative.RigidBodyState[state.ActiveBodies.Length];
                active = (int)RapierNative.r3ActiveRigidBodies(world.World, state.ActiveBodies, (UIntPtr)state.ActiveBodies.Length);
            }
            if (active == 0)
                return 0;

            int read = (int)RapierNative.r3RigidBodyReadStates(world.World, state.ActiveBodies, (UIntPtr)active,
                                                                state.ActiveStates, (UIntPtr)state.ActiveStates.Length);
            RapierNative.Check(RapierNative.r3LastStatus());

            int count = Math.Min(read, updates.Length);
            for (int ii = 0; ii < count; ii++)
            {
                RapierNative.RigidBodyState bodyState = state.ActiveStates[ii];
                EntityProperties update = new EntityProperties();
                update.ID = (uint)bodyState.userData.low;
                update.Position = ToVector3(bodyState.position.translation);
                update.Rotation = ToQuaternion(bodyState.position.rotation);
                update.Velocity = ToVector3(bodyState.linvel);
                update.Acceleration = Vector3.Zero;
                update.RotationalVelocity = ToVector3(bodyState.angvel);
                updates[ii] = update;
            }
            state.LastUpdateCount = count;
            return count;
        }

        public override bool PushUpdate(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(PushUpdate));
        }
    }
}
