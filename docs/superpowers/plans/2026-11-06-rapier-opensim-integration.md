# Rapier physics engine for OpenSim: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Region owners can pick **Rapier** as a physics engine in DreamGrid, next to ubODE and BulletSim. A
region set to Rapier runs on Rapier 0.36.1 with the same prims, linksets, vehicles, avatars, scripts and terrain
behaviour BulletSim gives today.

**Architecture:**

- **Where it lives.** Rapier becomes a third backend under BulletS's engine interface, `BSAPITemplate`, next to
  `BSAPIUnman` (native Bullet) and `BSAPIXNA` (managed Bullet). All of BulletS's C# stays as it is: prims, linksets,
  vehicles (`BSDynamics`), avatar movement, actors and terrain management. The backend translates its 184 engine
  calls into Rapier's official C API (`rapier.h`, rapier 0.36.1's `bindings/c`), called over P/Invoke from a native
  library we build and ship in `bin/lib64`, the same way ubODE's library is shipped.
- **How it's selected.** The launcher offers "Rapier" and writes `physics = BulletSim` plus
  `[BulletSim] BulletEngine = Rapier`.

**Tech stack:**

- **C#:** .NET 10, P/Invoke with `DllImport` and Cdecl, blittable structs.
- **Native:** Rust stable with Cargo, building Rapier's C bindings into a cdylib.
- **Tests and CI:** xunit.v3 for tests, GitHub Actions for the native builds.

**Spec:** these documents carry the design; read all three before starting.

- [`docs/superpowers/specs/2026-11-06-rapier-bsapi-map.md`](../specs/2026-11-06-rapier-bsapi-map.md): the
  method-by-method map. Sections H1–H12 are the design for the hard parts.
- [`docs/superpowers/specs/2026-11-06-rapier-early-results.md`](../specs/2026-11-06-rapier-early-results.md): why
  we're doing this, and the benchmark numbers the result has to stay close to.
- [`docs/superpowers/plans/2026-11-06-rapier-feasibility.md`](2026-11-06-rapier-feasibility.md): the go/stop gates.
  This plan is that list's Phase 3 (the in-OpenSim prototype), taken to a shippable opt-in feature.

## Global Constraints

- **Version pin:** rapier `bindings/c` at tag **v0.36.1**, pinned by commit in `ThirdParty/rapier/source.env`.
  Bumping it is its own change.
- **Library name:** the native library name in `DllImport` is `"rapier"`. The files are:
  - `bin/lib64/librapier-x86_64.so` (Linux x64);
  - `bin/lib64/librapier-arm64.so` (Linux arm64);
  - `bin/lib64/rapier.dll` (Windows x64).

  There's no macOS build; on macOS the launcher doesn't offer Rapier.
- **Portability of the native builds:**
  - The Linux libraries build in a `debian:12` container, so they need glibc 2.36 at most.
  - The Windows DLL is built with `RUSTFLAGS=-C target-feature=+crt-static`, so it imports no VC++ runtime DLLs.
  - The Rapier `parallel` feature is on in every build.
- **Marshalling:**
  - `R3Bool` is a `uint`. Never marshal it as a C# `bool`.
  - Handles are 16-byte value structs copied from `rapier.h`, never `IntPtr`.
  - At start-up, `r3CheckAbi` and `r3PodLayout` must pass before any other call.
- **Threading:** every Rapier call happens on the thread that runs `BSScene`'s taints and step, or under
  `PhysicsEngineLock`, which is what `RayTest2` uses today. No Rapier call is made from anywhere else.
- **Defaults stay put:**
  - ubODE stays the default physics engine.
  - BulletSim's default `BulletEngine` stays `BulletUnmanaged`.
  - Nothing in `BSAPIUnman`, `BSAPIXNA` or ubOde changes behaviour.
- **Labelling:** Rapier is labelled **"Rapier (experimental)"** everywhere the website names it, until the owner's
  Phase 4 in-world checks pass.
- **Licence:** Apache-2.0. The licence text goes in `ThirdParty/rapier/LICENSE`, along with Rapier's NOTICE if
  the tag has one.
- **Bench code:** none of the bench-only code (frame logger, ubODE timers, `rbench`) goes into this branch's
  product code.

## Review Focus

These five are the failure modes most likely to hurt a real region that no feature test covers by accident. Each
one has a test pinned to its task.

1. **Varregion terrain.** A 512 × 512 region with sloped terrain has to collide where the heightmap says, at the
   corners as well as the middle, or objects sink or float on big regions. Pinned in Task 3:
   `Terrain_Varregion512_RaysHitHeightmapAtCorners`.
2. **Deleting a prim while it's touching something** (a subscribed collider in the middle of a contact). The next
   step must not crash or report a stale ID. Pinned in Task 5: `RemoveTouchingSubscribedPrim_NextStepIsClean`.
3. **More updates or collisions than the arrays hold.** With more moving bodies than `MaxUpdatesPerFrame` or more
   pairs than `MaxCollisionsPerFrame`, nothing overruns the array. The leftover updates are reported on the next
   frame, and collisions are cut off at the cap. Pinned in Task 4:
   `Updates_MoreThanArray_TruncatesAndCarriesOver`. Pinned in Task 5: `Collisions_MoreThanArray_Truncates`.
4. **A prim switching between physical and static** while it sleeps or is selected (`MakeDynamic`,
   `DISABLE_SIMULATION`). The handle has to survive, and the body must not stay frozen or drift. Pinned in Task 6:
   `PhysicalToStaticAndBack_WhileSleeping_KeepsHandleAndWakes`.
