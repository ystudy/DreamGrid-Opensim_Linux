# Rapier feasibility: early results (2026-11-06)

First numbers from the work list in
[`plans/2026-11-06-rapier-feasibility.md`](../plans/2026-11-06-rapier-feasibility.md). This covers Phase 0 (the
ubODE baseline), 0.5 (where ubODE's time goes) and a first cut of Phase 2 (a standalone Rapier benchmark). The
owner asked to skip BulletSim, so 0.4 covers ubODE only.

## Short answer

**Rapier is worth pursuing.** The newest release (rapier3d **0.36.1**) beat ubODE in every scene we tried. The
gains on one thread:

- **Piles that stay in motion: 6–10× faster.**
- **Spread-out moving objects: about 2.4× faster.**
- **Small scenes: still faster, but those are cheap on both engines.**

Rapier also stays well inside the frame at 2,000 moving objects, where ubODE overloads and the region slows to about a
quarter of normal speed. ubODE's cost is nearly all native ODE work, not OpenSim overhead, so these gains should
mostly survive inside OpenSim. That last point is an estimate, and only the Phase 3 prototype can prove it.

Against the plan's thresholds:

- **Decision 0** (is today's engine enough?): **no**, for busy regions. ubODE overloads somewhere between 500 and
  1,000 awake physical objects on this machine.
- **Decision 2** (at least 1.5× at the busy loads): **passed for piles and spread bodies.** Avatars (B5), mesh
  scene build (B1), rays (B6) and memory (B7) are **not tested yet**.

## Setup

- **Machine:** the Debian 12 test VM, with 2 vCPUs (i7-11800H host) and 1.9 GB RAM. It's a small machine, so
  absolute numbers are pessimistic. The 4–8 core runs are still to do.
- **ubODE:** the PR 48 build (with the trimesh preprocess fix), inside OpenSim on the `/root/bench` standalone
  region. A bench-only frame logger (`DG_FRAMELOG`) records physics milliseconds every frame.
- **Rapier:** a standalone Rust program, [`rapier-bench/`](rapier-bench/), with no OpenSim involved.
  - **The scenes** rebuild what `make_physoar.py` puts in the test OARs: the same shapes, sizes, positions and
    kick script behaviour.
  - **Simulation settings match ubODE's:** 0.02 s physics steps inside 0.0909 s OpenSim frames, gravity −9.8,
    density 10, friction 0.6, and ubODE's damping. The ground is a flat 256 × 256 heightfield.
  - **Rapier defaults:** 4 solver iterations, the same in 0.22 and 0.36.
- **Measurement:** physics milliseconds per OpenSim frame, from 30 to 90 s after the scene loads, once things have
  landed. A frame is 91 ms, and physics must fit inside it with room for scripts and networking.
- **The scenes:**
  - **pile-N:** N prims (box, sphere and cylinder, each 0.5 m) dropped in 12 × 12 columns, so they land as one big
    touching pile.
  - **kick-N:** N prims spread over the region. Each one's script gives it a random push and spin every 2–4 s.

## Results

Physics milliseconds per frame, median (p50):

| Scene | ubODE (in OpenSim) | Rapier 0.22 | **Rapier 0.36.1** | 0.36.1, sleeping off | 0.36.1, 2 threads* |
|---|---|---|---|---|---|
| pile-100 | 3.9 | 0.10 | **0.05** | 0.64 | |
| pile-500 | 48 | 1.1 | **0.95** | 5.0 | |
| pile-1000 | 113 † | 30 | **2.0** | 10.8 | 8.6 |
| pile-2000 | 120 † | 63 | **23** | 25 | 20 |
| kick-100 | 1.5 | 1.8 | **1.1** | 1.1 | |
| kick-500 | 10.4 | 10.1 | **6.1** | 6.2 | |
| kick-1000 | 29 | 21 | **12.1** | 12.3 | 8.4 |
| kick-2000 | 121 † | 44 | **26** | 26 | 18 |

\* The 2-thread runs had sleeping turned off.

† **ubODE overloaded:** time dilation 0.20–0.26, so the region ran at a quarter of normal speed. The number shown is
what it managed per frame; the real demand is about 4× higher.

How to read this:

- **Sleeping.** ubODE's piles never go to sleep: its rest thresholds (0.01 m/s and 0.001 rad/s) are strict.
  Rapier's do, so the "sleeping off" column is the like-for-like comparison of raw solver cost. Even there, 0.36.1
  is 6–10× faster on piles. With normal sleeping, a settled 1,000-prim pile costs 2 ms instead of more than a frame.
- **Rapier 0.22 against 0.36.1.**
  - **Faster:** 0.36.1 is 1.6–5× faster than 0.22, at the same solver iteration count.
  - **Why:** the gains come from engine changes such as contact clustering and contact recycling.
  - **API:** it also changed a lot, moving vectors from nalgebra to glam and changing what `step` takes. A backend
    should target 0.36 or later.
- **Threads.** On this 2-vCPU VM, 2 threads saved 20–30%. ubODE can't use a second core at all. The 4- and 8-core
  numbers are still to measure.

### Other ubODE scenes (no Rapier version yet)

| Scene | ubODE p50 ms | Note |
|---|---|---|
| cars-20 / cars-50 | 1.3 / 1.2 | Cheap. I didn't check that the vehicles actually drove. |
| rays-100 / rays-1000 | 0.01 / 0.01 | Suspiciously cheap. The ray casts may not be reaching physics, so treat this as unverified. |
| npcs-10 / 25 / 50 | 0.07 / 0.19 / 0.38 | Cheap. I didn't confirm all NPCs were created and walking. |
| DragonFyre + pile-500 | 55 | Like pile-500 alone, plus about 7 ms for collisions against the meshes. |

## Where ubODE's time goes (0.5)

These come from temporary bench-only timers in `ODEScene.Simulate`. The timer patch isn't in the repo, and the
bench DLL was restored afterwards. Milliseconds per frame:

| Scene | Total | Native ODE: collision tests + QuickStep | Collision dispatch (mixed) | OpenSim C# work |
|---|---|---|---|---|
| pile-100 | 4.1 | 3.5 (85%) | 0.6 | 0.1 |
| pile-500 | 52.5 | 45.4 (87%) | 6.5 | 0.6 |
| kick-500 | 11.8 | 7.0 (60%) | 4.2 | 0.6 |
| kick-1000 | 29.5 | 14.7 (50%) | 13.5 | 1.3 |
| pile-1000 † | 113 | 89 (79%) | 22.6 | 1.7 |

- **OpenSim C# work** covers the change queue, applying forces (`Move`), collision reports and position updates.
  It's only 1–5% of the total.
- **Collision dispatch** is ODE's broad phase plus the C# near-callback that builds contact joints. The timers
  can't split the two parts.
- **What it means for a Rapier backend:**
  - It keeps the 1–2 ms of OpenSim work.
  - It replaces both the native ODE work and the collision dispatch.
  - It adds the cost of reporting collisions that scripts listen for. These scenes had no collision scripts, so
    that cost is unmeasured.
- **Estimates inside OpenSim:**
  - pile-500: about 52 → 6 ms, with sleeping off.
  - kick-1000: about 30 → 13 ms.

## Caveats

- **Standalone versus in-OpenSim.** Rapier ran without OpenSim. The breakdown above says OpenSim's own work is
  small, but a real backend also does marshalling across P/Invoke and contact-event reporting. Phase 3 is where
  that gets proven.
- **Behaviour isn't compared yet.** I haven't checked whether piles look the same, whether objects jitter or sink,
  or how sliding feels. Rapier sleeps much more readily than ubODE, which is good for cost but could stop slow
  movement that ubODE keeps going.
- **Avatars, meshes and rays aren't benchmarked in Rapier yet.** Those are B5 (capsules walking on meshes), B1
  (DragonFyre scene build time) and B6 (rays), all from Phase 2. Mesh-heavy start-up is where ubODE hurt before
  PR 48, so B1 matters.
- **Small machine.** All runs are on 2 vCPUs. The multi-core runs on a 4- to 8-core machine are still open.

## Next steps (from the work list)

1. **Phase 2.1 export, then B1 and B5.** Dump DragonFyre's actual physics meshes from ubODE and load them into the
   Rapier bench. That measures scene build time and capsules walking on meshes.
2. **Phase 1 desk checks.** These are mostly done in the API map
   [`2026-11-06-rapier-bsapi-map.md`](2026-11-06-rapier-bsapi-map.md). It needs updating for the 0.36 API, since
   that map was written against the C bindings.
3. **A bigger machine** for the 4- and 8-thread numbers.
4. **Then Phase 3,** the `BSAPIRapier` prototype inside OpenSim.

## Where the raw data is (VM `/root/bench`)

- **ubODE results:** `physics-results.csv`. The rows tagged `ubODE-timed-*` came from the timer build.
- **Rapier results:** `rapier-results.csv` (0.22) and `rapier036-results.csv` (0.36.1).
- **Section timings:** `ubodetime-ubODE-timed-*.csv`. The columns are: timestamp, frames, steps per frame, then ms
  per frame for changes, move, collide, narrow phase, reports, quickstep and updates, then collision calls, contacts
  and active objects.
- **Scripts:**
  - **The scene and run scripts:** `make_physoar.py`, `physrun.py`, `physmatrix.sh` and `timed.sh`.
  - **The Rapier bench:** `rbench/` (0.22) and `rbench36/` (0.36.1). The copy in this repo is the 0.36.1 one.
- **Build tools:** gcc and a Rust toolchain were installed on the VM for this. `pkgs-before-rust.txt` lists the
  packages from before, for removing them later.
