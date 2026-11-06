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
        // The terrain and ground plane calls carry no world, so they use the scene's.
        private BulletWorldRapier SceneWorld
        {
            get { return (BulletWorldRapier)PhysicsScene.World; }
        }

        // BSScene builds a ground plane and a terrain while it initializes, so the scene needs both before any prim can
        // be added. These are stand-in shapes that make that possible: the ground plane is the slab the real one will be
        // (a thin box whose top is at the given height), and the terrain is a flat slab over the heightmap's Z range.
        // The heightfield terrain replaces the terrain shape with the terrain work (H8).

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

        public override BulletShape CreateTerrainShape(UInt32 id, Vector3 size, float minHeight, float maxHeight, float[] heightMap,
                                        float scaleFactor, float collisionMargin)
        {
            float thickness = Math.Max(maxHeight - minHeight, 0.1f);
            Vector3 slab = new Vector3(size.X, size.Y, thickness);
            return RegisterShape(SceneWorld, RapierNative.r3CuboidSharedShape(ToNative(slab * 0.5f)),
                        BSPhysicsShapeType.SHAPE_TERRAIN, slab);
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