5. **A missing or wrong native library** (an old `rapier.dll`, or the file not shipped). The region log must say
   exactly what's wrong, and BSScene must not crash with an unclear `DllNotFoundException`. Pinned in Task 1:
   `VerifyAbi_ReportsMismatch`. Pinned in Task 2: `MissingLibrary_SelectEngineLogsAndReturnsNull`.

---

## File structure

```
ThirdParty/rapier/
  source.env                 RAPIER_REPO, RAPIER_TAG=v0.36.1, RAPIER_COMMIT=<sha>
  build-linux.sh             fetch the pinned commit, cargo build bindings/c --release --features parallel, strip, copy to an output path
  build-windows.ps1          same for MSVC x64 with crt-static
  include/rapier.h           the header from the pinned commit (reference + export list source)
  exports.txt                sorted r3* function names extracted from rapier.h (CI compares against this)
  LICENSE, README.md         licence; how to rebuild, what's pinned, how to bump
.github/workflows/rapier.yml linux x64/arm64 (debian:12 container) + windows x64; export and import checks; uploads libs
bin/lib64/librapier-x86_64.so, librapier-arm64.so, rapier.dll   built by rapier.yml, committed (as for ubODE)
bin/OpenSim.Region.PhysicsModule.BulletS.dll.config             + dllmap lines for "rapier"

OpenSim/Region/PhysicsModules/BulletS/Rapier/
  RapierNative.cs            DllImports for the r3* functions used + blittable structs, copied from rapier.h; VerifyAbi()
  BSAPIRapier.cs             class BSAPIRapier : BSAPITemplate (partial); ctor, names, version, wrappers, world lifetime
  BSAPIRapier.Step.cs        PhysicsStep: substeps, update gathering (H1), collision gathering (H2), force reset
  BSAPIRapier.Shapes.cs      native shapes, mesh, hull, compound-as-colliders, scaling, shape cache (H5a, H9)
  BSAPIRapier.Terrain.cs     ground plane + heightfield (H8)
  BSAPIRapier.Bodies.cs      body lifetime, pose, velocity, forces, mass, materials, damping, gravity, CCD (H6, H7, H11)
  BSAPIRapier.Flags.cs       collision flags, activation states, filter groups, sensors (H3, H4)
  BSAPIRapier.Constraints.cs 6-DOF, fixed and spring joints, world anchor, limits, motors, breaking (H5b)
  BSAPIRapier.Queries.cs     RayTest2 and the remaining misc/debug members
  RapierWorldState.cs        per-world C# state: last-sent states, wants-collisions set, never-sleep set,
                             forced-update set, hidden-child set, world-anchor body, shape table
OpenSim/Region/PhysicsModules/BulletS/BSScene.cs                 + case "rapier" in SelectUnderlyingBulletEngine
OpenSim/Region/PhysicsModules/BulletS/BSParam.cs                 + RapierNumThreads, RapierSolverIterations
OpenSim/Region/PhysicsModules/BulletS/OpenSim.Region.PhysicsModule.BulletS.csproj  + Compile items (EnableDefaultItems is false)

tests/DreamGrid.Physics.Rapier.Tests/
  DreamGrid.Physics.Rapier.Tests.csproj   xunit.v3; references BulletS, SharedBase, Meshing, Framework; copies bin/lib64/** and the BulletS .dll.config to output
  RapierTestScene.cs         builds a BSScene with BulletEngine=Rapier (pattern: BulletS/Tests/BulletSimTestsUtil.cs) and steps it
  NativeTests.cs, WorldAndStepTests.cs, TerrainTests.cs, UpdateTests.cs, CollisionTests.cs,
  BodyStateTests.cs, ShapeTests.cs, LinksetTests.cs, ConstraintTests.cs, AvatarTests.cs, CoverageTests.cs

Launcher/DreamGrid.Launcher.Core/Settings/RegionSettingsRules.cs  + "Rapier" engine, platform check, message
Launcher/DreamGrid.Launcher.Core/Settings/SettingLabels.cs        + "Rapier (experimental)"
Launcher/DreamGrid.Launcher.Core/Config/GridConfigWriter.cs       + Rapier → BulletSim + BulletEngine=Rapier + Meshmerizer
tests/DreamGrid.Smoke/SmokeRun.cs, Program.cs                     + --physics Rapier
.github/workflows/dreamgrid-ci.yml                                + Rapier unit tests and smoke step
```

**Why it's split this way:**

- `BSAPIRapier` is one class, because `BSScene` holds a single `BSAPITemplate`. Making it `partial` keeps each file
  about one part of the map, at a few hundred lines.
- `RapierNative.cs` is the only file that knows `rapier.h`'s layout.
- `RapierWorldState.cs` holds all the C#-side state the emulations need, so the partial files share one owner.

**Shared names.** Every task uses these:

- **Nested wrapper classes** in `BSAPIRapier`, following `BSAPIXNA`'s pattern:
  - `BulletWorldRapier : BulletWorld` holds `R3World World` and `RapierWorldState State`.
  - `BulletBodyRapier : BulletBody` holds `R3RigidBodyHandle Body` and `List<R3ColliderHandle> Colliders`.
  - `BulletShapeRapier : BulletShape` holds `R3SharedShape Shape`, `Vector3 Scale`, the
    `List<(BulletShapeRapier child, Vector3 pos, Quaternion rot)> Children` for compounds, and `float[] MeshVerts`
    with `int[] MeshIndices` for hull rebuilds.
  - `BulletConstraintRapier : BulletConstraint` holds `R3ImpulseJointHandle Joint`.
