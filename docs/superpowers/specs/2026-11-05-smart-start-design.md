# Smart Start for Linux: sleeping regions — design

Date: 2026-11-05. Status: design, not started. The tests behind it are in Appendix A.

Everything below is my call unless a line says it is the owner's.
- **(owner, 2026-11-05)** On freezing an empty region instead of shutting it down: "I like the freeze Idea. test it!"
- **(owner, 2026-11-05)** After teleporting into a frozen DragonFyre region on the test grid: "it worked well and as expected."
- **(owner)** Who may ask for an always-on region, and whether it is paid for: "for a later time" (section 5.8).

## 1. Why

Today every region runs all the time, whether anyone is in it or not. Each one holds its memory (170 MB for an empty program, 470–600 MB for a heavy mesh region like DragonFyre) and spends CPU on physics and scripts. So a machine can only hold as much **land** as its RAM allows, not as much as its visitors need.

Smart Start makes a region sleep when nobody is in it and wake when someone wants in. The machine then works as hard as the number of people online, not the amount of land owned.

The obvious way to sleep a region is to stop it and boot it again on demand. The tests (Appendix A) show that is too slow for real content: DragonFyre takes **19 seconds** to start, 15 of them building physics. Freezing the program instead, and pushing its memory to disk, wakes it in **0.015 seconds**, with physics, scripts and objects exactly as they were. So freezing is the main mode of this design, and stopping is the fallback.

## 2. What exists already

### 2.1 Old DreamGrid (Windows)
The Windows DreamGrid launcher had Smart Start (`.ref/txt/SmartStart.txt`):
- **Shutdown mode:** an empty region quits after a delay. A visitor heading there waits in a "parking region" (Welcome) while it boots, then is teleported on.
- **Suspend mode:** an empty region is frozen in RAM (an ice-cube icon), and Windows moves its memory to disk when needed. A teleport thaws it.
- **Surrounding sims ("Fill Size"):** neighbours boot when a visitor arrives, so they can walk across borders.
- **Endless Land and Sea:** new regions built around a travelling visitor.

That launcher's source isn't available. Its old repo (`github.com/Outworldz/DreamWorld`) is gone, and outworldz.com offers only the Windows installer (checked 2026-11-05). The design below is new code; the manual and the OpenSim hooks give the behaviour and the protocol.

### 2.2 OpenSim hooks (in this tree, switched off)
Before an arrival, OpenSim asks DreamGrid over HTTP whether the destination is ready ([SmartStartAltClient.cs](../../../OpenSim/Framework/SmartStartAltClient.cs)):

```
GET <[SmartStart] URL>?alt=<region uuid>&agentid=<agent uuid>&password=<MachineID>[&clientversion=…]
→ body: a region UUID. The same UUID means "go ahead"; another means "go there instead".
```

- **Timeout 5 s, and the caller waits** (synchronous). Any failure means "go ahead".
- It is asked from three places:
  - [EntityTransferModule.cs:763](../../../OpenSim/Region/CoreModules/Framework/EntityTransfer/EntityTransferModule.cs) (through `Scene.GetSmartStartALTRegion`): local teleports, with the agent's id. If the answer is the region the avatar is standing in, the viewer is told "Destination region Loading. Teleport will happen soon".
  - [GridService.cs:549](../../../OpenSim/Services/GridService/GridService.cs) (`GetRegionByUUID`, in Robust): **every** region lookup by UUID, with agent id zero. Logins go through here, but so do other lookups, so the agent is unknown.
  - [GatekeeperService.cs:330](../../../OpenSim/Services/HypergridService/GatekeeperService.cs): hypergrid arrivals, with the agent's id.
- Notifications the other way, region to DreamGrid: `GET <URL>?type=<msg>&UUID=<uuid>` ([Region/Application/SmartStart.cs](../../../OpenSim/Region/Application/SmartStart.cs)), for example `oarsaved` / `oarnotsaved`.

