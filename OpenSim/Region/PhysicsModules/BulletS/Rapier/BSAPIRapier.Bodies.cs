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
        // ---- Body lifetime ----

        // Insert a rigid body with a collider for the shape (one per child for a compound). Rapier bodies are in the
        // world from the moment they are inserted, while BulletS adds a body to the world in a separate step, so the
        // body starts disabled and AddObjectToWorld enables it (map row 12). The body's user data carries the localID.
        internal static BulletBodyRapier CreateRigidBody(BulletWorldRapier world, BulletShapeRapier shape, uint id,
                                                    Vector3 pos, Quaternion rot, uint bodyType)
        {
            if (!shape.HasPhysicalShape)
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
            world.State.CollidersStale = true;

            BulletBodyRapier body = new BulletBodyRapier(id, handle, world.State);
            body.KnownPosition = pos;
            body.KnownRotation = rot;
            try
            {
                AttachShape(body, shape);
            }
            catch
            {
                // AttachShape has taken its colliders back out of the tables; the native body must not stay behind
                // without a C# owner.
                RapierNative.r3RemoveRigidBody(handle, 1);
                body.Clear();
                throw;
            }
            world.State.Bodies[id] = body;
            return body;
        }

        // Put a native shape on the body as a collider at this pose relative to the body. The collider carries no
        // density: the body's mass comes from SetMassProps, as Bullet's does. Friction and restitution of two bodies
        // multiply, as in Bullet. Its user data is the body's localID, which for a linkset is the root's. What BulletS
        // has already set on the body that lives in colliders is given to it (ConfigureCollider).
        private static RapierNative.ColliderHandle AddCollider(BulletBodyRapier body, BulletShapeRapier shape, Vector3 pos, Quaternion rot)
        {
            RapierNative.ColliderDesc desc = RapierNative.r3DefaultColliderDesc();
            desc.shape.kind = RapierNative.ShapeKindShared;
            desc.shape.sharedShape = shape.Shape;
            desc.position.translation = ToNative(pos);
            desc.position.rotation = ToNative(rot);
            desc.massMode = RapierNative.MassModeDensity;
            desc.density = 0f;
            desc.frictionCombineRule = RapierNative.CombineMultiply;
            desc.restitutionCombineRule = RapierNative.CombineMultiply;
            desc.userData.low = body.ID;
            RapierNative.ColliderHandle collider = RapierNative.r3InsertCollider(body.Body, in desc);
            RapierNative.Check(RapierNative.r3LastStatus());
            body.State.CollidersStale = true;
            body.Colliders.Add(collider);
            body.State.ColliderOwners[collider] = body;
            body.State.NoteShapeUse(shape.Shape, collider);
            ConfigureCollider(body, collider);
            return collider;
        }

        // Give a new collider what the body was told that lives in the colliders: the filter group and mask, no contact
        // response, friction, restitution, collision events and being a sensor. A body's first collider is made before
        // any of that is set, so it gets nothing here.
        private static void ConfigureCollider(BulletBodyRapier body, RapierNative.ColliderHandle collider)
        {
            if (body.HasGroups)
                RapierNative.Check(RapierNative.r3Collider_SetCollisionGroups(collider, body.Groups));
            if ((body.Flags & CollisionFlags.CF_NO_CONTACT_RESPONSE) != 0)
                RapierNative.Check(RapierNative.r3Collider_SetSolverGroups(collider, NoContactResponseGroups));
            if (body.Friction.HasValue)
                RapierNative.Check(RapierNative.r3Collider_SetFriction(collider, body.Friction.Value));
            if (body.Restitution.HasValue)
                RapierNative.Check(RapierNative.r3Collider_SetRestitution(collider, body.Restitution.Value));
            if ((body.Flags & WantsCollisionsFlags) != 0)
                SetWantsCollisions(body, collider, true);
            if (body.IsGhost)
                MakeSensor(body, collider);
        }

        // Take a collider off the body and out of every table.
        private static void RemoveBodyCollider(BulletBodyRapier body, RapierNative.ColliderHandle collider)
        {
            body.State.ForgetCollider(collider);
            body.Colliders.Remove(collider);
            RapierNative.Check(RapierNative.r3RemoveCollider(collider, 0));
        }

        // Make the shape the body's colliders: one for a plain shape, one per (flattened) child for a compound, which is
        // then on this body until another shape replaces it, the body goes or the compound is deleted.
        //
        // All or nothing: if Rapier refuses a collider, the colliders this call made are removed again and the body is
        // left with no shape and no compound, so no table claims a collider or shape the native side does not have.
        // The exception then goes on to the caller.
        private static void AttachShape(BulletBodyRapier body, BulletShapeRapier shape)
        {
            int firstNew = body.Colliders.Count;
            CompoundState compound = shape.Compound;
            try
            {
                body.Shape = shape;
                if (compound == null)
                {
                    AddCollider(body, shape, shape.LocalPos, shape.LocalRot);
                    NoteRootChild(body, null);
                    return;
                }
                // A compound is on one body at a time; one already on another body leaves that body's colliders alone.
                if (compound.Body != null)
                    DetachCompound(compound.Body);
                compound.Body = body;
                body.Compound = compound;
                compound.Colliders.Clear();
                foreach ((BulletShapeRapier child, Vector3 pos, Quaternion rot) in compound.Children)
                    compound.Colliders.Add(AddChildColliders(body, child, pos, rot));
                NoteRootChild(body, compound);
            }
            catch
            {
                RemoveCollidersFrom(body, firstNew);
                if (compound != null)
                    DetachCompound(body);
                body.Shape = null;
                NoteRootChild(body, null);
                throw;
            }
        }

        // The colliders for one compound child at this pose: one per native shape the child is made of. All or nothing,
        // as AttachShape.
        private static List<ChildCollider> AddChildColliders(BulletBodyRapier body, BulletShapeRapier child, Vector3 pos, Quaternion rot)
        {
            List<(BulletShapeRapier leaf, Vector3 pos, Quaternion rot)> leaves = new List<(BulletShapeRapier leaf, Vector3 pos, Quaternion rot)>();
            Flatten(child, Vector3.Zero, Quaternion.Identity, 0, leaves);
            List<ChildCollider> parts = new List<ChildCollider>(leaves.Count);
            int firstNew = body.Colliders.Count;
            try
            {
                foreach ((BulletShapeRapier leaf, Vector3 leafPos, Quaternion leafRot) in leaves)
                {
                    Compose(pos, rot, leafPos, leafRot, out Vector3 at, out Quaternion turned);
                    parts.Add(new ChildCollider { Collider = AddCollider(body, leaf, at, turned), Pos = leafPos, Rot = leafRot });
                }
            }
            catch
            {
                RemoveCollidersFrom(body, firstNew);
                throw;
            }
            return parts;
        }

        // Undo a failed attach: the body's colliders from index first on leave every table and, as far as Rapier still
        // has them, the world. (AddCollider records a collider as soon as Rapier has inserted it, so these are exactly
        // the ones the failed call made.) The native status is not checked: the attach has already failed, and a collider
        // Rapier no longer has is gone either way.
        private static void RemoveCollidersFrom(BulletBodyRapier body, int first)
        {
            for (int ii = body.Colliders.Count - 1; ii >= first; ii--)
            {
                RapierNative.ColliderHandle collider = body.Colliders[ii];
                body.State.ForgetCollider(collider);
                body.Colliders.RemoveAt(ii);
                RapierNative.r3RemoveCollider(collider, 0);
            }
        }

        // The compound stops following the body: its colliders stay as they are.
        private static void DetachCompound(BulletBodyRapier body)
        {
            CompoundState compound = body.Compound;
            if (compound == null)
                return;
            if (ReferenceEquals(compound.Body, body))
            {
                compound.Body = null;
                compound.Colliders.Clear();
            }
            body.Compound = null;
        }

        // Child 0's pose, for BS_RETURN_ROOT_COMPOUND_SHAPE (H1): kept from the compound on the body, and none when the
        // body's shape is not a compound (or is an empty one).
        private static void NoteRootChild(BulletBodyRapier body, CompoundState compound)
        {
            if (compound != null && compound.Children.Count > 0)
            {
                body.HasRootChild = true;
                body.RootChildPos = compound.Children[0].pos;
                body.RootChildRot = compound.Children[0].rot;
            }
            else
            {
                body.HasRootChild = false;
                body.RootChildPos = Vector3.Zero;
                body.RootChildRot = Quaternion.Identity;
            }
        }

        // A body whose shape changed must look at its contacts again.
        private static void WakeAfterShapeChange(BulletBodyRapier body)
        {
            if (body.HasPhysicalBody && body.Reporting && RapierNative.r3RigidBody_BodyType(body.Body) == RapierNative.BodyDynamic)
                RapierNative.Check(RapierNative.r3RigidBody_WakeUp(body.Body, 1));
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
            DetachCompound(body);
            body.State.ForgetBody(body);
            // r3RemoveRigidBody takes the body's joints with it.
            body.KillJoints();
            // The C# side has already forgotten the body, which is right whatever Rapier says; a failure is only logged.
            if (RapierNative.r3RemoveRigidBody(body.Body, 1) != 0)
                PhysicsScene.Logger.WarnFormat("{0} Could not remove the body of {1} from Rapier: {2}", LogHeader, body.ID,
                            RapierNative.LastError());
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
            body.State.CollidersStale = true;
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
            body.State.CollidersStale = true;
            body.InWorld = false;
            body.State.ForgetUpdates(body.ID);
            DropAppliedForces(body);
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

        // The getters never ask Rapier about a body that has been destroyed, and a read Rapier refuses (a stale handle)
        // is not taken as a value: either way the answer is the last value read or set (the creation pose, zero
        // velocities, until there is one). BulletS writes these back to the prim, so zeros would move it to the origin.
        public override Vector3 GetPosition(BulletBody obj)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (!body.HasPhysicalBody)
                return body.KnownPosition;
            RapierNative.Vector value = RapierNative.r3RigidBody_Translation(body.Body);
            if (RapierNative.r3LastStatus() == 0)
                body.KnownPosition = ToVector3(value);
            return body.KnownPosition;
        }

        public override Quaternion GetOrientation(BulletBody obj)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (!body.HasPhysicalBody)
                return body.KnownRotation;
            RapierNative.Rotation value = RapierNative.r3RigidBody_Rotation(body.Body);
            if (RapierNative.r3LastStatus() == 0)
                body.KnownRotation = ToQuaternion(value);
            return body.KnownRotation;
        }

        public override void SetTranslation(BulletBody obj, Vector3 position, Quaternion rotation)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            RapierNative.Pose pose = new RapierNative.Pose { translation = ToNative(position), rotation = ToNative(rotation) };
            RapierNative.Check(RapierNative.r3RigidBody_SetPosition(body.Body, pose, 1));
            body.State.CollidersStale = true;
            body.KnownPosition = position;
            body.KnownRotation = rotation;
        }

        public override Vector3 GetLinearVelocity(BulletBody obj)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (!body.HasPhysicalBody)
                return body.KnownVelocity;
            RapierNative.Vector value = RapierNative.r3RigidBody_Linvel(body.Body);
            if (RapierNative.r3LastStatus() == 0)
                body.KnownVelocity = ToVector3(value);
            return body.KnownVelocity;
        }

        public override Vector3 GetAngularVelocity(BulletBody obj)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (!body.HasPhysicalBody)
                return body.KnownAngularVelocity;
            RapierNative.Vector value = RapierNative.r3RigidBody_Angvel(body.Body);
            if (RapierNative.r3LastStatus() == 0)
                body.KnownAngularVelocity = ToVector3(value);
            return body.KnownAngularVelocity;
        }

        public override void SetLinearVelocity(BulletBody obj, Vector3 val)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            RapierNative.Check(RapierNative.r3RigidBody_SetLinvel(body.Body, ToNative(val), 1));
            body.KnownVelocity = val;
        }

        public override void SetAngularVelocity(BulletBody obj, Vector3 angularVelocity)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            RapierNative.Check(RapierNative.r3RigidBody_SetAngvel(body.Body, ToNative(angularVelocity), 1));
            body.KnownAngularVelocity = angularVelocity;
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
            body.Mass = Math.Max(mass, 0f);
            body.Inertia = new Vector3(Math.Max(inertia.X, 0f), Math.Max(inertia.Y, 0f), Math.Max(inertia.Z, 0f));
            ApplyMassProperties(body);
        }

        // Give Rapier the mass, inertia, centre of mass and inertia frame the body was last told (map rows 102, 105, 111).
        private static void ApplyMassProperties(BulletBodyRapier body)
        {
            if (!body.HasPhysicalBody)
                return;
            RapierNative.MassProperties props = new RapierNative.MassProperties
            {
                localCom = ToNative(body.CenterOfMass),
                mass = body.Mass,
                principalInertia = ToNative(body.Inertia),
                principalInertiaLocalFrame = ToNative(body.InertiaFrame)
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
            body.Gravity = val;
            float worldZ = body.State.Gravity.Z;
            float scale = (worldZ == 0f) ? 0f : val.Z / worldZ;
            RapierNative.Check(RapierNative.r3RigidBody_SetGravityScale(body.Body, scale, 0));
        }

        // ---- Materials and damping ----

        public override void SetFriction(BulletBody obj, float val)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            body.Friction = val;
            foreach (RapierNative.ColliderHandle collider in body.Colliders)
                RapierNative.Check(RapierNative.r3Collider_SetFriction(collider, val));
        }

        public override void SetRestitution(BulletBody obj, float val)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            body.Restitution = val;
            foreach (RapierNative.ColliderHandle collider in body.Colliders)
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
            SetLinearDamping(obj, lin_damping);
            SetAngularDamping(obj, ang_damping);
        }

        // The inverse of DampingRate: the Bullet damping that a Rapier rate stands for.
        private static float BulletDamping(float rate)
        {
            if (rate <= 0f)
                return 0f;
            if (rate >= StopDampingRate)
                return 1f;
            return 1f - (float)Math.Exp(-rate);
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

        // Replace the body's colliders with the shape's (map row 59): BSLinksetCompound puts the linkset's compound on
        // the root this way, between RemoveObjectFromWorld and AddObjectToWorld, and the body (its handle, pose, velocity
        // and mass) stays. The old colliders leave every table; the new ones get what the body was told (groups, no
        // contact response, friction, restitution, collision events, sensor). A null shape is BulletS clearing a
        // body's shape before destroying it so Bullet does not free a shape other bodies share; a Rapier body never
        // frees BulletS's shapes, so there is nothing to do.
        public override void SetCollisionShape(BulletWorld sim, BulletBody obj, BulletShape shape)
        {
            if (shape == null)
                return;
            BulletBodyRapier body = (BulletBodyRapier)obj;
            BulletShapeRapier newShape = (BulletShapeRapier)shape;
            if (!body.HasPhysicalBody)
                return;
            if (!newShape.HasPhysicalShape)
                throw new InvalidOperationException("Rapier: SetCollisionShape was given a shape that has been deleted");

            DetachCompound(body);
            foreach (RapierNative.ColliderHandle collider in body.Colliders.ToArray())
                RemoveBodyCollider(body, collider);
            AttachShape(body, newShape);
            WakeAfterShapeChange(body);
        }

        // Bullet's CCD bookkeeping with no Rapier counterpart (map rows 84 and 85): the value is kept for the getter.
        public override void SetHitFraction(BulletBody obj, float val)
        {
            ((BulletBodyRapier)obj).HitFraction = val;
        }

        // A gap (map row 112): rapier.h has no sleep-threshold setting, so Rapier's own sleep rules apply. The values are
        // kept for GetLinearSleepingThreshold.
        public override void SetSleepingThresholds(BulletBody obj, float lin_threshold, float ang_threshold)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            body.LinearSleepingThreshold = lin_threshold;
            body.AngularSleepingThreshold = ang_threshold;
            LogOnce(body, "sleep-thresholds", "SetSleepingThresholds is ignored: Rapier has no sleep thresholds to set "
                        + "(map row 112); its own sleep rules apply");
        }

        private static bool LiveBody(BulletBody obj)
        {
            return obj.HasPhysicalBody;
        }

        private static float Reciprocal(float value)
        {
            return value > 0f ? 1f / value : 0f;
        }

        public override BulletShape GetCollisionShape(BulletBody obj)
        {
            BulletShapeRapier shape = ((BulletBodyRapier)obj).Shape;
            return shape ?? new BulletShapeRapier(IntPtr.Zero, BSPhysicsShapeType.SHAPE_UNKNOWN, Vector3.One);
        }

        public override bool IsStaticObject(BulletBody obj)
        {
            return LiveBody(obj) && RapierNative.r3RigidBody_IsFixed(((BulletBodyRapier)obj).Body) != 0;
        }

        public override bool IsKinematicObject(BulletBody obj)
        {
            return LiveBody(obj) && RapierNative.r3RigidBody_IsKinematic(((BulletBodyRapier)obj).Body) != 0;
        }

        public override bool IsStaticOrKinematicObject(BulletBody obj)
        {
            return LiveBody(obj) && (RapierNative.r3RigidBody_IsFixed(((BulletBodyRapier)obj).Body) != 0
                        || RapierNative.r3RigidBody_IsKinematic(((BulletBodyRapier)obj).Body) != 0);
        }

        public override float GetRestitution(BulletBody obj)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (body.Colliders.Count > 0)
                return RapierNative.r3Collider_Restitution(body.Colliders[0]);
            return body.Restitution ?? RapierNative.r3DefaultColliderDesc().restitution;
        }

        public override float GetFriction(BulletBody obj)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (body.Colliders.Count > 0)
                return RapierNative.r3Collider_Friction(body.Colliders[0]);
            return body.Friction ?? RapierNative.r3DefaultColliderDesc().friction;
        }

        public override Vector3 GetAnisotripicFriction(BulletConstraint constrain)
        {
            return ((BulletConstraintRapier)constrain).AnisotropicFriction;
        }

        public override Vector3 SetAnisotripicFriction(BulletConstraint constrain, Vector3 frict)
        {
            // A gap (map row 76): Rapier's friction is one coefficient per collider. The call is logged once and changes nothing.
            BulletConstraintRapier constraint = (BulletConstraintRapier)constrain;
            LogOnce(constraint.State, "anisotropic-friction", "SetAnisotripicFriction is ignored: Rapier's friction is isotropic (map row 76)");
            return constraint.AnisotropicFriction;
        }

        public override bool HasAnisotripicFriction(BulletConstraint constrain)
        {
            return false;
        }

        public override void SetInterpolationLinearVelocity(BulletBody obj, Vector3 vel)
        {
            // Rapier keeps no interpolation state (map row 81).
        }

        public override void SetInterpolationVelocity(BulletBody obj, Vector3 linearVel, Vector3 angularVel)
        {
            // Rapier keeps no interpolation state (map row 83).
        }

        public override float GetHitFraction(BulletBody obj)
        {
            return ((BulletBodyRapier)obj).HitFraction;
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
            // The low half of the user data is the localID; the pointer lives in the high half.
            return new IntPtr(unchecked((long)RapierNative.r3RigidBody_UserData(((BulletBodyRapier)obj).Body).high));
        }

        public override void SetUserPointer(BulletBody obj, IntPtr val)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            RapierNative.UserData data = RapierNative.r3RigidBody_UserData(body.Body);
            data.high = unchecked((ulong)val.ToInt64());
            RapierNative.Check(RapierNative.r3RigidBody_SetUserData(body.Body, data));
        }

        public override void ApplyGravity(BulletBody obj)
        {
            // Bullet adds gravity * mass as a force (map row 92). Like any force it stays until the step's reset.
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (!body.HasPhysicalBody)
                return;
            Vector3 force = GetGravity(obj) * RapierNative.r3RigidBody_Mass(body.Body);
            RapierNative.Check(RapierNative.r3RigidBody_AddForce(body.Body, ToNative(force), force != Vector3.Zero ? 1u : 0u));
            body.State.ForcedBodies.Add(body);
        }

        public override Vector3 GetGravity(BulletBody obj)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            return body.Gravity ?? body.State.Gravity;
        }

        public override void SetLinearDamping(BulletBody obj, float lin_damping)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            RapierNative.Check(RapierNative.r3RigidBody_SetLinearDamping(body.Body, DampingRate(lin_damping)));
        }

        public override void SetAngularDamping(BulletBody obj, float ang_damping)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            RapierNative.Check(RapierNative.r3RigidBody_SetAngularDamping(body.Body, DampingRate(ang_damping)));
        }

        public override float GetLinearDamping(BulletBody obj)
        {
            return BulletDamping(RapierNative.r3RigidBody_LinearDamping(((BulletBodyRapier)obj).Body));
        }

        public override float GetAngularDamping(BulletBody obj)
        {
            return BulletDamping(RapierNative.r3RigidBody_AngularDamping(((BulletBodyRapier)obj).Body));
        }

        public override float GetLinearSleepingThreshold(BulletBody obj)
        {
            return ((BulletBodyRapier)obj).LinearSleepingThreshold;
        }

        public override void ApplyDamping(BulletBody obj, float timeStep)
        {
            // btRigidBody::applyDamping: the velocities lose (1 - damping)^timeStep.
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (!body.HasPhysicalBody)
                return;
            float linear = MathF.Pow(1f - GetLinearDamping(obj), timeStep);
            float angular = MathF.Pow(1f - GetAngularDamping(obj), timeStep);
            RapierNative.Check(RapierNative.r3RigidBody_SetLinvel(body.Body, ToNative(GetLinearVelocity(obj) * linear), 0));
            RapierNative.Check(RapierNative.r3RigidBody_SetAngvel(body.Body, ToNative(GetAngularVelocity(obj) * angular), 0));
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
            // The position becomes the local centre of mass of the additional mass properties and the rotation their inertia
            // frame (map row 105); the colliders stay where they are in the body.
            BulletBodyRapier body = (BulletBodyRapier)obj;
            body.CenterOfMass = pos;
            body.InertiaFrame = rot;
            ApplyMassProperties(body);
            body.State.CollidersStale = true;
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

        // A body that stops being stepped (RemoveObjectFromWorld, DISABLE_SIMULATION) before PhysicsStep has reset the
        // forces added to it this frame loses them now. Otherwise they would stay on the native body, which PhysicsStep no
        // longer resets, and push it for good once it is back (Bullet drops them at the end of the step either way).
        private static void DropAppliedForces(BulletBodyRapier body)
        {
            if (!body.State.ForcedBodies.Remove(body) || !body.HasPhysicalBody)
                return;
            RapierNative.Check(RapierNative.r3RigidBody_ResetForces(body.Body, 0));
            RapierNative.Check(RapierNative.r3RigidBody_ResetTorques(body.Body, 0));
        }

        // Replace the force of this step with this one (map row 107). The torque stays.
        public override void SetObjectForce(BulletBody obj, Vector3 force)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (!body.HasPhysicalBody)
                return;
            RapierNative.Check(RapierNative.r3RigidBody_ResetForces(body.Body, 0));
            RapierNative.Check(RapierNative.r3RigidBody_AddForce(body.Body, ToNative(force), force != Vector3.Zero ? 1u : 0u));
            body.State.ForcedBodies.Add(body);
        }

        // What has been added since the last step, in world space (map row 108).
        public override Vector3 GetTotalForce(BulletBody obj)
        {
            return ToVector3(RapierNative.r3RigidBody_UserForce(((BulletBodyRapier)obj).Body));
        }

        public override Vector3 GetTotalTorque(BulletBody obj)
        {
            return ToVector3(RapierNative.r3RigidBody_UserTorque(((BulletBodyRapier)obj).Body));
        }

        public override Vector3 GetInvInertiaDiagLocal(BulletBody obj)
        {
            Vector3 inertia = ((BulletBodyRapier)obj).Inertia;
            return new Vector3(Reciprocal(inertia.X), Reciprocal(inertia.Y), Reciprocal(inertia.Z));
        }

        public override void SetInvInertiaDiagLocal(BulletBody obj, Vector3 inert)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            body.Inertia = new Vector3(Reciprocal(inert.X), Reciprocal(inert.Y), Reciprocal(inert.Z));
            ApplyMassProperties(body);
        }

        public override void ApplyTorque(BulletBody obj, Vector3 torque)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (!body.HasPhysicalBody)
                return;
            RapierNative.Check(RapierNative.r3RigidBody_AddTorque(body.Body, ToNative(torque), torque != Vector3.Zero ? 1u : 0u));
            body.State.ForcedBodies.Add(body);
        }

        // Bullet's point (btRigidBody::applyForce's rel_pos) is the offset from the centre of mass, in world axes; Rapier
        // wants the world point (map row 114).
        public override void ApplyForce(BulletBody obj, Vector3 force, Vector3 pos)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (!body.HasPhysicalBody)
                return;
            RapierNative.Vector point = ToNative(ToVector3(RapierNative.r3RigidBody_CenterOfMass(body.Body)) + pos);
            RapierNative.Check(RapierNative.r3RigidBody_AddForceAtPoint(body.Body, ToNative(force), point, force != Vector3.Zero ? 1u : 0u));
            body.State.ForcedBodies.Add(body);
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

        // As ApplyForce, the point is relative to the centre of mass (map row 117).
        public override void ApplyImpulse(BulletBody obj, Vector3 imp, Vector3 pos)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (!body.HasPhysicalBody)
                return;
            RapierNative.Vector point = ToNative(ToVector3(RapierNative.r3RigidBody_CenterOfMass(body.Body)) + pos);
            RapierNative.Check(RapierNative.r3RigidBody_ApplyImpulseAtPoint(body.Body, ToNative(imp), point, imp != Vector3.Zero ? 1u : 0u));
        }

        // Bullet's clearForces: the force and torque of this step go (map row 118); the velocities stay.
        public override void ClearForces(BulletBody obj)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (!body.HasPhysicalBody)
                return;
            RapierNative.Check(RapierNative.r3RigidBody_ResetForces(body.Body, 0));
            RapierNative.Check(RapierNative.r3RigidBody_ResetTorques(body.Body, 0));
            body.State.ForcedBodies.Remove(body);
        }

        public override Vector3 GetVelocityInLocalPoint(BulletBody obj, Vector3 pos)
        {
            // As ApplyForce, the point is relative to the centre of mass (map row 125).
            BulletBodyRapier body = (BulletBodyRapier)obj;
            RapierNative.Vector point = ToNative(ToVector3(RapierNative.r3RigidBody_CenterOfMass(body.Body)) + pos);
            return ToVector3(RapierNative.r3RigidBody_VelocityAtPoint(body.Body, point));
        }

        public override void Translate(BulletBody obj, Vector3 trans)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (body.HasPhysicalBody)
            {
                RapierNative.Check(RapierNative.r3RigidBody_SetTranslation(body.Body, ToNative(BodyPosition(body) + trans), 1));
                body.State.CollidersStale = true;
            }
        }

        public override void UpdateDeactivation(BulletBody obj, float timeStep)
        {
            // Rapier runs its own sleep logic (map row 127).
        }

        public override bool WantsSleeping(BulletBody obj)
        {
            return LiveBody(obj) && RapierNative.r3RigidBody_IsSleeping(((BulletBodyRapier)obj).Body) != 0;
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
            List<BulletConstraint> refs = ((BulletBodyRapier)obj).ConstraintRefs;
            if (!refs.Contains(constrain))
                refs.Add(constrain);
        }

        public override void RemoveConstraintRef(BulletBody obj, BulletConstraint constrain)
        {
            ((BulletBodyRapier)obj).ConstraintRefs.Remove(constrain);
        }

        public override BulletConstraint GetConstraintRef(BulletBody obj, int index)
        {
            BulletBodyRapier body = (BulletBodyRapier)obj;
            if (index >= 0 && index < body.ConstraintRefs.Count)
                return body.ConstraintRefs[index];
            // Out of range: a constraint with nothing behind it, as BulletS expects for a missing one.
            return new BulletConstraintRapier(body.State, null, null);
        }

        public override int GetNumConstraintRefs(BulletBody obj)
        {
            return ((BulletBodyRapier)obj).ConstraintRefs.Count;
        }

        public override bool ClearCollisionProxyCache(BulletWorld world, BulletBody obj)
        {
            // Rapier has no pair cache to clear (map row 15).
            return true;
        }
    }
}
