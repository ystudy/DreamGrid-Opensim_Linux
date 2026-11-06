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
        // ---- Body lifetime ----

        // Insert a rigid body with one collider for the shape. Rapier bodies are in the world from the moment they are
        // inserted, while BulletS adds a body to the world in a separate step, so the body starts disabled and
        // AddObjectToWorld enables it (map row 12). The body's user data carries the localID.
        internal static BulletBodyRapier CreateRigidBody(BulletWorldRapier world, BulletShapeRapier shape, uint id,
                                                    Vector3 pos, Quaternion rot, uint bodyType)
        {
            if (shape.Children != null && shape.Children.Count > 0)
                throw new NotImplementedException("BSAPIRapier." + nameof(CreateBodyFromShape) + " for compound shapes");
            if (shape.Shape == IntPtr.Zero)
                throw new InvalidOperationException("Rapier: a body was asked for from a shape that has been deleted");

            RapierNative.RigidBodyDesc desc = (bodyType == RapierNative.BodyFixed)
                        ? RapierNative.r3FixedRigidBodyDesc()
                        : RapierNative.r3DynamicRigidBodyDesc();
            desc.position.translation = ToNative(pos);
            desc.position.rotation = ToNative(rot);
            desc.userData.low = id;
            desc.enabled = 0;
            RapierNative.RigidBodyHandle handle = RapierNative.r3InsertRigidBody(world.World, in desc);
            RapierNative.Check(RapierNative.r3LastStatus());

            BulletBodyRapier body = new BulletBodyRapier(id, handle, world.State);
            AddCollider(body, shape, id);
            world.State.Bodies[id] = body;
            return body;
        }

        // Put the shape on the body as a collider. The collider carries no density: the body's mass comes from
        // SetMassProps, as Bullet's does. Friction and restitution of two bodies multiply, as in Bullet.
        private static void AddCollider(BulletBodyRapier body, BulletShapeRapier shape, uint id)
        {
            RapierNative.ColliderDesc desc = RapierNative.r3DefaultColliderDesc();
            desc.shape.kind = RapierNative.ShapeKindShared;
            desc.shape.sharedShape = shape.Shape;
            desc.position.translation = ToNative(shape.LocalPos);
            desc.position.rotation = ToNative(shape.LocalRot);
            desc.massMode = RapierNative.MassModeDensity;
            desc.density = 0f;
            desc.frictionCombineRule = RapierNative.CombineMultiply;
            desc.restitutionCombineRule = RapierNative.CombineMultiply;
            desc.userData.low = id;
            RapierNative.ColliderHandle collider = RapierNative.r3InsertCollider(body.Body, in desc);
            RapierNative.Check(RapierNative.r3LastStatus());
            body.Colliders.Add(collider);
            body.State.ColliderOwners[collider] = body;
        }

        public override BulletBody CreateBodyFromShape(BulletWorld pWorld, BulletShape pShape, UInt32 id, Vector3 pos, Quaternion rot)
        {
            return CreateRigidBody((BulletWorldRapier)pWorld, (BulletShapeRapier)pShape, id, pos, rot, RapierNative.BodyDynamic);
        }

        // Removes the body, its colliders and its joints. A body that owns its shape (terrain) takes the shape with it.
        public override void DestroyObject(BulletWorld pWorld, BulletBody pBody)
        {
            BulletWorldRapier world = (BulletWorldRapier)pWorld;
            BulletBodyRapier body = (BulletBodyRapier)pBody;
            if (!body.HasPhysicalBody)
                return;
            BulletShapeRapier ownedShape = body.OwnedShape;
            body.State.ForgetBody(body);
            RapierNative.r3RemoveRigidBody(body.Body, 1);
            body.Clear();
            if (ownedShape != null)
                DeleteCollisionShape(world, ownedShape);
        }

        // Bullet's 'in the world' is Rapier's 'enabled' (map rows 12 to 14); DISABLE_SIMULATION also disables a body that
        // stays in the world, which is why InWorld is kept apart from the native flag.
        public override bool AddObjectToWorld(BulletWorld pWorld, BulletBody pBody)
        {
            BulletBodyRapier body = (BulletBodyRapier)pBody;
            if (!body.HasPhysicalBody)
                return false;
            RapierNative.Check(RapierNative.r3RigidBody_SetEnabled(body.Body, 1));
            body.InWorld = true;
            body.SimulationDisabled = false;
            // The collision groups live in the colliders, which are only worth setting once the body is in.
            pBody.ApplyCollisionMask(pWorld.physicsScene);
            return true;
        }

        public override bool RemoveObjectFromWorld(BulletWorld pWorld, BulletBody pBody)
        {
            BulletBodyRapier body = (BulletBodyRapier)pBody;
            if (!body.HasPhysicalBody)
                return false;
            RapierNative.Check(RapierNative.r3RigidBody_SetEnabled(body.Body, 0));
            body.InWorld = false;
            body.State.ForgetUpdates(body.ID);
            body.State.ForcedBodies.Remove(body);
            return true;
        }

        public override bool IsInWorld(BulletWorld pWorld, BulletBody pBody)
        {
            BulletBodyRapier body = (BulletBodyRapier)pBody;
            // A simulation-disabled body is disabled in Rapier but still in the world as far as BulletS is concerned.
            return body.HasPhysicalBody && body.InWorld;
        }

        // Every body is a rigid body; a ghost object is a rigid body with a sensor collider, and BulletS (MakeSolid) wants
        // to hear that.
        public override CollisionObjectTypes GetBodyType(BulletBody obj)
        {
            return ((BulletBodyRapier)obj).IsGhost ? CollisionObjectTypes.CO_GHOST_OBJECT : CollisionObjectTypes.CO_RIGID_BODY;
        }

        // ---- Pose and velocity ----

        public override Vector3 GetPosition(BulletBody obj)
        {
            return ToVector3(RapierNative.r3RigidBody_Translation(((BulletBodyRapier)obj).Body));
        }

        public override Quaternion GetOrientation(BulletBody obj)
        {
            return ToQuaternion(RapierNative.r3RigidBody_Rotation(((BulletBodyRapier)obj).Body));
        }

        public override void SetTranslation(BulletBody obj, Vector3 position, Quaternion rotation)
        {
            RapierNative.Pose pose = new RapierNative.Pose { translation = ToNative(position), rotation = ToNative(rotation) };
            RapierNative.Check(RapierNative.r3RigidBody_SetPosition(((BulletBodyRapier)obj).Body, pose, 1));
        }

        public override Vector3 GetLinearVelocity(BulletBody obj)
        {
            return ToVector3(RapierNative.r3RigidBody_Linvel(((BulletBodyRapier)obj).Body));
        }

        public override Vector3 GetAngularVelocity(BulletBody obj)
        {
            return ToVector3(RapierNative.r3RigidBody_Angvel(((BulletBodyRapier)obj).Body));
        }

        public override void SetLinearVelocity(BulletBody obj, Vector3 val)
        {
            RapierNative.Check(RapierNative.r3RigidBody_SetLinvel(((BulletBodyRapier)obj).Body, ToNative(val), 1));
        }

        public override void SetAngularVelocity(BulletBody obj, Vector3 angularVelocity)
        {
            RapierNative.Check(RapierNative.r3RigidBody_SetAngvel(((BulletBodyRapier)obj).Body, ToNative(angularVelocity), 1));
        }

        // Bullet's clearForces plus zero linear and angular velocity. (BulletSim's ClearAllForces does the same.)
        public override void ClearAllForces(BulletBody obj)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            RapierNative.Check(RapierNative.r3RigidBody_ResetForces(body.Body, 0));
            RapierNative.Check(RapierNative.r3RigidBody_ResetTorques(body.Body, 0));
            RapierNative.Check(RapierNative.r3RigidBody_SetLinvel(body.Body, default(RapierNative.Vector), 0));
            RapierNative.Check(RapierNative.r3RigidBody_SetAngvel(body.Body, default(RapierNative.Vector), 0));
        }

        // Rapier keeps no interpolation state (map rows 81 to 83).
        public override void SetInterpolationAngularVelocity(BulletBody obj, Vector3 vel)
        {
        }

        // ---- Mass and gravity ----

        // The collider has no density, so the body's mass and inertia are the 'additional mass properties' (map row 102).
        // BulletS gives the inertia as the diagonal in the body frame.
        public override void SetMassProps(BulletBody obj, float mass, Vector3 inertia)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            RapierNative.MassProperties props = new RapierNative.MassProperties
            {
                localCom = default(RapierNative.Vector),
                mass = Math.Max(mass, 0f),
                principalInertia = ToNative(new Vector3(Math.Max(inertia.X, 0f), Math.Max(inertia.Y, 0f), Math.Max(inertia.Z, 0f))),
                principalInertiaLocalFrame = ToNative(Quaternion.Identity)
            };
            RapierNative.Check(RapierNative.r3RigidBody_SetAdditionalMassProperties(body.Body, props, 0));
        }

        // Rapier keeps the world-space inertia up to date itself (map row 120).
        public override void UpdateInertiaTensor(BulletBody obj)
        {
        }

        // Bullet has a gravity vector per body, Rapier a scale of the world's. BulletS only passes vectors that run
        // along the world's gravity (buoyancy, gravity modifiers), so the scale is exact (H6).
        public override void SetGravity(BulletBody obj, Vector3 val)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            float worldZ = body.State.Gravity.Z;
            float scale = (worldZ == 0f) ? 0f : val.Z / worldZ;
            RapierNative.Check(RapierNative.r3RigidBody_SetGravityScale(body.Body, scale, 0));
        }

        // ---- Materials and damping ----

        public override void SetFriction(BulletBody obj, float val)
        {
            foreach (RapierNative.ColliderHandle collider in ((BulletBodyRapier)obj).Colliders)
                RapierNative.Check(RapierNative.r3Collider_SetFriction(collider, val));
        }

        public override void SetRestitution(BulletBody obj, float val)
        {
            foreach (RapierNative.ColliderHandle collider in ((BulletBodyRapier)obj).Colliders)
                RapierNative.Check(RapierNative.r3Collider_SetRestitution(collider, val));
        }

        // A Bullet damping of 1 or more stops the body at once. Rapier's rate is finite, so this one stops it within a step
        // (a 1/60 s step leaves 1 / (1 + 1000 / 60), under 6% of the speed).
        private const float StopDampingRate = 1000f;

        // Bullet's damping d is the fraction of speed kept per second, as a power of the time: every step does
        //     v = v * (1 - d)^dt                       (btRigidBody::applyDamping)
        // Rapier's coefficient k is applied once per step, in rigid_body_components.rs (RigidBodyVelocity::apply_damping,
        // called with the step length dt from the island solver's write-back, staged_island_solver/worker.rs):
        //     v = v * (1 / (1 + dt * k))
        // After a time T of T/dt steps Bullet leaves (1 - d)^T and Rapier leaves (1 + dt k)^(-T/dt). As dt goes to 0 the
        // latter tends to e^(-k T), and (1 - d)^T = e^(T ln(1 - d)), so the two agree for
        //     k = -ln(1 - d).
        // At BulletS's step of 1/60 s the discretisation costs about k dt / 2 in the exponent: for d = 0.5 the body keeps
        // 50.2% of its speed after a second, against Bullet's 50%. The same rate serves the angular side.
        public override void SetDamping(BulletBody obj, float lin_damping, float ang_damping)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            RapierNative.Check(RapierNative.r3RigidBody_SetLinearDamping(body.Body, DampingRate(lin_damping)));
            RapierNative.Check(RapierNative.r3RigidBody_SetAngularDamping(body.Body, DampingRate(ang_damping)));
        }

        private static float DampingRate(float bulletDamping)
        {
            if (bulletDamping <= 0f)
                return 0f;
            if (bulletDamping >= 1f)
                return StopDampingRate;
            return (float)-Math.Log(1.0 - bulletDamping);
        }

        // Linear and angular factors (H11). BulletS only uses 0 (an avatar's locked rotation) and 1 (the default); those
        // become Rapier's locked axes. A factor in between has no counterpart in Rapier: the axis is left free and the
        // gap is logged once for the body.
        private void ApplyFactors(BulletBodyRapier body)
        {
            byte locked = 0;
            bool fractional = false;
            locked |= AxisLock(body.LinearFactor.X, RapierNative.LockTranslationX, ref fractional);
            locked |= AxisLock(body.LinearFactor.Y, RapierNative.LockTranslationY, ref fractional);
            locked |= AxisLock(body.LinearFactor.Z, RapierNative.LockTranslationZ, ref fractional);
            locked |= AxisLock(body.AngularFactor.X, RapierNative.LockRotationX, ref fractional);
            locked |= AxisLock(body.AngularFactor.Y, RapierNative.LockRotationY, ref fractional);
            locked |= AxisLock(body.AngularFactor.Z, RapierNative.LockRotationZ, ref fractional);
            if (fractional)
            {
                LogOnce(body, "factor:" + body.ID, "Body " + body.ID + " has a linear or angular factor between 0 and 1, "
                            + "which Rapier cannot do (it only locks axes); the axis stays free");
            }
            if (!body.HasPhysicalBody || locked == body.LockedAxes)
                return;
            body.LockedAxes = locked;
            RapierNative.Check(RapierNative.r3RigidBody_SetLockedAxes(body.Body, locked, 0));
        }

        private static byte AxisLock(float factor, byte bit, ref bool fractional)
        {
            if (factor == 0f)
                return bit;
            if (factor != 1f)
                fractional = true;
            return 0;
        }

        // BulletS clears a body's shape before destroying it so Bullet does not free a shape other bodies share. A
        // Rapier body never frees BulletS's shapes, so there is nothing to clear. Putting another shape on a body
        // comes with the shape work.
        public override void SetCollisionShape(BulletWorld sim, BulletBody obj, BulletShape shape)
        {
            if (shape != null)
                throw new NotImplementedException("BSAPIRapier." + nameof(SetCollisionShape));
        }

        // Bullet's contact bookkeeping with no Rapier counterpart (map rows 85 and 112).
        public override void SetHitFraction(BulletBody obj, float val)
        {
        }

        // rapier.h has no sleep-threshold setting; Rapier's own sleep rules apply (map row 112).
        public override void SetSleepingThresholds(BulletBody obj, float lin_threshold, float ang_threshold)
        {
        }

        public override BulletShape GetCollisionShape(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetCollisionShape));
        }

        public override bool IsStaticObject(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(IsStaticObject));
        }

        public override bool IsKinematicObject(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(IsKinematicObject));
        }

        public override bool IsStaticOrKinematicObject(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(IsStaticOrKinematicObject));
        }

        public override float GetRestitution(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetRestitution));
        }

        public override float GetFriction(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetFriction));
        }

        public override Vector3 GetAnisotripicFriction(BulletConstraint constrain)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetAnisotripicFriction));
        }

        public override Vector3 SetAnisotripicFriction(BulletConstraint constrain, Vector3 frict)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetAnisotripicFriction));
        }

        public override bool HasAnisotripicFriction(BulletConstraint constrain)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(HasAnisotripicFriction));
        }

        public override void SetInterpolationLinearVelocity(BulletBody obj, Vector3 vel)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetInterpolationLinearVelocity));
        }

        public override void SetInterpolationVelocity(BulletBody obj, Vector3 linearVel, Vector3 angularVel)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetInterpolationVelocity));
        }

        public override float GetHitFraction(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetHitFraction));
        }

        public override float GetCcdMotionThreshold(BulletBody obj)
        {
            return ((BulletBodyRapier)obj).CcdMotionThreshold;
        }

        // H7: BulletS turns CCD on with a motion threshold above 0 (it is 0 by default, so CCD is off). Rapier has CCD per
        // body and sweeps the real shape, so the threshold only switches it on or off.
        public override void SetCcdMotionThreshold(BulletBody obj, float val)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            body.CcdMotionThreshold = val;
            if (body.HasPhysicalBody)
                RapierNative.Check(RapierNative.r3RigidBody_SetCcdEnabled(body.Body, val > 0f ? 1u : 0u));
        }

        public override float GetCcdSweptSphereRadius(BulletBody obj)
        {
            return ((BulletBodyRapier)obj).CcdSweptSphereRadius;
        }

        // Kept for the getter only: Rapier's CCD sweeps the shape itself (H7).
        public override void SetCcdSweptSphereRadius(BulletBody obj, float val)
        {
            ((BulletBodyRapier)obj).CcdSweptSphereRadius = val;
        }

        public override IntPtr GetUserPointer(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetUserPointer));
        }

        public override void SetUserPointer(BulletBody obj, IntPtr val)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetUserPointer));
        }

        public override void ApplyGravity(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(ApplyGravity));
        }

        public override Vector3 GetGravity(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetGravity));
        }

        public override void SetLinearDamping(BulletBody obj, float lin_damping)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetLinearDamping));
        }

        public override void SetAngularDamping(BulletBody obj, float ang_damping)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetAngularDamping));
        }

        public override float GetLinearDamping(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetLinearDamping));
        }

        public override float GetAngularDamping(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetAngularDamping));
        }

        public override float GetLinearSleepingThreshold(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetLinearSleepingThreshold));
        }

        public override void ApplyDamping(BulletBody obj, float timeStep)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(ApplyDamping));
        }

        public override Vector3 GetLinearFactor(BulletBody obj)
        {
            return ((BulletBodyRapier)obj).LinearFactor;
        }

        public override void SetLinearFactor(BulletBody obj, Vector3 factor)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            body.LinearFactor = factor;
            ApplyFactors(body);
        }

        public override void SetCenterOfMassByPosRot(BulletBody obj, Vector3 pos, Quaternion rot)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetCenterOfMassByPosRot));
        }

        // A force and a torque stay on a Rapier body until reset, so the body is noted and PhysicsStep resets it (H11).
        public override void ApplyCentralForce(BulletBody obj, Vector3 force)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (!body.HasPhysicalBody)
                return;
            RapierNative.Check(RapierNative.r3RigidBody_AddForce(body.Body, ToNative(force), force != Vector3.Zero ? 1u : 0u));
            body.State.ForcedBodies.Add(body);
        }

        public override void SetObjectForce(BulletBody obj, Vector3 force)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetObjectForce));
        }

        public override Vector3 GetTotalForce(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetTotalForce));
        }

        public override Vector3 GetTotalTorque(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetTotalTorque));
        }

        public override Vector3 GetInvInertiaDiagLocal(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetInvInertiaDiagLocal));
        }

        public override void SetInvInertiaDiagLocal(BulletBody obj, Vector3 inert)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetInvInertiaDiagLocal));
        }

        public override void ApplyTorque(BulletBody obj, Vector3 torque)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (!body.HasPhysicalBody)
                return;
            RapierNative.Check(RapierNative.r3RigidBody_AddTorque(body.Body, ToNative(torque), torque != Vector3.Zero ? 1u : 0u));
            body.State.ForcedBodies.Add(body);
        }

        public override void ApplyForce(BulletBody obj, Vector3 force, Vector3 pos)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(ApplyForce));
        }

        public override void ApplyCentralImpulse(BulletBody obj, Vector3 imp)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (body.HasPhysicalBody)
                RapierNative.Check(RapierNative.r3RigidBody_ApplyImpulse(body.Body, ToNative(imp), imp != Vector3.Zero ? 1u : 0u));
        }

        public override void ApplyTorqueImpulse(BulletBody obj, Vector3 imp)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (body.HasPhysicalBody)
                RapierNative.Check(RapierNative.r3RigidBody_ApplyTorqueImpulse(body.Body, ToNative(imp), imp != Vector3.Zero ? 1u : 0u));
        }

        public override void ApplyImpulse(BulletBody obj, Vector3 imp, Vector3 pos)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(ApplyImpulse));
        }

        public override void ClearForces(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(ClearForces));
        }

        public override Vector3 GetVelocityInLocalPoint(BulletBody obj, Vector3 pos)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetVelocityInLocalPoint));
        }

        public override void Translate(BulletBody obj, Vector3 trans)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(Translate));
        }

        public override void UpdateDeactivation(BulletBody obj, float timeStep)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(UpdateDeactivation));
        }

        public override bool WantsSleeping(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(WantsSleeping));
        }

        public override void SetAngularFactor(BulletBody obj, float factor)
        {
            SetAngularFactorV(obj, new Vector3(factor, factor, factor));
        }

        public override void SetAngularFactorV(BulletBody obj, Vector3 factor)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            body.AngularFactor = factor;
            ApplyFactors(body);
        }

        public override Vector3 GetAngularFactor(BulletBody obj)
        {
            return ((BulletBodyRapier)obj).AngularFactor;
        }

        public override void AddConstraintRef(BulletBody obj, BulletConstraint constrain)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(AddConstraintRef));
        }

        public override void RemoveConstraintRef(BulletBody obj, BulletConstraint constrain)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(RemoveConstraintRef));
        }

        public override BulletConstraint GetConstraintRef(BulletBody obj, int index)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetConstraintRef));
        }

        public override int GetNumConstraintRefs(BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetNumConstraintRefs));
        }

        public override bool ClearCollisionProxyCache(BulletWorld world, BulletBody obj)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(ClearCollisionProxyCache));
        }
    }
}