### 2.3 The Linux launcher today
- Every generated INI has `[SmartStart] Enabled=False`, `URL=http://127.0.0.1:<WebPort>`, `MachineID=<MachineId>` ([GridConfigWriter.cs:303](../../../Launcher/DreamGrid.Launcher.Core/Config/GridConfigWriter.cs)).
- [LegacyEndpoints.cs:64](../../../Launcher/DreamGrid.Launcher/LegacyEndpoints.cs) already answers `?alt=` by echoing the UUID ("go ahead"), and logs `?type=` notifications.
- The regions spec ([2026-10-27-regions-design.md](2026-10-27-regions-design.md)) left "SmartStart, freeze/thaw" on the Coming-soon list. Its power menu has **Stop** (until the next world start) and **Park** (until Unpark) ([RegionPowerPlan.cs](../../../Launcher/DreamGrid.Launcher.Core/Grid/RegionPowerPlan.cs)).
- The launcher only reacts when a region program **exits** (`ManagedProcess.MonitorAsync`), so a frozen program isn't mistaken for a crash. Checked on the test grid: freezing Welcome and Test logged nothing in the launcher, Robust or the region.
- `dreamgrid.service` runs as user `dreamgrid` with `Delegate=no` and `KillMode=process` (regions outlive a launcher restart).

## 3. Goals and non-goals

