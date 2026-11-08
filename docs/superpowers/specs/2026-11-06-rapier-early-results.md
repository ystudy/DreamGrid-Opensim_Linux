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
- **Rapier:** a standalone Rust program (`rbench`), with no OpenSim involved. It is bench-only and kept on the VM, not in the repo.
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

## In OpenSim (BSAPIRapier)

Task 13 of the integration plan. The same scenes, run through `BSAPIRapier` (rapier 0.36.1) inside OpenSim on the
same VM and the same bench region. **Result: the gate is only half met.** pile-500 passes, kick-1000 doesn't, and
Rapier is slower than ubODE in most of the small scenes.

- **Build:** branch `feature/rapier-backend` at `58c75be7`, Release build. Only `OpenSim.Region.PhysicsModule.BulletS.dll`
  (and its `.dll.config`) and `lib64/librapier-x86_64.so` were swapped into the bench. The rest of the bench is the PR 48
  build, so ubODE and Rapier ran on the same OpenSim code. The branch's only other OpenSim change is an unrelated
  `estate reload` console command in `OpenSim.dll`, which wasn't swapped in.
- **Settings:** `physics = BulletSim`, `meshing = Meshmerizer`, `[BulletSim] BulletEngine = Rapier`,
  `UseSeparatePhysicsThread = false`. Rapier ran on 1 thread, with the backend's defaults (4 solver iterations).
- **Measurement:** the same `physrun.py` and the same bench frame logger as the ubODE runs. The logger sits in
  `Scene.Update`, so it measures `PhysicsScene.Simulate` plus the physics sync for any engine. No product code
  changed. The ubODE columns are the rows already in `physics-results.csv`. The standalone column is from the
  Results table above.
- **Windows:** the table is the 30–90 s window. The run script starts its clock after the CPU has settled (it waits
  up to 10 minutes), for ubODE and Rapier alike. Both windows (0–20 s and 30–90 s) agree to within a few percent
  except in pile-100 and the cars scenes, where the first window was the slower one.

Physics milliseconds per frame (p50 / p99) and time dilation. A frame is 91 ms:

| Scene | ubODE p50 / p99 | ubODE dilation | **BSAPIRapier p50 / p99** | Rapier dilation | Rapier / ubODE (p50) | Standalone 0.36.1 p50 |
|---|---|---|---|---|---|---|
| pile-100 | 3.9 / 5.5 | 1.0 | **1.1 / 1.9** | 1.0 | 0.29 | 0.05 |
| pile-500 | 48.4 / 58.4 | 1.0 | **10.9 / 13.6** | 1.0 | **0.22** | 0.95 |
| pile-1000 | 112.9 / 128.8 | 0.24 | **20.4 / 23.6** | 1.0 | 0.18 | 2.0 |
| pile-2000 | 119.6 / 125.9 | 0.20 | **39.7 / 46.7** | 1.0 | 0.33 | 23 |
| kick-100 | 1.5 / 2.0 | 1.0 | **3.0 / 4.0** | 1.0 | 2.0 | 1.1 |
| kick-500 | 10.4 / 12.9 | 1.0 | **12.5 / 15.5** | 1.0 | 1.2 | 6.1 |
| kick-1000 | 29.3 / 37.9 | 1.0 | **26.5 / 31.5** | 1.0 | **0.90** | 12.1 |
| kick-2000 | 121.4 / 129.7 | 0.26 | **57.9 / 68.2** | 1.0 | 0.48 | 26 |
| cars-20 | 1.3 / 1.9 | 1.0 | **2.5 / 3.5** | 1.0 | 1.9 | |
| cars-50 | 1.2 / 2.3 | 1.0 | **3.3 / 4.5** | 1.0 | 2.8 | |
| rays-100 | 0.01 / 0.05 | 1.0 | **0.49 / 0.79** | 1.0 | | |
| rays-1000 | 0.01 / 0.05 | 1.0 | **0.15 / 0.50** | 1.0 | | |
| npcs-10 | 0.07 / 3.9 | 1.0 | **0.58 / 0.92** | 1.0 | 8 | |
| npcs-25 | 0.19 / 4.1 | 1.0 | **1.2 / 1.6** | 1.0 | 6 | |
| npcs-50 | 0.38 / 4.1 | 1.0 | **2.1 / 2.9** | 1.0 | 5.6 | |
| DragonFyre + pile-500 | 54.8 / 69.0 | 1.0 | **14.2 / 16.8** | 1.0 | 0.26 | |