- **The engine name:** `BSAPIRapier.BulletEngineName` returns `"Rapier"`. `BulletEngineVersion` returns
  `r3Version()`, plus `"+parallel"` when `r3BuildFeatures` says the library was built with the `parallel` feature.
- **The test helper:**
  - `RapierTestScene.Create(Dictionary<string,string> overrides = null, uint regionSize = 256)` returns `BSScene`.
  - `RapierTestScene.Step(BSScene s, float seconds)` calls `s.Simulate(0.0909f)` until `seconds` have passed.

---

### Task 1: Native library build, shipping, and the P/Invoke layer

**Files:**
- Create: everything under `ThirdParty/rapier/`, `.github/workflows/rapier.yml`
- Create: `OpenSim/Region/PhysicsModules/BulletS/Rapier/RapierNative.cs`
- Modify: `bin/OpenSim.Region.PhysicsModule.BulletS.dll.config`, `OpenSim/Region/PhysicsModules/BulletS/OpenSim.Region.PhysicsModule.BulletS.csproj`
- Create: `tests/DreamGrid.Physics.Rapier.Tests/` (csproj + `NativeTests.cs`); add the project to `DreamGrid.sln`
- Ship: `bin/lib64/librapier-x86_64.so`, `librapier-arm64.so`, `rapier.dll`

**Interfaces:**
- Produces, in `internal static class RapierNative`:
  - `const string Lib = "rapier"`.
  - Every struct and `r3*` function later tasks use, copied verbatim from `include/rapier.h`. Field order and types
    match exactly, `R3Bool` is `uint`, and handles are 16-byte structs.
  - `static string VerifyAbi()` returns null when `r3CheckAbi` and `r3PodLayout` agree with this file's struct
    sizes. Otherwise it returns a message that names the first struct that disagrees, with the expected and actual
    sizes.
  - `static bool TryLoad(out string error)`. It loads the library from the path in the BulletS `.dll.config`
    mapping, unless `internal static string LibraryPathOverride` is set (tests use the override).

- [ ] **Step 1: Pin and fetch.** Write `source.env` with the v0.36.1 commit SHA from the upstream tag. Write
  `build-linux.sh OUTDIR` and `build-windows.ps1 -OutDir`, using the `ThirdParty/ubODE` scripts from PR 48 as the
  model:
  - an absolute output path;
  - a sparse fetch of `bindings/c` and the crates it needs, at the pinned commit;
  - `cargo build --release --features parallel`;
  - strip the result, then rename it to the file names in Global Constraints.

  Copy `rapier.h` to `include/`, and generate `exports.txt` from it (every `r3`-prefixed function declaration,
  sorted).
- [ ] **Step 2: Run the Linux build locally** (WSL or the VM): `ThirdParty/rapier/build-linux.sh /tmp/rapier-out`.
  Expected:
  - `nm -D --defined-only /tmp/rapier-out/librapier-x86_64.so | awk '$3 ~ /^r3/ {print $3}' | sort` is identical to
    `exports.txt`;
  - `objdump -T` shows no GLIBC symbol newer than 2.36.
- [ ] **Step 3: Write `rapier.yml`.** It has three jobs: Linux x64 and arm64 in a `debian:12` container, and
  Windows x64. Each job runs the export check from Step 2. The Windows job also runs an import check:
  `dumpbin /dependents` lists only KERNEL32, ntdll and api-ms-win-core-* or bcrypt, with no VCRUNTIME and no
  MSVCP. Each job uploads its library as an artifact. Push and run it.
  Expected: all three jobs green.
- [ ] **Step 4: Ship the libraries.** Copy the three artifacts into `bin/lib64`. Add these dllmap lines to the
  BulletS `.dll.config`:
  - `os="windows" cpu="x86-64" dll="rapier" target="lib64/rapier.dll"`;
  - `os="!windows,osx" cpu="x86-64" dll="rapier" target="lib64/librapier-x86_64.so"`;
  - `os="!windows,osx" cpu="arm64" dll="rapier" target="lib64/librapier-arm64.so"`.
- [ ] **Step 5: Write the failing tests** in `NativeTests.cs`:
  - `Version_Is_0_36_1`: `RapierNative.r3Version()` (as a string) starts with `"0.36.1"`.
  - `VerifyAbi_PassesWithShippedLibrary`: `RapierNative.VerifyAbi()` is null.
  - `VerifyAbi_ReportsMismatch`: run the size comparison that `VerifyAbi()` uses with one expected size changed,
    through an `internal static string CompareLayout(IReadOnlyList<(string name, int expected, int actual)>)`.
    The returned message contains that struct's name and both sizes.
  - `BoxFallsUnderGravity`: through RapierNative only, create a world with gravity (0, 0, −9.8) and a dynamic
    1 m box at z = 100, and step 50 × 0.02 s. The box's z is 100 − ½·9.8·1² within 2%, and its x and y are
    unchanged.
- [ ] **Step 6: Run the tests and confirm they fail.**
  `dotnet test --project tests/DreamGrid.Physics.Rapier.Tests -c Release`.
  Expected: they fail to compile, because `RapierNative` isn't written yet.
- [ ] **Step 7: Implement `RapierNative.cs`** with only the functions the four tests need, plus the world, body,
  collider and step functions Task 2 needs. Later tasks add declarations as they need them.
