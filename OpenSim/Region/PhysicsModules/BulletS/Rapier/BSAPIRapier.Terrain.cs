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
using System.Runtime.InteropServices;

using OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS.Rapier
{
    public sealed partial class BSAPIRapier
    {
        // The terrain and ground plane calls carry no world, so they use the scene's.
        private BulletWorldRapier SceneWorld
        {
            get { return (BulletWorldRapier)PhysicsScene.World; }
        }

        // The ground plane is a thin slab (half height 0.5) whose top face is at 'height': a very large cuboid, because
        // Rapier's plane (a half-space) has no thickness for a body to rest on.
        public override BulletShape CreateGroundPlaneShape(UInt32 id, float height, float collisionMargin)
        {
            const float halfExtent = 16384f;
            const float halfThickness = 0.5f;
            BulletShapeRapier shape = RegisterShape(SceneWorld,
                        RapierNative.r3CuboidSharedShape(ToNative(new Vector3(halfExtent, halfExtent, halfThickness))),
                        BSPhysicsShapeType.SHAPE_GROUNDPLANE, new Vector3(halfExtent * 2f, halfExtent * 2f, halfThickness * 2f));
            // The body sits at the plane's altitude and the plane is 'height' above that, so the slab hangs below it.
            shape.LocalPos = new Vector3(0f, 0f, height - halfThickness);
            return shape;
        }

        // The heightmap is row-major with X fastest (heightMap[y * sizeX + x]) and sample (x, y) belongs at world (x, y),
        // 1 m apart, so the field spans size - 1 metres. BSTerrainHeightmap then puts the body at the map's centre in
        // X and Y (size / 2) and at (minZ + maxZ) / 2 in Z (H8).
        //
        // Rapier's heightfield is Y-up, centred on its origin in X and Z, with its row index running along +Z and its
        // heights absolute. Turning the collider a quarter about +X sends Rapier's Y to world Z and Rapier's Z to world
        // -Y, so row i holds world row (rows - 1 - i). The collider is then moved so sample (0, 0) lands on the map's
        // origin, which is half a metre from where the body's centre puts it, and down by the body's Z centring.
        // Rapier's heightfield ray cast can miss a ray that runs exactly along a cell edge, a vertex or a diagonal: with
        // the samples on whole metres, a ray straight down at (128, 128) missed about a third of the time. Scripts
        // cast at whole numbers often, so the grid is moved off the whole metres by a fraction of a millimetre, by
        // different amounts in X and Y so the cell diagonals are avoided too. The height changes by less than 0.1 mm
        // on a 10% slope. That would leave the region's own edges (x or y of 0) a hair outside the field, so the field
        // has one extra row and column on every side that repeats the edge heights.
        private const float GridNudgeX = 0.0007f;
        private const float GridNudgeY = 0.0011f;

        public override BulletShape CreateTerrainShape(UInt32 id, Vector3 size, float minHeight, float maxHeight, float[] heightMap,
                                        float scaleFactor, float collisionMargin)
        {
            int columns = (int)size.X;
            int rows = (int)size.Y;
            if (columns < 2 || rows < 2 || heightMap == null || heightMap.Length < columns * rows)
                throw new ArgumentException("Rapier: the heightmap does not hold " + columns + " x " + rows + " samples");

            // The field is the map plus a one-sample border that repeats the edge heights (see GridNudgeX). Rapier wants
            // nalgebra's column-major order: the sample at (row, column) is at column * rows + row.
            int fieldColumns = columns + 2;
            int fieldRows = rows + 2;
            float[] samples = new float[fieldColumns * fieldRows];
            for (int fy = 0; fy < fieldRows; fy++)
            {
                int y = Math.Min(Math.Max(fy - 1, 0), rows - 1);
                int row = fieldRows - 1 - fy;
                for (int fx = 0; fx < fieldColumns; fx++)
                {
                    int x = Math.Min(Math.Max(fx - 1, 0), columns - 1);
                    samples[fx * fieldRows + row] = heightMap[y * columns + x] * scaleFactor;
                }
            }

            IntPtr native;
            GCHandle pinned = GCHandle.Alloc(samples, GCHandleType.Pinned);
            try
            {
                RapierNative.ShapeDesc desc = new RapierNative.ShapeDesc();
                desc.kind = RapierNative.ShapeKindHeightfield;
                desc.flags = RapierNative.HeightfieldFixInternalEdges;
                desc.heights = new RapierNative.View { data = pinned.AddrOfPinnedObject(), count = (UIntPtr)samples.Length };
                desc.rows = (UIntPtr)fieldRows;
                desc.columns = (UIntPtr)fieldColumns;
                // The scale is the field's whole extent: 1 m between samples.
                desc.scale = new RapierNative.Vector { x = fieldColumns - 1, y = 1f, z = fieldRows - 1 };
                native = RapierNative.r3ShapeDesc_Build(in desc);
            }
            finally
            {
                pinned.Free();
            }

            BulletShapeRapier shape = RegisterShape(SceneWorld, native, BSPhysicsShapeType.SHAPE_TERRAIN,
                        new Vector3(size.X, size.Y, maxHeight - minHeight));
            float zCentre = (minHeight + maxHeight) * 0.5f;
            SceneWorld.State.TerrainZOffset = -zCentre;
            shape.LocalRot = YAxisToZAxis;
            shape.LocalPos = new Vector3(-0.5f + GridNudgeX, -0.5f + GridNudgeY, SceneWorld.State.TerrainZOffset);
            return shape;
        }

        // A body that does not move and owns its shape: the terrain and the ground plane.
        public override BulletBody CreateBodyWithDefaultMotionState(BulletShape pShape, UInt32 id, Vector3 pos, Quaternion rot)
        {
            BulletShapeRapier shape = (BulletShapeRapier)pShape;
            BulletBodyRapier body = CreateRigidBody(SceneWorld, shape, id, pos, rot, RapierNative.BodyFixed);
            body.OwnedShape = shape;
            return body;
        }

    }
}
