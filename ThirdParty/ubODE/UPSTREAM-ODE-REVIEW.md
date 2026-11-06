# Upstream ODE changes since ubODE's base: what would help OpenSim

Reviewed 2026-11-06. ubODE (opensim-libs `trunk/unmanaged/ubODE-OpenSim`, labelled 0.13.4) is ODE 0.13.1 with
Ubit Umarov's changes; its CHANGELOG ends at 11/17/2014. Upstream ODE is at 0.16.6 and moved to
<https://github.com/odedevs/ode> in 2026.

**Replacing ubODE with upstream is not worth it.** ubODE rewrote `quickstep.cpp` and `step.cpp` (about 7,700 changed
lines), most of the trimesh colliders, rays and spaces, and removed threading. It also adds its own terrain geom
(`osTerrain.cpp`), and OpenSim calls 4 terrain functions upstream doesn't have. OpenSim's C# code calls 104 ODE
functions. Moving to 0.16 means porting all of that again and re-testing vehicles, avatars and terrain, for little
gain. The useful upstream changes can be taken one at a time instead.

What OpenSim uses (checked in `OpenSim/Region/PhysicsModules/ubOde`):
- **Geoms:** trimesh (OPCODE), sphere, box, capsule (avatars), ray and osTerrain.
- **Spaces:** simple and quadtree.
- **Joints:** contacts plus an AMotor.
- **Collisions:** `dCollide` with up to 80 contacts. Mesh-against-mesh does happen.

## Worth doing

1. **Done: the trimesh preprocess loop.** See `README.md`. Upstream made the same kind of change in Nov 2016.
2. **Small triangles are ignored by every mesh collider. A likely real bug, not yet tested in-world.**
   - **The test:** ubODE changed `_dSafeNormalize3` (odemath.cpp) and the inline normal checks in the trimesh
     colliders to reject a normal when its squared length is below `dEpsilon` (`FLT_EPSILON`, config.h). The
     normal there is the unnormalised edge cross product, whose length is twice the triangle's area.
   - **The effect:** any mesh triangle smaller than about 1.7 cm² (a 2 cm equilateral) is skipped. That covers ray
     (`collision_trimesh_ray.cpp`), sphere, capsule (avatars), box (`collision_trimesh_box.cpp` ~line 408) and
     trimesh-trimesh. Upstream and 0.13.1 reject only an exactly zero normal. Small or finely detailed physical
     meshes could let avatars and objects through in places, and `llCastRay` could miss them.
   - **Next step:** test in-world first, with small mesh objects and ray casts, old library against fixed library.
     Ubit may have chosen the threshold on purpose to avoid jitter.
   - **Size:** a small change, but it does change behaviour, because tiny triangles start colliding again.
3. **Mesh-relative coordinates in the OPCODE colliders** (upstream db32abec, Nov 2015).
   - **What it does:** before the OPCODE query, upstream moves sphere, box and ray positions (and the second mesh's
     matrix for mesh-mesh) into the mesh's frame.
   - **Why it helps:** in single precision this avoids rounding of about region-coordinate × 6e-8, which is
     0.015 mm at 256 m and 0.24 mm on large var-regions. Mostly it affects edge culling and ray hit distances.
   - **Size:** small and mechanical, about 5 files, low risk.
4. **Box-against-mesh keeps the deepest contacts** (32f6b567, Aug 2015). When the contact array is full, ubODE
   drops new contacts, and upstream replaces the shallowest one. Low value with a limit of 80. Small, low risk.
5. **ICE container clean-up** (9f607999, d3d16a77, 3da3b7b9, May 2016). Turns off `CONTAINER_STATS`, a non-atomic
   static counter that every region's physics thread writes to, and adds growth and overflow guards. Trivial, no
   behaviour change.

## Already in ubODE, or not needed

- **Capsule-ray divide by zero (Nov 2014):** already in.
- **QuickStep early exit:** ubODE has its own (it stops when the adjustment total drops below 1e-4).
- **Upstream's "iterate longer" half (2019):** medium-to-large work on a rewritten solver, value unclear.
- **Reorder and Jcopy fixes (2018):** don't apply. ubODE doesn't reorder and already indexes correctly.
- **Box-box, sphere, capsule and quadtree:** no functional upstream changes since 0.13.1.

## Not used by OpenSim

- **Spaces OpenSim doesn't use:** the HashSpace fixes, and the SAP NaN fix.
- **GIMPACT and libccd features:** GIMPACT/libccd colliders, convex-trimesh, face angles (`Preprocess2`).
- **Geoms and joints OpenSim doesn't create:** the new box-plane collider (no planes), and the Hinge2,
  Transmission, PU, DBall and reversed-AMotor fixes.
- **Threading:** all of the threading work (ubODE is built without it).
- **Inelastic contacts (2025):** they would change the `dSurfaceParameters` layout the C# side passes.
- **`dGeomBoxPointDepth` fix:** only affects points outside the box, which ubODE's one caller never uses.