- [ ] **Step 8: Run the tests again.** Expected: all 4 pass on Windows x64 and Linux x64.
- [ ] **Step 9: Commit** with the message `rapier: pinned native build, shipped libs, P/Invoke layer`.

### Task 2: BSAPIRapier skeleton, engine selection, bodies falling in a BSScene

**Files:**
- Create: `BulletS/Rapier/BSAPIRapier.cs`, `BSAPIRapier.Step.cs` (bodies only), `BSAPIRapier.Bodies.cs`,
  `BSAPIRapier.Shapes.cs` (native shapes only), `RapierWorldState.cs`, and empty partials for the rest
- Modify: `BulletS/BSScene.cs:440-474` (`SelectUnderlyingBulletEngine`), `BulletS/BSParam.cs`, BulletS csproj
- Test: `tests/DreamGrid.Physics.Rapier.Tests/RapierTestScene.cs`, `WorldAndStepTests.cs`

**Interfaces:**
- Consumes: `RapierNative` (Task 1).
- Produces:
  - `public sealed partial class BSAPIRapier : BSAPITemplate` with constructor `BSAPIRapier(string paramName, BSScene physScene)`.
  - `RapierWorldState`, the four wrapper classes, and `RapierTestScene`.
  - BSParam entries: `RapierNumThreads` (float, default 1, "Threads Rapier uses inside a step") and
    `RapierSolverIterations` (float, default 4).

- [ ] **Step 1: Write the failing tests** in `WorldAndStepTests.cs`:
  - `SelectsRapier`: `RapierTestScene.Create()` returns a scene whose `PE.BulletEngineName == "Rapier"` and whose
    `EngineName` contains `"0.36.1"`.
  - `PhysicalBoxFreeFalls`: add a physical 0.5 m box prim at (128, 128, 200) through `scene.AddPrimShape`, then
    step 1.0 s. The prim's `Position.Z` is 200 − 4.9 within 3%, and `Velocity.Z` is −9.8 within 3%.
  - `StaticBoxDoesNotMove`: a non-physical box at z = 200 is still at z = 200 after 1 s.
  - `MissingLibrary_SelectEngineLogsAndReturnsNull`: with `RapierNative.LibraryPathOverride` set to a path that
    doesn't exist, `SelectUnderlyingBulletEngine("Rapier")` returns null and logs a line containing "Rapier native library".
    `SelectUnderlyingBulletEngine` is private; reach it through a new
    `internal static BSAPITemplate SelectEngineForTest` seam.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Add the engine case and the class skeleton.**
  - **In `SelectUnderlyingBulletEngine`:** add `case "rapier":`. It calls `RapierNative.TryLoad`, then
    `RapierNative.VerifyAbi()`. On failure it logs the message and returns null. On success it constructs
    `BSAPIRapier`.
  - **Before adding the case, fix the hyphen bug:** the selection name must be `Substring(0, hyphenIndex)`. Today
    it is `hyphenIndex - 1`, which chops the last letter.
  - **Every member of `BSAPIRapier`** this task doesn't implement throws
    `new NotImplementedException("BSAPIRapier." + nameof(Member))`.
- [ ] **Step 4: Implement the world.**
  - **`Initialize`:**
    - create the world, with gravity from `parms.gravity`;
    - set the solver iterations from `RapierSolverIterations`;
    - set the threads from `RapierNumThreads`;
    - keep references to the update and collision arrays.
  - **`Shutdown`:** free the world and every cached shape.
  - **`PhysicsStep`, bodies only:**
    - run up to `maxSubSteps` steps of `r3Step` at `fixedTimeStep`, accumulating as BSAPIXNA does;
    - fill the update array from `r3ActiveRigidBodies` + `r3RigidBodyReadStates`, reporting every active body for
      now (Task 4 adds the tolerances);
    - set `collidersCount = 0`.
- [ ] **Step 5: Implement bodies and native shapes.**
  - **Native shapes:** `BuildNativeShape` for box, sphere, cylinder and capsule, built at final size (H9).
  - **Body lifetime:** `CreateBodyFromShape`, `AddObjectToWorld`, `RemoveObjectFromWorld`, `IsInWorld`,
    `DestroyObject`.
  - **Pose and velocity setters and getters:** `SetTranslation`, `GetPosition`, `GetOrientation`,
    `SetLinearVelocity`, `GetLinearVelocity`, `SetAngularVelocity`, `GetAngularVelocity`.
  - **Mass:** `SetMassProps`, `UpdateInertiaTensor`, `CalculateLocalInertia`.
  - **Gravity:** `SetGravity`, through the gravity scale (H6).
  - **userData** holds the localID.

  Implement any further members the free-fall test hits, and leave the rest throwing.
- [ ] **Step 6: Run the tests again.** Expected: all pass. Any `NotImplementedException` names the next member to
  implement in this task.
- [ ] **Step 7: Commit** with the message `rapier: BSAPIRapier skeleton, engine selection, falling bodies`.

### Task 3: Terrain and ray casts (H8)

**Files:**
- Modify: `BSAPIRapier.Terrain.cs`, `BSAPIRapier.Queries.cs`
- Test: `TerrainTests.cs`

**Interfaces:**
- Consumes: Task 2.
- Produces: `CreateGroundPlaneShape`, `CreateTerrainShape`, `RayTest2`. The terrain's world-Z offset is stored in
  `RapierWorldState.TerrainZOffset`.

