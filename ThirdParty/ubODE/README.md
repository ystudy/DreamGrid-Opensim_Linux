# ubODE: faster trimesh preprocessing

DreamGrid ships OpenSim's ubODE physics library (`bin/lib64/libubode-*.so`, `bin/lib64/ubode.dll`) with one fix on
top of the version in [opensim-libs](https://github.com/opensim/opensim-libs). The fix makes regions with many mesh
objects start several times faster and changes nothing about how they collide. This folder holds the patch, the
scripts that build the library, and the evidence, so the fix can be sent to the OpenSimulator project as is.

| File | What it is |
|---|---|
| `0001-ubode-make-trimesh-preprocess-concave-vertex-pass-linear.patch` | The fix, as a `git format-patch` against opensim-libs (apply from that repository's root). |
| `check-old-vs-new-preprocess.patch` | A test build that runs the old and the new code on every mesh and counts differences. Not for release. |
| `UPSTREAM-ODE-REVIEW.md` | Which later upstream ODE changes would help OpenSim, and a likely small-triangle bug. |
| `source.env` | The opensim-libs commit the patches apply to. |
| `build-linux.sh`, `build-windows.ps1` | Fetch that commit, apply a patch and build the library. |
| `../../.github/workflows/ubode.yml` | Runs both scripts on GitHub (Linux x64 and arm64 in Debian 12, Windows x64) and checks the exports. |

The libraries in `bin/lib64` built from this folder are `libubode-x86_64.so`, `libubode-arm64.so` and `ubode.dll`.
They come from the `ubode` workflow, run 42248258902, at commit 18d0ca3. They export the same 619 functions as the
binaries they replace, `ubode.dll` imports the same runtime DLLs, and the Linux ones need at most glibc 2.14 (x64)
or 2.17 (arm64). On the bench, the x64 library from that run started DragonFyre in 5.7 s and 5.65 s, with a physics
step of 1.5 s.

## The problem

When a region starts, ubODE turns every physics mesh into an ODE trimesh and calls `dGeomTriMeshDataPreprocess`
(from `OdePrim.GetMeshGeom`). In ubODE that is `dxTriMeshData::Preprocess` in
`trunk/unmanaged/ubODE-OpenSim/ode/src/collision_trimesh_opcode.cpp`. Its last pass clears the vertex "use" flags of
every vertex on a concave edge:

```cpp
for (unsigned int i = 0; i < numEdges; i++)            // every edge...
{
    EdgeRecord& er = records[i];
    if (er.Concave)                                    // ...that is concave
    {
        for (unsigned int j = 0; j < numEdges; j++)    // scans every edge of the mesh again
        {
            ...
```

That is O(concave edges × edges) for each mesh. Real content has many concave edges, so a few thousand meshes cost
seconds. Upstream ODE removed the same pattern in November 2016 (its CHANGELOG: preprocessing "optimized to only
contain a sort and a single pass over edges (used to be a sort and O(N^2))"). ubODE is based on ODE 0.13.1 (2014)
and still has the old loop.

## The fix

Mark each vertex of a concave edge in a per-vertex array, then make one pass over the edges and clear the flags of
the marked vertices:

```cpp
// concaveVert[v] = 1 for both vertices of every concave edge, then:
for (unsigned int j = 0; j < numEdges; j++)
{
    EdgeRecord& curER = records[j];
    if (concaveVert[curER.VertIdx1])
        UseFlags[curER.TriIdx] &= ~curER.Vert1Flags;
    if (concaveVert[curER.VertIdx2])
        UseFlags[curER.TriIdx] &= ~curER.Vert2Flags;
}
```

**Why the result is the same.** The old loop clears `Vert1Flags` of edge `j` when its first vertex equals either
vertex of *any* concave edge, and the same for `Vert2Flags`. That condition is "this vertex belongs to some concave
edge", which is exactly what `concaveVert` records. The pass only clears bits, and nothing sets `UseFlags` after the
earlier pass, so the order of the edges doesn't matter. The extra memory is one byte per vertex for the length of
the call.

The new code is linear apart from the `qsort` that was already there. It doesn't change the public API, the data
structures, or any other function, and the library still exports the same functions. This is a smaller change than
porting upstream's rewrite: upstream also changed the concavity threshold and added face angles, which would change
collision behaviour.

## Evidence

All on a 2 vCPU, 1.9 GB Debian 12 VM, OpenSim 0.9.3.1 on .NET 10. The test region is a real one: DragonFyre, 4,090
objects, 13,534 prims, 905 scripts and about 5,000 meshes, which become 7,294 physics actors. Of those, 4,498 are
trimeshes with 2.85 million triangles in total.

**Where the time went (before).** Timers added to ubOde for one run showed that `ProcessPreSimulation` took 15.1 s in
total. Of that, `dGeomTriMeshDataPreprocess` took 14.1 s, building the trimeshes 0.8 s, and fetching and decoding
meshes on the mesh worker thread 0.6 s.

**Start-up, same machine, same region:**

| ubODE library | Physics pre-simulation | Region up |
|---|---|---|
| Shipped binary from opensim-libs | 15.4 s | 19.7 s |
| Same source, built locally, unchanged | 13.7 s | 18.1 s |
| Same source with this patch | **1.5 s** (two runs) | **5.6 s** (two runs) |

**Same collision data.** `check-old-vs-new-preprocess.patch` runs the new pass, then the original loop on a copy of
`UseFlags`, and compares the two for every mesh. Over DragonFyre's meshes it reported 0 differing meshes and 0
differing triangles (more than 4,000 meshes and 2.4 million triangles counted). Since `UseFlags` is the only output
of this pass, the colliders see exactly the same data.

**Same interface.** The patched library exports the same 619 `d*` functions as the shipped Linux binary. The
`ubode` workflow checks this for every platform it builds.

## Building

```bash
sh ThirdParty/ubODE/build-linux.sh bin/lib64/libubode-x86_64.so      # on Debian 12 x64
sh ThirdParty/ubODE/build-linux.sh bin/lib64/libubode-arm64.so       # on Debian 12 arm64
```

```powershell
ThirdParty\ubODE\build-windows.ps1 -Output bin\lib64\ubode.dll       # Visual Studio 2022 with C++
```

Or run the `ubode` workflow on GitHub and take the files from its artifacts. To repeat the check, pass
`ThirdParty/ubODE/check-old-vs-new-preprocess.patch` as the second argument (or `-Patch`) and start a region with
many meshes: the region's console output gets `[UBODE VERIFY] ... meshes differing 0, triangles differing 0`.

The macOS library (`bin/lib64/libubode.dylib`) is still the unpatched opensim-libs binary. DreamGrid doesn't ship a
macOS package. It can be rebuilt with `./configure --disable-asserts --enable-shared --disable-threading-intf` once
per architecture, joining the two with `lipo`.

## Sending it upstream

The patch is ready for OpenSimulator's Mantis (<http://opensimulator.org/mantis/>) or the opensim-dev list, or as a
pull request to opensim-libs. A short summary for the report:

> ubODE `dxTriMeshData::Preprocess` (collision_trimesh_opcode.cpp) clears concave-edge vertex flags with a nested
> loop over all edges, O(concave edges × edges) per mesh. On a 13.5k-prim region with 4,498 physics meshes it takes
> 14 of the 15 s of physics start-up. The attached patch marks concave vertices in a per-vertex array and clears
> their flags in one pass. The UseFlags are identical: a test build running both versions side by side found 0
> differing triangles in 2.4 M+. Start-up goes from 19.7 s to 5.6 s. No API or behaviour change.
