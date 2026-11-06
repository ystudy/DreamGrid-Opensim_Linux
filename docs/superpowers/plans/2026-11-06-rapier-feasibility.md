# Rapier as an OpenSim physics engine: feasibility work list

**Question:** should DreamGrid spend a real project on a Rapier-based physics engine for OpenSim? This list is
the testing that answers it. Each phase ends in a go/stop decision, so we stop spending as soon as the answer is
clear.

**Status:** not started. Written 2026-11-06 from that day's work: the ubODE start-up fix (PR 48), the upstream ODE
review (`ThirdParty/ubODE/UPSTREAM-ODE-REVIEW.md`), and the threading and Rust discussion.

## Why look at all

- **Start-up is no longer the reason.** With PR 48, DragonFyre's physics start-up is 0.6–1.5 s.
- **Quiet regions don't need it.** Physics runs far faster than OpenSim asks (DragonFyre: 55 physics frames per
  second with 913 scripts).
- **The open question is busy regions:** many moving objects, vehicles, physics games, crowds of avatars. ubODE
  solves and collides on one thread with a 2001-era design. Rapier (Rust, Apache-2.0) is built for many cores and
  SIMD. If a busy region gets several times more headroom, that matters for a grid. If not, we stop.
- **Stability may be a second gain.** A crash in native physics takes the whole region down, and Rust rules out
  most memory bugs.

## What we already know (2026-11-06)

- **Rapier:**
  - rapier3d **0.36.1** (2026-10-10), Apache-2.0, actively maintained (dimforge/rapier, roughly monthly releases).
    Still 0.x, so its API can change between releases.
  - It ships **official C bindings** (`bindings/c`, `rapier.h`, about 300 `r3*` functions). They are built with
    CMake and Cargo. Options: f32 or f64, multi-threading (thread count set with `r3SetNumThreads`), 8-lane SIMD
    (f32 only), and a profiler. The header covers trimesh, heightfield, convex hulls and decomposition, capsules,
    a character controller, ray and shape casts, contact events, kinematic bodies, CCD, and fixed and revolute
    joints.
- **OpenSim's side:**
  - The engine interface is `PhysicsScene` (11 abstract and 25 virtual members) and `PhysicsActor` (55 abstract,
    29 virtual), in `OpenSim/Region/PhysicsModules/SharedBase`.
  - **BulletSim (`BulletS`, about 22,600 lines of C#) talks to its engine only through `BSAPITemplate`:** 180
    abstract methods, implemented by `BSAPIUnman` (native BulletSim) and `BSAPIXNA` (managed). The backend is
    picked with `[BulletSim] BulletEngine=`.
    - **This is the shortcut.** A third backend that maps those 180 calls onto Rapier would reuse all of BulletS's
      linksets, vehicles, avatar movement, terrain and materials.
    - **The catch:** those calls mirror Bullet's own concepts (motion states, activation states, collision flags,
      compound shapes, 6-DOF springs).
    - BulletS's step already returns updates and collisions in bulk, so the per-object call overhead of ubODE goes
      away.
  - ubODE is about 80,000 lines of C/C++ plus 19,000 lines of C# (`ubOde`, `ubOdeMeshing`). Its behaviour is what
    current content is tuned for.
- **Bench tools on the Debian VM:**
  - `/root/bench`: an isolated OpenSim copy with its own MariaDB `dgbench`.
  - `bench.py`: start-up phases from the log.
  - `make_oar.py`: synthetic OARs.
  - Real content: the DragonFyre and Mountain Retreat OARs. There is also a stats and freeze tool.
  - The VM is small (2 vCPU, 1.9 GB). Multi-core results also need a bigger machine.

## Success, stop and failure