- [ ] **Step 1: Write the failing tests.**
  - `Terrain_Flat_BoxRestsOnSurface`: on flat terrain at 21 m, a physical box dropped from 30 m rests with its
    centre at 21.25 m ± 0.05 after 3 s.
  - `Terrain_Sloped_RaysHitHeightmap`: give the heightmap h(x, y) = 20 + 0.1·x + 0.05·y. `RayTest2` straight down
    at (10, 10), (128, 128) and (250, 200) hits at h within 0.05.
  - `Terrain_Varregion512_RaysHitHeightmapAtCorners`: do the same with a 512 × 512 region, at (1, 1), (510, 1),
    (1, 510), (510, 510) and (256, 256).
  - `Ray_Filter_IgnoresMaskedGroup`: a ray whose filter mask excludes the static-object group passes through a
    static box and hits the terrain.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement it as H8 describes.**
  - Copy the heightmap into column-major order.
  - Rotate the collider +90° about X and flip the row order.
  - Add a Z offset that undoes Bullet's centring between minZ and maxZ.
  - Use 1 m sample spacing.
  - Build with `R3_HEIGHTFIELD_FIX_INTERNAL_EDGES`.
  - `CreateGroundPlaneShape` is a thin cuboid (half-height 0.5) whose top is at `height`.
  - `RayTest2` uses the query filter with `use_groups = 1` and returns the localID from the collider's userData.

  If `sizeX − 1` versus `sizeX` metres doesn't match, the corner tests will show which one Bullet uses. Match it.
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit** with the message `rapier: heightfield terrain, ground plane, ray tests`.

### Task 4: Update reporting (H1)

**Files:**
- Modify: `BSAPIRapier.Step.cs`, `RapierWorldState.cs`
- Test: `UpdateTests.cs`

**Interfaces:**
- Produces:
  - `RapierWorldState.LastSent: Dictionary<uint, EntityProperties>`;
  - `ForcedUpdates: HashSet<uint>`;
  - `PendingUpdates: Queue<uint>` for carry-over;
  - `PushUpdate`.

- [ ] **Step 1: Write the failing tests.**
  - `Updates_StillBodyStopsReportingAfterSleep`: a box resting on terrain stops appearing in the update array
    within 5 s. Its last reported update has zero linear and angular velocity.
  - `Updates_BelowTolerance_NotReported`: a body that moves less than BulletSim's tolerances isn't reported in
    that frame. Copy the exact tolerance values from BulletSim's `SimMotionState::setWorldTransform`
    (`BulletSim.h`), and put them in the test as named constants.
  - `Updates_PushUpdate_AlwaysReported`: a sleeping body is reported on the next step after `PushUpdate`.
  - `Updates_MoreThanArray_TruncatesAndCarriesOver`: create 3,000 falling bodies with `maxUpdates` = 1,000. The
    first step reports exactly 1,000 without an exception. Over the next three steps, every body is reported at
    least once.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement it as H1 describes.**
  - Compare each body against `LastSent` with the tolerances.
  - Send one final zero-velocity update for bodies that left the active set.
  - Merge in `ForcedUpdates`.
  - Report from `PendingUpdates` first, then new ones, up to the array length. The overflow goes back into
    `PendingUpdates`.
  - Apply the `BS_RETURN_ROOT_COMPOUND_SHAPE` child-0 offset when that flag is set.
- [ ] **Step 4: Run the tests again.** Expected: all pass, and Task 2's tests still pass.
- [ ] **Step 5: Commit** with the message `rapier: BulletSim-compatible update reporting`.

### Task 5: Collision flags, collision reporting, volume detect (H2, H3 flags, H4)

**Files:**
- Modify: `BSAPIRapier.Flags.cs`, `BSAPIRapier.Step.cs`, `RapierWorldState.cs`
- Test: `CollisionTests.cs`

**Interfaces:**
- Produces:
  - `RapierWorldState.WantsCollisions: HashSet<R3ColliderHandle>`;
  - `SetCollisionFlags`, `AddToCollisionFlags`, `RemoveFromCollisionFlags`, `GetCollisionFlags`;
  - `CreateGhostFromShape`;
  - the collision array, filled once per pair per frame.

- [ ] **Step 1: Write the failing tests.**
  - `Collisions_SubscribedPrimOnTerrain_Reported`: a box with `BS_SUBSCRIBE_COLLISION_EVENTS` that lands on terrain
    produces a CollisionDesc pair with the terrain's ID. The normal's Z is above 0.9 and the penetration is ≥ 0.
  - `Collisions_UnsubscribedPair_NotReported`: two boxes without the flag touch, and the count stays 0.
  - `Collisions_NoContactResponse_PassesThroughButReports`: a subscribed box with `CF_NO_CONTACT_RESPONSE` falls
    through a static box. It's still above terrain afterwards, and a pair is reported.
  - `VolumeDetect_SensorReportsOverlap_NoForce`: a static volume-detect prim reports a dynamic box falling
    through it, and the box's velocity isn't changed by it.
  - `RemoveTouchingSubscribedPrim_NextStepIsClean`: remove a subscribed box while it's resting on another subscribed
    box, then step 1 s. There's no exception, and no pair contains the removed ID.
  - `Collisions_MoreThanArray_Truncates`: with `maxCollisions` = 100 and 400 subscribed boxes in a pile,
    `collidersCount` is exactly 100 and nothing throws.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement the flags and collision gathering.**
  - **Flags:** follow H3's flag table.
  - **Collisions:** follow H2. After each substep, walk `WantsCollisions` with `r3Collider_ContactPairs` and
    `r3ContactPoints`. Take the deepest point in world space, and keep each pair once per frame. Add sensor
    overlaps from `r3IntersectionPairs`.
  - **Sensors:** sensor colliders get the wider `activeCollisionTypes` from H4.
  - **Removal:** removing a body removes its colliders from `WantsCollisions` first.
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit** with the message `rapier: collision flags, collision reporting, volume detect`.