How to read this:

- **Where Rapier wins:** busy scenes. Piles are 3–6× cheaper, and at 1,000 and 2,000 objects Rapier holds time
  dilation at 1.0 where ubODE slows the region to about a quarter. kick-2000 costs 58 ms, well inside the frame.
- **Where it loses:** small and spread-out scenes. kick-100 and kick-500, the cars and the NPC scenes are all
  1.2–8× more expensive than ubODE. All of them stay under 13 ms, so none of them hurts, but they show a fixed
  per-object cost that ubODE doesn't pay.
- **How much of the standalone gain survived:** not much on piles (pile-500: 0.95 ms alone, 10.9 ms in OpenSim,
  about 11×). Kick scenes cost about 2.2× their standalone figure (kick-1000: 12.1 against 26.5). So most of the
  difference is not Rapier's solver. I haven't profiled where it goes. Likely suspects are per-prim work in
  BulletS and P/Invoke calls, but that is a guess. Whether sleeping takes effect through the backend is unchecked
  too.
- **Rays and NPCs:** ubODE's 0.01 ms for rays was already suspect (the rays may never have reached physics).
  Rapier's 0.15–0.5 ms suggests that here they do, but I didn't verify it. I also didn't check that the NPCs and
  cars actually moved the way they should.

### Decision 3 check

| Check | Needed | Got | Result |
|---|---|---|---|
| pile-500 p50 | at most 0.5 × 52 = 26 ms | 10.9 ms (0.22 ×) | **pass** |
| kick-1000 p50 | at most 0.5 × 30 = 15 ms | 26.5 ms (0.90 ×) | **fail** |
| No scene with time dilation below ubODE's | every scene at least ubODE's | all 16 at 1.0 | **pass** |

So the "experimental" label stays on. The kick-1000 miss is close to ubODE's number, not a rounding error.

### DragonFyre start-up (B1 gate)

DragonFyre alone in the bench region (4,082 objects, 905 scripts under Rapier). Warm, run twice per engine, timed
with `bench.py`. I re-ran ubODE the same day to compare. It matched the PR 48 figures of about 5.6 s and 1.5 s.

| Seconds from launch | ubODE (2 runs) | BSAPIRapier (2 to 3 runs) |
|---|---|---|
| Scripts handed to the engine | 3.6 / 3.5 | 3.6 / 3.6 |
| "Region up" log line | 5.4 / 5.3 | 3.8 / 3.9 (4.2 / 3.9 in two earlier runs) |
| Physics built (ubODE: "prim actors loaded") | 5.2 / 5.1 | not logged; see below |
| First frame done, physics ready | 5.4 / 5.3 | 7.8 (one run with a frame log) |
| CPU settled | 6.0 / 5.9 | 8.9 / 8.9 / 8.9 |
| Memory after start (RSS) | 465 / 461 MB | 1,156 / 1,143 / 1,133 MB |

- **Why the "region up" line misleads for Rapier.** BulletS builds its physics shapes inside the first physics
  step, after that line is logged. The frame log shows that first frame taking **4.0 s** of physics. ubODE builds
  during start-up, about 1.6 s after the scripts are handed over. Rapier has no `prim actors loaded` line, so
  the physics start-up is read from the frame log instead.
- **Physics start-up on its own:** about 4.0 s for Rapier against about 1.6 s for ubODE (+2.4 s).
- **Gate (no more than 3 s slower):** passes, but with little room.
  - Until physics is ready: 7.8 s against 5.3 s, so **+2.5 s**.
  - Until the CPU settles: 8.9 s against 5.9 s, so **+3.0 s**. That is right on the limit.
  - By the log line alone it looks 1.5 s faster, which isn't a fair reading.
- **Memory is the surprise.** The process holds about 1.1–1.4 GB after DragonFyre loads under Rapier, against about
  0.5 GB for ubODE. On the 1.9 GB VM that is most of the free memory. I haven't found out why.

