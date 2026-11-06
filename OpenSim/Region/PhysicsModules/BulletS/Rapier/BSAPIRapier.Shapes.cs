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
        // A quarter turn about X, which takes Rapier's Y axis to BulletS's Z axis (H9).
        private static readonly Quaternion YAxisToZAxis = new Quaternion((float)Math.Sin(Math.PI / 4), 0f, 0f, (float)Math.Cos(Math.PI / 4));

        // Remember a native shape the engine built, so it is freed once: by DeleteCollisionShape or, if BulletS never
        // releases it, by Shutdown.
        private static BulletShapeRapier RegisterShape(BulletWorldRapier world, IntPtr native, BSPhysicsShapeType type, Vector3 scale)
        {
            if (native == IntPtr.Zero)
                RapierNative.Check(RapierNative.r3LastStatus());
            world.State.Shapes.Add(native);
            return new BulletShapeRapier(native, type, scale);
        }

        // Bullet builds unit shapes and scales them. Rapier's shapes cannot be scaled, so each one is built at its final
        // size (H9): a box is Scale/2 on each side, a sphere has radius Scale.X/2, a cylinder radius Scale.X/2 and
        // half height Scale.Z/2. A capsule fits the Scale box the same way, its radius Scale.X/2 and its length Scale.Z.
        // Rapier's cylinders and capsules run along Y, so they sit on the body turned a quarter about X.
        public override BulletShape BuildNativeShape(BulletWorld pWorld, ShapeData shapeData)
        {
            BulletWorldRapier world = (BulletWorldRapier)pWorld;
            Vector3 scale = shapeData.Scale;
            BulletShapeRapier shape;
            switch (shapeData.Type)
            {
                case BSPhysicsShapeType.SHAPE_BOX:
                    shape = RegisterShape(world, RapierNative.r3CuboidSharedShape(ToNative(scale * 0.5f)),
                                            shapeData.Type, scale);
                    break;
                case BSPhysicsShapeType.SHAPE_SPHERE:
                    shape = RegisterShape(world, RapierNative.r3BallSharedShape(scale.X * 0.5f), shapeData.Type, scale);
                    break;
                case BSPhysicsShapeType.SHAPE_CYLINDER:
                    shape = RegisterShape(world, RapierNative.r3CylinderSharedShape(scale.Z * 0.5f, scale.X * 0.5f),
                                            shapeData.Type, scale);
                    shape.LocalRot = YAxisToZAxis;
                    break;
                case BSPhysicsShapeType.SHAPE_CAPSULE:
                    shape = BuildZCapsule(world, scale.X * 0.5f, Math.Max(scale.Z - scale.X, 0f), scale);
                    break;
                default:
                    throw new NotImplementedException("BSAPIRapier." + nameof(BuildNativeShape) + " for " + shapeData.Type);
            }
            shape.isNativeShape = true;
            return shape;
        }

        // BulletS's capsule: a radius and the length of its cylinder part, along Z, both multiplied by the scale
        // (Bullet scales a Z capsule's radius by Scale.X and its half length by Scale.Z).
        public override BulletShape BuildCapsuleShape(BulletWorld pWorld, float radius, float height, Vector3 scale)
        {
            BulletShapeRapier shape = BuildZCapsule((BulletWorldRapier)pWorld, radius * scale.X, height * scale.Z, scale);
            shape.isNativeShape = true;
            return shape;
        }

        // A capsule along Z with this radius and cylinder length, already at its final size.
        private static BulletShapeRapier BuildZCapsule(BulletWorldRapier world, float radius, float cylinderLength, Vector3 scale)
        {
            RapierNative.Vector a = new RapierNative.Vector { x = 0f, y = 0f, z = -cylinderLength * 0.5f };
            RapierNative.Vector b = new RapierNative.Vector { x = 0f, y = 0f, z = cylinderLength * 0.5f };
            // The segment is given along Z, so the shape needs no turn.
            return RegisterShape(world, RapierNative.r3CapsuleSharedShape(a, b, radius), BSPhysicsShapeType.SHAPE_CAPSULE, scale);
        }

        public override bool IsNativeShape(BulletShape shape)
        {
            return shape.isNativeShape;
        }

        public override bool DeleteCollisionShape(BulletWorld pWorld, BulletShape pShape)
        {
            BulletWorldRapier world = (BulletWorldRapier)pWorld;
            BulletShapeRapier shape = (BulletShapeRapier)pShape;
            // A collider keeps its own reference to its shape, so the shape can go while bodies still use it.
            if (shape.Shape != IntPtr.Zero && world.State.Shapes.Remove(shape.Shape))
                RapierNative.r3FreeSharedShape(shape.Shape);
            shape.Clear();
            return true;
        }

        // The size the shape was built at; BulletS compares it with the prim's scale to see if the shape is stale.
        public override Vector3 GetLocalScaling(BulletShape shape)
        {
            return ((BulletShapeRapier)shape).Scale;
        }

        // Bullet's local inertia: the diagonal of the inertia tensor in the body's frame for this mass.
        public override Vector3 CalculateLocalInertia(BulletShape pShape, float mass)
        {
            BulletShapeRapier shape = (BulletShapeRapier)pShape;
            RapierNative.MassProperties unit = RapierNative.r3SharedShape_MassProperties(shape.Shape, 1f);
            RapierNative.Check(RapierNative.r3LastStatus());
            if (mass <= 0f || unit.mass <= 0f)
                return Vector3.Zero;

            // Rapier gives the inertia of unit density about the principal axes. Scale it to the mass, then express the
            // diagonal in the body's frame: the shape's principal frame, turned by the collider's rotation.
            float scaleToMass = mass / unit.mass;
            float[] principal =
            {
                unit.principalInertia.x * scaleToMass,
                unit.principalInertia.y * scaleToMass,
                unit.principalInertia.z * scaleToMass
            };
            Quaternion frame = shape.LocalRot * ToQuaternion(unit.principalInertiaLocalFrame);
            Vector3[] axes =
            {
                Rotate(frame, Vector3.UnitX), Rotate(frame, Vector3.UnitY), Rotate(frame, Vector3.UnitZ)
            };
            // The body-frame diagonal entry for body axis i is the sum over principal axes j of (axis j's i-th
            // component) squared times that axis's inertia.
            return new Vector3(
                axes[0].X * axes[0].X * principal[0] + axes[1].X * axes[1].X * principal[1] + axes[2].X * axes[2].X * principal[2],
                axes[0].Y * axes[0].Y * principal[0] + axes[1].Y * axes[1].Y * principal[1] + axes[2].Y * axes[2].Y * principal[2],
                axes[0].Z * axes[0].Z * principal[0] + axes[1].Z * axes[1].Z * principal[1] + axes[2].Z * axes[2].Z * principal[2]);
        }

        // Rotate a vector by a unit quaternion.
        internal static Vector3 Rotate(Quaternion q, Vector3 v)
        {
            Vector3 u = new Vector3(q.X, q.Y, q.Z);
            Vector3 t = Vector3.Cross(u, v) * 2f;
            return v + t * q.W + Vector3.Cross(u, t);
        }

        public override BulletShape CreateMeshShape(
            BulletWorld world,
            int indicesCount,
            int[] indices,
            int verticesCount,
            float[] vertices )
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(CreateMeshShape));
        }

        public override BulletShape CreateGImpactShape(
            BulletWorld world,
            int indicesCount,
            int[] indices,
            int verticesCount,
            float[] vertices )
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(CreateGImpactShape));
        }

        public override BulletShape CreateHullShape(BulletWorld world, int hullCount, float[] hulls)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(CreateHullShape));
        }

        public override BulletShape BuildHullShapeFromMesh(BulletWorld world, BulletShape meshShape, HACDParams parms)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(BuildHullShapeFromMesh));
        }

        public override BulletShape BuildConvexHullShapeFromMesh(BulletWorld world, BulletShape meshShape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(BuildConvexHullShapeFromMesh));
        }

        public override BulletShape CreateConvexHullShape(
            BulletWorld world,
            int indicesCount,
            int[] indices,
            int verticesCount,
            float[] vertices )
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(CreateConvexHullShape));
        }

        public override void SetShapeCollisionMargin(BulletShape shape, float margin)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetShapeCollisionMargin));
        }

        public override BulletShape CreateCompoundShape(BulletWorld sim, bool enableDynamicAabbTree)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(CreateCompoundShape));
        }

        public override int GetNumberOfCompoundChildren(BulletShape cShape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetNumberOfCompoundChildren));
        }

        public override void AddChildShapeToCompoundShape(
            BulletShape cShape,
            BulletShape addShape,
            Vector3 pos,
            Quaternion rot)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(AddChildShapeToCompoundShape));
        }

        public override BulletShape GetChildShapeFromCompoundShapeIndex(BulletShape cShape, int indx)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetChildShapeFromCompoundShapeIndex));
        }

        public override BulletShape RemoveChildShapeFromCompoundShapeIndex(BulletShape cShape, int indx)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(RemoveChildShapeFromCompoundShapeIndex));
        }

        public override void RemoveChildShapeFromCompoundShape(BulletShape cShape, BulletShape removeShape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(RemoveChildShapeFromCompoundShape));
        }

        public override void UpdateChildTransform(
            BulletShape pShape,
            int childIndex,
            Vector3 pos,
            Quaternion rot,
            bool shouldRecalculateLocalAabb)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(UpdateChildTransform));
        }

        public override void RecalculateCompoundShapeLocalAabb(BulletShape cShape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(RecalculateCompoundShapeLocalAabb));
        }

        public override BulletShape DuplicateCollisionShape(BulletWorld sim, BulletShape srcShape, UInt32 id)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(DuplicateCollisionShape));
        }

        public override int GetShapeType(BulletShape shape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetShapeType));
        }

        public override void SetLocalScaling(BulletShape shape, Vector3 scale)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetLocalScaling));
        }

        public override void SetMargin(BulletShape shape, float val)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(SetMargin));
        }

        public override float GetMargin(BulletShape shape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetMargin));
        }

        public override float GetAngularMotionDisc(BulletShape shape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetAngularMotionDisc));
        }

        public override float GetContactBreakingThreshold(BulletShape shape, float defaultFactor)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(GetContactBreakingThreshold));
        }

        public override bool IsPolyhedral(BulletShape shape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(IsPolyhedral));
        }

        public override bool IsConvex2d(BulletShape shape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(IsConvex2d));
        }

        public override bool IsConvex(BulletShape shape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(IsConvex));
        }

        public override bool IsNonMoving(BulletShape shape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(IsNonMoving));
        }

        public override bool IsConcave(BulletShape shape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(IsConcave));
        }

        public override bool IsCompound(BulletShape shape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(IsCompound));
        }

        public override bool IsSoftBody(BulletShape shape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(IsSoftBody));
        }

        public override bool IsInfinite(BulletShape shape)
        {
            throw new NotImplementedException("BSAPIRapier." + nameof(IsInfinite));
        }
    }
}