### Task 6: Activation, filter groups, materials, damping, factors, CCD (H3, H6, H7, H11)

**Files:**
- Modify: `BSAPIRapier.Bodies.cs`, `BSAPIRapier.Flags.cs`
- Test: `BodyStateTests.cs`

**Interfaces:**
- Produces:
  - `ForceActivationState`, `SetCollisionGroupMask`;
  - friction and restitution setters, `SetDamping`;
  - `SetLinearFactor`, `SetAngularFactor`, `SetAngularFactorV`;
  - the CCD setters;
  - `ApplyCentralImpulse`, `ApplyCentralForce`, `ApplyTorque`, `ApplyTorqueImpulse`;
  - `RapierWorldState.NeverSleep`.

- [ ] **Step 1: Write the failing tests.**
  - `DisableDeactivation_BodyNeverSleeps`: a body at rest stays in the active set for 10 s.
  - `GroupMask_ExcludedPairDoesNotCollide`: two dynamic boxes whose groups exclude each other pass through each
    other.
  - `Friction_Zero_Slides_One_Stops`: on a 20° slope, a box with friction 0 slides more than 2 m in 2 s, and a box
    with friction 1 slides less than 0.1 m.
  - `LinearDamping_MatchesBulletDecay`: with damping 0.5 and no gravity, the speed after 1 s is
    v0 × (1 − 0.5)^1 within 3%. That's Bullet's formula, so this checks the conversion.
  - `AngularFactorZero_DoesNotRotate`: a torque impulse leaves the rotation unchanged.
  - `Buoyancy_GravityScale`: a body given gravity (0, 0, −4.9) accelerates at half of g.
  - `Forces_ClearedAfterStep`: `ApplyCentralForce` once, then step twice. The second step adds no velocity.
  - `PhysicalToStaticAndBack_WhileSleeping_KeepsHandleAndWakes`:
    - Put a box to sleep and make it static (CF_STATIC_OBJECT, mass 0), then make it dynamic again.
    - It should be the same `BulletBody` object (`HasPhysicalBody` true), and it should fall when the support
      under it is removed.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement it as H3, H6, H7 and H11 describe.**
  - Damping is converted from Bullet's (1 − d)^dt to Rapier's form.
  - Friction and restitution use `R3_COMBINE_MULTIPLY`.
  - Factors of 0 and 1 become locked axes. A fractional factor is logged once per body.
  - `NeverSleep` bodies are woken before each step.
  - Forces are reset after the step.
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit** with the message `rapier: activation, groups, materials, damping, factors, CCD`.

### Task 7: Mesh, hull and scaled shapes (H9)

**Files:**
- Modify: `BSAPIRapier.Shapes.cs`
- Test: `ShapeTests.cs`

**Interfaces:**
- Produces:
  - `CreateMeshShape`, `CreateGImpactShape`, `CreateHullShape`, `BuildHullShapeFromMesh`,
    `BuildConvexHullShapeFromMesh` and `CreateConvexHullShape`;
  - `SetLocalScaling` and `GetLocalScaling`;
  - `DeleteCollisionShape`, `IsNativeShape`, `GetShapeType` and `DuplicateCollisionShape`.

- [ ] **Step 1: Write the failing tests.**
  - `Trimesh_StaticFloor_SphereRests`: a 10 × 10 m two-triangle static mesh at z = 50, with a sphere dropped onto
    it. The sphere rests at 50 + r ± 0.03.
  - `Hull_OfCubeVerts_BehavesLikeBox`: a hull built from a unit cube's 8 vertices rests at the same height as a
    native box, ± 0.03.
  - `HullList_TwoHulls_BothCollide`: `CreateHullShape` gets two hulls 2 m apart, and a ray hits each one.
  - `SetLocalScaling_RebuildsShape`: scale a box to 2×. A ray hits at the new half-extent, ± 0.01.
  - `DeleteShape_FreesNative`: create and delete 1,000 shapes. The shape table is empty and nothing throws.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement it as H9 describes.**
  - Trimeshes use `R3_TRIMESH_FIX_INTERNAL_EDGES | R3_TRIMESH_MERGE_DUPLICATE_VERTICES`.
  - Hull lists are parsed in BulletSim's float[] layout. Read `BSShapes.cs` for the format.
  - Scaling builds a new shape and calls `r3Collider_SetShape`.
  - Keep the mesh vertices and indices on the wrapper for rebuilds.
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Measure scene build time.** Load DragonFyre on the bench with Rapier. Expected: region start-up is
  no more than 3 s slower than ubODE with PR 48 (5.6 s), the feasibility plan's B1 gate. Record the number in the
  early-results document.
- [ ] **Step 6: Commit** with the message `rapier: mesh, hull and scaled shapes`.

### Task 8: Compound shapes and linksets (H5a)

**Files:**
- Modify: `BSAPIRapier.Shapes.cs`, `BSAPIRapier.Bodies.cs`
- Test: `LinksetTests.cs`

