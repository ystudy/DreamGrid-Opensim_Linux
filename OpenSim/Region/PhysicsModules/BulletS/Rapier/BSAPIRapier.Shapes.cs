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
using System.Runtime.InteropServices;

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
            BSPhysicsShapeType type = shapeData.Type;
            Vector3 scale = shapeData.Scale;
            if (type != BSPhysicsShapeType.SHAPE_BOX && type != BSPhysicsShapeType.SHAPE_SPHERE
                    && type != BSPhysicsShapeType.SHAPE_CYLINDER && type != BSPhysicsShapeType.SHAPE_CAPSULE)
                throw new NotImplementedException("BSAPIRapier." + nameof(BuildNativeShape) + " for " + type);

            BulletShapeRapier shape = RegisterShape(world, MakeNativeShape(type, scale), type, scale);
            // Rapier's cylinders run along Y, so they sit on the body turned a quarter about X.
            if (type == BSPhysicsShapeType.SHAPE_CYLINDER)
                shape.LocalRot = YAxisToZAxis;
            shape.BuildNativeAt = at => MakeNativeShape(type, at);
            shape.isNativeShape = true;
            return shape;
        }

        // The native shape for one of BulletS's four native types at this size.
        private static IntPtr MakeNativeShape(BSPhysicsShapeType type, Vector3 scale)
        {
            switch (type)
            {
                case BSPhysicsShapeType.SHAPE_BOX:
                    return RapierNative.r3CuboidSharedShape(ToNative(scale * 0.5f));
                case BSPhysicsShapeType.SHAPE_SPHERE:
                    return RapierNative.r3BallSharedShape(scale.X * 0.5f);
                case BSPhysicsShapeType.SHAPE_CYLINDER:
                    return RapierNative.r3CylinderSharedShape(scale.Z * 0.5f, scale.X * 0.5f);
                default:
                    return MakeZCapsule(scale.X * 0.5f, Math.Max(scale.Z - scale.X, 0f));
            }
        }

        // BulletS's capsule: a radius and the length of its cylinder part, along Z, both multiplied by the scale
        // (Bullet scales a Z capsule's radius by Scale.X and its half length by Scale.Z).
        public override BulletShape BuildCapsuleShape(BulletWorld pWorld, float radius, float height, Vector3 scale)
        {
            BulletShapeRapier shape = RegisterShape((BulletWorldRapier)pWorld, MakeZCapsule(radius * scale.X, height * scale.Z),
                                                    BSPhysicsShapeType.SHAPE_CAPSULE, scale);
            shape.BuildNativeAt = at => MakeZCapsule(radius * at.X, height * at.Z);
            shape.isNativeShape = true;
            return shape;
        }

        // A capsule along Z with this radius and cylinder length, already at its final size.
        private static IntPtr MakeZCapsule(float radius, float cylinderLength)
        {
            RapierNative.Vector a = new RapierNative.Vector { x = 0f, y = 0f, z = -cylinderLength * 0.5f };
            RapierNative.Vector b = new RapierNative.Vector { x = 0f, y = 0f, z = cylinderLength * 0.5f };
            // The segment is given along Z, so the shape needs no turn.
            return RapierNative.r3CapsuleSharedShape(a, b, radius);
        }

        public override bool IsNativeShape(BulletShape shape)
        {
            return shape.isNativeShape;
        }

        // Frees the native shape now, whether or not a body still uses it. A collider holds its own reference to the
        // geometry (r3InsertCollider and r3Collider_SetShape share it, and do not consume the shape), so a body that
        // still has a collider from this shape goes on working with the geometry it has. What is lost is only that
        // SetLocalScaling can no longer reach it. A shape deleted twice, or one the world already freed, is ignored.
        public override bool DeleteCollisionShape(BulletWorld pWorld, BulletShape pShape)
        {
            BulletWorldRapier world = (BulletWorldRapier)pWorld;
            BulletShapeRapier shape = (BulletShapeRapier)pShape;
            // A compound frees nothing native. A body it is on keeps the colliders it has (as with any shape), which
            // just no longer follow the compound.
            if (shape.Compound != null && shape.Compound.Body != null)
                DetachCompound(shape.Compound.Body);
            if (shape.Shape != IntPtr.Zero)
            {
                world.State.ForgetShape(shape.Shape);
                if (world.State.Shapes.Remove(shape.Shape))
                    RapierNative.r3FreeSharedShape(shape.Shape);
            }
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
            if (mass <= 0f)
                return Vector3.Zero;
            if (shape.Compound != null)
                return CompoundInertia(shape, mass);
            if (shape.Shape == IntPtr.Zero)
                return Vector3.Zero;
            // A trimesh has no volume, so Rapier gives it no mass properties. A mesh that ends up on a dynamic body
            // (BulletS's GImpact shape) gets the inertia of its bounding box instead.
            if (shape.MeshVerts != null
                    && (shape.shapeType == BSPhysicsShapeType.SHAPE_MESH || shape.shapeType == BSPhysicsShapeType.SHAPE_GIMPACT))
                return BoundingBoxInertia(shape.MeshVerts, mass);

            RapierNative.MassProperties unit = RapierNative.r3SharedShape_MassProperties(shape.Shape, 1f);
            RapierNative.Check(RapierNative.r3LastStatus());
            if (unit.mass <= 0f)
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

        // btCompoundShape::calculateLocalInertia: the inertia of a solid box of this mass that fills the compound's bounds
        // in its own frame (Bullet's 'approximation: take the inertia from the aabb for now'). Rapier has no margins, so
        // the bounds are the shapes' own.
        private static Vector3 CompoundInertia(BulletShapeRapier compound, float mass)
        {
            List<(BulletShapeRapier leaf, Vector3 pos, Quaternion rot)> leaves = new List<(BulletShapeRapier leaf, Vector3 pos, Quaternion rot)>();
            Flatten(compound, Vector3.Zero, Quaternion.Identity, 0, leaves);
            if (leaves.Count == 0)
                return Vector3.Zero;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach ((BulletShapeRapier leaf, Vector3 pos, Quaternion rot) in leaves)
            {
                RapierNative.Pose pose = new RapierNative.Pose { translation = ToNative(pos), rotation = ToNative(rot) };
                RapierNative.Aabb box = RapierNative.r3SharedShape_ComputeAabb(leaf.Shape, pose);
                min = Vector3.Min(min, ToVector3(box.mins));
                max = Vector3.Max(max, ToVector3(box.maxs));
            }
            Vector3 size = max - min;
            float k = mass / 12f;
            return new Vector3(k * (size.Y * size.Y + size.Z * size.Z),
                               k * (size.X * size.X + size.Z * size.Z),
                               k * (size.X * size.X + size.Y * size.Y));
        }

        // The inertia of a solid box of this mass that fits the vertices (x, y, z triples), about its own axes.
        private static Vector3 BoundingBoxInertia(float[] verts, float mass)
        {
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int ii = 0; ii + 2 < verts.Length; ii += 3)
            {
                min = new Vector3(Math.Min(min.X, verts[ii]), Math.Min(min.Y, verts[ii + 1]), Math.Min(min.Z, verts[ii + 2]));
                max = new Vector3(Math.Max(max.X, verts[ii]), Math.Max(max.Y, verts[ii + 1]), Math.Max(max.Z, verts[ii + 2]));
            }
            Vector3 size = max - min;
            float k = mass / 12f;
            return new Vector3(k * (size.Y * size.Y + size.Z * size.Z),
                               k * (size.X * size.X + size.Z * size.Z),
                               k * (size.X * size.X + size.Y * size.Y));
        }

        // Rotate a vector by a unit quaternion.
        internal static Vector3 Rotate(Quaternion q, Vector3 v)
        {
            Vector3 u = new Vector3(q.X, q.Y, q.Z);
            Vector3 t = Vector3.Cross(u, v) * 2f;
            return v + t * q.W + Vector3.Cross(u, t);
        }

        // ---- Meshes and hulls (H9) ----
        //
        // BulletS gives meshes already scaled, so a mesh or hull reports a scale of one until SetLocalScaling changes it.
        // What BulletS passes (BSShapes.CreatePhysicalMeshShape, BSTerrainMesh): indicesCount is the number of ints in
        // 'indices' to use (a multiple of 3; the array can be longer, since degenerate triangles are removed in place),
        // verticesCount is the number of vertices (not floats) and 'vertices' holds x, y, z for each. Rapier reads the
        // arrays only during the call, but the wrapper keeps its own copies for rebuilds (SetLocalScaling), which also
        // protects it from BulletS reusing its arrays.

        // A shape that has no native shape: BulletS sees HasPhysicalShape false and falls back, as it does when
        // Bullet cannot build a mesh.
        private BulletShapeRapier FailedShape(BSPhysicsShapeType type, string why)
        {
            PhysicsScene.Logger.WarnFormat("{0} Could not build a {1} shape: {2}", LogHeader, type, why);
            return new BulletShapeRapier(IntPtr.Zero, type, Vector3.One);
        }

        // The part of the arrays the counts say is in use, copied; false and the reason in 'why' if it cannot be a mesh.
        private static bool TryCopyMesh(int indicesCount, int[] indices, int verticesCount, float[] vertices,
                                    out int[] indexCopy, out float[] vertexCopy, out string why)
        {
            indexCopy = null;
            vertexCopy = null;
            why = null;
            if (indices == null || vertices == null)
                why = "no geometry was given";
            else
            {
                int vertexCount = Math.Min(verticesCount, vertices.Length / 3);
                int indexCount = Math.Min(indicesCount, indices.Length);
                indexCount -= indexCount % 3;
                if (vertexCount < 3 || indexCount < 3)
                    why = "fewer than one triangle";
                else
                {
                    for (int ii = 0; ii < indexCount; ii++)
                    {
                        if (indices[ii] < 0 || indices[ii] >= vertexCount)
                        {
                            why = "index " + indices[ii] + " is outside the " + vertexCount + " vertices";
                            break;
                        }
                    }
                    if (why == null && !AllFinite(vertices, vertexCount * 3))
                        why = "a vertex is not a finite number";
                    if (why == null)
                    {
                        indexCopy = new int[indexCount];
                        Array.Copy(indices, indexCopy, indexCount);
                        vertexCopy = new float[vertexCount * 3];
                        Array.Copy(vertices, vertexCopy, vertexCopy.Length);
                    }
                }
            }
            return why == null;
        }

        private static bool AllFinite(float[] values, int count)
        {
            for (int ii = 0; ii < count; ii++)
            {
                if (float.IsNaN(values[ii]) || float.IsInfinity(values[ii]))
                    return false;
            }
            return true;
        }

        private static RapierNative.View PinnedView(GCHandle pinned, int elements, int firstFloat = 0)
        {
            return new RapierNative.View
            {
                data = pinned.AddrOfPinnedObject() + firstFloat * sizeof(float),
                count = (UIntPtr)elements
            };
        }

        // r3TrimeshSharedShapeWithFlags over the geometry. FIX_INTERNAL_EDGES makes a ball roll across the seams between
        // triangles without catching on them (it includes MERGE_DUPLICATE_VERTICES, which BulletS's meshes need: sculpt
        // meshes repeat vertices).
        private static IntPtr BuildTrimeshNative(float[] vertices, int[] indices)
        {
            GCHandle v = GCHandle.Alloc(vertices, GCHandleType.Pinned);
            GCHandle i = GCHandle.Alloc(indices, GCHandleType.Pinned);
            try
            {
                return RapierNative.r3TrimeshSharedShapeWithFlags(PinnedView(v, vertices.Length / 3), PinnedView(i, indices.Length / 3),
                            RapierNative.TrimeshFixInternalEdges | RapierNative.TrimeshMergeDuplicateVertices);
            }
            finally
            {
                i.Free();
                v.Free();
            }
        }

        private static IntPtr BuildConvexHullNative(float[] vertices)
        {
            GCHandle v = GCHandle.Alloc(vertices, GCHandleType.Pinned);
            try
            {
                return RapierNative.r3ConvexHullSharedShape(PinnedView(v, vertices.Length / 3));
            }
            finally
            {
                v.Free();
            }
        }

        private static IntPtr BuildDecompositionNative(float[] vertices, int[] indices)
        {
            GCHandle v = GCHandle.Alloc(vertices, GCHandleType.Pinned);
            GCHandle i = GCHandle.Alloc(indices, GCHandleType.Pinned);
            try
            {
                return RapierNative.r3ConvexDecompositionSharedShape(PinnedView(v, vertices.Length / 3), PinnedView(i, indices.Length / 3));
            }
            finally
            {
                i.Free();
                v.Free();
            }
        }

        // A shape made from geometry: registered, with the arrays and the way to build it again kept.
        private BulletShapeRapier GeometryShape(BulletWorldRapier world, IntPtr native, BSPhysicsShapeType type,
                                    float[] vertices, int[] indices, Func<float[], IntPtr> build)
        {
            if (native == IntPtr.Zero)
                return FailedShape(type, RapierNative.LastError());
            BulletShapeRapier shape = RegisterShape(world, native, type, Vector3.One);
            shape.MeshVerts = vertices;
            shape.MeshIndices = indices;
            shape.BuildFromVerts = build;
            return shape;
        }

        public override BulletShape CreateMeshShape(BulletWorld pWorld, int indicesCount, int[] indices, int verticesCount, float[] vertices)
        {
            BulletWorldRapier world = (BulletWorldRapier)pWorld;
            if (!TryCopyMesh(indicesCount, indices, verticesCount, vertices, out int[] triangles, out float[] points, out string why))
                return FailedShape(BSPhysicsShapeType.SHAPE_MESH, why);
            return GeometryShape(world, BuildTrimeshNative(points, triangles), BSPhysicsShapeType.SHAPE_MESH, points, triangles,
                                    scaled => BuildTrimeshNative(scaled, triangles));
        }

        // BulletS asks for this only when ShouldUseGImpactShapeForPrims is set (default false): a trimesh on a dynamic
        // body. Rapier has no GImpact. A dynamic trimesh collides with the usual shapes, but a trimesh has no volume, so
        // the body's mass comes from SetMassProps and CalculateLocalInertia uses the mesh's bounding box.
        public override BulletShape CreateGImpactShape(BulletWorld pWorld, int indicesCount, int[] indices, int verticesCount, float[] vertices)
        {
            BulletWorldRapier world = (BulletWorldRapier)pWorld;
            LogOnce(world, true, "gimpact", "A GImpact shape was requested (ShouldUseGImpactShapeForPrims). Rapier has no GImpact: "
                        + "it is a trimesh on a dynamic body, with the inertia of its bounding box.");
            if (!TryCopyMesh(indicesCount, indices, verticesCount, vertices, out int[] triangles, out float[] points, out string why))
                return FailedShape(BSPhysicsShapeType.SHAPE_GIMPACT, why);
            return GeometryShape(world, BuildTrimeshNative(points, triangles), BSPhysicsShapeType.SHAPE_GIMPACT, points, triangles,
                                    scaled => BuildTrimeshNative(scaled, triangles));
        }

        // One convex hull of the vertices (the triangle indices are not needed).
        public override BulletShape CreateConvexHullShape(BulletWorld pWorld, int indicesCount, int[] indices, int verticesCount, float[] vertices)
        {
            BulletWorldRapier world = (BulletWorldRapier)pWorld;
            int vertexCount = (vertices == null) ? 0 : Math.Min(verticesCount, vertices.Length / 3);
            if (vertexCount < 4)
                return FailedShape(BSPhysicsShapeType.SHAPE_CONVEXHULL, "a convex hull needs at least four vertices");
            float[] points = new float[vertexCount * 3];
            Array.Copy(vertices, points, points.Length);
            if (!AllFinite(points, points.Length))
                return FailedShape(BSPhysicsShapeType.SHAPE_CONVEXHULL, "a vertex is not a finite number");
            return GeometryShape(world, BuildConvexHullNative(points), BSPhysicsShapeType.SHAPE_CONVEXHULL, points, null, BuildConvexHullNative);
        }

        // BulletS's default for physical prims (ShouldUseSingleConvexHullForPrims): the hull of the mesh's vertices, which
        // the mesh shape keeps for the purpose.
        public override BulletShape BuildConvexHullShapeFromMesh(BulletWorld pWorld, BulletShape pMesh)
        {
            BulletShapeRapier mesh = (BulletShapeRapier)pMesh;
            if (mesh.MeshVerts == null)
                return FailedShape(BSPhysicsShapeType.SHAPE_CONVEXHULL, "the source shape is not a mesh");
            BulletShapeRapier hull = (BulletShapeRapier)CreateConvexHullShape(pWorld, 0, null, mesh.MeshVerts.Length / 3, mesh.MeshVerts);
            hull.Scale = mesh.Scale;
            return hull;
        }

        // HACD, only with ShouldUseBulletHACD (default false). Rapier's decomposition takes no parameters, so HACDParams
        // are ignored.
        public override BulletShape BuildHullShapeFromMesh(BulletWorld pWorld, BulletShape pMesh, HACDParams parms)
        {
            BulletWorldRapier world = (BulletWorldRapier)pWorld;
            BulletShapeRapier mesh = (BulletShapeRapier)pMesh;
            if (mesh.MeshVerts == null || mesh.MeshIndices == null)
                return FailedShape(BSPhysicsShapeType.SHAPE_HULL, "the source shape is not a mesh");
            LogOnce(world, false, "hacd", "Rapier's convex decomposition takes no parameters: HACDParams are ignored.");
            int[] triangles = mesh.MeshIndices;
            BulletShapeRapier hulls = GeometryShape(world, BuildDecompositionNative(mesh.MeshVerts, triangles), BSPhysicsShapeType.SHAPE_HULL,
                                    mesh.MeshVerts, triangles, scaled => BuildDecompositionNative(scaled, triangles));
            hulls.Scale = mesh.Scale;
            return hulls;
        }

        // BulletSim's hull list: hulls[0] is the number of hulls and then, for each hull, its vertex count, its centroid
        // (x, y, z) and its vertices (x, y, z each). BSAPIXNA.CreateHullShape reads it the same way: the centroid is the
        // hull's offset in the compound and the vertices are not moved by it (BulletS always writes a centroid of zero).
        //
        // The hulls become one native compound shape, so the whole list is one collider on its body; Bullet builds a
        // btCompoundShape of hulls the same way. In a linkset a hull list is one child, and each child is its own
        // collider on the root body (H5, option b), so this native compound is never put inside another native compound
        // (Parry allows no compound inside a compound). BulletS compounds are not native shapes at all (CompoundState).
        private static IntPtr BuildHullListNative(float[] hulls)
        {
            int hullCount = (int)hulls[0];
            List<IntPtr> parts = new List<IntPtr>();
            List<Vector3> offsets = new List<Vector3>();
            bool single = false;
            GCHandle pinned = GCHandle.Alloc(hulls, GCHandleType.Pinned);
            try
            {
                int at = 1;
                for (int hull = 0; hull < hullCount; hull++)
                {
                    int vertexCount = (int)hulls[at];
                    IntPtr part = RapierNative.r3ConvexHullSharedShape(PinnedView(pinned, vertexCount, at + 4));
                    if (part != IntPtr.Zero)
                    {
                        parts.Add(part);
                        offsets.Add(new Vector3(hulls[at + 1], hulls[at + 2], hulls[at + 3]));
                    }
                    at += 4 + vertexCount * 3;
                }
                if (parts.Count == 0)
                    return IntPtr.Zero;
                // A lone hull that is not offset is the shape itself, not a compound of one.
                single = parts.Count == 1 && offsets[0] == Vector3.Zero;
                if (single)
                    return parts[0];

                RapierNative.ShapeDesc template = RapierNative.r3DefaultColliderDesc().shape;
                RapierNative.CompoundShapeDesc[] children = new RapierNative.CompoundShapeDesc[parts.Count];
                for (int ii = 0; ii < parts.Count; ii++)
                {
                    children[ii].shape = template;
                    children[ii].shape.kind = RapierNative.ShapeKindShared;
                    children[ii].shape.sharedShape = parts[ii];
                    children[ii].pose.translation = ToNative(offsets[ii]);
                    children[ii].pose.rotation = ToNative(Quaternion.Identity);
                }
                GCHandle childrenPinned = GCHandle.Alloc(children, GCHandleType.Pinned);
                try
                {
                    return RapierNative.r3CompoundSharedShape(new RapierNative.View
                    {
                        data = childrenPinned.AddrOfPinnedObject(),
                        count = (UIntPtr)children.Length
                    });
                }
                finally
                {
                    childrenPinned.Free();
                }
            }
            finally
            {
                pinned.Free();
                // The compound holds its own references to the parts, so ours go (but not a lone hull that is returned).
                if (!single)
                {
                    foreach (IntPtr part in parts)
                        RapierNative.r3FreeSharedShape(part);
                }
            }
        }

        // Checks the layout before it is read, and returns a copy of the part in use.
        private static bool TryCopyHullList(int hullCount, float[] hulls, out float[] copy, out string why)
        {
            copy = null;
            why = null;
            if (hulls == null || hulls.Length < 1 || hullCount < 1)
                why = "no hulls were given";
            else
            {
                int at = 1;
                for (int hull = 0; hull < hullCount; hull++)
                {
                    if (at >= hulls.Length || !(hulls[at] >= 0f) || hulls[at] > int.MaxValue / 4
                            || at + 4 + (long)hulls[at] * 3 > hulls.Length)
                    {
                        why = "hull " + hull + " runs past the end of the list";
                        break;
                    }
                    at += 4 + (int)hulls[at] * 3;
                }
                if (why == null && !AllFinite(hulls, at))
                    why = "a hull value is not a finite number";
                if (why == null)
                {
                    copy = new float[at];
                    Array.Copy(hulls, copy, at);
                    // The count the caller gave is the one that was checked.
                    copy[0] = hullCount;
                }
            }
            return why == null;
        }

        public override BulletShape CreateHullShape(BulletWorld pWorld, int hullCount, float[] hulls)
        {
            BulletWorldRapier world = (BulletWorldRapier)pWorld;
            if (!TryCopyHullList(hullCount, hulls, out float[] list, out string why))
                return FailedShape(BSPhysicsShapeType.SHAPE_HULL, why);
            BulletShapeRapier shape = GeometryShape(world, BuildHullListNative(list), BSPhysicsShapeType.SHAPE_HULL, list, null, BuildHullListNative);
            shape.VertsAreHullList = true;
            return shape;
        }

        // Rapier has no collision margins (map row 24); the nearest thing, r3Collider_SetContactSkin, stays 0. The value is
        // kept for GetMargin.
        public override void SetShapeCollisionMargin(BulletShape shape, float margin)
        {
            ((BulletShapeRapier)shape).Margin = margin;
        }

        // ---- Compound shapes (H5a, option b) ----
        //
        // A compound is a C# list of children (CompoundState) and nothing native until it is put on a body
        // (CreateBodyFromShape, SetCollisionShape). On a body every child is a collider of that body, at the child's pose
        // relative to the body, with the body's localID as its user data: BulletS's linkset compounds are reported under
        // the root's ID, as Bullet reports a compound under its one user pointer. A child that is itself a BulletS
        // compound is flattened, one collider per shape it holds. Changes to a compound that is on a body (a child
        // added, moved or removed) change that body's colliders at once.

        public override BulletShape CreateCompoundShape(BulletWorld sim, bool enableDynamicAabbTree)
        {
            BulletShapeRapier shape = new BulletShapeRapier(IntPtr.Zero, BSPhysicsShapeType.SHAPE_COMPOUND, Vector3.One);
            shape.Compound = new CompoundState();
            return shape;
        }

        public override int GetNumberOfCompoundChildren(BulletShape cShape)
        {
            CompoundState compound = ((BulletShapeRapier)cShape).Compound;
            return (compound == null) ? 0 : compound.Children.Count;
        }

        public override void AddChildShapeToCompoundShape(BulletShape cShape, BulletShape addShape, Vector3 pos, Quaternion rot)
        {
            CompoundState compound = CompoundOf(cShape, nameof(AddChildShapeToCompoundShape));
            if (compound == null)
                return;
            BulletShapeRapier child = (BulletShapeRapier)addShape;
            compound.Children.Add((child, pos, rot));
            BulletBodyRapier body = compound.Body;
            if (body == null)
                return;
            compound.Colliders.Add(AddChildColliders(body, child, pos, rot));
            if (compound.Children.Count == 1)
                NoteRootChild(body, compound);
            WakeAfterShapeChange(body);
        }

        // Bullet does not check the index (BSLinksetCompound does before it calls); one out of range gets a shape with
        // nothing in it.
        public override BulletShape GetChildShapeFromCompoundShapeIndex(BulletShape cShape, int indx)
        {
            CompoundState compound = ((BulletShapeRapier)cShape).Compound;
            if (compound == null || indx < 0 || indx >= compound.Children.Count)
                return new BulletShapeRapier(IntPtr.Zero, BSPhysicsShapeType.SHAPE_UNKNOWN, Vector3.One);
            return compound.Children[indx].child;
        }

        // Returns the child's own wrapper, which BulletS then releases (BSShapeCompound.Dereference).
        public override BulletShape RemoveChildShapeFromCompoundShapeIndex(BulletShape cShape, int indx)
        {
            CompoundState compound = CompoundOf(cShape, nameof(RemoveChildShapeFromCompoundShapeIndex));
            if (compound == null || indx < 0 || indx >= compound.Children.Count)
                return new BulletShapeRapier(IntPtr.Zero, BSPhysicsShapeType.SHAPE_UNKNOWN, Vector3.One);
            BulletShapeRapier child = compound.Children[indx].child;
            compound.Children.RemoveAt(indx);
            BulletBodyRapier body = compound.Body;
            if (body != null)
            {
                foreach (ChildCollider part in compound.Colliders[indx])
                    RemoveBodyCollider(body, part.Collider);
                compound.Colliders.RemoveAt(indx);
                if (indx == 0)
                    NoteRootChild(body, compound);
                WakeAfterShapeChange(body);
            }
            return child;
        }

        // Every child made from this shape goes, as btCompoundShape::removeChildShape removes them all.
        public override void RemoveChildShapeFromCompoundShape(BulletShape cShape, BulletShape removeShape)
        {
            CompoundState compound = CompoundOf(cShape, nameof(RemoveChildShapeFromCompoundShape));
            if (compound == null)
                return;
            BulletShapeRapier remove = (BulletShapeRapier)removeShape;
            for (int ii = compound.Children.Count - 1; ii >= 0; ii--)
            {
                BulletShapeRapier child = compound.Children[ii].child;
                if (ReferenceEquals(child, remove) || child.ReferenceSame(remove))
                    RemoveChildShapeFromCompoundShapeIndex(cShape, ii);
            }
        }

        // The child's new pose relative to the compound; on a body, its colliders move with it (map row 32).
        public override void UpdateChildTransform(BulletShape pShape, int childIndex, Vector3 pos, Quaternion rot, bool shouldRecalculateLocalAabb)
        {
            CompoundState compound = CompoundOf(pShape, nameof(UpdateChildTransform));
            if (compound == null || childIndex < 0 || childIndex >= compound.Children.Count)
                return;
            BulletShapeRapier child = compound.Children[childIndex].child;
            compound.Children[childIndex] = (child, pos, rot);
            BulletBodyRapier body = compound.Body;
            if (body == null)
                return;
            foreach (ChildCollider part in compound.Colliders[childIndex])
            {
                Compose(pos, rot, part.Pos, part.Rot, out Vector3 partPos, out Quaternion partRot);
                RapierNative.Pose pose = new RapierNative.Pose { translation = ToNative(partPos), rotation = ToNative(partRot) };
                RapierNative.Check(RapierNative.r3Collider_SetPositionWrtParent(part.Collider, pose));
            }
            if (childIndex == 0)
                NoteRootChild(body, compound);
            WakeAfterShapeChange(body);
        }

        // Each child is its own collider, so there is no compound bound to recompute (map row 33).
        public override void RecalculateCompoundShapeLocalAabb(BulletShape cShape)
        {
        }

        private CompoundState CompoundOf(BulletShape shape, string caller)
        {
            CompoundState compound = ((BulletShapeRapier)shape).Compound;
            if (compound == null)
                LogOnce(SceneWorld, true, "not-compound-" + caller, caller + " was given a shape that is not a compound; ignored.");
            return compound;
        }

        // The pose (pos2, rot2) given relative to (pos1, rot1), as one pose: Bullet's transform product, with the rotations
        // multiplied as btQuaternion multiplies (rot1 rot2, Hamilton's order), whatever OpenMetaverse's operator does.
        internal static void Compose(Vector3 pos1, Quaternion rot1, Vector3 pos2, Quaternion rot2, out Vector3 pos, out Quaternion rot)
        {
            pos = pos1 + Rotate(rot1, pos2);
            rot = Multiply(rot1, rot2);
        }

        internal static Quaternion Multiply(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        }

        // Nested compounds deeper than this are not followed (a compound that holds itself would never end).
        private const int MaxCompoundDepth = 16;

        // The native shapes a shape is made of, each with its pose relative to the shape: a plain shape is itself at its
        // own offset (LocalPos, LocalRot); a compound is what its children are made of, at their poses.
        private static void Flatten(BulletShapeRapier shape, Vector3 pos, Quaternion rot, int depth,
                                    List<(BulletShapeRapier leaf, Vector3 pos, Quaternion rot)> leaves)
        {
            if (shape.Compound != null)
            {
                if (depth >= MaxCompoundDepth)
                    return;
                foreach ((BulletShapeRapier child, Vector3 childPos, Quaternion childRot) in shape.Compound.Children)
                {
                    Compose(pos, rot, childPos, childRot, out Vector3 at, out Quaternion turned);
                    Flatten(child, at, turned, depth + 1, leaves);
                }
                return;
            }
            // A child whose native shape was deleted has nothing to collide with.
            if (shape.Shape == IntPtr.Zero)
                return;
            Compose(pos, rot, shape.LocalPos, shape.LocalRot, out Vector3 leafPos, out Quaternion leafRot);
            leaves.Add((shape, leafPos, leafRot));
        }

        // A new wrapper over a new native handle that shares the original's geometry (r3SharedShape_Clone), so the two
        // are freed separately: deleting one leaves the other alone. Bodies made from either start with their own
        // colliders; the two do not follow each other's SetLocalScaling.
        public override BulletShape DuplicateCollisionShape(BulletWorld pWorld, BulletShape srcShape, UInt32 id)
        {
            BulletWorldRapier world = (BulletWorldRapier)pWorld;
            BulletShapeRapier source = (BulletShapeRapier)srcShape;
            BulletShapeRapier copy = (BulletShapeRapier)source.Clone();
            if (source.Shape != IntPtr.Zero)
            {
                copy.Shape = RapierNative.r3SharedShape_Clone(source.Shape);
                if (copy.Shape == IntPtr.Zero)
                    RapierNative.Check(RapierNative.r3LastStatus());
                world.State.Shapes.Add(copy.Shape);
            }
            // A copied compound has the same children and is on no body.
            if (source.Compound != null)
            {
                copy.Compound = new CompoundState();
                copy.Compound.Children.AddRange(source.Compound.Children);
            }
            return copy;
        }

        public override int GetShapeType(BulletShape shape)
        {
            return (int)shape.shapeType;
        }

        // Rapier's shapes cannot be scaled, so the shape is built again at the new size and every collider made from the
        // old one is given the new one (r3Collider_SetShape). The old native shape is freed once they all have it: a
        // collider holds its own reference, so nothing uses it after that. A native shape is built at the new scale; a
        // mesh or hull has its stored vertices multiplied by new scale / stored scale (a hull list leaves its counts
        // alone). A shape that cannot be rebuilt (terrain, ground plane, deleted) is left as it is.
        public override void SetLocalScaling(BulletShape pShape, Vector3 scale)
        {
            BulletShapeRapier shape = (BulletShapeRapier)pShape;
            if (shape.Shape == IntPtr.Zero || scale == shape.Scale)
                return;
            BulletWorldRapier world = SceneWorld;

            float[] scaledVerts = null;
            IntPtr fresh;
            if (shape.BuildNativeAt != null)
            {
                fresh = shape.BuildNativeAt(scale);
            }
            else if (shape.BuildFromVerts != null && shape.MeshVerts != null)
            {
                Vector3 was = shape.Scale;
                if (was.X == 0f || was.Y == 0f || was.Z == 0f)
                {
                    LogOnce(world, true, "scale-from-zero", "A mesh scaled to zero cannot be scaled again; the shape is left as it is.");
                    return;
                }
                scaledVerts = ScaleVertices(shape.MeshVerts, new Vector3(scale.X / was.X, scale.Y / was.Y, scale.Z / was.Z),
                                            shape.VertsAreHullList);
                fresh = shape.BuildFromVerts(scaledVerts);
            }
            else
            {
                LogOnce(world, false, "scale-" + shape.shapeType, "SetLocalScaling is ignored for " + shape.shapeType + " shapes.");
                return;
            }
            if (fresh == IntPtr.Zero)
            {
                PhysicsScene.Logger.WarnFormat("{0} Could not rebuild a {1} shape at scale {2}: {3}", LogHeader, shape.shapeType, scale,
                            RapierNative.LastError());
                return;
            }
            world.State.Shapes.Add(fresh);

            IntPtr previous = shape.Shape;
            if (world.State.ShapeUsers.TryGetValue(previous, out List<RapierNative.ColliderHandle> users))
            {
                world.State.ShapeUsers.Remove(previous);
                List<RapierNative.ColliderHandle> following = new List<RapierNative.ColliderHandle>();
                foreach (RapierNative.ColliderHandle collider in users)
                {
                    uint status = RapierNative.r3Collider_SetShape(collider, fresh);
                    if (status != 0)
                    {
                        PhysicsScene.Logger.WarnFormat("{0} A collider did not take the rescaled shape: {1}", LogHeader, RapierNative.LastError());
                        world.State.ColliderShapes.Remove(collider);
                        continue;
                    }
                    following.Add(collider);
                    world.State.ColliderShapes[collider] = fresh;
                    // A body that was resting on the old size has to look again.
                    if (world.State.ColliderOwners.TryGetValue(collider, out BulletBodyRapier owner))
                        RapierNative.r3RigidBody_WakeUp(owner.Body, 1);
                }
                if (following.Count > 0)
                    world.State.ShapeUsers[fresh] = following;
            }

            if (world.State.Shapes.Remove(previous))
                RapierNative.r3FreeSharedShape(previous);
            shape.Shape = fresh;
            shape.Scale = scale;
            if (scaledVerts != null)
                shape.MeshVerts = scaledVerts;
        }

        // The vertices multiplied componentwise; in a hull list that means the vertices and the centroids, not the counts.
        private static float[] ScaleVertices(float[] verts, Vector3 factor, bool isHullList)
        {
            float[] scaled = (float[])verts.Clone();
            if (!isHullList)
            {
                ScaleTriples(scaled, 0, scaled.Length, factor);
                return scaled;
            }
            int hullCount = (int)scaled[0];
            int at = 1;
            for (int hull = 0; hull < hullCount; hull++)
            {
                int vertexCount = (int)scaled[at];
                // The centroid and the vertices alike are x, y, z triples.
                ScaleTriples(scaled, at + 1, at + 4 + vertexCount * 3, factor);
                at += 4 + vertexCount * 3;
            }
            return scaled;
        }

        private static void ScaleTriples(float[] values, int from, int to, Vector3 factor)
        {
            for (int ii = from; ii + 3 <= to; ii += 3)
            {
                values[ii] *= factor.X;
                values[ii + 1] *= factor.Y;
                values[ii + 2] *= factor.Z;
            }
        }

        // As SetShapeCollisionMargin: stored, and nothing native (map rows 40 and 41). BSCharacter sets one on the avatar
        // shape before scaling it.
        public override void SetMargin(BulletShape shape, float val)
        {
            ((BulletShapeRapier)shape).Margin = val;
        }

        public override float GetMargin(BulletShape shape)
        {
            return ((BulletShapeRapier)shape).Margin;
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

        // A BulletS compound (CreateCompoundShape). A hull list is one native shape, as BulletS treats it.
        public override bool IsCompound(BulletShape shape)
        {
            return ((BulletShapeRapier)shape).Compound != null;
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
