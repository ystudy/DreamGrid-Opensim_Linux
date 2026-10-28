# DreamGrid launcher — Regions (more regions, the world map, OAR library)

Date: 2026-10-27
Builds on: `2026-10-24-crossplatform-launcher-v1-design.md` (version 1) and the Bootstrap restyle (PR #6, merged into `V7` as 564faa1).
Repo: `ystudy/DreamGrid-Opensim_Linux`, branch `V7`.

This is the second of three agreed website steps: (1) Bootstrap for every page (done), (2) **Regions**, (3) a status dashboard with graphs (uptime, usage, errors), which gets its own design later.

Marking: **(owner)** means the project owner decided it in conversation. **(my call)** means the designer chose it and the owner has not ruled on it; the builder may revisit those with good reason.

## 1. Intent

Version 1 runs exactly one region, created by the setup checklist. "More regions" is the first item on the dashboard's "Coming soon" card that gets built. It turns the launcher's website into the place where an admin grows their world: they see their world on a map, drop new regions onto it, look after each region, and move region content in and out as OAR files.

**Who it is for:** the same newcomer as version 1, and an admin running a bigger world. Computers range from an old machine a young person is learning on to a production server. **(owner)** Eight regions is the starting target.

**Why:** a world with one region can't grow. The owner wants several regions, both connected (touching on the map, so people can walk or fly across) and separate islands (reached by teleport).

## 2. Goals, non-goals, anti-goals

### Goals
1. A **Regions page** built around **Robust's world map**: drag to move, zoom in and out, and a button that brings you back to your regions. **(owner)**
2. **Region types** beside the map, dragged onto an empty spot (or selected, then the spot clicked) to create a region. Built in: **Default Island, Sandy Beach, Forest, Meadow**. **(owner)**
3. **Connected or island is decided by placement:** touching an existing region means connected, open water means a separate island. The page says which, in words, before the admin confirms. **(owner)**
4. When adding a region, the admin picks **how it runs**: on its own (its own OpenSim program) or sharing a program with existing regions. Each choice shows plain-language **warnings of its limits**. **(owner)**
5. When adding a region, the admin picks its **size**: 1×1 by default, larger sizes offered with their risks. **(owner)**
6. **Clicking a region on the map** opens its info panel: status and information, actions (Import OAR, Save as OAR, Restart, Stop/Start, Move, Delete), and per-region settings. **(owner)**
7. An **OAR library**: a folder the launcher creates, and a page to list, upload, download (export) and delete OAR files. **(owner)**
8. Any library OAR can become a **custom region type** that appears beside the built-in four. **(owner)**
9. **Deleting a region is recoverable:** it goes to "Recently deleted" and can be restored. **(owner)**
10. **All of DreamGrid's per-region settings** that make sense without other features; the rest arrive with their own features (§6.7). **(owner)**

### Non-goals (this step)
- SmartStart, freeze/thaw, Endless Land, Hypergrid, Birds, Tides, Backups, Voice. They keep their "Coming soon" place. Their per-region settings come with them (§6.7).
- Regions that run on another computer (a second server joining this grid).
- Editing the terrain or objects from the website (map editor, terrain painter).
- Graphs and history: step 3.
- More than one admin, or regions owned by other avatars. New regions belong to the owner avatar's estate.

### Anti-goals: failures even if it technically works
- A newcomer has to know what a region port, an instance or an INI file is to add a region.
- Adding, restarting or deleting one region stops or disturbs **other** regions without the page saying so first.
- A region placed on top of another, or a spot that looks like empty water but is taken (a region not yet drawn on the map, a stopped region, a deleted one).
- Delete loses work with one click.
- The website loads anything from the internet (version 1 rule, enforced by `LayoutTests`), or stops working without JavaScript where version 1 pages worked without it. The map page itself may need JavaScript; it must say so plainly when it's off.

## 3. Constraints
- Everything from version 1 still holds: .NET 10, Linux (Debian 12/13) and Windows, the launcher's private MariaDB, all INIs generated from the prototypes, no hand-edited INI, every settings change through `IGridController.UpdateSettings`, minimal changes under `OpenSim/` (monthly Fred merges, spec §4.6), and regions keep running when the launcher restarts (spec §4.12, adoption by PID and start identity).
- The website uses Bootstrap 5.3 (bundled, `wwwroot/lib/bootstrap`) and must keep the plain `<footer>DreamGrid <version></footer>` tag: the install smokes read the version from it.
- Upgrades: an existing version 1 install has `LauncherSettings.Region` (one region, instance folder, ports 8004 and console 8011). An upgrade must turn that into the new region list without moving or recreating the region (same UUID, same folder, same ports, same database rows), and without a restart of a running region beyond what the launcher upgrade already does.
- OpenSim facts the design relies on:
  - A region's map spot is `Location = X,Y` in its region `.ini`. Two regions are connected exactly when their squares touch; there is no separate setting.
  - Robust refuses a region whose squares overlap a registered region ("Region overlaps another region"). A crashed region can stay registered until deregistered.
  - Several region `.ini` files in one instance's `Region/` folder run in one OpenSim process. DreamGrid's layout is `Regions/<InstanceName>/Region/<RegionName>.ini`, and version 1 already writes that layout.
  - Each region needs its own `InternalPort` (UDP); each OpenSim process needs its own `http_listener_port` and REST console port.
  - All regions share the `opensim` database; a region's land and objects are keyed by its region UUID.
  - Robust's `MapImageService` (enabled in `bin/Robust.HG.ini.proto`) serves map tiles; a region uploads its tile only after it has run for a while, and how it draws it follows the region's map setting.
  - `save oar` / `load oar` run on the region's console and need the region running. Saving gathers every asset the region uses, so time and size vary from seconds (empty) to tens of minutes and gigabytes (heavy builds).
  - Region sizes are multiples of 256 m. DreamGrid offered 1×1 up to 16×16. Larger regions use more memory and run slower; some viewers handle very large regions poorly.

## 4. Words used here
- **Region:** one piece of land on the map (256 m × 256 m per square).
- **Program** (OpenSim instance, DreamGrid's "DOS box"): one OpenSim process hosting one or more regions. This document's word only: the website never says "program" or "instance", it says "runs on its own" or "runs with Home, Beach". **(owner)**
- **Parked:** a region the admin has switched off for good, until they unpark it (§6.8).
- **Region type:** a named OAR shown beside the map for dragging onto it. Built in, or custom (made from a library OAR).
- **OAR library:** the launcher's folder of OAR files and the page that manages it.

## 5. The pages

### 5.1 Navigation
The top bar gains **Regions** (between Home and Advanced). The OAR library is reached from the Regions page and from the bar's Regions entry. The dashboard keeps its overview and links each region's row to the Regions page; "More regions" leaves the "Coming soon" card. **(my call)** The library's place in navigation (a sub-page of Regions versus its own top-bar entry) is left to the builder.

### 5.2 Regions page: the map
- **Layout (owner):** the region-type list beside the map (left on wide screens), the map in the middle, the info panel on the right when a region is selected. On a phone they stack, and the info panel opens over the map. **(my call for phone)**
- **The map** shows Robust's tiles. Drag to move, + / − and the mouse wheel to zoom, and a "Back to my regions" button. The whole world area can be explored; there's always room for separate islands.
- **The gap fix (owner):** every region the launcher knows about is drawn from the launcher's own list, not only from Robust's tiles: running, stopped, starting, failed, not yet drawn, and Recently deleted (marked "reserved"). A region without a tile yet is a marked square with its name (the mockup used stripes). The tile, when it exists, is the background; the launcher's marker and name are always on top.
- **Placing (owner):** drag a type onto the map, or select a type and click a spot. A marker the size of the chosen region shows where it will go. The page states in words what the spot means: "This region will join **Beach** on its east side" or "This will be a **separate island**; visitors reach it by teleport". A spot that overlaps any region (including stopped and deleted ones) can't be chosen, and the page says why.
- **Tiles come through the launcher (my call):** the page loads tiles from a launcher endpoint that proxies Robust's map service, so the browser never needs Robust's port, and the "no external URLs" rule holds.

### 5.3 Add a region (after placing)
A form (a dialog, or a panel beside the map) with:
- **Region name**, validated like setup's region name (`CreateRegionAsync` rules), unique across all regions, Recently deleted included.
- **The type** chosen and the spot, with the connected or island sentence.
- **Size (owner):** 1×1 preselected; 2×2, 3×3, 4×4 … up to 16×16. The marker on the map grows to match, and placement is rechecked. Each larger size shows its risk, growing with size, for example: "A 4×4 region is 16 squares of land in one program. It uses several times the memory of a 1×1 and can feel slower to visitors. Many viewers handle very large regions poorly." A region type's 1×1 OAR goes in the middle of a larger region, with the rest left as water or plain land. **(my call)**
- **How it runs (owner):**
  - **On its own** (preselected unless memory is short): its own program. Warning text uses this computer's numbers, for example: "Uses about 400 MB more memory. This computer has 5.2 GB free, enough for about 12 more regions run this way. If it crashes, only this region goes offline."
  - **Share with [a program]**: a list of existing programs by their regions, e.g. "Home, Beach (2 regions)". Warning: "Saves memory, but these regions start, stop, restart and crash together. Restarting Sunset Shore also restarts Home and Beach, and drops everyone in them."
  - When free memory is short for "On its own", the page says so and suggests sharing. **(my call)**
  - The memory figure starts as an estimate (about 400 MB per program for a 1×1, scaled for size) and switches to what this computer's own programs actually use once some have run. **(my call)**
- **Create** shows progress with plain stages, like setup's region step: "Creating your region", "Loading the region content", "Waiting for the region to accept logins". A failure shows a plain message and a "Show details" log excerpt, and the admin can retry or change the choices.
- Adding a region to a **shared** program restarts that program, so the form names the regions that will restart and asks before going ahead. **(my call: OpenSim has `create region` on a running console, but a restart is simpler and matches how the program's settings are written; the builder may use `create region` if it proves reliable.)**

### 5.4 The region info panel
Opened by clicking a region on the map, or from the dashboard. Shows:
- **Status:** running, starting, stopped, failed (with the plain message and log excerpt, as on the dashboard). Who's in it now (names, from the existing who's-online data), how long it has run, its program's memory, map spot, size and neighbours, and which program it runs in (with the other regions sharing it).
- **Actions:**
  - **Import OAR…** picks a file from the OAR library (§5.5) and replaces the region's land and objects after an "are you sure?" that says the current content will be replaced. Progress is shown.
  - **Save as OAR** writes the region to the library as `<RegionName>-<date>.oar`, with progress.
  - **The region's power menu** (§6.8): Restart, Stop, Park; or, when parked, Unpark and start, and Unpark. When the region shares a program, each item names every region it affects, and confirms first.
  - **Move…** chooses a new spot on the map with the same placement rules. The region's program restarts to apply it (the page names the regions affected).
  - **How it runs:** change between on its own and sharing (or which program it shares). It applies at the next restart of the programs involved, and the page offers "Restart now".
  - **Delete…** (§5.6).
- **Settings tabs** (§6.7). A change that needs a restart says so and offers "Restart now", naming every region that will restart.

### 5.5 The OAR library
- **The folder (owner):** the launcher creates it in its data directory (my call for the name: `<data>/oars`). It is owner-only like the rest of the data directory.
- **The page:** lists every `.oar` file with name, size and date. **Upload** (from the browser, with a progress bar; large files allowed, see §8), **Download**, **Delete** (with "are you sure?"), and **Use as a region type**.
- Files copied straight into the folder (for example over SFTP on a server) appear on the page. **(my call)** That is the suggested route for very large files.
- Import OAR (§5.4) picks from this list; Save as OAR writes into it.

### 5.6 Recently deleted
- **Delete (owner)** moves a region to "Recently deleted". It is instant and works even when the region is broken. The region is stopped (its program restarts without it when shared), its region `.ini` is set aside (DreamGrid renamed it `.bak`), it is deregistered from Robust, and its land and objects stay in the database.
- Its map spot shows as **reserved** and can't be taken.
- **Restore** puts it back as it was, at the same spot and in its old program, or on its own if that program is gone. **(my call)**
- **Delete forever** erases its rows from the database for good, after an "are you sure?".
- **Nothing is deleted automatically (my call);** the list shows how much space each deleted region still holds.
- **Optional:** a checkbox on Delete, "Also save it to the OAR library", off by default, explained as taking from seconds to many minutes and needing the region to be running.

### 5.7 Region types
- **Built in (owner):** Default Island, Sandy Beach, Forest, Meadow. They are **finished regions** (trees, plants, things on them) that the project owner builds in-world and saves as OARs. They ship with the launcher at fixed paths beside the starter region (version 1 spec §6.3; e.g. `content/region-types/<type>.oar`). Replacing a file changes the type with no code change.
- **Until an OAR exists (my call, as for the starter region):** the launcher generates land of roughly the right shape with OpenSim's terrain commands (the existing `IslandTerrain` approach): island, flat sandy shore, greener rolling meadow, hilly forest ground. No objects. The page works from day one.
- **Custom types (owner):** in the OAR library, "Use as a region type" asks for a name and makes the OAR appear in the list beside the map. Custom types can be removed from the list (the file stays in the library). Built-in types can't be removed.
- **Pictures in the list:** a small picture per type. For built-in types, shipped with the OAR. For custom types, the builder chooses (a default picture, or the map tile of a region made from it). **(my call)**

## 6. Behaviour

### 6.1 Settings model (my call)
`LauncherSettings.Region` (one) becomes a list of regions and a list of programs:
- **Region:** name, UUID, map spot X/Y, size, region type it was made from, its program, its UDP port, its per-region settings (§6.7), a parked flag (§6.8), and a deleted flag with the time it was deleted.
- **Program:** a stable folder name (the instance), its HTTP port, its REST console port, and its CPU settings (§6.7, CPU applies per program).
The settings schema version goes up. Loading an older file converts the single region into one region in one program, keeping the folder name, UUID and ports, so a running region is adopted unchanged.

### 6.2 Ports (my call)
- Version 1's region (8004 UDP and HTTP, console 8011) keeps its ports.
- New programs and regions take the next free ports from ranges the launcher picks, avoiding every other configured port and checking that the ports are free when starting, as version 1 does (`PortCheck`). A taken port shows the same plain "port in use" message as today.
- **Advanced → Network** shows the region ports in use and where new ones come from. The old single "Region port" field becomes a first port for the range. How exactly the range is shown is left to the builder.
- Firewall: the Windows installer opens a fixed set of ports today. New region ports need opening on Windows (the region UDP and HTTP ports must be reachable from the LAN) — see §8.

### 6.3 Supervision
- Start order stays: MariaDB → Robust → regions. Programs start one after another, or a few at a time; the order and parallelism are left to the builder, but Robust must answer first and one program failing must not stop the others from starting.
- **Each program is a supervised child**, with the same crash restart, backoff and "stop retrying after N rapid failures" as version 1's single region, and adoption at launcher start (PID file and `.start` sidecar per program).
- **RegionReady alerts** carry the region name and UUID, so the launcher can tell which region in a program is ready.
- Stop all stops every program, then Robust, then MariaDB. Per-program stop sends `quit` and kills after a timeout, as now.
- A program whose regions are all stopped or deleted is not started.

### 6.4 Status
- `GridStatus` reports every program and every region (state, uptime, memory per program, which regions are in which program, start failures). The dashboard's status card lists regions (or a summary when there are many) and links to the Regions page. `/api/status` keeps its anonymous/admin split (names and states only for anonymous callers).
- Who's online gains each person's region name.

### 6.5 Creating a region
Writes the region `.ini` (and the program's `OpenSim.ini` when it is a new program), starts or restarts the program, waits for RegionReady, then loads the type's OAR (or generates the placeholder terrain), with progress reported as in setup. The new region belongs to the owner avatar's estate. **(my call)** The setup checklist's first region uses the same path, so there is one way regions get made.

### 6.6 OAR jobs
Import, Save and the optional save-on-delete are long jobs. They run in the background (like setup's region job), survive the browser closing, run one at a time per program **(my call)**, report progress the page can poll, and are awaited at shutdown like other background work. They talk to the region's program through the REST console, using the console's `change region` before `load oar` / `save oar` in a shared program.

### 6.7 Per-region settings
**In this step (owner),** as tabs in the info panel, written into the region's `.ini` or its program's INIs.

**Region settings and program settings (my call).** OpenSim reads some of these per region (from the region `.ini`) and some only per program (from the program's `OpenSim.ini`): physics engine (`[Startup] physics`), script engine (`[Startup] DefaultScriptEngine`), frame time, the god permissions, map-tile drawing and CPU. Those are **program settings**: every region sharing a program shares them, the tab says so ("Home and Beach share this setting because they run in one program"), and changing one restarts the program, naming every region affected. The "Share with" choice when adding or changing how a region runs lists the settings that will change for it (the target program's values win), as part of its warnings. OpenSim is not patched to read them per region (spec §4.6). Which keys are per region and which per program is checked against the OpenSim sources while planning.

- **Basics (my call on what can change):**
  - **Name:** can be changed while the region is stopped. The region file is renamed and it registers under the new name.
  - **Size:** can grow, if the larger area is free on the map. Shrinking warns that land and objects outside the new edge are lost, and offers "Save as OAR first".
  - **Map spot:** the same as Move (§5.4).
  - **Region UUID:** shown, read-only. Changing it would leave the old content orphaned in the database for no gain the admin can see.
- **Options:** maximum visitors (`MaxAgents`), maximum objects (`MaxPrims`), largest physical and non-physical object size, and clamp object size.
- **Map:** how the map tile is drawn: None, Simple, Good, Better, Best (DreamGrid's map overrides).
- **Physics:** grid default, ubODE, BulletSim, Bullet in its own thread, or Hybrid.
- **Scripts:** grid default, On (YEngine) or Off; script timer rate and frame rate.
- **Permissions:** allow gods, estate owner is god, estate manager is god.
- **Access:** keep residents out (only owners and managers can enter), and announce visitors in chat (Concierge).
- **CPU:** cores and process priority (a program setting, like the ones above). "Real time" and "High" carry a warning that they can make the whole computer unresponsive. The Linux and Windows mechanisms differ (affinity and nice on Linux, affinity and priority class on Windows); priorities that need extra rights on Linux are shown as unavailable with a plain reason.

"Grid default" means a grid-wide setting decides, as in DreamGrid. Where no grid-wide setting exists yet (physics and scripts engines), this step adds it to Advanced Settings. **(my call)**

**With their own features later (owner):** Birds, Tides, keep Hypergrid visitors out (Hypergrid), skip automatic OAR backup (Backups), Publicity and the OpenSimWorld key, Gloebit, and the teleport sign. Each adds its rows to the same panel through the module model (version 1 spec §6.9).

### 6.8 Power menus and parked regions (owner)
Modelled on the Windows power button: one button that opens a short menu. Mockup: `power-menus.html` (§9).

**The world's power menu** (dashboard, replacing today's Start all / Stop all / Restart row; the per-component restarts stay under "More"):
- **Start:** MariaDB, Robust and every region except parked ones. This is also what the launcher does at boot and when it adopts children.
- **Start everything:** parked regions too; they stop being parked.
- **Restart:** every running region, Robust and the database. Parked regions stay off.
- **Stop all:** everything goes offline. Parked regions stay parked.

**A region's power menu** (info panel):
- **Restart:** restarts its program. Every region that runs with it is named ("Home restarts too, they run together").
- **Stop:** off until the world next starts (Start, or a launcher or computer restart). In a shared program, the program restarts without this region, so the others go offline briefly and come back.
- **Park:** off, and it stays off through world starts and restarts until unparked. Shared programs behave as for Stop.
- **When parked:** "Unpark and start", and "Unpark" (it starts with the world next time).

A parked region shows "Parked" on the map; its spot stays taken. A program whose regions are all stopped or parked doesn't run. "Stopped" is not saved across a world start; "parked" is saved in the settings (a per-region flag, §6.1). The legacy port-8001 `StopRegion` is a Stop, never a Park. **(my call)**

## 7. Decisions recap

| Decision | By |
|---|---|
| The map is Robust's world map, scrollable and zoomable; regions without tiles are always drawn as named markers | owner |
| Region types beside the map, dragged on; built in: Default Island, Sandy Beach, Forest, Meadow | owner |
| Built-in types are finished regions shipped as OARs the owner builds; generated land stands in until each exists | owner (stand-in: my call, following the version 1 starter region) |
| Connected or island comes from placement alone | owner |
| The admin chooses "on its own" or "share with" per region, with warnings; "on its own" is preselected | owner (preselection: my call) |
| Size is chosen per region, 1×1 by default, risks shown | owner |
| Clicking a region shows its info, actions (incl. Import OAR) and settings | owner |
| OAR library folder and page with upload, download, delete | owner |
| Library OARs can become custom region types | owner |
| Delete goes to "Recently deleted", restorable; nothing is deleted automatically | owner (no automatic erase: my call) |
| All per-region settings that stand alone now; feature-tied ones with their features | owner |
| Power menus like Windows' for the world and each region; Stop is temporary, Park lasts; one region's Stop restarts its shared program without it | owner |
| The website never says "program" or "instance" | owner |
| Settings model, ports, OAR job rules, tiles through a launcher proxy, setup reusing the same create path | my call |

## 8. Left to the builder
- The map library or drawing approach (a bundled JavaScript map library, or hand-written; bundled, never from a CDN), tile caching, and zoom levels.
- How the library page is reached in navigation; the exact wording of warnings (plain language, numbers from this computer).
- The upload mechanism for large OARs (streamed, chunked or resumable), the size limit (if any), and Kestrel's request size settings. Uploads must not hold a whole file in memory.
- The port ranges, how they're shown on Advanced → Network, and how the Windows installer and firewall rules cover region ports opened after install (a port range rule at install time, or rules added by the service). The Debian side has no firewall step today.
- Whether adding a region to a shared program uses a program restart or OpenSim's `create region` on the live console (§5.3).
- How the memory estimate is measured and updated.
- How "Delete forever" removes a region's rows (OpenSim console commands where they exist, otherwise SQL against the `opensim` database for that region UUID), and how the space a deleted region holds is measured.
- **The port-8001 legacy command API (my call):** `regionlist` and `RegionStatus` cover every region. `StartRegion`, `StopRegion` and `RestartRegion` act on the named region with the same behaviour as the website's buttons (§5.4), and log which other regions they affected. No script can see a warning, so the log line is the record.
- **The end-to-end "logs in" check (my call):** an XML-RPC `login_to_simulator` to Robust with the second region as the start location, checking it returns that region's address and port. A full UDP handshake is not required.
- Tests: the settings migration from version 1 (single region → lists, adoption unchanged), placement rules (overlap, adjacency, size), ports, Recently deleted and restore, the library endpoints (auth, path safety: no `..`, only `.oar`), and the existing smokes still passing. An end-to-end test that adds a second region and logs in to it, in CI on Windows and Debian.

## 9. References
- Version 1 design: `docs/superpowers/specs/2026-10-24-crossplatform-launcher-v1-design.md` (§4.12 adoption, §6.3 starter content, §6.6 supervision, §6.7 INI generation, §6.9 modules).
- DreamGrid manuals (`.ref/txt/`, untracked): `Region.txt` (region panel, DOS boxes, overrides, CPU), `Regions.txt` (overlap errors), `Load OAR.txt`, `Physics.txt`, `Scripts.txt`, `Permissions.txt`, `Map Overrides.txt`, `Maps.txt`.
- Mockups from the conversation: `.superpowers/brainstorm/51-1793128151/content/` (untracked): `region-map-v3.html` (layout), `add-region-program.html` (how it runs) and `power-menus.html` (power menus).
- Code: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs` (`RegionSettings`), `Grid/GridController.cs`, `Grid/IslandTerrain.cs`, `Config/GridConfigWriter.cs` (region INIs), `OpenSim/RegionAlert.cs`, `OpenSim/RestConsoleClient.cs`.
