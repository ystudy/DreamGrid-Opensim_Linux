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
        // Collision flags, activation states and filter groups (H3). So far: the flags are kept on the body and
        // CF_STATIC_OBJECT makes it fixed, the filter groups reach the colliders, and a body can be woken or put to
        // sleep. The other flags and the other activation states are not acted on yet.

        public override CollisionFlags GetCollisionFlags(BulletBody obj)
        {
            return ((BulletBodyRapier)obj).Flags;
        }

        public override CollisionFlags SetCollisionFlags(BulletBody obj, CollisionFlags flags)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            CollisionFlags old = body.Flags;
            body.Flags = flags;
            LogIgnoredFlags(body, flags);
            ApplyStaticFlag(body, old);
            return body.Flags;
        }

        public override CollisionFlags AddToCollisionFlags(BulletBody obj, CollisionFlags flags)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            CollisionFlags old = body.Flags;
            body.Flags |= flags;
            LogIgnoredFlags(body, flags);
            ApplyStaticFlag(body, old);
            return body.Flags;
        }

        public override CollisionFlags RemoveFromCollisionFlags(BulletBody obj, CollisionFlags flags)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            CollisionFlags old = body.Flags;
            body.Flags &= ~flags;
            ApplyStaticFlag(body, old);
            return body.Flags;
        }

        // TODO(Task 5): CF_KINEMATIC_OBJECT, CF_NO_CONTACT_RESPONSE, CF_CHARACTER_OBJECT, BS_SUBSCRIBE_COLLISION_EVENTS,
        // BS_VEHICLE_COLLISIONS and BS_RETURN_ROOT_COMPOUND_SHAPE are stored but have no effect yet (H3). Logged at debug
        // level, once per flag value.
        private void LogIgnoredFlags(BulletBodyRapier body, CollisionFlags flags)
        {
            CollisionFlags ignored = flags & ~CollisionFlags.CF_STATIC_OBJECT;
            if (ignored != 0)
                LogOnce(body, "flags:" + (uint)ignored, "Collision flags " + ignored + " are stored but not acted on yet");
        }

        // CF_STATIC_OBJECT turns the body fixed, and clearing it makes the body dynamic again. Only a change of the
        // flag does anything, so a body made fixed another way (terrain) is left alone.
        private static void ApplyStaticFlag(BulletBodyRapier body, CollisionFlags old)
        {
            bool wasStatic = (old & CollisionFlags.CF_STATIC_OBJECT) != 0;
            bool isStatic = (body.Flags & CollisionFlags.CF_STATIC_OBJECT) != 0;
            if (wasStatic == isStatic || !body.HasPhysicalBody)
                return;
            RapierNative.Check(RapierNative.r3RigidBody_SetBodyType(body.Body,
                                isStatic ? RapierNative.BodyFixed : RapierNative.BodyDynamic, 1));
        }

        // Bullet's group and mask become the collider's memberships and filter. A test mode of 0 needs both bodies to
        // pass each other's filter, which is Bullet's rule.
        public override bool SetCollisionGroupMask(BulletBody pBody, UInt32 filter, UInt32 mask)
        {
            BulletBodyRapier body = (BulletBodyRapier)pBody;
            if (!body.HasPhysicalBody)
                return false;
            RapierNative.InteractionGroups groups = new RapierNative.InteractionGroups { memberships = filter, filter = mask, testMode = 0 };
            foreach (RapierNative.ColliderHandle collider in body.Colliders)
                RapierNative.Check(RapierNative.r3Collider_SetCollisionGroups(collider, groups));
            return true;
        }

        // Waking and sleeping a body; the states that keep a body awake or switch it off come with the activation work.
        public override void ForceActivationState(BulletBody pBody, ActivationState state)
        {
            BulletBodyRapier body = (BulletBodyRapier)pBody;
            if (!body.HasPhysicalBody || RapierNative.r3RigidBody_BodyType(body.Body) != RapierNative.BodyDynamic)
                return;
            if (state == ActivationState.ACTIVE_TAG)
                RapierNative.Check(RapierNative.r3RigidBody_WakeUp(body.Body, 1));
            else if (state == ActivationState.ISLAND_SLEEPING)
                RapierNative.Check(RapierNative.r3RigidBody_Sleep(body.Body));
            else
            {
                // TODO(Task 6): DISABLE_DEACTIVATION (the never-sleep set) and DISABLE_SIMULATION are not acted on yet (H3).
                LogOnce(body, "state:" + state, "Activation state " + state + " is ignored for now");
            }
        }

        // Wake the body (map row 69). A fixed body has nothing to wake.
        public override void Activate(BulletBody obj, bool forceActivation)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (body.HasPhysicalBody && RapierNative.r3RigidBody_BodyType(body.Body) != RapierNative.BodyFixed)
                RapierNative.Check(RapierNative.r3RigidBody_WakeUp(body.Body, forceActivation ? 1u : 0u));
        }

        // Bullet's contact bookkeeping with no per-body Rapier counterpart (map row 142).
        public override void SetContactProcessingThreshold(BulletBody obj, float val)
        {
        }

        public override bool HasContactResponse(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(HasContactResponse));
        }

        public override float GetContactProcessingThreshold(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetContactProcessingThreshold));
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

        public override BulletBody CreateGhostFromShape(
            BulletWorld sim,
            BulletShape shape,
            UInt32 id,
            Vector3 pos,
            Quaternion rot)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(CreateGhostFromShape));
        }
    }
}
