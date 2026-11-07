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
    /// 6-DOF constraints, axis locks and springs (H5b). A Bullet btGeneric6DofConstraint is one Rapier impulse joint
    /// (a generic joint). BulletS gives its limits per axis in several calls, so the constraint wrapper keeps Bullet's
    /// state and every change rebuilds the joint's whole description from it (RebuildAxes) and gives that to Rapier.
    ///
    /// Frames. Bullet's frame1 and frame2 are the joint frame in bodies A and B's local space, which is what Rapier's
    /// localFrame1 and localFrame2 are. Limits are the displacement of frame B from frame A along frame A's axes, as in
    /// Bullet. BulletS's Create6DofConstraintToPoint gives frames with no rotation, at the join point, in each body's
    /// own space. A constraint 'to the world' (Create6DofConstraintFixed) is Bullet's btGeneric6DofConstraint(rbB,
    /// frameInB): frame A is the world-space pose of frame B at creation, frame B is the one given, so limits are
    /// relative to where the body was when the constraint was made. Body A is the state's world anchor.
    /// </summary>
    public sealed partial class BSAPIRapier
    {
        // Bullet's wide 'free' ranges, set by BSPrim.ApplyAxisLimits to unlock an axis: linearMax 23000 and pi. A range at
        // least this wide is a free axis here, not a limit that is never reached.
        private const float FreeLinearRange = 22999f;
        private const float FreeAngularRange = 3.14f;

        // ---- Creation ----

        public override BulletConstraint Create6DofConstraint(
            BulletWorld pWorld,
            BulletBody obj1,
            BulletBody obj2,
            Vector3 frame1loc,
            Quaternion frame1rot,
            Vector3 frame2loc,
            Quaternion frame2rot,
            bool useLinearReferenceFrameA,
            bool disableCollisionsBetweenLinkedBodies)
        {
            // useLinearReferenceFrameA only chooses the frame the linear limits are read in; Rapier reads them in frame 1.
            return CreateJoint((BulletWorldRapier)pWorld, (BulletBodyRapier)obj1, (BulletBodyRapier)obj2,
                        MakePose(frame1loc, frame1rot), MakePose(frame2loc, frame2rot), disableCollisionsBetweenLinkedBodies);
        }

        // Bullet's version: both frames are at the join point, and have no rotation in their own body's space.
        public override BulletConstraint Create6DofConstraintToPoint(
            BulletWorld pWorld,
            BulletBody obj1,
            BulletBody obj2,
            Vector3 joinPoint,
            bool useLinearReferenceFrameA,
            bool disableCollisionsBetweenLinkedBodies)
        {
            BulletBodyRapier body1 = (BulletBodyRapier)obj1;
            BulletBodyRapier body2 = (BulletBodyRapier)obj2;
            if (!body1.HasPhysicalBody || !body2.HasPhysicalBody)
                return CreateJoint((BulletWorldRapier)pWorld, body1, body2, default(RapierNative.Pose), default(RapierNative.Pose), false);
            Vector3 local1 = Rotate(Conjugate(BodyRotation(body1)), joinPoint - BodyPosition(body1));
            Vector3 local2 = Rotate(Conjugate(BodyRotation(body2)), joinPoint - BodyPosition(body2));
            return CreateJoint((BulletWorldRapier)pWorld, body1, body2,
                        MakePose(local1, Quaternion.Identity), MakePose(local2, Quaternion.Identity), disableCollisionsBetweenLinkedBodies);
        }

        // One body to the world, as BSActorLockAxis does for axis locks.
        public override BulletConstraint Create6DofConstraintFixed(
            BulletWorld pWorld,
            BulletBody obj1,
            Vector3 frameInBloc,
            Quaternion frameInBrot,
            bool useLinearReferenceFrameB,
            bool disableCollisionsBetweenLinkedBodies)
        {
            BulletWorldRapier world = (BulletWorldRapier)pWorld;
            BulletBodyRapier body = (BulletBodyRapier)obj1;
            if (!body.HasPhysicalBody)
                return CreateJoint(world, null, body, default(RapierNative.Pose), default(RapierNative.Pose), false);
            // Frame A (the world) is frame B as it is now, in world space: the anchor has no pose of its own.
            Vector3 position = BodyPosition(body);
            Quaternion rotation = BodyRotation(body);
            RapierNative.Pose inWorld = MakePose(position + Rotate(rotation, frameInBloc), Multiply(rotation, frameInBrot));
            return CreateJoint(world, null, body, inWorld, MakePose(frameInBloc, frameInBrot), disableCollisionsBetweenLinkedBodies);
        }

        // The spring is the same joint: its springs are per-axis motors, switched on by SpringEnable.
        public override BulletConstraint Create6DofSpringConstraint(
            BulletWorld pWorld,
            BulletBody obj1,
            BulletBody obj2,
            Vector3 frame1loc,
            Quaternion frame1rot,
            Vector3 frame2loc,
            Quaternion frame2rot,
            bool useLinearReferenceFrameA,
            bool disableCollisionsBetweenLinkedBodies)
        {
            return CreateJoint((BulletWorldRapier)pWorld, (BulletBodyRapier)obj1, (BulletBodyRapier)obj2,
                        MakePose(frame1loc, frame1rot), MakePose(frame2loc, frame2rot), disableCollisionsBetweenLinkedBodies);
        }

        // Body1 null means the world anchor. A constraint with a body that is gone comes back dead, as BulletS expects
        // (HasPhysicalConstraint is false).
        private static BulletConstraintRapier CreateJoint(BulletWorldRapier world, BulletBodyRapier body1, BulletBodyRapier body2,
                        RapierNative.Pose frame1, RapierNative.Pose frame2, bool disableCollisions)
        {
            BulletConstraintRapier constraint = new BulletConstraintRapier(world.State, body1, body2);
            if ((body1 != null && !body1.HasPhysicalBody) || !body2.HasPhysicalBody)
                return constraint;

            RapierNative.RigidBodyHandle handle1 = (body1 != null) ? body1.Body : EnsureWorldAnchor(world);
            RapierNative.JointDesc desc = RapierNative.r3DefaultJointDesc();
            desc.localFrame1 = frame1;
            desc.localFrame2 = frame2;
            desc.contactsEnabled = disableCollisions ? 0u : 1u;
            desc.enabled = 1;
            constraint.Desc = desc;
            RebuildAxes(constraint);

            RapierNative.ImpulseJointHandle joint = RapierNative.r3InsertImpulseJoint(handle1, body2.Body, in constraint.Desc);
            RapierNative.Check(RapierNative.r3LastStatus());
            constraint.Activate(joint);
            return constraint;
        }

        // The fixed body constraints to the world are joined to. No collider, so nothing can touch it, and fixed, so it is
        // never in the active list that updates are read from.
        private static RapierNative.RigidBodyHandle EnsureWorldAnchor(BulletWorldRapier world)
        {
            RapierWorldState state = world.State;
            if (!state.HasWorldAnchor)
            {
                RapierNative.RigidBodyDesc desc = RapierNative.r3FixedRigidBodyDesc();
                desc.position.rotation = ToNative(Quaternion.Identity);
                desc.userData.low = uint.MaxValue;
                desc.enabled = 1;
                state.WorldAnchor = RapierNative.r3InsertRigidBody(world.World, in desc);
                RapierNative.Check(RapierNative.r3LastStatus());
                state.HasWorldAnchor = true;
            }
            return state.WorldAnchor;
        }

        // ---- Removing, enabling ----

        // Rapier removes the joint with either body, so BulletS may hold a constraint that is already dead.
        public override bool DestroyConstraint(BulletWorld pWorld, BulletConstraint constrain)
        {
            BulletConstraintRapier constraint = (BulletConstraintRapier)constrain;
            if (!constraint.HasPhysicalConstraint)
                return true;
            RapierNative.Check(RapierNative.r3RemoveImpulseJoint(constraint.Joint, 1));
            constraint.MarkDead();
            return true;
        }

        public override void SetConstraintEnable(BulletConstraint constrain, float numericTrueFalse)
        {
            SetEnabled((BulletConstraintRapier)constrain, numericTrueFalse != 0f);
        }

        // Joints are in the world from creation; adding and removing one switches it on and off.
        public override bool AddConstraintToWorld(
            BulletWorld world,
            BulletConstraint constrain,
            bool disableCollisionsBetweenLinkedObjects)
        {
            return SetEnabled((BulletConstraintRapier)constrain, true);
        }

        public override bool RemoveConstraintFromWorld(BulletWorld world, BulletConstraint constrain)
        {
            return SetEnabled((BulletConstraintRapier)constrain, false);
        }

        private static bool SetEnabled(BulletConstraintRapier constraint, bool enabled)
        {
            if (!constraint.HasPhysicalConstraint)
                return false;
            constraint.Desc.enabled = enabled ? 1u : 0u;
            // A joint BulletS switches on again is a fresh one: it has not broken.
            if (enabled)
                constraint.Broken = false;
            RapierNative.Check(RapierNative.r3ImpulseJoint_SetEnabled(constraint.Joint, constraint.Desc.enabled, 1));
            UpdateBreakable(constraint);
            return true;
        }

        // Rapier has no per-joint count: the iterations are the bodies' extra ones on top of the world's.
        public override void SetConstraintNumSolverIterations(BulletConstraint constrain, float iterations)
        {
            BulletConstraintRapier constraint = (BulletConstraintRapier)constrain;
            if (!constraint.HasPhysicalConstraint)
                return;
            int extra = Math.Max(0, (int)iterations - Math.Max(1, (int)BSParam.RapierSolverIterations));
            foreach (BulletBodyRapier body in new[] { constraint.Body1, constraint.Body2 })
            {
                if (body != null && body.HasPhysicalBody)
                    RapierNative.Check(RapierNative.r3RigidBody_SetAdditionalSolverIterations(body.Body, (UIntPtr)extra));
            }
        }

        // ---- Frames, limits, motors ----

        public override bool SetFrames(
            BulletConstraint constrain,
            Vector3 frameA,
            Quaternion frameArot,
            Vector3 frameB,
            Quaternion frameBrot)
        {
            BulletConstraintRapier constraint = (BulletConstraintRapier)constrain;
            if (!constraint.HasPhysicalConstraint)
                return false;
            constraint.Desc.localFrame1 = MakePose(frameA, frameArot);
            constraint.Desc.localFrame2 = MakePose(frameB, frameBrot);
            ApplyDesc(constraint);
            return true;
        }

        // Bullet's per-axis convention: low == high locks the axis, low < high limits it, low > high leaves it free.
        public override bool SetLinearLimits(BulletConstraint constrain, Vector3 low, Vector3 hi)
        {
            BulletConstraintRapier constraint = (BulletConstraintRapier)constrain;
            if (!constraint.HasPhysicalConstraint)
                return false;
            constraint.LinearLow = low;
            constraint.LinearHigh = hi;
            RebuildAxes(constraint);
            ApplyDesc(constraint);
            return true;
        }

        public override bool SetAngularLimits(BulletConstraint constrain, Vector3 low, Vector3 hi)
        {
            BulletConstraintRapier constraint = (BulletConstraintRapier)constrain;
            if (!constraint.HasPhysicalConstraint)
                return false;
            constraint.AngularLow = low;
            constraint.AngularHigh = hi;
            RebuildAxes(constraint);
            ApplyDesc(constraint);
            return true;
        }

        // A Bullet solver detail (whether the linear limits use the frame offset) that Rapier has no counterpart for.
        public override bool UseFrameOffset(BulletConstraint constrain, float enable)
        {
            return ((BulletConstraintRapier)constrain).HasPhysicalConstraint;
        }

        // BulletSim drives only the X axis of Bullet's translational limit motor.
        public override bool TranslationalLimitMotor(
            BulletConstraint constrain,
            float enable,
            float targetVel,
            float maxMotorForce)
        {
            BulletConstraintRapier constraint = (BulletConstraintRapier)constrain;
            if (!constraint.HasPhysicalConstraint)
                return false;
            constraint.TransMotor = enable != 0f;
            constraint.TransMotorVelocity = targetVel;
            constraint.TransMotorForce = Math.Max(0f, maxMotorForce);
            RebuildAxes(constraint);
            ApplyDesc(constraint);
            return true;
        }

        // Bullet breaks (disables) a constraint whose applied impulse is over the threshold. PhysicsStep checks the
        // joints with a finite one after each substep.
        public override bool SetBreakingImpulseThreshold(BulletConstraint constrain, float threshold)
        {
            BulletConstraintRapier constraint = (BulletConstraintRapier)constrain;
            if (!constraint.HasPhysicalConstraint)
                return false;
            constraint.BreakingThreshold = (float.IsNaN(threshold) || threshold >= 1e30f) ? float.PositiveInfinity : threshold;
            UpdateBreakable(constraint);
            return true;
        }

        // Bullet's btGeneric6DofConstraint has no computed state to refresh here: Rapier works out joint frames each step.
        public override bool CalculateTransforms(BulletConstraint constrain)
        {
            return ((BulletConstraintRapier)constrain).HasPhysicalConstraint;
        }

        // ---- Springs ----

        public override bool SpringEnable(BulletConstraint constrain, int index, float numericTrueFalse)
        {
            BulletConstraintRapier constraint = (BulletConstraintRapier)constrain;
            if (!constraint.HasPhysicalConstraint || !ValidAxis(index))
                return false;
            constraint.SpringOn[index] = numericTrueFalse != 0f;
            RebuildAxes(constraint);
            ApplyDesc(constraint);
            return true;
        }

        // The point the spring pulls to, as a displacement along the axis. Like Bullet's, index SPRING_NOT_SPECIFIED means
        // every axis and a point of SPRING_NOT_SPECIFIED means the axis as it is now.
        public override bool SpringSetEquilibriumPoint(BulletConstraint constrain, int index, float equilibriumPoint)
        {
            BulletConstraintRapier constraint = (BulletConstraintRapier)constrain;
            if (!constraint.HasPhysicalConstraint)
                return false;
            if (index == SPRING_NOT_SPECIFIED)
            {
                for (int axis = 0; axis < RapierNative.JointDof; axis++)
                    constraint.Equilibrium[axis] = CurrentAxisValue(constraint, axis);
            }
            else if (ValidAxis(index))
            {
                constraint.Equilibrium[index] = (equilibriumPoint == SPRING_NOT_SPECIFIED)
                            ? CurrentAxisValue(constraint, index) : equilibriumPoint;
            }
            else
            {
                return false;
            }
            RebuildAxes(constraint);
            ApplyDesc(constraint);
            return true;
        }

        public override bool SpringSetStiffness(BulletConstraint constrain, int index, float stiffnesss)
        {
            BulletConstraintRapier constraint = (BulletConstraintRapier)constrain;
            if (!constraint.HasPhysicalConstraint || !ValidAxis(index))
                return false;
            constraint.Stiffness[index] = Math.Max(0f, stiffnesss);
            RebuildAxes(constraint);
            ApplyDesc(constraint);
            return true;
        }

        public override bool SpringSetDamping(BulletConstraint constrain, int index, float damping)
        {
            BulletConstraintRapier constraint = (BulletConstraintRapier)constrain;
            if (!constraint.HasPhysicalConstraint || !ValidAxis(index))
                return false;
            constraint.Damping[index] = Math.Max(0f, damping);
            RebuildAxes(constraint);
            ApplyDesc(constraint);
            return true;
        }

        // ---- ERP and CFM ----

        // Bullet's error reduction and constraint force mixing are one soft constraint. At step length dt that is a spring
        // of stiffness k = erp / (cfm dt) and a damper c = (1 - erp) / cfm; Rapier's softness is a natural frequency and
        // a damping ratio, which need a mass: that of the two bodies together (m1 m2 / (m1 + m2), a body that cannot move
        // counting as infinite). So f = sqrt(k / m) / 2 pi and zeta = c / (2 sqrt(k m)), kept inside what the step length
        // can resolve. A CFM of zero is a rigid constraint: Rapier's default stiffness. The axis is ignored: BulletS sets
        // the same value on all of them, and the softness is the joint's, not an axis's. It is an approximation, not an
        // equivalence.
        public override bool SetConstraintParam(
            BulletConstraint constrain,
            ConstraintParams paramIndex,
            float value,
            ConstraintParamAxis axis)
        {
            BulletConstraintRapier constraint = (BulletConstraintRapier)constrain;
            if (!constraint.HasPhysicalConstraint)
                return false;
            switch (paramIndex)
            {
                case ConstraintParams.BT_CONSTRAINT_ERP:
                case ConstraintParams.BT_CONSTRAINT_STOP_ERP:
                    constraint.Erp = value;
                    break;
                case ConstraintParams.BT_CONSTRAINT_CFM:
                case ConstraintParams.BT_CONSTRAINT_STOP_CFM:
                    constraint.Cfm = value;
                    break;
                default:
                    return false;
            }
            ApplySoftness(constraint);
            ApplyDesc(constraint);
            return true;
        }

        private static void ApplySoftness(BulletConstraintRapier constraint)
        {
            float cfm = constraint.Cfm;
            if (!(cfm > 0f))
            {
                constraint.Desc.softness = RapierNative.r3DefaultJointDesc().softness;
                return;
            }
            float step = constraint.State.TimeStep > 0f ? constraint.State.TimeStep : 1f / 60f;
            float erp = Math.Min(Math.Max(constraint.Erp, 1e-4f), 1f);
            float stiffness = erp / (cfm * step);
            float damping = (1f - erp) / cfm;
            float mass = EffectiveMass(constraint);
            float frequency = MathF.Sqrt(stiffness / mass) / (2f * MathF.PI);
            float ratio = damping / (2f * MathF.Sqrt(stiffness * mass));
            constraint.Desc.softness = new RapierNative.SpringCoefficients
            {
                naturalFrequency = Math.Min(Math.Max(frequency, 0.5f), 0.25f / step),
                dampingRatio = Math.Min(Math.Max(ratio, 0.1f), 10f)
            };
        }

        private static float EffectiveMass(BulletConstraintRapier constraint)
        {
            float inverse = 0f;
            foreach (BulletBodyRapier body in new[] { constraint.Body1, constraint.Body2 })
            {
                if (body == null || !body.HasPhysicalBody || RapierNative.r3RigidBody_BodyType(body.Body) != RapierNative.BodyDynamic)
                    continue;
                float mass = RapierNative.r3RigidBody_Mass(body.Body);
                if (mass > 0f)
                    inverse += 1f / mass;
            }
            return inverse > 0f ? 1f / inverse : 1f;
        }

        // ---- Building the joint from Bullet's state ----

        private static bool ValidAxis(int index)
        {
            return index >= 0 && index < RapierNative.JointDof;
        }

        // Rapier takes the whole description at once, so a change rewrites it all.
        private static void ApplyDesc(BulletConstraintRapier constraint)
        {
            RapierNative.Check(RapierNative.r3ImpulseJoint_SetDesc(constraint.Joint, in constraint.Desc, 1));
        }

        // The lock, limit and motor masks and arrays of the description, from the limits, the translational motor and the
        // springs. A locked axis (low == high == 0) is a lock; low == high elsewhere is a limit of zero width; a range as
        // wide as BulletS's 'free' one, or low above high, is no constraint on the axis.
        private static void RebuildAxes(BulletConstraintRapier constraint)
        {
            RapierNative.JointDesc defaults = RapierNative.r3DefaultJointDesc();
            ref RapierNative.JointDesc desc = ref constraint.Desc;
            byte locked = 0, limited = 0, motored = 0;
            for (int axis = 0; axis < RapierNative.JointDof; axis++)
            {
                bool linear = axis < 3;
                float low = Component(linear ? constraint.LinearLow : constraint.AngularLow, axis % 3);
                float high = Component(linear ? constraint.LinearHigh : constraint.AngularHigh, axis % 3);
                float free = linear ? FreeLinearRange : FreeAngularRange;
                byte bit = (byte)(1 << axis);
                desc.limits[axis] = defaults.limits[axis];
                if (float.IsNaN(low) || float.IsNaN(high) || low > high || (low <= -free && high >= free))
                {
                    // Free.
                }
                else if (low == high && low == 0f)
                {
                    locked |= bit;
                }
                else
                {
                    limited |= bit;
                    desc.limits[axis] = new RapierNative.JointLimits { min = low, max = high };
                }

                desc.motors[axis] = defaults.motors[axis];
                if (constraint.SpringOn[axis])
                {
                    // Force-based, so the stiffness is a force per unit of displacement, as Bullet's is.
                    RapierNative.JointMotor motor = defaults.motors[axis];
                    motor.targetPos = constraint.Equilibrium[axis];
                    motor.targetVel = 0f;
                    motor.stiffness = constraint.Stiffness[axis];
                    motor.damping = constraint.Damping[axis];
                    motor.model = 1;
                    desc.motors[axis] = motor;
                    motored |= bit;
                }
                else if (axis == 0 && constraint.TransMotor)
                {
                    // A velocity motor that gets to the speed within a step and is limited by its force, as Bullet's.
                    RapierNative.JointMotor motor = defaults.motors[axis];
                    motor.targetPos = 0f;
                    motor.targetVel = constraint.TransMotorVelocity;
                    motor.stiffness = 0f;
                    motor.damping = 1e4f;
                    motor.maxForce = constraint.TransMotorForce;
                    motor.model = 1;
                    desc.motors[axis] = motor;
                    motored |= bit;
                }
            }
            desc.lockedAxes = locked;
            desc.limitAxes = limited;
            desc.motorAxes = motored;
        }

        // ---- Breaking ----

        // A joint is read for breaking while it is enabled, not broken, and has a finite threshold.
        private static void UpdateBreakable(BulletConstraintRapier constraint)
        {
            if (constraint.HasPhysicalConstraint && constraint.Desc.enabled != 0 && !constraint.Broken
                        && !float.IsPositiveInfinity(constraint.BreakingThreshold))
                constraint.State.Breakable.Add(constraint);
            else
                constraint.State.Breakable.Remove(constraint);
        }

        // After a substep: a joint whose last impulse is over its threshold is disabled, once. It stays, and can be read
        // or switched on again.
        private static void BreakOverstrainedJoints(RapierWorldState state)
        {
            if (state.Breakable.Count == 0)
                return;
            List<BulletConstraintRapier> broken = null;
            foreach (BulletConstraintRapier constraint in state.Breakable)
            {
                if (ImpulseMagnitude(RapierNative.r3ImpulseJoint_Impulses(constraint.Joint)) > constraint.BreakingThreshold)
                    (broken ??= new List<BulletConstraintRapier>()).Add(constraint);
            }
            if (broken == null)
                return;
            foreach (BulletConstraintRapier constraint in broken)
            {
                constraint.Broken = true;
                constraint.Desc.enabled = 0;
                RapierNative.Check(RapierNative.r3ImpulseJoint_SetEnabled(constraint.Joint, 0, 1));
                state.Breakable.Remove(constraint);
            }
        }

        // The biggest impulse the joint applied: its locked linear and angular parts together, or any one limit or motor.
        private static float ImpulseMagnitude(RapierNative.JointImpulses impulses)
        {
            float lockedLinear = impulses.linear.x * impulses.linear.x + impulses.linear.y * impulses.linear.y
                        + impulses.linear.z * impulses.linear.z;
            float lockedAngular = impulses.angular.x * impulses.angular.x + impulses.angular.y * impulses.angular.y
                        + impulses.angular.z * impulses.angular.z;
            float biggest = MathF.Sqrt(lockedLinear + lockedAngular);
            for (int axis = 0; axis < RapierNative.JointDof; axis++)
            {
                biggest = Math.Max(biggest, Math.Abs(impulses.limits[axis]));
                biggest = Math.Max(biggest, Math.Abs(impulses.motors[axis]));
            }
            return biggest;
        }

        // ---- Frames and poses ----

        private static RapierNative.Pose MakePose(Vector3 position, Quaternion rotation)
        {
            return new RapierNative.Pose { translation = ToNative(position), rotation = ToNative(rotation) };
        }

        private static Vector3 BodyPosition(BulletBodyRapier body)
        {
            return ToVector3(RapierNative.r3RigidBody_Translation(body.Body));
        }

        private static Quaternion BodyRotation(BulletBodyRapier body)
        {
            return ToQuaternion(RapierNative.r3RigidBody_Rotation(body.Body));
        }

        private static float Component(Vector3 v, int index)
        {
            return index == 0 ? v.X : (index == 1 ? v.Y : v.Z);
        }

        private static Quaternion Conjugate(Quaternion q)
        {
            return new Quaternion(-q.X, -q.Y, -q.Z, q.W);
        }

        // How far frame B is from frame A along an axis of frame A now: a distance for 0 to 2 and, for the angular axes, the
        // rotation about the axis taken as twice the vector part of the relative rotation (exact for small angles).
        private static float CurrentAxisValue(BulletConstraintRapier constraint, int axis)
        {
            if ((constraint.Body1 != null && !constraint.Body1.HasPhysicalBody) || !constraint.Body2.HasPhysicalBody)
                return 0f;
            Vector3 position1 = Vector3.Zero, position2 = BodyPosition(constraint.Body2);
            Quaternion rotation1 = Quaternion.Identity, rotation2 = BodyRotation(constraint.Body2);
            if (constraint.Body1 != null)
            {
                position1 = BodyPosition(constraint.Body1);
                rotation1 = BodyRotation(constraint.Body1);
            }
            RapierNative.Pose local1 = constraint.Desc.localFrame1, local2 = constraint.Desc.localFrame2;
            Quaternion frame1 = Multiply(rotation1, ToQuaternion(local1.rotation));
            Quaternion frame2 = Multiply(rotation2, ToQuaternion(local2.rotation));
            Vector3 at1 = position1 + Rotate(rotation1, ToVector3(local1.translation));
            Vector3 at2 = position2 + Rotate(rotation2, ToVector3(local2.translation));
            if (axis < 3)
                return Component(Rotate(Conjugate(frame1), at2 - at1), axis);
            Quaternion relative = Multiply(Conjugate(frame1), frame2);
            float sign = relative.W < 0f ? -2f : 2f;
            return sign * Component(new Vector3(relative.X, relative.Y, relative.Z), axis - 3);
        }

        // ---- Constraint types BulletS never creates ----

        public override BulletConstraint CreateHingeConstraint(
            BulletWorld world,
            BulletBody obj1,
            BulletBody obj2,
            Vector3 pivotinA,
            Vector3 pivotinB,
            Vector3 axisInA,
            Vector3 axisInB,
            bool useLinearReferenceFrameA,
            bool disableCollisionsBetweenLinkedBodies)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(CreateHingeConstraint));
        }

        public override BulletConstraint CreateSliderConstraint(
            BulletWorld world,
            BulletBody obj1,
            BulletBody obj2,
            Vector3 frameInAloc,
            Quaternion frameInArot,
            Vector3 frameInBloc,
            Quaternion frameInBrot,
            bool useLinearReferenceFrameA,
            bool disableCollisionsBetweenLinkedBodies)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(CreateSliderConstraint));
        }

        public override BulletConstraint CreateConeTwistConstraint(
            BulletWorld world,
            BulletBody obj1,
            BulletBody obj2,
            Vector3 frameInAloc,
            Quaternion frameInArot,
            Vector3 frameInBloc,
            Quaternion frameInBrot,
            bool disableCollisionsBetweenLinkedBodies)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(CreateConeTwistConstraint));
        }

        public override BulletConstraint CreateGearConstraint(
            BulletWorld world,
            BulletBody obj1,
            BulletBody obj2,
            Vector3 axisInA,
            Vector3 axisInB,
            float ratio,
            bool disableCollisionsBetweenLinkedBodies)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(CreateGearConstraint));
        }

        public override BulletConstraint CreatePoint2PointConstraint(
            BulletWorld world,
            BulletBody obj1,
            BulletBody obj2,
            Vector3 pivotInA,
            Vector3 pivotInB,
            bool disableCollisionsBetweenLinkedBodies)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(CreatePoint2PointConstraint));
        }

        public override bool HingeSetLimits(
            BulletConstraint constrain,
            float low,
            float high,
            float softness,
            float bias,
            float relaxation)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(HingeSetLimits));
        }

        public override bool SliderSetLimits(BulletConstraint constrain, int lowerUpper, int linAng, float val)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SliderSetLimits));
        }

        public override bool SliderSet(
            BulletConstraint constrain,
            int softRestDamp,
            int dirLimOrtho,
            int linAng,
            float val)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SliderSet));
        }

        public override bool SliderMotorEnable(BulletConstraint constrain, int linAng, float numericTrueFalse)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SliderMotorEnable));
        }

        public override bool SliderMotor(BulletConstraint constrain, int forceVel, int linAng, float val)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SliderMotor));
        }
    }
}