### Errors in the region log

All 16 Rapier runs plus the start-up runs:

- **No** `NotImplementedException`, **no** crash, and **no** warning or error that mentions Rapier. The only
  Rapier lines are `[BULLETS SCENE] Selected bullet engine Rapier -> Rapier/0.36.1+c.0+parallel` at each start.
- The usual bench noise (GatekeeperURI, no search server, the RegionReady alert URI, WebRTC module type, and
  `SendGridInstantMessageViaXMLRPC` connection refused) appears in every run, ubODE included.
- **New, and worth a look:** 588 `[ERROR] Physical object kick ... went out of bounds` lines and 23 for `car ...`,
  with a matching `[WARN] [BULLETS PRIM] Too many crossing failures` for each. Per scene: kick-100 6, kick-500 84,
  kick-1000 164, kick-2000 334, cars-20 9, cars-50 14. The ubODE runs of the same scenes logged none.
  - **What happens:** kicked objects and driven cars reach the edge of the region. There's no neighbour to cross
    into, so OpenSim stops them at the edge. ubODE kept them inside.
  - **Effect on the numbers:** up to 16–17% of the kick objects were stopped over the course of a run, so the
    late kick figures are for slightly fewer moving objects than ubODE had. That flatters Rapier a little. It
    doesn't change the kick-1000 verdict, which is already a miss.
  - **Open question:** whether the real cause is a missing region-edge wall in the backend, or normal BulletS
    behaviour that the Bullet engines show too. I haven't run a Bullet engine to check.

### Caveats for this section

- **One run per scene.** No repeats, so run-to-run spread is unknown. The two windows inside each run agree well.
- **Not the same content.** ubODE and Rapier loaded the same OAR files, but the 8 extra objects in the ubODE
  DragonFyre start (4,090 against 4,082) are unexplained.
- **Nothing here is behaviour.** These are costs only. Walking, sitting, vehicles, collisions and the DragonFyre
  floors are the owner's Firestorm checks, and they decide whether Rapier is correct enough, whatever its cost.
- **Small VM.** 2 vCPUs, 1 Rapier thread. The bench shared the machine with the real grid, which was idle.
- **Start-up frame log:** the one physics-ready figure comes from a single run with a frame log. The other
  start-up runs didn't have one, but their CPU-settled times match to 0.1 s.
- **Still to do:** the owner's Phase 4 checks in Firestorm, Rapier region against ubODE region: walking, stairs and
  slopes, sitting, a vehicle, a linkset door, a volume-detect trigger, a collision script and the DragonFyre floors.
  Results go in a `rapier-decision.md` (feasibility 5.1).
- **Also still to do:**
  - **Out-of-bounds check:** run kick-500 once with `BulletEngine=BulletUnmanaged`. If the out-of-bounds count is
    similar, it is BulletS's own edge handling plus Bullet-style friction, as the final review concluded. If it is
    near zero, the backend is the cause.
  - **Memory A/B on DragonFyre RSS:** (a) as is; (b) without the trimesh internal-edge fix on static meshes;
    (c) without the managed vertex copies.
  - **Where the in-OpenSim time goes:** see [`2026-11-06-rapier-perf-investigation.md`](2026-11-06-rapier-perf-investigation.md),
    which ranks ten fixes.

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
- **Rapier in OpenSim:** `BulletSim-Rapier-*` rows in `physics-results.csv`, per-frame
  `frames-BulletSim-Rapier-*.csv`, `physmatrix-rapier.log`, `startup-load-rapier.log` and `frames-startup-*.csv`.
  `physrun.py` accepts the engine `BulletSim-Rapier`, and `physmatrix-rapier.sh` runs the scenes. These bench-only
  edits are not in the repo.
- **Scripts:**
  - **The scene and run scripts:** `make_physoar.py`, `physrun.py`, `physmatrix.sh` and `timed.sh`.
  - **The Rapier bench:** `rbench/` (0.22) and `rbench36/` (0.36.1). Neither is in the repo.
- **Build tools:** gcc and a Rust toolchain were installed on the VM for this. `pkgs-before-rust.txt` lists the
  packages from before, for removing them later.
