# Rapier

DreamGrid's third physics engine for the BulletS module (next to BulletSim and BulletXNA) is
[Rapier](https://rapier.rs), a Rust physics engine, used through the C bindings in its repository
(`bindings/c`, crate `rapier3d-ffi`). This folder holds what is needed to rebuild the native library and check it.
OpenSim reaches it through `OpenSim/Region/PhysicsModules/BulletS/Rapier/RapierNative.cs`, the only C# file that knows
the layout of `include/rapier.h`.

| File | What it is |
|---|---|
| `source.env` | The Rapier tag and the commit it points to (`v0.36.1`, `3406750a38286a0d3c7d0f4be2f9d53153e81d90`). The scripts fetch the commit. |
| `build-linux.sh OUTDIR [single\|mt]` | Fetches that commit (sparse), runs `cargo build --release` (with `--features parallel` for `mt`), strips the result and writes `OUTDIR/librapier-x86_64.so` or `librapier-arm64.so` (`single`, the default), or `librapier-mt-x86_64.so` or `librapier-mt-arm64.so` (`mt`). |
| `build-windows.ps1 -OutDir DIR [-Parallel]` | The same for 64-bit Windows, with `-C target-feature=+crt-static`; writes `DIR/rapier.dll`, or `DIR/rapier-mt.dll` with `-Parallel`. |
| `include/rapier.h` | The C header from that commit (`bindings/c/include/rapier.h`), unchanged apart from line endings. |
| `exports.txt` | The `r3` functions the header declares for these builds (3D, single precision, no FEM, no robotics): 681 names, the same for `single` and `mt` (a build without `parallel` still exports the thread-pool functions; they answer `R3_UNSUPPORTED`). `gen-exports.sh` writes it from the header. |
| `patches/parry3d-0.31.1-edge-codes.patch` | DreamGrid's patch to Parry, Rapier's collision library (see [The Parry patch](#the-parry-patch)). |
| `edge-codes-test/` | Rust tests for that patch; both scripts run them against the patched Parry before building Rapier. |
| `LICENSE` | Rapier's Apache-2.0 licence. The tag has no NOTICE file. |
| `../../.github/workflows/rapier.yml` | Runs both scripts on GitHub for both builds (Linux x64 and arm64 in Debian 12, Windows x64) and checks exports, glibc and DLL imports. |

## Shipped libraries

Two builds of the same commit, each selected by its own lines in `bin/OpenSim.Region.PhysicsModule.BulletS.dll.config`:

| dllmap name | Build | Files | Loaded when |
|---|---|---|---|
| `rapier` | without `parallel` | `bin/lib64/librapier-x86_64.so`, `librapier-arm64.so`, `rapier.dll` | `RapierNumThreads` is 1 (the default) |
| `rapier_mt` | `--features parallel` (rayon) | `bin/lib64/librapier-mt-x86_64.so`, `librapier-mt-arm64.so`, `rapier-mt.dll` | `RapierNumThreads` is more than 1 |

Why two: a parallel build runs every step inside its rayon pool, even a pool of one thread, and waking that pool's
worker after the gap between two frames costs 35-40 µs per step, more than the whole step of a small scene. Without a
pool (never calling `r3SetNumThreads`, or `r3ClearThreadPool`) its parallel parts would run on rayon's global pool, one
thread per core, so every region would quietly become multi-threaded. The single-threaded build has neither problem; the
parallel one is kept for regions that ask for more threads.

`RapierNative.TryLoad` loads one of them, once per process, chosen by the first Rapier region's `RapierNumThreads`. If
`rapier_mt` cannot be loaded it logs a warning and runs at 1 thread with `rapier`. A later region that asks for a thread
count the loaded build does not fit gets a warning and the loaded build. The region log names the file in use
(`Rapier 0.36.1 native library: ...`, with `+parallel` after the version for `rapier_mt`).

- `librapier-x86_64.so` (7.0 MB), `librapier-mt-x86_64.so` (7.7 MB), `rapier.dll` (8.2 MB) and `rapier-mt.dll`
  (9.2 MB) are built from the pinned commit, with the Parry patch. The Linux ones were built in a `debian:12` container and need at most
  glibc 2.35 (Debian 12 has 2.36); they need only libgcc_s, libm and libc. The DLLs import only KERNEL32, ntdll and
  api-ms-win-core-synch (no VCRUNTIME, no MSVCP). All four export exactly the 681 names in `exports.txt`.
- `librapier-arm64.so` and `librapier-mt-arm64.so` are not in the repository yet. They are built by `rapier.yml` (the
  `ubuntu-24.04-arm` runner); copy the `librapier-arm64` and `librapier-mt-arm64` artifacts into `bin/lib64` after its
  first successful run. Until then the dllmap lines for arm64 point at files that do not exist, and
  `RapierNative.TryLoad` reports that the library could not be loaded.

## Building

- Linux: `ThirdParty/rapier/build-linux.sh /abs/path/out`, and again with `mt` after the directory. Needs git, a C compiler (the linker) and rustup with the
  toolchain named in `source.env` installed (`rustup toolchain install 1.99.1`). Build on Debian 12.
- Windows: `ThirdParty\rapier\build-windows.ps1 -OutDir out`, and again with `-Parallel`. Needs git, rustup with the `source.env` toolchain and its
  `x86_64-pc-windows-msvc` target installed, and the C++ build tools of Visual Studio 2022 (run it from a "x64 Native
  Tools" prompt if a newer preview Visual Studio is installed that has no C++ libraries).

## The Parry patch

BulletS builds every mesh as a Rapier trimesh with `FIX_INTERNAL_EDGES` (flags 144), so that a ball or a box slides
across the seams between triangles without catching on them. Unpatched, Parry keeps for that a pseudo-normal per vertex
and three per triangle: about 42 bytes per triangle, roughly 100 MB on a large region (DragonFyre, 2.6 M triangles).

ubODE solves the same problem with a one-off preprocess (`dGeomTriMeshDataPreprocess`) that keeps a byte of edge flags
per triangle. `patches/parry3d-0.31.1-edge-codes.patch` does the same for Parry, keeping Parry's own rule:

- Each edge pseudo-normal is the normalised sum of the normals of the triangles that share the edge. All of them are
  orthogonal to the edge, so it is one angle about the edge, measured from the triangle's own normal.
- With `FIX_INTERNAL_EDGES` and not `ORIENTED`, the patch keeps, instead of the pseudo-normals, one `u32` per triangle:
  that angle for each of the three edges, in 10 bits (steps of 0.18 degrees). A flat seam or a boundary is exactly 0, as
  before.
- `TriMesh::triangle_normal_constraints`, the one function contact generation reads them through, decodes them.
- `ORIENTED` meshes keep the full pseudo-normals, which point-containment tests need.
- Also fixed: flags with `MERGE_DUPLICATE_VERTICES` alone no longer compute pseudo-normals, and `TriMesh::scaled`
  recomputes the edge data for the new shape.
- Visible change: `TriMesh::pseudo_normals()` is `None` for a mesh built with `FIX_INTERNAL_EDGES` alone, so Rapier's
  debug renderer draws no pseudo-normals for it.
- One case is not reproduced. Some edges are shared by three or more triangles, such as a flat fold (two faces with
  opposite normals) plus a third face. There, the sum can be more than 90 degrees from a triangle's normal. The angle
  is stored as 90 degrees, so contacts at that edge are unconstrained, as without `FIX_INTERNAL_EDGES`. Parry would
  still narrow the cone there.

The cost of the internal-edge data falls from about 42 to 4 bytes per triangle. Precision:

- Each stored edge pseudo-normal is within 0.088 degrees (half a step) of Parry's. `edge-codes-test` allows 0.12, for
  f32 rounding.
- On flat tiles they are exactly Parry's.
- In contact generation, a direction that the cone corrects is reflected, which magnifies the stored error. Across
  many random directions on a noisy mesh, the corrected normals were within 0.17 degrees of unpatched Parry's for 99 %
  of them.
- Rapier runs with the patch and without it ended within 12 mm of each other over 5 s on a curved mesh,
  and identically on flat tiles. `MeshSeamTests` in `tests/DreamGrid.Physics.Rapier.Tests` checks this on a flat and
  a curved floor.

`edge-codes-test` checks that `triangle_normal_constraints` gives what Parry's own pseudo-normals give on these
meshes:

- a tiled floor and a curved mesh;
- creases;
- slivers, including needles across a crease, and degenerate triangles;
- non-manifold edges;
- two-sided meshes.

It also checks the memory, `set_vertices`, the transforms, scaling and reversal.

To run it by hand (from this folder, against an extracted and patched Parry):
`cargo +1.99.1 test --release --manifest-path edge-codes-test/Cargo.toml --config "patch.crates-io.parry3d.path='<dir>'"`.
Without the `--config` argument, Cargo cannot resolve `parry3d`, because the lockfile records it as a patched path
dependency.

How the scripts use it:

1. They download `parry3d-$PARRY_VERSION.crate` from crates.io and check its SHA-256 against `PARRY_CRATE_SHA256` in
   `source.env`.
2. They extract it outside the Rapier checkout and `git apply` the patch. **The build stops if the patch does not
   apply**, or if `src/shape/trimesh.rs` does not have the patch's `EDGE-CODES PATCH` marker afterwards.
3. They run `edge-codes-test`. The build stops unless every `#[test]` in the file ran and passed.
4. They build Rapier with `--config patch.crates-io.parry3d.path=<that directory>`.

`Cargo.lock` therefore has no `source` or `checksum` line for `parry3d`: Cargo records a patched path dependency that
way, and the scripts' SHA-256 check takes its place.

On a Parry upgrade (a new Rapier usually brings one):

1. Download and extract the new `parry3d-X.Y.Z.crate`, and commit it to a scratch git repository.
2. Try `git apply --3way` with the old patch, or re-apply its hunks by hand. Every hunk is in `src/shape/trimesh.rs` and
   marked `EDGE-CODES PATCH`. The one that matters is in `triangle_normal_constraints`; the others keep the codes in
   step with the mesh.
3. Write the result with `git diff > patches/parry3d-X.Y.Z-edge-codes.patch`.
4. Set `PARRY_VERSION`, and `PARRY_CRATE_SHA256` (`sha256sum` of the `.crate`, which is also the checksum the new
   `Cargo.lock` lists before the edit).
5. Point `edge-codes-test/Cargo.toml` at the new version.
6. Regenerate both lockfiles. Delete the `source` and `checksum` lines of `parry3d` from the new `Cargo.lock`, or let
   `cargo` write them with the `--config` patch.
7. Run either script.

If Parry upstream gains an equivalent, drop the patch: remove the download and patch steps, `edge-codes-test` and
the `--config` argument, and restore `Cargo.lock`'s `parry3d` lines.

## What is pinned

A rebuild must produce the same code as the committed binaries, so three things are fixed:

- the Rapier source: `RAPIER_COMMIT` in `source.env` (the commit `v0.36.1` points to);
- every dependency version: `Cargo.lock` in this folder. Rapier's repository has no lockfile, so both scripts copy this one
  into the checkout and build with `cargo build --locked` (the build fails if the lockfile no longer fits);
- Parry's source: `PARRY_VERSION` and `PARRY_CRATE_SHA256` in `source.env`, plus the patch (see above);
- the Rust compiler: `RUST_TOOLCHAIN` in `source.env` (1.99.1). The scripts run `cargo +1.99.1`, and `rapier.yml`
  installs exactly that version with rustup.

One dependency is not from crates.io: Rapier's workspace patches `puffin_egui` (0.29.0) to a git repository
(github.com/tedsteen/puffin), and `Cargo.lock` pins it to rev `11771ebe00fd257aedbb545df3339ad597b1cc34`. The build is
therefore not hermetic: it still needs network access to fetch crates.io and that git repository.

The x86_64 Linux libraries rebuilt from these pins in a `debian:12` container are byte-identical to the committed ones,
whoever builds them. This was checked by building both twice, as root and with `HOME=/github/home` as GitHub's
container jobs have it.

Rust records the source paths of dependencies in the library, so `build-linux.sh` builds everything under one fixed
directory, `/tmp/rapier-build-<variant>`, with Cargo's downloads there too (`CARGO_HOME`). The directory doubles as a
lock: a second build of the same variant on one machine stops at once.

`--remap-path-prefix` cannot do this job, because its text is hashed into the build. Remapping a random directory
therefore still changes the library.

The Windows DLL maps its build and Cargo directories to `/build` and `/cargo`, so it names no user or temporary path.
It still differs from build to build, so compare its exports and imports, not its bytes.

To bump Rapier or Rust: change `RAPIER_TAG`/`RAPIER_COMMIT` and/or `RUST_TOOLCHAIN`; fetch the new commit in a scratch
checkout (same sparse set as the scripts), delete `Cargo.lock` there, run `cargo +NEWVERSION generate-lockfile`, and copy
it here; replace `include/rapier.h`, run `sh gen-exports.sh`, and update `RapierNative.cs` for any header change; run
`rapier.yml`, then copy its artifacts (both builds) into `bin/lib64`.


## What the build checks

For each of the two builds:

```sh
nm -D --defined-only librapier-x86_64.so | awk '$3 ~ /^r3/ {print $3}' | LC_ALL=C sort | diff - ThirdParty/rapier/exports.txt
objdump -T librapier-x86_64.so | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1     # at most GLIBC_2.36
dumpbin /dependents rapier.dll                                                    # KERNEL32, ntdll, api-ms-win-core-*, bcrypt only
```

At run time `RapierNative.VerifyAbi()` asks the library (`r3CheckAbi`, `r3PodLayout`) whether its structures have the
sizes `RapierNative.cs` assumes; the engine must not be used if it returns a message.
