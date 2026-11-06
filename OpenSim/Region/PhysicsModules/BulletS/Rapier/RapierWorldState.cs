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

        // The arrays BSScene gave Initialize. PhysicsStep fills them and reports how many entries it used.
        public EntityProperties[] UpdateArray;
        public CollisionDesc[] CollisionArray;
        public int LastUpdateCount;

        // Scratch buffers for r3ActiveRigidBodies and r3RigidBodyReadStates, grown as needed and reused.
        public RapierNative.RigidBodyHandle[] ActiveBodies = new RapierNative.RigidBodyHandle[256];
        public RapierNative.RigidBodyState[] ActiveStates = new RapierNative.RigidBodyState[256];

        // Every native shape that has been built and not freed, so Shutdown frees the ones BulletS never released and
        // a shape is never freed twice.
        public readonly HashSet<IntPtr> Shapes = new HashSet<IntPtr>();

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