**Goals**
- An empty region goes to sleep on its own after a delay, and wakes for a login, a teleport, a hypergrid visitor, or anything else that connects to it, quickly enough that the visitor doesn't notice.
- A sleeping region uses no CPU and very little RAM.
- Nothing is lost: a region always saves before it sleeps.
- Regions can be kept **always on** (scripts that must keep running, landing regions, busy event regions).
- The owner can see which regions are asleep and how much memory and swap sleeping regions use.
- **No changes under `OpenSim/`** (the regions spec's rule, because of the monthly merges from Fred). The hooks are enough.

**Non-goals (for now)**
- Endless Land and Sea.
- Booting neighbours ahead of a visitor ("Fill Size").
- Who may ask for always-on, and payment for it (owner: later).
- Moving a running region between programs, or between machines.
- Windows. The launcher is cross-platform, but this design is Linux first (section 5.5 notes the Windows route).

## 4. What the tests showed (summary)

Full numbers in Appendix A. Test machine: the Debian 12 VM, 2 vCPU, 1.9 GB RAM, 976 MB swap.

| | Start from nothing | Wake from frozen |
|---|---|---|
| Empty region | ~3 s | 0.015 s (Welcome, after 55 min frozen, on a real login) |
| Mountain Retreat (3,060 prims, 172 scripts, no mesh) | 3.9 s | — |
| DragonFyre (13,534 prims, 905 scripts, ~5,000 meshes) | 19 s, 15 of them physics | 0.013 s (on a real teleport), 0.14 s with its memory on disk and its database links dropped |

- Frozen DragonFyre held **52–82 MB of RAM**, down from ~470–600 MB. The rest was on disk and came back only as touched.
- After waking it ran normally: time dilation 1.00, 55 frames per second for both simulation and physics, all 913 scripts active, database writes saved.
- A spare program that's already running saves only the ~1.5 s program start. It doesn't help the 15 s of physics, so stop-and-boot can't be made fast for mesh regions.

## 5. The design

### 5.1 Region power states
| State | What it means | Shown as |
|---|---|---|
| Running | Normal. | as today |
| **Sleeping** | Program frozen; its memory mostly on disk. Wakes on demand in well under a second. | new: "Sleeping" (an ice-cube icon, as in old DreamGrid) |
| Stopped / Parked | As today. With Smart Start, a stopped region can also wake on demand (section 5.4). | as today |

Freezing works per **program**, so every region in a program sleeps and wakes together. A program can sleep only when **all** its regions may sleep and none has an avatar in it. The add-region page should suggest giving a Smart Start region its own program (the owner did this for "Test" without being told). One region per program is also what the earlier design discussion recommended.

### 5.2 Going to sleep
A program goes to sleep when all of these hold:
1. Smart Start is on for the grid, and on for every region in the program.
2. No avatar has been in any of its regions for the **sleep delay** (setting, default 10 minutes). Presence comes from Robust's `Presence` table, which the tests used, or the region's own agent count.
3. No region in it is **always on** (5.8), and none borders a region with an avatar in it (5.7).
4. No OAR job, save, restart or console command is running for it.
5. There is room on disk for its memory (5.6).

Then:
1. **Save.** Run `backup` on its console, then wait until the database stops changing. One minute after an OAR import, OpenSim had saved only 10 of 13,229 prims; the full save took about 140 s. A program that stays dirty after the wait doesn't sleep this round.
2. **Freeze** it (cgroup freezer, 5.5): instant, about 0.005 s.
3. **Push its memory out** (`memory.reclaim` on its cgroup, 5.5): about 1 s, leaving ~50–80 MB in RAM.
4. Mark it Sleeping, and start watching for knocks (5.3).

### 5.3 Waking
Any of these wakes a sleeping program. Waking means unfreezing it (0.015 s), then marking it Running.
1. **The Smart Start hook (main path).** Turn `[SmartStart] Enabled=True` on in the generated INIs. When `?alt=<uuid>` names a region in a sleeping program, the launcher thaws it and then answers with the **same UUID**, all within the hook's 5 s. No parking region and no second teleport are needed. The answer must come from memory with no disk or database work, because Robust asks on every region lookup (2.2).
2. **A knock on its ports (safety net).** While a program sleeps, the launcher watches its HTTP port and its regions' UDP ports. Anything waiting to be read there, such as a pending connection or a packet, wakes it. This catches what the hook can't see: logins, which reach the hook only with agent id zero; a script's HTTP-in address being called; region-to-region traffic. The test prototype read `ss` every 20 ms. The launcher can read `/proc/net/tcp` and `/proc/net/udp` the same way. In the tests it woke a frozen region 0.013–0.015 s after the knock.
3. **The website or console.** Anything that talks to a sleeping region's console (region pages, OAR jobs, restarts, the console page) wakes it first, or it would wait on a frozen console. It goes back to sleep after the normal delay.
4. **Later:** waking on a schedule (busy hours, events from the calendar).

Spurious wakes (a map lookup, a neighbour's chatter) are harmless: the region just goes back to sleep after the delay. The cost is a little RAM until then. How often idle regions get spurious wakes on a real grid hasn't been measured yet (section 7).

### 5.4 Stopped regions (shutdown mode)
For a region whose program isn't running at all (after a world start with "boot on demand", or when the machine is out of swap), the old DreamGrid flow applies:
- The hook answers with the **parking region's** UUID (setting; default Welcome). The visitor waits there, seeing "Destination region Loading. Teleport will happen soon".
- The launcher boots the program and, when the region is ready, teleports the visitor on. RemoteAdmin's `admin_teleport_agent` is the likely way; it still needs checking that it's available and works here.
- It's slow for mesh regions (DragonFyre 19 s), so it's the fallback, not the main mode.

This is a later stage (section 6); freezing covers the common case.

### 5.5 How a program is frozen on Linux
- **Each region program runs in its own cgroup**, created by the launcher before it starts the program: `dreamgrid.service/region-<Instance>`. A program must *start* in its cgroup. Memory stays charged to the cgroup a process started in, so moving it in later frees almost nothing (seen in the tests: 14 MB instead of 386 MB).
- **Freeze and thaw:** write `1` / `0` to that cgroup's `cgroup.freeze`, and wait for `frozen 1` / `frozen 0` in `cgroup.events`.
- **Memory out:** write to that cgroup's `memory.reclaim`, in steps, until it stops giving memory back.
- **Permission:** the launcher runs as `dreamgrid`, not root, so `dreamgrid.service` gets **`Delegate=yes`**. systemd then hands the service its own cgroup subtree to manage. Under cgroup v2, processes must live in leaf cgroups, so the launcher moves itself to `dreamgrid.service/launcher` at start-up, and Robust and MariaDB get their own leaves too. All the tests ran as root, so **this part is untested** and is the first thing stage 1 must prove (section 7).
- **Adopted programs** (still running from before a launcher restart) are already in their cgroup, since cgroups survive a launcher restart.
- **Fallback without cgroups** (an older system, or delegation refused): `SIGSTOP` / `SIGCONT` (same user, no extra rights) plus `process_madvise(MADV_PAGEOUT)`, which pushed DragonFyre from 454 to 71 MB in the tests regardless of cgroup. Whether it works without root still needs checking.
- **Windows (later):** suspend the process (the old DreamGrid way, `NtSuspendProcess` / `NtResumeProcess`) and trim its working set (`EmptyWorkingSet`). Tested on a standalone bench 2026-11-08 (A.5): it works, with three differences from Linux:
  - Trimmed memory goes to Windows' modified list, not straight to disk. Windows writes it to the page file only when RAM runs short, so the launcher can't force or measure "pushed out" the way `memory.reclaim` does. The RAM is still free for other programs.
  - **TCP knocks are visible:** while a program is suspended, Windows still completes incoming TCP connections, and `GetExtendedTcpTable` (what `netstat -ano` reads) shows them as ESTABLISHED to the suspended process. The request is answered as soon as it resumes.
  - **UDP knocks are not:** Windows has no per-socket view of waiting datagrams. Viewer traffic must wake a sleeping program through the Smart Start hook (5.3, item 1), or through the launcher watching TCP.

### 5.6 Swap
A sleeping region's memory goes to swap, so swap decides how much land can sleep.
- With Welcome and Test both frozen, the VM's 976 MB swap was 925 MB full.
- **The installer makes a swap file** sized for the expected sleeping regions (proposal: 4 GB, or the RAM size, whichever is larger), unless the machine already has enough. The owner should be told, since it uses disk space.
- **The launcher doesn't push a program out if free swap is less than its memory.** It still freezes it (no CPU), but leaves its memory in RAM.
- The dashboard shows RAM and swap used by sleeping regions.

### 5.7 Neighbours
A visitor near a border keeps child agents in the neighbouring regions and talks to them. A sleeping neighbour would be knocked awake straight away, and a visitor walking across a border into a frozen region needs it awake already.
- **Rule:** a region that borders a region with an avatar in it doesn't go to sleep. So a whole connected area stays awake while anyone is in it, and sleeps together after everyone leaves.
- This replaces old DreamGrid's "Fill Size" for regions that already exist.
- Islands (regions touching nothing) sleep and wake alone. The regions spec already knows which regions touch (`Location`, `SizeX`/`SizeY`).

### 5.8 Always on
Scripts don't run while a region sleeps. Timers stop, and outside callers can't reach it; a script's HTTP-in address also changes each time a region *starts*, though not when it thaws.
- **A per-region "Always on" switch** (website, region settings). Default: on for the parking region and the welcome region, off for others.
- **Later:** a launcher check that warns when a sleeping-enabled region holds scripts that use `llHTTPRequest`, `llRequestURL`, `llEmail` or long timers, so an owner isn't surprised.
- **Later:** a small always-on "services" region for residents' background scripts, so their own regions can sleep (the discussion's example: Joe, a resident whose script relays chat). **(owner)** Requests, approval by staff, and paying for always-on wait for a later design.

### 5.9 Website
- **Grid settings → Smart Start:** on/off; sleep delay; parking region; "push sleeping regions' memory to disk" on/off.
- **Each region's settings:** "Can sleep" on/off (on by default when Smart Start is on), and "Always on".
- **Regions list and dashboard:** a Sleeping state, wake time; RAM and swap held by sleeping regions; a warning when swap is nearly full.
- **Power menu:** "Sleep now" and "Wake now", next to Stop and Park.

### 5.10 Traps found on the way
- **OpenSim's `remove-region` erases the region's objects from the database.** `RemoveRegion` calls `scene.DeleteAllSceneObjects()` before `CloseScene` ([OpenSimBase.cs:741](../../../OpenSim/Region/Application/OpenSimBase.cs)). It's not an unload, so sleeping must never use it. The launcher doesn't use it today (checked 2026-11-05).
- **A program doesn't give memory back when one of its regions is removed.** One more reason for one region per program.
- **Freezing doesn't save.** Always run the save step first (5.2).

## 6. Stages
1. **Sleep and wake by hand.** `Delegate=yes`, a cgroup per program, freeze / thaw / memory-out, "Sleep now" / "Wake now" on the power menu, the swap check. Proves the unprivileged path (section 7, item 1).
2. **Automatic.** The sleep delay and the save-first step; `[SmartStart] Enabled=True` and the `?alt=` thaw; the knock watcher; the website states.
3. **Always on and neighbours.** The switch, the neighbour rule, the swap file in the installer, dashboard numbers.
4. **Stopped regions on demand.** Parking region, boot, teleport on.
5. **Later:** scheduled waking, the script check, the services region, Windows.

## 7. Open questions (to test)
1. **Freezing without root:** cgroup freeze and `memory.reclaim` from the `dreamgrid` user with `Delegate=yes` (stage 1).
2. **Long sleeps:** hours and days. The database side is covered by the dropped-connection test (MariaDB `wait_timeout` is 8 h). Robust and hypergrid links after a long sleep are not.
3. **Many sleeping regions,** and several waking at once at peak (swap reads in parallel).
4. **How often Robust's lookups and neighbours knock** idle regions awake on a real grid.
5. **Thread-pool jobs cancelled after a long sleep (found 2026-11-08, Windows bench; same code on Linux):** OpenSim's pool watchdog (`Util.ThreadPoolWatchdog`, OpenSim/Framework/Util.cs) cancels any pool job that has been running for 10 minutes by wall-clock tick count. A sleep longer than that makes every job that was mid-run when it froze look too old, and the watchdog cancels it as the program wakes. In the 30-minute test 9 jobs were cancelled (they started during start-up, just before the freeze). The region ran and saved normally afterwards, but in-flight work can be lost. Sleeping only after the idle delay makes this rarer. A fix in OpenSim: have the watchdog notice a long gap between its own runs (a freeze) and restart the jobs' clocks instead of cancelling them.
6. **Windows under memory pressure:** whether trimmed pages really reach the page file, and how fast they come back, wasn't tested (the test PC has 63 GB).
5. **RemoteAdmin teleport** for the parking path (5.4).
6. **Unexplained:** loading DragonFyre reported 4,660 objects, but only about 4,082–4,090 came back after a restart, varying slightly. It may be temporary objects or scripts rezzing things. It is not part of this design, but worth its own look.

## 8. Related ideas from the same discussion (each its own design)
- **Let visitors in before physics finishes:** build physics around the landing point first, so cold starts take seconds. That needs an OpenSim change.
- **Simpler physics for decorative mesh:** suggest "Convex" or "None" for small decorative objects. DragonFyre's 12,757 "Prim"-shaped prims are where its 15 s go.
- **A start queue at peak:** no more starts at once than CPU cores; visitors waiting first.
- **Sizing:** the machine still needs RAM for its busiest hour. Sleeping helps the rest of the day, not the peak.

---

## Appendix A. Test results (2026-11-05)

All on the owner's Debian 12 test VM (Proxmox; 2 vCPU, 1.9 GB RAM, 976 MB swap, kernel 6.1, OpenSim 0.9.3.1 on .NET 10.0.14, MariaDB 10.11).

### A.1 Start-up times (isolated bench copy, standalone mode)
A copy of the runtime in `/root/bench`, with its own ports and MariaDB database (`dgbench`); the real grid was untouched. Times are seconds from launch until the region is up with its scripts started.

**Synthetic content** (box linksets, small scripts):
| Content | Warm | All cold* | Added to a running spare program | Memory |
|---|---|---|---|---|
| Empty | 3.4 (first boot) | — | — | 170 MB |
| 3,000 prims, 300 scripts (100 different) | 2.3 | 2.8 | — | ~195 MB |
| 15,000 prims, 1,500 scripts (400 different) | 3.9 | 4.8 | 1.9 | ~270 MB |

\* No compiled scripts, no asset cache, and the OS disk cache dropped.

Heavy synthetic, warm: program start ~1.5 s, loading objects from the database ~1.1 s, scripts ~0.8 s (~1.6 s without compiled scripts), physics ~0.1 s.

**The owner's OARs:**
| Region | Contents | Warm | All cold | Spare program | Memory |
|---|---|---|---|---|---|
| Mountain Retreat | 282 objects, 3,060 prims, 172 scripts, no mesh | 3.9 | 4.4 | — | ~220 MB |
| DragonFyre (repaired copy) | 4,090 objects, 13,534 prims, 905 scripts, ~5,000 meshes | 19.0 | 20.5 | 17.3 | ~470 MB |

- DragonFyre: physics took 15 s (ubODE: 7,286 actors, 28,463 operations on one thread). Clearing the physics mesh cache added ~1 s; compiled scripts saved ~1 s. Physics shapes: 12,757 Prim, 520 Convex, 257 None.
- The owner's DragonFyre file is damaged after 21,407 assets ("invalid compressed data"). All objects, terrain and land come before the damage, so a repaired copy was made of everything readable. Some meshes are missing, so real physics would be slightly slower.

### A.2 Freeze on the bench (DragonFyre)
| Step | Result |
|---|---|
| Freeze (cgroup freezer) | 0.005 s |
| Memory out (`memory.reclaim`) | 468 → 82 MB in RAM, the rest to swap |
| Frozen 5 min, then its 2 database connections killed and the OS disk cache dropped | — |
| Wake: web status answers | 0.14 s |
| Database write after waking (`edit scale` + `backup`) | saved correctly, no log errors |
| Running after waking | dilation 1.00, 55 sim FPS, 55 physics FPS, 913 scripts active |
| Same test with memory left in RAM | wake 0.01 s |

### A.3 Freeze on the real grid
- **Welcome (empty) and a login:** frozen 16:34:23. The owner's Firestorm login reached Robust at 17:29:35; Welcome was woken 0.015 s later by a watcher that read Robust's log. Arrival ("Complete movement") was at 17:29:39, 4 s after the login request; a second login with Welcome awake took 3 s. A box rezzed afterwards was saved. The owner reported nothing unusual.
- **Test (DragonFyre, its own program) and a teleport:** frozen with memory pushed out (52 MB in RAM, 521 MB in swap). The owner teleported from Welcome; the connection reached Test's port at 17:54:06.492, it was awake at 17:54:06.505, and the avatar arrived at 17:54:10. The owner: "it worked well and as expected." The asset and material warnings in Test's log afterwards come from the damaged OAR.
- During a freeze, the launcher, Robust and the frozen region logged nothing, and the website kept working.

### A.4 Test tools (on the VM, in `/root/bench`)
- `make_oar.py`: synthetic OARs. `bench.py`: start, time phases from the log, stop. `series.sh`: warm and cold restarts. `attach.sh`: the spare-program test. `load.sh`: load an OAR and save it.
- `freeze.py`: the bench freeze test. `welcome_freeze.py`: Welcome freeze with wake-on-login (watches Robust's log). `region_freeze.py PROGRAM status|freeze|wake|watch [--pageout]`: any program, wake on a knock, memory out with `process_madvise`.
- Logs: `/root/bench/welcome-freeze.log`, `/root/bench/region-freeze.log`.

### A.5 Suspend on Windows (bench, 2026-11-08)
A standalone copy of this branch's `bin` on the owner's Windows 11 PC (20 threads, 63 GB RAM, 4 GB page file), set up like the VM bench but with SQLite, ports 9200/9201, Groups and Gloebit off. The owner was playing a game at the same time, so start-up times are noisy.

| Step | Mountain Retreat, 60 s | DragonFyre, 5 min | DragonFyre, 30 min |
|---|---|---|---|
| Suspend (`NtSuspendProcess`) | < 0.001 s | < 0.001 s | < 0.001 s |
| CPU used while suspended | 0 | 0 | 0 |
| Trim (`EmptyWorkingSet`) | 44 → 0 MB, 0.01 s | 462 → 0 MB, 0.05 s | 486 → 0 MB, 0.06 s |
| Windows' modified list | — | 73 → 483 MB | 79 → 511 MB |
| Page file in use | 80 MB, unchanged | 82 MB, unchanged | 82 MB, unchanged |
| TCP knock while suspended | connected, shown ESTABLISHED to the region | same | same |
| Resume, then web status answers | 0.00 s | 0.01 s | 0.01 s |
| The request sent while suspended | answered on resume | answered on resume | answered on resume |
| Working set 30 s after waking | 22 MB | 278 MB | 284 MB |
| Database write after waking | saved | saved | saved (later than the test's 10 s check) |
| Running after waking | dilation 1.00, 55 FPS, 172 scripts | dilation 1.00, 55 FPS, 912 scripts | dilation 1.00, 55 FPS, 911 scripts |
| Log after waking | clean | 1 missing-mesh warning (damaged OAR) | the same, plus 9 "Timeout in threadfunc" (section 7, item 5) |

- **ubODE on Windows:** this branch still ships the old `ubode.dll` (PR 48 is not merged). DragonFyre's physics start-up took 16.3 s with it, and 1.4 s with PR 48's `ubode.dll` (region up 22.0 s → 10.5 s). So the trimesh fix holds on Windows too.
- Loading the 2.5 GB DragonFyre OAR into SQLite took 351 s, and saving it took about 14 minutes (on MariaDB on the VM it took about 140 s). That's a SQLite cost, not part of sleeping.
- Tools, in `C:\Users\ystud\Desktop\CODE\winbench` (outside the repo): `make_ini.py` writes the config from the VM bench's OpenSim.ini. `winbench.py` does start, cmd, wait and stop. `winfreeze.py SECONDS [--trim] [--knock] [--object NAME] [--scale X]` is the test. Results are in `results-*.txt`.
