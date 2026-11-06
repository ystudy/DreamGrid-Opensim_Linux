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
        public override BulletConstraint Create6DofConstraint(
            BulletWorld world,
            BulletBody obj1,
            BulletBody obj2,
            Vector3 frame1loc,
            Quaternion frame1rot,
            Vector3 frame2loc,
            Quaternion frame2rot,
            bool useLinearReferenceFrameA,
            bool disableCollisionsBetweenLinkedBodies)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(Create6DofConstraint));
        }

        public override BulletConstraint Create6DofConstraintToPoint(
            BulletWorld world,
            BulletBody obj1,
            BulletBody obj2,
            Vector3 joinPoint,
            bool useLinearReferenceFrameA,
            bool disableCollisionsBetweenLinkedBodies)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(Create6DofConstraintToPoint));
        }

        public override BulletConstraint Create6DofConstraintFixed(
            BulletWorld world,
            BulletBody obj1,
            Vector3 frameInBloc,
            Quaternion frameInBrot,
            bool useLinearReferenceFrameB,
            bool disableCollisionsBetweenLinkedBodies)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(Create6DofConstraintFixed));
        }

        public override BulletConstraint Create6DofSpringConstraint(
            BulletWorld world,
            BulletBody obj1,
            BulletBody obj2,
            Vector3 frame1loc,
            Quaternion frame1rot,
            Vector3 frame2loc,
            Quaternion frame2rot,
            bool useLinearReferenceFrameA,
            bool disableCollisionsBetweenLinkedBodies)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(Create6DofSpringConstraint));
        }

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

        public override void SetConstraintEnable(BulletConstraint constrain, float numericTrueFalse)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetConstraintEnable));
        }

        public override void SetConstraintNumSolverIterations(BulletConstraint constrain, float iterations)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetConstraintNumSolverIterations));
        }

        public override bool SetFrames(
            BulletConstraint constrain,
            Vector3 frameA,
            Quaternion frameArot,
            Vector3 frameB,
            Quaternion frameBrot)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetFrames));
        }

        public override bool SetLinearLimits(BulletConstraint constrain, Vector3 low, Vector3 hi)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetLinearLimits));
        }

        public override bool SetAngularLimits(BulletConstraint constrain, Vector3 low, Vector3 hi)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetAngularLimits));
        }

        public override bool UseFrameOffset(BulletConstraint constrain, float enable)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(UseFrameOffset));
        }

        public override bool TranslationalLimitMotor(
            BulletConstraint constrain,
            float enable,
            float targetVel,
            float maxMotorForce)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(TranslationalLimitMotor));
        }

        public override bool SetBreakingImpulseThreshold(BulletConstraint constrain, float threshold)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetBreakingImpulseThreshold));
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

        public override bool SpringEnable(BulletConstraint constrain, int index, float numericTrueFalse)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SpringEnable));
        }

        public override bool SpringSetEquilibriumPoint(BulletConstraint constrain, int index, float equilibriumPoint)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SpringSetEquilibriumPoint));
        }

        public override bool SpringSetStiffness(BulletConstraint constrain, int index, float stiffnesss)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SpringSetStiffness));
        }

        public override bool SpringSetDamping(BulletConstraint constrain, int index, float damping)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SpringSetDamping));
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

        public override bool CalculateTransforms(BulletConstraint constrain)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(CalculateTransforms));
        }

        public override bool SetConstraintParam(
            BulletConstraint constrain,
            ConstraintParams paramIndex,
            float value,
            ConstraintParamAxis axis)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetConstraintParam));
        }

        public override bool DestroyConstraint(BulletWorld world, BulletConstraint constrain)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(DestroyConstraint));
        }

        public override bool AddConstraintToWorld(
            BulletWorld world,
            BulletConstraint constrain,
            bool disableCollisionsBetweenLinkedObjects)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(AddConstraintToWorld));
        }

        public override bool RemoveConstraintFromWorld(BulletWorld world, BulletConstraint constrain)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(RemoveConstraintFromWorld));
        }
    }
}