- **Worth a project (go)** if Rapier, on the same machine and content:
  - gives **at least 2× the physics headroom** at the busy-region test loads (B3 to B5 below), measured as physics
    milliseconds per frame at p50 and p99, on 4+ cores and not worse on 2;
  - has no feature gap that can't be filled (P1);
  - and passes the in-world behaviour checklist (Phase 4) well enough that remaining differences look like tuning,
    not redesign.
- **Stop early** if:
  - the desk check finds a gap that would need changes inside Rapier itself;
  - the standalone benchmark (Phase 2) shows less than about 1.5× at the busy loads;
  - loading a mesh-heavy scene is much slower than ubODE after PR 48 (more than about 3 s for DragonFyre);
  - or avatars can't be made to walk, climb steps and stand on meshes acceptably in the prototype.
- **Failure even if it "works":** existing content behaves differently enough that residents notice; vehicles,
  stairs and sitting are the usual suspects. Also: a dependency we can't build for Linux x64, Linux arm64 and
  Windows x64, or one whose API churn we can't keep up with.

## Phase 0: baseline with today's engines (on the bench, no new code)

Everything later is compared against this, so build it first.

- [ ] **0.1 Stress OARs.** Extend `make_oar.py` with physics scenes, all on the same terrain:
  - (a) N physical prims falling into a pile: boxes, spheres, cylinders and mixed linksets, N = 100 / 500 / 1,000;
  - (b) N physical prims spread out so they don't touch, which gives many islands;
  - (c) 20 scripted vehicles driving (LSL car vehicle parameters, a fixed path);
  - (d) DragonFyre plus scene (a) dropped onto its meshes, for mesh collisions;
  - (e) a ray-cast script firing 100 / 1,000 `llCastRay` calls per second.
- [ ] **0.2 Avatar load without viewers.** NPCs are real avatars to physics. Enable OSSL NPC functions on the
  bench only, then script 10 / 25 / 50 NPCs walking around the terrain and the DragonFyre meshes.
- [ ] **0.3 Measurement.** Record physics ms per frame (p50 and p99), sim frame rate, time dilation, CPU per core
  and RSS. Do it over 2 minutes after the region settles, with the stats from `show stats` or the stats web page,
  plus `pidstat`. Add a `bench.py measure TAG SECONDS` command that writes one CSV row per run.
- [ ] **0.4 Run ubODE (with PR 48) and BulletSim** on every scene, on the VM and on a 4- to 8-core machine. ⚠️
  BulletSim may already be the answer for busy regions. If it gives most of the headroom we want, the Rapier
  question gets much smaller.
- [ ] **0.5 Write down where ubODE's frame time goes** (collision against solver) at the heaviest loads, with
  `perf` on the region process or temporary timers like the ones used for PR 48. That shows how much multi-core
  stepping alone could win.

**Decision 0:** if ubODE and BulletSim already handle the heaviest realistic load with headroom to spare, park
Rapier and write that down.

## Phase 1: desk checks (reading only)

- [ ] **1.1 Feature map.** One row per OpenSim need, giving the Rapier C API equivalent, a gap, or a workaround:
  - **shapes:** box, sphere, cylinder, capsule, convex hull, trimesh, heightfield terrain, compound shapes for
    linksets, varregion terrain sizes;
  - **body types:** static, dynamic, kinematic and phantom; volume detect (sensors); selected and frozen prims;
  - **forces and velocities:** forces, torques, impulses, set velocity, buoyancy, hover, move-to-target,
    look-at, axis locks;
  - **joints:** what OpenSim's joints and `llSetStatus` need;
  - **avatar movement:** walking, running, flying, jumping, stairs and step height, slopes, sitting, pushing;
  - **events and queries:** collision events with the data `collision_start` needs, ray casts with OpenSim's
    filters, sleep and wake;
  - **materials:** friction, restitution, density;
  - **CCD for fast objects.**
