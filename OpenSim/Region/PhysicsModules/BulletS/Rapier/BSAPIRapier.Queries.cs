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
        // Sweep the body's shape from one point to another and report the first thing it touches (map row 175). The body
        // keeps its own orientation; a compound is swept as the shapes it is made of. The hit's fraction is the share of the
        // way from 'from' to 'to'. Rapier has no margin, so that is ignored; the body itself is not hit.
        public override SweepHit ConvexSweepTest2(
            BulletWorld world,
            BulletBody obj,
            Vector3 from,
            Vector3 to,
            float margin)
        {
            BulletWorldRapier rapierWorld = (BulletWorldRapier)world;
            BulletBodyRapier body = (BulletBodyRapier)obj;
            SweepHit result = new SweepHit();
            Vector3 delta = to - from;
            if (!body.HasPhysicalBody || body.Shape == null || delta == Vector3.Zero)
                return result;

            List<(BulletShapeRapier leaf, Vector3 pos, Quaternion rot)> leaves = new List<(BulletShapeRapier leaf, Vector3 pos, Quaternion rot)>();
            Flatten(body.Shape, Vector3.Zero, Quaternion.Identity, 0, leaves);
            if (leaves.Count == 0)
                return result;

            PropagateIfStale(rapierWorld);
            RapierNative.QueryOptions options = new RapierNative.QueryOptions();
            options.filter = RapierNative.r3DefaultQueryFilter();
            options.filter.excludeRigidBody = body.Body;
            RapierNative.ShapeCastOptions castOptions = RapierNative.r3DefaultShapeCastOptions();
            castOptions.maxTimeOfImpact = 1f;

            Quaternion bodyRotation = BodyRotation(body);
            bool found = false;
            float best = float.MaxValue;
            foreach ((BulletShapeRapier leaf, Vector3 pos, Quaternion rot) in leaves)
            {
                // Colliders of the shape at the start of the sweep: the body's turn applied to each one's place in the body.
                RapierNative.Pose pose = MakePose(from + Rotate(bodyRotation, pos), bodyRotation * rot);
                RapierNative.ShapeCastHit hit = RapierNative.r3CastShape(rapierWorld.World, in options, pose, ToNative(delta),
                            leaf.Shape, castOptions);
                uint status = RapierNative.r3LastStatus();
                if (status == RapierNative.StatusNotFound)
                    continue;
                RapierNative.Check(status);
                if (hit.timeOfImpact >= best)
                    continue;
                best = hit.timeOfImpact;
                found = true;
                result.ID = (uint)RapierNative.r3Collider_UserData(hit.collider).low;
                result.Fraction = hit.timeOfImpact;
                result.Normal = ToVector3(hit.normal1);
                result.Point = ToVector3(hit.witness1);
            }
            return found ? result : new SweepHit();
        }

        // Pass the poses C# set since the last step or propagate on to the colliders, once: a frame's rays after the
        // first find nothing to do. The broad phase itself still only learns the new bounding boxes in the next step.
        private static void PropagateIfStale(BulletWorldRapier world)
        {
            if (!world.State.CollidersStale)
                return;
            RapierNative.Check(RapierNative.r3RigidBodyPropagateModifiedBodyPositionsToColliders(world.World));
            world.State.CollidersStale = false;
        }

        public override RaycastHit RayTest2(
            BulletWorld world,
            Vector3 from,
            Vector3 to,
            uint filterGroup,
            uint filterMask)
        {
            BulletWorldRapier rapierWorld = (BulletWorldRapier)world;
            Vector3 delta = to - from;
            RaycastHit result = new RaycastHit();
            if (delta == Vector3.Zero)
                return result;

            // Colliders that moved since the last step are not in the query structures until their poses are passed on.
            PropagateIfStale(rapierWorld);

            // The ray is from + delta * t for t in [0, 1], so the time of impact is Bullet's hit fraction. The same
            // group and mask rule as the colliders' own decides what it can hit (H3).
            RapierNative.QueryOptions options = new RapierNative.QueryOptions();
            options.filter = RapierNative.r3DefaultQueryFilter();
            options.filter.useGroups = 1;
            options.filter.groups = new RapierNative.InteractionGroups { memberships = filterGroup, filter = filterMask, testMode = 0 };
            RapierNative.RayHit hit = RapierNative.r3CastRay(rapierWorld.World, in options, ToNative(from), ToNative(delta), 1f, 1);
            uint status = RapierNative.r3LastStatus();
            if (status == RapierNative.StatusNotFound)
                return result;
            RapierNative.Check(status);

            result.ID = (uint)RapierNative.r3Collider_UserData(hit.collider).low;
            result.Fraction = hit.timeOfImpact;
            result.Normal = ToVector3(hit.normal);
            result.Point = from + delta * hit.timeOfImpact;
            return result;
        }
    }
}
