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
        // Collision flags, activation states and filter groups (H3). The flags are kept on the body and each one that
        // means something to Rapier is applied to it:
        //  - CF_STATIC_OBJECT / CF_KINEMATIC_OBJECT choose the body type (fixed, kinematic or dynamic), in place;
        //  - CF_NO_CONTACT_RESPONSE takes the colliders out of the contact solver (their solver groups filter nothing),
        //    so the contacts are found and reported but push nothing;
        //  - BS_SUBSCRIBE_COLLISION_EVENTS and BS_VEHICLE_COLLISIONS put the colliders in WantsCollisions (H2);
        //  - BS_RETURN_ROOT_COMPOUND_SHAPE is read when updates are reported (H1).
        // CF_CHARACTER_OBJECT (a marker), CF_CUSTOM_MATERIAL_CALLBACK and BS_FLOATS_ON_WATER (BulletSim's native code never
        // reads it) have nothing to do in Rapier. BulletSim sets the callback flag on terrain so a contact callback can
        // correct the normals at the internal edges of the terrain mesh; Rapier does that when the shape is built instead:
        // CreateMeshShape uses R3_TRIMESH_FIX_INTERNAL_EDGES and CreateTerrainShape R3_HEIGHTFIELD_FIX_INTERNAL_EDGES.

        private const CollisionFlags WantsCollisionsFlags = CollisionFlags.BS_SUBSCRIBE_COLLISION_EVENTS | CollisionFlags.BS_VEHICLE_COLLISIONS;

        // The solver groups of a CF_NO_CONTACT_RESPONSE collider: a member of every group, solving against none.
        private static readonly RapierNative.InteractionGroups NoContactResponseGroups =
                    new RapierNative.InteractionGroups { memberships = uint.MaxValue, filter = 0u, testMode = 0 };

        public override CollisionFlags GetCollisionFlags(BulletBody obj)
        {
            return ((BulletBodyRapier)obj).Flags;
        }

        public override CollisionFlags SetCollisionFlags(BulletBody obj, CollisionFlags flags)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            CollisionFlags old = body.Flags;
            body.Flags = flags;
            ApplyFlags(body, old);
            return body.Flags;
        }

        public override CollisionFlags AddToCollisionFlags(BulletBody obj, CollisionFlags flags)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            CollisionFlags old = body.Flags;
            body.Flags |= flags;
            ApplyFlags(body, old);
            return body.Flags;
        }

        public override CollisionFlags RemoveFromCollisionFlags(BulletBody obj, CollisionFlags flags)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            CollisionFlags old = body.Flags;
            body.Flags &= ~flags;
            ApplyFlags(body, old);
            return body.Flags;
        }

        // Do what a change from the old flags to the body's current ones calls for. Only a change does anything, so a
        // body made fixed another way (terrain) is left alone.
        private static void ApplyFlags(BulletBodyRapier body, CollisionFlags old)
        {
            if (!body.HasPhysicalBody)
                return;
            CollisionFlags now = body.Flags;

            uint oldType = BodyTypeFor(old);
            uint newType = BodyTypeFor(now);
            if (oldType != newType)
                RapierNative.Check(RapierNative.r3RigidBody_SetBodyType(body.Body, newType, 1));

            if (((old ^ now) & CollisionFlags.CF_NO_CONTACT_RESPONSE) != 0)
            {
                RapierNative.InteractionGroups groups = (now & CollisionFlags.CF_NO_CONTACT_RESPONSE) != 0
                            ? NoContactResponseGroups
                            : RapierNative.r3DefaultColliderDesc().solverGroups;
                foreach (RapierNative.ColliderHandle collider in body.Colliders)
                    RapierNative.Check(RapierNative.r3Collider_SetSolverGroups(collider, groups));
            }

            bool wasWanting = (old & WantsCollisionsFlags) != 0;
            bool isWanting = (now & WantsCollisionsFlags) != 0;
            if (wasWanting != isWanting)
            {
                foreach (RapierNative.ColliderHandle collider in body.Colliders)
                {
                    if (isWanting)
                        body.State.WantsCollisions.Add(collider);
                    else
                        body.State.WantsCollisions.Remove(collider);
                }
            }
        }

        private static uint BodyTypeFor(CollisionFlags flags)
        {
            if ((flags & CollisionFlags.CF_STATIC_OBJECT) != 0)
                return RapierNative.BodyFixed;
            if ((flags & CollisionFlags.CF_KINEMATIC_OBJECT) != 0)
                return RapierNative.BodyKinematicPositionBased;
            return RapierNative.BodyDynamic;
        }

        // Bullet's group and mask become the collider's memberships and filter. A test mode of 0 needs both bodies to
        // pass each other's filter, which is Bullet's rule.
        public override bool SetCollisionGroupMask(BulletBody pBody, UInt32 filter, UInt32 mask)
        {
            BulletBodyRapier body = (BulletBodyRapier)pBody;
            if (!body.HasPhysicalBody)
                return false;
            RapierNative.InteractionGroups groups = new RapierNative.InteractionGroups { memberships = filter, filter = mask, testMode = 0 };
            body.Groups = groups;
            body.HasGroups = true;
            foreach (RapierNative.ColliderHandle collider in body.Colliders)
                RapierNative.Check(RapierNative.r3Collider_SetCollisionGroups(collider, groups));
            return true;
        }

        // Bullet's activation states (H3):
        //  - ACTIVE_TAG wakes the body;
        //  - ISLAND_SLEEPING puts it to sleep;
        //  - WANTS_DEACTIVATION is Bullet's 'about to sleep', which Rapier works out for itself: it only ends the other
        //    states;
        //  - DISABLE_DEACTIVATION puts the body in the never-sleep set, which PhysicsStep wakes before each step;
        //  - DISABLE_SIMULATION leaves the terrain and the ground plane (the bodies that own their shapes) as they are,
        //    since they do not simulate but must still collide, and disables any other body in Rapier, fixed or dynamic.
        //    BulletS sends it to those for one reason: BSLinksetCompound hides the linkset children whose shapes it has put
        //    on the root this way, and a child's own body, static or physical, must then make no contacts, push nothing
        //    and report nothing, or every touch of the child would be counted twice. BulletS still counts the body as in
        //    the world (IsInWorld); it just reports nothing for it (BulletBodyRapier.Reporting) until another state, or
        //    AddObjectToWorld, enables it.
        // Any state but DISABLE_DEACTIVATION takes the body out of the never-sleep set, as Bullet's one tag replaces
        // another.
        public override void ForceActivationState(BulletBody pBody, ActivationState state)
        {
            BulletBodyRapier body = (BulletBodyRapier)pBody;
            if (!body.HasPhysicalBody)
                return;

            if (state == ActivationState.DISABLE_SIMULATION)
            {
                DisableSimulation(body);
                return;
            }

            if (state != ActivationState.DISABLE_DEACTIVATION)
                body.State.NeverSleep.Remove(body);
            if (body.SimulationDisabled)
                EnableSimulation(body);
            if (RapierNative.r3RigidBody_BodyType(body.Body) != RapierNative.BodyDynamic)
                return;

            switch (state)
            {
                case ActivationState.ACTIVE_TAG:
                    RapierNative.Check(RapierNative.r3RigidBody_WakeUp(body.Body, 1));
                    break;
                case ActivationState.ISLAND_SLEEPING:
                    RapierNative.Check(RapierNative.r3RigidBody_Sleep(body.Body));
                    break;
                case ActivationState.DISABLE_DEACTIVATION:
                    body.State.NeverSleep.Add(body);
                    RapierNative.Check(RapierNative.r3RigidBody_WakeUp(body.Body, 1));
                    break;
                // WANTS_DEACTIVATION: nothing more to do.
            }
        }

        private static void DisableSimulation(BulletBodyRapier body)
        {
            if (body.SimulationDisabled || body.OwnedShape != null)
                return;
            body.SimulationDisabled = true;
            body.State.NeverSleep.Remove(body);
            DropAppliedForces(body);
            // Nothing more is reported for it, and what was last sent for it means nothing now.
            body.State.ForgetUpdates(body.ID);
            if (body.InWorld)
                RapierNative.Check(RapierNative.r3RigidBody_SetEnabled(body.Body, 0));
        }

        private static void EnableSimulation(BulletBodyRapier body)
        {
            body.SimulationDisabled = false;
            // A body outside the world stays disabled until AddObjectToWorld.
            if (body.InWorld)
                RapierNative.Check(RapierNative.r3RigidBody_SetEnabled(body.Body, 1));
        }

        // Wake the body (map row 69). A fixed body has nothing to wake.
        public override void Activate(BulletBody obj, bool forceActivation)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (body.HasPhysicalBody && RapierNative.r3RigidBody_BodyType(body.Body) != RapierNative.BodyFixed)
                RapierNative.Check(RapierNative.r3RigidBody_WakeUp(body.Body, forceActivation ? 1u : 0u));
        }

        // Bullet's contact bookkeeping with no per-body Rapier counterpart (map rows 142 and 143): the value is kept.
        public override void SetContactProcessingThreshold(BulletBody obj, float val)
        {
            ((BulletBodyRapier)obj).ContactProcessingThreshold = val;
        }

        public override float GetContactProcessingThreshold(BulletBody obj)
        {
            return ((BulletBodyRapier)obj).ContactProcessingThreshold;
        }

        // The flag is the answer (map row 141).
        public override bool HasContactResponse(BulletBody obj)
        {
            return (((BulletBodyRapier)obj).Flags & CollisionFlags.CF_NO_CONTACT_RESPONSE) == 0;
        }

        public override int GetActivationState(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetActivationState));
        }

        public override void SetActivationState(BulletBody obj, int state)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetActivationState));
        }

        public override void SetDeactivationTime(BulletBody obj, float dtime)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetDeactivationTime));
        }

        public override float GetDeactivationTime(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetDeactivationTime));
        }

        public override bool IsActive(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(IsActive));
        }

        // A volume-detect body (H4): a rigid body whose collider is a sensor. A sensor reports overlaps instead of making
        // contacts, and its collision types are widened so that a static volume also sees fixed and kinematic bodies.
        // BulletS makes it fixed with CF_STATIC_OBJECT and non-solid with CF_NO_CONTACT_RESPONSE, as for any prim.
        public override BulletBody CreateGhostFromShape(
            BulletWorld sim,
            BulletShape shape,
            UInt32 id,
            Vector3 pos,
            Quaternion rot)
        {
            BulletBodyRapier body = CreateRigidBody((BulletWorldRapier)sim, (BulletShapeRapier)shape, id, pos, rot, RapierNative.BodyDynamic);
            body.IsGhost = true;
            foreach (RapierNative.ColliderHandle collider in body.Colliders)
                MakeSensor(body, collider);
            return body;
        }

        private static void MakeSensor(BulletBodyRapier body, RapierNative.ColliderHandle collider)
        {
            RapierNative.Check(RapierNative.r3Collider_SetSensor(collider, 1));
            RapierNative.Check(RapierNative.r3Collider_SetActiveCollisionTypes(collider,
                        (ushort)(RapierNative.CollisionTypesDefault | RapierNative.CollisionTypesKinematicFixed | RapierNative.CollisionTypesFixedFixed)));
            body.State.Sensors.Add(collider);
        }
    }
}