- [ ] **1.2 Route choice: a new `BSAPITemplate` backend, or a new module.**
  - **Backend:** go through the 180 `BSAPITemplate` calls and mark each as a direct map, emulated, or impossible on
    Rapier.
  - **New module:** estimate one written from scratch, ubOde-style.
  - **Expected answer:** the backend for the prototype, because it reuses vehicles, linksets and avatars. Confirm
    or reject that.
- [ ] **1.3 Binding route.**
  - **The candidates:** P/Invoke straight into `rapier.h` from C#; a small Rust shim crate with our own bulk API
    (one call per step for all changes and all results); or generated bindings (csbindgen or Interoptopus).
  - **Check:** struct layout and handle rules (the C API copies description structs; handles go invalid on
    removal), and thread safety across regions, given one region per process.
- [ ] **1.4 Build and ship check.**
  - **Builds:** build the C bindings with CMake and Cargo on Debian 12 x64, Linux arm64 and Windows x64.
  - **Library:** note the size, the glibc and VC++ runtime imports, and whether multi-threading and SIMD can be in
    one build.
  - **Releases:** check how often 0.x releases have broken the C API.
- [ ] **1.5 Licence check.** Apache-2.0 alongside OpenSim's BSD and the LGPL/BSD ubODE. Confirm and note the
  NOTICE requirements.

**Decision 1:** stop if a core need (avatar movement, mesh collision, linksets) has no workable route.

## Phase 2: standalone Rapier benchmark (no OpenSim)

The cheapest real numbers: is Rapier fast enough on our content before we touch OpenSim?

- [ ] **2.1 Export the scene.** Add a temporary dump to ubOde (bench only, like the PR 48 timers) that writes
  every physics actor after the start-up drain: shape type, size, position and rotation, and for meshes the exact
  vertices and indices ubODE uses (`.obj` plus a JSON manifest). Run it on DragonFyre and on the Phase 0 scenes.
- [ ] **2.2 Harness.** A small Rust (or C, against `rapier.h`) program that loads the export into a Rapier world
  at OpenSim's step rate. It runs the Phase 0 scenarios: falling piles, spread bodies, kinematic capsules moving
  like avatars with the character controller, and ray casts. Builds:
  - single-threaded, f32;
  - multi-threaded on 2 / 4 / 8 threads;
  - SIMD on and off;
  - and on the PR 48 ubODE, the same scenes through a matching C harness, so both engines are compared outside
    OpenSim. If the ubODE harness gets too fiddly, compare against the Phase 0 in-OpenSim numbers and say so.
- [ ] **2.3 Measure:**
  - B1, scene build time (all trimeshes, which is the start-up cost);
  - B2, an idle step with only statics and sleepers;
  - B3, piles of 100 / 500 / 1,000;
  - B4, spread bodies;
  - B5, 50 capsules walking on meshes;
  - B6, 1,000 rays per second;
  - B7, memory.

  Report ms per step p50 and p99 against ubODE on the VM and on the bigger machine.
- [ ] **2.4 Sanity of results:** objects settle and don't jitter or sink; capsules don't fall through mesh floors;
  rays hit what ubODE's rays hit. Compare a sample of hits.

**Decision 2:** stop if B3 to B5 aren't at least about 1.5× better, or B1 is much slower than ubODE, or meshes
misbehave.

## Phase 3: prototype inside OpenSim (bench only)

- [ ] **3.1** Implement the chosen route (probably `BulletEngine=Rapier` behind `BSAPITemplate`) far enough for:
  - terrain, static prims and meshes, physical prims, linksets;
  - avatars;
  - ray casts, collision events and phantom/volume detect.
  
  Everything else can log "not supported" for now.
- [ ] **3.2 Build the native library** for Debian 12 x64 the same way PR 48 builds ubODE (a script plus a workflow
  job), and load it on the bench only.
- [ ] **3.3 Correctness basics on the bench:**
  - a region with DragonFyre starts;
  - an OAR loads;
  - prims fall and stop;
  - linksets stay together;
  - NPCs walk the terrain and the meshes without falling through;
  - `llCastRay` returns hits;
  - `collision_start` fires.