**Interfaces:**
- Produces:
  - `CreateCompoundShape`, `AddChildShapeToCompoundShape`, `RemoveChildShapeFromCompoundShape` and
    `RemoveChildShapeFromCompoundShapeIndex`;
  - `GetChildShapeFromCompoundShapeIndex`, `GetNumberOfCompoundChildren` and `UpdateChildTransform`;
  - `SetCollisionShape`, `RecalculateCompoundShapeLocalAabb` (a no-op) and `SetAdditionalMassProperties`.

- [ ] **Step 1: Write the failing tests.**
  - `Linkset_ThreePrims_FallsAndRestsAsOne`: link three physical boxes through BSScene's normal linking path. They
    land on terrain together and keep their relative offsets within 0.01 m.
  - `Linkset_CollisionReportsRootId`: when a subscribed linkset's child touches terrain, the reported pair carries
    the root's localID.
  - `Linkset_UpdateChildTransform_MovesCollider`: move a child 1 m. A ray at the new position hits, and a ray at
    the old one misses.
  - `Linkset_NestedHullChild_Collides`: a child whose shape is a two-hull compound collides with both hulls.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement H5 option (b): one collider per child.**
  - All child colliders hang off the root body, and nested compounds are flattened.
  - Every collider's userData is the root's localID.
  - Colliders get density 0, and BulletS's mass goes in through `SetAdditionalMassProperties`.
  - `SetCollisionShape` replaces the body's colliders.
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit** with the message `rapier: compound shapes and linksets`.

### Task 9: Constraints, axis locks, springs (H5b)

**Files:**
- Modify: `BSAPIRapier.Constraints.cs`, `RapierWorldState.cs` (world-anchor body)
- Test: `ConstraintTests.cs`

**Interfaces:**
- Produces:
  - `Create6DofConstraint`, `Create6DofConstraintToPoint`, `Create6DofConstraintFixed` and
    `Create6DofSpringConstraint`;
  - `SetLinearLimits`, `SetAngularLimits`, `UseFrameOffset`, `TranslationalLimitMotor`, `SetBreakingImpulseThreshold`,
    `SpringEnable`, `SpringSetEquilibriumPoint`, `SpringSetStiffness`, `SpringSetDamping`, `AddConstraint`,
    `RemoveConstraint` and `DestroyConstraint`;
  - `RapierWorldState.WorldAnchor`.

  Take the full list of constraint members that are "called" from the map's Constraints table.

- [ ] **Step 1: Write the failing tests.**
  - `LockAxis_XTranslationLocked`: with BSActorLockAxis's 6-DOF to the world, an X impulse moves the body less
    than 0.001 m in X, while a Y impulse still moves it.
  - `Spring_ReturnsToEquilibrium`: a spring pulled 1 m off its equilibrium point is within 0.1 m of it after 5 s.
  - `Fixed_TwoBodiesStayTogether`: two bodies joined by a fixed constraint keep their offset within 0.01 m while
    they fall.
  - `Breaking_ImpulseThreshold_Breaks`: a fixed joint with threshold 1 is disabled after a 100 N·s impulse.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement it as H5 describes.**
  - Bullet's per-axis limits translate as: lo == hi means locked, lo < hi means limited, lo > hi means free.
  - Spring motors use model 1.
  - Breaking is checked against `r3ImpulseJoint_Impulses` after each step.
  - ERP and CFM map to softness approximately.
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit** with the message `rapier: constraints, axis locks and springs`.

### Task 10: Avatars (H10)

**Files:**
- Modify: whichever partials the avatar path still throws in; `BSParam.cs` only if the step defaults need Rapier
  values
- Test: `AvatarTests.cs`

**Interfaces:**
- Consumes: Tasks 2–6.
- Produces: BulletS's `BSCharacter` working unchanged on Rapier.

- [ ] **Step 1: Write the failing tests**, all through `scene.AddAvatar`:
  - `Avatar_StandsOnTerrain`: the avatar settles with its feet within 0.05 m of the terrain within 2 s, and doesn't
    sleep for 10 s.
  - `Avatar_WalksForward`: with `TargetVelocity` (1.5, 0, 0) for 3 s, it moves 4.5 m ± 0.7 in X and stays within
    0.1 m of the ground.
  - `Avatar_ClimbsStep`: it walks onto a 0.3 m static box step and ends on top, with z rising by 0.3 ± 0.05.
  - `Avatar_Flies`: with flying on and no target velocity, it holds its height within 0.2 m for 3 s.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement the remaining calls** that `BSCharacter` and `BSActorAvatarMove` make. If
  `Avatar_ClimbsStep` fails because of margins, tune the `AvatarStep*` defaults inside the backend when
  `BulletEngineName == "Rapier"`. Don't add a new actor. That's out of scope here and goes to a follow-up if
  Phase 4 needs it.
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit** with the message `rapier: avatars on BulletS's character code`.

### Task 11: Close every remaining member

**Files:**
- Modify: `BSAPIRapier.Queries.cs` and any partial still throwing
- Test: `CoverageTests.cs`

- [ ] **Step 1: Write the failing test.** `NoMemberThrowsNotImplemented` reads every `BSAPIRapier*.cs` file under
  `OpenSim/Region/PhysicsModules/BulletS/Rapier/` (located from the test assembly up to the repo root). It asserts
  that none of them contains `NotImplementedException`.