- [ ] **3.4 Re-run all of Phase 0** with the prototype and add the results to the same CSV.
- [ ] **3.5 Stability:** a 12-hour soak with scene (c) plus the NPCs. Check memory growth, crashes and log errors.

**Decision 3:** go to Phase 4 only if the prototype keeps most of Phase 2's advantage inside OpenSim, since
interop and BulletS's C# work can eat it.

## Phase 4: in-world behaviour (owner, Firestorm)

On the VM grid, with one region on the prototype and its twin on ubODE (same OAR), compared side by side.

- [ ] **4.1 Avatars:**
  - walk, run, fly, jump;
  - stairs (prim stairs and mesh stairs), slopes and ramps;
  - stand on small and moving prims;
  - sit and stand up;
  - push other avatars;
  - fall from a height;
  - teleport in and land.
- [ ] **4.2 Objects:** physical prims and linksets; `llSetStatus`, `llApplyImpulse`, `llMoveToTarget`,
  `llSetBuoyancy`, `llSetHoverHeight`, `llLookAt`; phantom; volume detect; selected or edited physical objects.
- [ ] **4.3 Vehicles:** LSL car, boat and aircraft vehicles from free content; crossing a region border.
- [ ] **4.4 Tricky content:**
  - doors and elevators made of kinematic or scripted prims;
  - `llCastRay` scripts such as weapons and sensors;
  - mesh furniture and interiors;
  - small mesh details (this also checks the ubODE small-triangle question from the upstream review).
- [ ] **4.5** Note every difference as "tuning" (a parameter fixes it) or "design" (it needs new code or isn't
  possible).

**Decision 4:** go if the differences are mostly tuning.

## Phase 5: decision report

- [ ] **5.1** Write `docs/superpowers/specs/<date>-rapier-decision.md` with:
  - the CSV tables from Phases 0, 2 and 3;
  - the feature map;
  - the Phase 4 differences;
  - build and ship facts;
  - an effort estimate for a real engine module, and who would maintain it.
- [ ] **5.2** Recommend one of:
  - **go:** a project, with an outline plan;
  - **park:** revisit when Rapier reaches 1.0 or when busy regions become a real need;
  - **switch busy regions to BulletSim:** if Phase 0 showed that's enough;
  - **stop.**

## Rough effort

| Phase | Work | Rough size |
|---|---|---|
| 0 Baseline | Stress OARs, NPC load, measurement command, two engines × scenes × two machines | 1–2 days |
| 1 Desk checks | Feature map, 180-call map, binding and build checks | 1–2 days |
| 2 Standalone benchmark | Scene export, Rust harness, ubODE harness or fallback, runs | 3–5 days |
| 3 Prototype in OpenSim | `BSAPITemplate` backend subset, native build, bench tests, soak | 2–4 weeks |
| 4 In-world | Owner sessions with Firestorm, notes | a few sessions |
| 5 Report | Write-up and recommendation | 1 day |

Phases 0 to 2 (about a week) answer most of the question before the expensive prototype.

## Notes and constraints

- **Test machines:** use the bench on the VM (isolated OpenSim, its own database). The real grid only gets a
  prototype region in Phase 4, and the owner does the Firestorm checks. Remember that `remove-region` deletes a
  region's objects: unload by quitting the program.
- **A bigger machine is needed for multi-core numbers.** The 2-vCPU VM can only show the 2-core case. Options: a
  larger Proxmox VM, or a cloud runner for Phase 2. The cloud only suits the standalone harness, not in-world
  tests.
- **Baseline engine:** the ubODE baseline is the PR 48 build, not the old shipped library, so we don't credit
  Rapier with a speed-up that ubODE already has.
- **Throwaway code:** all Phase 2 and 3 code is throwaway until the report says go. Keep it on its own branch.