- [ ] **Step 2: Run it and confirm it fails.**
- [ ] **Step 3: Implement every member that's left** as the map's tables say:
  - no-op rows return the documented value;
  - the five gaps (`SetSleepingThresholds`, `SetDeactivationTime`, `SetAnisotripicFriction`,
    `CreateGearConstraint`, `SliderSet`) log once per world at Debug level and return a neutral value;
  - the debug members keep their empty defaults.
- [ ] **Step 4: Run all the tests in the project.** Expected: all pass.
- [ ] **Step 5: Commit** with the message `rapier: implement remaining BSAPITemplate members`.

### Task 12: Launcher option, smoke test, CI

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/RegionSettingsRules.cs:12,37-38`, `SettingLabels.cs:10`,
  `Config/GridConfigWriter.cs:136-139`
- Modify: `tests/DreamGrid.Smoke/Program.cs`, `SmokeRun.cs:137-139`, `.github/workflows/dreamgrid-ci.yml`
- Test: `Launcher/DreamGrid.Launcher.Tests/GridConfigWriterTests.cs`, a new `RegionSettingsRulesRapierTests`

**Interfaces:**
- Produces:
  - `RegionSettingsRules.PhysicsEngines` includes `"Rapier"`.
  - `RegionSettingsRules.RapierSupported(OSPlatform os, Architecture arch)` is true for Linux x64, Linux arm64 and
    Windows x64.
  - `SettingLabels.Physics("Rapier") == "Rapier (experimental)"`.

- [ ] **Step 1: Write the failing tests.**
  - `GridConfigWriter_Rapier_WritesBulletSimWithRapierEngine`: for a program with physics "Rapier", OpenSim.ini
    has:
    - `[Startup] physics = BulletSim`;
    - `[Startup] meshing = Meshmerizer`;
    - `[BulletSim] BulletEngine = Rapier`;
    - `[BulletSim] UseSeparatePhysicsThread = false`.
  - `GridConfigWriter_BulletSim_DoesNotWriteRapierEngine`: for a program with physics "BulletSim", OpenSim.ini has
    either no `BulletEngine` key or `BulletUnmanaged`.
  - `Program_Rapier_RejectedOnUnsupportedPlatform`: validation on macOS gives the Physics error "Rapier isn't
    available on this computer.", and the same physics is accepted on Linux x64.
  - `SettingLabels_Rapier_IsExperimental`.
- [ ] **Step 2: Run them and confirm they fail.**
  `dotnet test --project Launcher/DreamGrid.Launcher.Tests -c Release`.
- [ ] **Step 3: Implement the launcher changes.**
  - Add the engine and the platform check.
  - Change the error message to "Pick ubODE, BulletSim, BulletSimThreaded or Rapier, or use the grid default."
  - Write the config lines in `GridConfigWriter`.
- [ ] **Step 4: Add the smoke option.** `--physics Rapier` sets `physics = BulletSim`, `meshing = Meshmerizer`
  and `BulletEngine = Rapier`. The run passes when the region log contains
  `Selected bullet engine Rapier -> Rapier/0.36.1`.
- [ ] **Step 5: Add the CI steps** to both platform jobs in `dreamgrid-ci.yml`:
  - `dotnet test --project tests/DreamGrid.Physics.Rapier.Tests -c Release`;
  - `dotnet run --project tests/DreamGrid.Smoke -c Release -- --bin bin --keep --physics Rapier`.

  Push and run it. Expected: everything green.
- [ ] **Step 6: Commit** with the message `launcher: Rapier (experimental) physics option, smoke and CI`.

### Task 13: Bench and in-world gates (feasibility Phases 3 and 4)

This task is measurement only, with no code. Its results decide whether the "experimental" label comes off.

- [ ] **Step 1: Package a build and install it on the VM** using the steps in the VM memory.
- [ ] **Step 2: Run the physics matrix on Rapier.** First extend the VM's bench script `physrun.py`: accept the
  engine `BulletSim-Rapier` and have it write `physics=BulletSim` and `BulletEngine=Rapier`. Then run
  `physmatrix.sh` with that engine. Cover pile, kick, cars, rays and npcs, plus
  DragonFyre + pile-500.
  Expected, per the feasibility plan's Decision 3:
  - p50 for pile-500 and kick-1000 is ≤ 0.5× ubODE's (52 ms and 30 ms);
  - no scene has a time dilation below ubODE's.
- [ ] **Step 3: Write the numbers into the early-results document,** next to the standalone ones. That shows how
  much of Rapier's advantage survives inside OpenSim.
- [ ] **Step 4: Owner checks in Firestorm** (Phase 4), on a Rapier region against a ubODE region:
  - walking, stairs and slopes;
  - sitting;
  - a vehicle;
  - a linkset door;
  - a volume-detect trigger;
  - a collision script;
  - DragonFyre floors.

  Results go in `docs/superpowers/specs/<date>-rapier-decision.md` (feasibility 5.1).
- [ ] **Step 5: Commit** the results documents with the message `docs: Rapier in-OpenSim bench and in-world results`.

---

## Out of scope (follow-ups, each its own change)

- **A ubOde-style module written from scratch.** Only if Task 13 shows BulletS's own C# behaviour or cost is the
  problem (the map's verdict, reasons 1–2).
- **An avatar actor built on Rapier's kinematic character controller** (H10 fallback).
- **macOS builds.**
- **A Rust bulk shim.** Only if Task 13's profile shows per-collider P/Invoke cost in H2.
- **Sending any of this upstream to OpenSim.**
