# Plan 4a — Multi-region engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The launcher runs any number of regions, each on its own OpenSim program or sharing one, with Windows-style power actions (Start, Start everything, Restart, Stop all; per region Start, Stop, Restart, Park, Unpark), and upgrades a version 1 install without touching its running region.

**Architecture:** `LauncherSettings` swaps its single `Region` for lists of programs and regions (schema 2, migrated on load). Pure helpers decide placement and ports. `GridConfigWriter` writes one `OpenSim.ini` per program and one region file per loadable region. `GridController` supervises one child per program, tracks each region's state from RegionReady alerts and the console, and exposes region-level operations. The website and the port-8001 API move to the new status and operations; the map-based Regions page is plan 4b.

**Tech Stack:** C# / .NET 10, ASP.NET Core Razor Pages, xUnit v3 (Microsoft Testing Platform), OpenSim REST console, MariaDB.

**Spec:** `docs/superpowers/specs/2026-10-27-regions-design.md` (and the version 1 spec it builds on, `2026-10-24-crossplatform-launcher-v1-design.md`).

## Where this plan sits

The Regions design is three plans, each shipping working software:

- **4a (this plan): the engine.** Settings model and migration, placement and port rules, per-program supervision, adding regions, power actions and parking, status, dashboard power menu, port-8001 API, Advanced → Network. There's no map UI yet; regions are added through `IGridController.AddRegionAsync` (tests and plan 4b).
- **4b: the Regions page.** Robust map tiles through a launcher proxy; the map with markers for every known region; region types beside the map (built-in four with generated stand-in land); the add-region form (name, size, how it runs, warnings with memory numbers); the info panel with the region power menu, Move, how-it-runs changes and the per-region settings tabs (spec §6.7, including program settings and grid-wide physics and script defaults); navigation.
- **4c: OAR library and Recently deleted.** `<data>/oars`, the library page (upload, download, delete), Import / Save as OAR jobs, custom region types, Delete → Recently deleted, Restore, Delete forever.

Plans 4b and 4c are written after 4a lands, against the interfaces below.

## Global Constraints

- .NET 10; Linux (Debian 12/13) and Windows; no changes under `OpenSim/` (spec §3, version 1 §4.6).
- Every settings change goes through `IGridController.UpdateSettings` (version 1 §6.8); the installers' `set-web-port` and the `--web-port` host option stay the only exceptions.
- Regions keep running when the launcher restarts: adopt by PID file and `.start` sidecar, never start a duplicate (version 1 §4.12).
- An upgrade from a version 1 install keeps the region's folder, UUID, ports and database rows, and adopts a running region without restarting it (spec §3).
- The website loads nothing from the internet, keeps working without JavaScript where it did, and keeps the bare `<footer>DreamGrid <version></footer>` tag (the install smokes read it).
- `/api/status` keeps `grid.loginsEnabled` (the install smokes and `InstallSmoke` read it), and keeps names and states only for anonymous callers.
- The website never says "program" or "instance"; it says "runs on its own" or "runs with Home, Beach" (spec §4).
- Region names: 2–32 letters, digits, spaces, `-`, `_`, `'`, no leading or trailing space (`GridController.IsValidRegionName`), unique ignoring case across all regions, deleted ones included.
- Region sizes 1 to 16 (squares per side), default 1 (spec §5.3).
- Two regions are connected exactly when their squares share an edge; no region may overlap another, parked and deleted ones included (spec §5.2).
- Stop is temporary (cleared by a world Start); Park is saved and lasts until Unpark; "Start everything" unparks (spec §6.8).
- No installer change for ports: the Windows firewall rule allows the private runtime's `dotnet.exe` (`setup/windows/DreamGrid.iss`, `FirewallRuleName`), not fixed ports, so new region ports are covered; Debian has no firewall step.
- The legacy `StopRegion` is a Stop, never a Park, and logs every other region it affected (spec §6.7, §8).

## Review Focus

1. **A version 1 settings file with a region running at upgrade:** the region is adopted, keeps ports 8004/8011 and its folder, and is not restarted. Pinned by `Migrates_v1_single_region` (Task 1) and `Upgraded_v1_grid_is_adopted_unchanged` (Task 10).
2. **A region alert for a region the launcher doesn't know** (a hand-made region file, a renamed region): ignored without changing any region's state. Pinned by `Alert_for_unknown_region_is_ignored` (Task 5).
3. **Stopping or parking the only loadable region of a shared program:** the program stops instead of restarting empty. Pinned by `Stopping_last_region_stops_its_program` (Task 7).
4. **The launcher restarts while a program runs without a stopped region:** after adoption that region shows Stopped, not Running, and a world Start brings it back by restarting its program. Pinned by `Adopted_program_reports_unloaded_region_as_stopped` (Task 5) and `World_start_restarts_program_missing_a_region` (Task 7).
5. **Port allocation next to configured ports:** new ports skip the web, MariaDB, Robust and console ports and every port a region holds, parked and deleted regions included. Pinned by `Next_port_skips_configured_and_reserved_ports` (Task 2).

---

### Task 1: Settings schema 2 and migration

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/SettingsStore.cs`
- Create: `Launcher/DreamGrid.Launcher.Core/Settings/RegionModel.cs`
- Modify (compile only, single-region behaviour kept): every `s.Region` use listed by `grep -rn "\.Region\b" Launcher --include=*.cs --include=*.cshtml` (GridController, GridConfigWriter, LegacyCommandApi, LogReader, StatusApi, SetupRunner, Pages/Index, Pages/Setup/Region) and the test fakes and seeds
- Test: `Launcher/DreamGrid.Launcher.Tests/SettingsStoreTests.cs`, `Launcher/DreamGrid.Launcher.Tests/RegionModelTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ProgramSettings(string Instance, int HttpPort, int ConsolePort, string Physics = "ubODE");`
  - `public sealed record RegionSettings(string Instance, string Name, Guid Uuid, int X = 1000, int Y = 1000, string Content = "starter", int Size = 1, int UdpPort = 0, bool Parked = false, DateTimeOffset? DeletedAt = null);` (`Physics` moves to the program; `Instance` names the region's program)
  - `LauncherSettings`: `SchemaVersion` default `2`; `List<ProgramSettings> Programs`, `List<RegionSettings> Regions` (both default empty); `RegionPort` and `RegionConsolePort` stay and now mean the first port tried for new programs/regions and new consoles.
  - `public static class RegionModel`: `IEnumerable<RegionSettings> Live(LauncherSettings s)` (not deleted), `IEnumerable<RegionSettings> In(LauncherSettings s, string instance)` (live, in that program), `ProgramSettings? ProgramOf(LauncherSettings s, RegionSettings r)`, `RegionSettings? Find(LauncherSettings s, Guid id)`, `RegionSettings? FindByName(LauncherSettings s, string name)` (ignoring case, deleted included).
- The version 1 JSON property `region` is read only for migration: a `[JsonPropertyName("region")] LegacyRegionV1? LegacyRegion` init property (`record LegacyRegionV1(string Instance, string Name, Guid Uuid, int X, int Y, string Physics, string Content)`), never written (`JsonIgnore(Condition = WhenWritingNull)`).

- [ ] **Step 1: Write the failing tests**
  - `Migrates_v1_single_region`: write a version 1 JSON (schemaVersion 1, `"region": {"instance":"Welcome","name":"Welcome","uuid":G,"x":1000,"y":1000,"physics":"BulletSim","content":"island"}`, regionPort 8004, regionConsolePort 8011); `LoadOrCreate()` returns `SchemaVersion == 2`, `Programs == [new ProgramSettings("Welcome", 8004, 8011, "BulletSim")]`, `Regions == [new RegionSettings("Welcome", "Welcome", G, 1000, 1000, "island", 1, 8004)]`, `LegacyRegion == null`.
  - `Migration_is_saved_once`: after the load above the file on disk has `"schemaVersion": 2`, `"programs"` and no `"region"` key.
  - `V1_without_region_migrates_to_empty_lists`.
  - `Schema_2_round_trips` (programs, regions, parked, deletedAt).
  - `RegionModel_find_by_name_ignores_case_and_includes_deleted`; `RegionModel_live_skips_deleted`.

- [ ] **Step 2: Run them and confirm they fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*SettingsStoreTests" --filter-class "*RegionModelTests"`. Expected: build errors or failures naming the missing members.

- [ ] **Step 3: Implement.** Migration lives in `SettingsStore.Read`: when `SchemaVersion < 2`, convert and `Save` before returning. Then fix every compile site with the region list (first live region where version 1 code assumed one), keeping behaviour unchanged; `GridConfigWriter` takes physics from `RegionModel.ProgramOf`.

- [ ] **Step 4: Run the whole unit suite.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests`. Expected: all pass (6 Linux-only skips on Windows).

- [ ] **Step 5: Commit** — `Launcher: settings schema 2 (programs and regions), migrated from version 1`.

### Task 2: Placement and ports

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Grid/RegionPlacement.cs`
- Create: `Launcher/DreamGrid.Launcher.Core/Grid/PortAllocator.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/RegionPlacementTests.cs`, `Launcher/DreamGrid.Launcher.Tests/PortAllocatorTests.cs`

**Interfaces:**
- Consumes: Task 1's model.
- Produces:
  - `public sealed record Neighbour(string Name, string Side);` — `Side` is the side of the existing region the new one touches: `"north"`, `"east"`, `"south"` or `"west"` (Y grows north).
  - `public sealed record PlacementCheck(bool Ok, string? OverlapsWith, IReadOnlyList<Neighbour> Neighbours);`
  - `public static class RegionPlacement { public const int MaxSize = 16; public static PlacementCheck Check(LauncherSettings s, int x, int y, int size, Guid? ignore = null); }` — a region at (x, y) of size n covers x..x+n−1 and y..y+n−1; checks every region, parked and deleted included, except `ignore`.
  - `public static class PortAllocator`:
    - `IReadOnlySet<int> Taken(LauncherSettings s)`: the web, MariaDB, Robust public, private and console ports, and every program's `HttpPort` and `ConsolePort` and every region's `UdpPort` (deleted included).
    - `int Next(LauncherSettings s, int from, IReadOnlySet<int>? alsoTaken = null)`: the first port ≥ `from` and ≤ 65535 that's in neither set; throws `InvalidOperationException("No free port from {from}.")`.
    - `LauncherSettings? Replace(LauncherSettings s, int oldPort, int newPort)`: the settings with every program or region assignment equal to `oldPort` moved to `newPort`, or null when no assignment uses `oldPort`.
- Allocation rules for later tasks (copy exactly): a new program gets `HttpPort = Next(s, s.RegionPort)` and `ConsolePort = Next(s, s.RegionConsolePort, {HttpPort})`; the program's first region gets `UdpPort = HttpPort` (TCP and UDP share the number, as version 1's 8004 does); a region joining an existing program gets `UdpPort = Next(s, s.RegionPort)`.

- [ ] **Step 1: Write the failing tests**
  - `Touching_east_edge_is_a_neighbour`: Home at (1000,1000) size 1; `Check(s, 1001, 1000, 1)` → `Ok`, `Neighbours == [new("Home", "east")]`.
  - `Corner_only_is_not_a_neighbour`: (1001,1001) → no neighbours.
  - `Overlap_with_parked_or_deleted_is_refused`: parked region at (1000,1000) → `Check(s,1000,1000,1)` gives `Ok == false`, `OverlapsWith == name`; the same for one with `DeletedAt` set.
  - `Large_region_overlaps_by_area`: a 4×4 at (998,998) overlaps Home at (1000,1000).
  - `Ignore_skips_the_region_itself` (for Move in 4b).
  - `Next_port_skips_configured_and_reserved_ports`: defaults (web 8001, Robust 8002/8003/8010, region console 8011, MariaDB 3307), one program 8004/8011 with region UDP 8004 and a deleted region holding 8005 → `Next(s, 8004) == 8006`; `Next(s, 8010) == 8012`.
  - `Next_port_throws_past_65535`.
  - `Replace_moves_program_and_region_ports`; `Replace_returns_null_for_unassigned_port`.

- [ ] **Step 2: Run and confirm they fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*RegionPlacementTests" --filter-class "*PortAllocatorTests"`.

- [ ] **Step 3: Implement** the two static classes.

- [ ] **Step 4: Run and confirm they pass** (same command).

- [ ] **Step 5: Commit** — `Launcher: region placement and port allocation rules`.

### Task 3: Config per program; port check and logs per program

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Config/GridConfigWriter.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/PortCheck.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Runtime/RuntimeStager.cs` (`WriteLogConfigs`)
- Modify: `Launcher/DreamGrid.Launcher.Core/Logs/LogReader.cs`, `Launcher/DreamGrid.Launcher/Setup/SetupRunner.cs` (log sources)
- Test: `Launcher/DreamGrid.Launcher.Tests/GridConfigWriterTests.cs`, `PortCheckTests.cs`, `LogReaderTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2.
- Produces:
  - `GridConfigWriter.Write(LauncherSettings s, IReadOnlyList<IIniContributor> contributors, IReadOnlySet<Guid>? held = null)`: for every program, `Regions/<Instance>/OpenSim.ini` with `http_listener_port = HttpPort`, `[Network] console_port = ConsolePort`, `[Startup] physics` and `meshing` from the program, PID file `<pids>/<Instance>.pid`; for every region of that program that is not deleted, not parked and not in `held`, `Regions/<Instance>/Region/<Name>.ini` with `Location = X,Y`, `SizeX = SizeY = 256 × Size`, `InternalPort = UdpPort`. Any other `*.ini` in that `Region/` folder is renamed to `<file>.off` (replacing an older `.off`), so OpenSim never loads a region the launcher holds back.
  - `RegionModel.Loadable(LauncherSettings s, string instance, IReadOnlySet<Guid> held)`: the regions the writer writes for that program (add to Task 1's class).
  - `PortCheck.FindConflictPort(LauncherSettings s, IReadOnlySet<int> skip, IReadOnlySet<string> programs)`: checks the fixed ports and, for each named program, its HTTP and console ports and its loadable regions' UDP ports. Messages: `Port {p} (Region {name}) …`, `Port {p} (Region {name} UDP) …`, `Port {p} (Region console for {name}) …`, naming the program's first region.
  - `RuntimeStager.WriteLogConfigs(IEnumerable<string> instances, string level)`; log sources `region-<Instance>` for every program.

- [ ] **Step 1: Write the failing tests**
  - `Writes_one_opensim_ini_per_program_with_its_ports`: two programs (8004/8011, 8006/8012) → both files, each with its own `http_listener_port` and `console_port`.
  - `Shared_program_gets_one_region_file_per_region`: Home (UDP 8004) and Beach (UDP 8005) in one program → two region files with `InternalPort` 8004 and 8005.
  - `Region_size_sets_SizeX_and_SizeY`: size 4 → `1024`.
  - `Parked_held_and_deleted_regions_are_set_aside`: a pre-existing `Beach.ini` for a parked Beach becomes `Beach.ini.off`; no `Beach.ini` remains; the same for a held region and a deleted one.
  - `Port_check_names_the_region_of_a_taken_program_port`: bind 8006 → message contains `Region Beach`.
  - `Log_sources_list_every_program`.

- [ ] **Step 2: Run and confirm they fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*GridConfigWriterTests" --filter-class "*PortCheckTests" --filter-class "*LogReaderTests"`.

- [ ] **Step 3: Implement.** Keep the existing single-program output byte-compatible for a migrated version 1 install (same file paths and keys), so its running region sees no config change.

- [ ] **Step 4: Run the whole unit suite** and confirm it passes.

- [ ] **Step 5: Commit** — `Launcher: write config, check ports and read logs per program`.

### Task 4: Status model

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/GridStatus.cs`, `Grid/IGridController.cs`
- Modify: `Launcher/DreamGrid.Launcher.Tests/FakeGridController.cs` and every test that builds a `GridStatus`
- Test: `Launcher/DreamGrid.Launcher.Tests/GridStatusTests.cs`

**Interfaces:**
- Produces (later tasks fill these in):
  - `public enum RegionState { Stopped, Starting, Running, Stopping, Failed, Parked }`
  - `public sealed record RegionStatus(Guid Id, string Name, string Instance, RegionState State, bool LoginsEnabled, int X, int Y, int Size, IReadOnlyList<string> RunsWith);` — `RunsWith` is the names of the other live regions in the same program.
  - `public sealed record ProgramStatus(string Instance, ComponentStatus Process, IReadOnlyList<Guid> Regions);`
  - `GridStatus(ComponentStatus Database, ComponentStatus Robust, IReadOnlyList<ProgramStatus> Programs, IReadOnlyList<RegionStatus> Regions, bool LoginsEnabled, string LoginUri, StartFailure? StartFailure = null, GridActivity Activity = GridActivity.None)` — `Region` is gone. `LoginsEnabled` is true when at least one region is loadable and every non-parked, non-held region accepts logins.
  - `public enum RegionPower { Start, Stop, Restart, Park, Unpark, UnparkAndStart }`
  - `public sealed record NewRegion(string Name, int X, int Y, int Size, string Content, string? ShareWith);` — `Content` is `"starter"` or `"island"` (4b/4c add region types); `ShareWith` is a program's `Instance`, or null for its own program.
  - New `IGridController` members:
    - `Task StartWorldAsync(bool everything, CancellationToken ct)`
    - `Task<RegionSettings> AddRegionAsync(NewRegion request, CancellationToken ct, IProgress<string>? progress = null)`
    - `Task RegionPowerAsync(Guid regionId, RegionPower action, CancellationToken ct)`
  - `StartComponentAsync`, `StopComponentAsync` and `RestartComponentAsync` accept only `"database"` and `"robust"` now; `"region"` throws the existing `ArgumentException`.
  - `GridStatus.Region` consumers (`StatusApi`, `Pages/Index`, `LegacyCommandApi`) compile against `Regions` in this task with the first region; Tasks 8 and 9 give them the real multi-region behaviour.

- [ ] **Step 1: Write the failing test** `Logins_enabled_needs_every_runnable_region` on the pure helper `GridStatus.ComputeLoginsEnabled(IReadOnlyList<RegionStatus> regions, IReadOnlySet<Guid> held)` (only regions that are neither parked nor held count; true when at least one counts and every counted one is Running with logins enabled):
  - `[]` → false;
  - Running+enabled, Parked → true;
  - Running+enabled, Stopped and held → true;
  - Running+enabled, Stopped and not held → false;
  - Running+enabled, Starting → false.
- [ ] **Step 2: Run and confirm it fails.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*GridStatusTests"`.
- [ ] **Step 3: Implement** the records, the helper, the interface members (`GridController` throws `NotImplementedException` for the three new members until Tasks 5–7; the fake records calls), and the compile fixes.
- [ ] **Step 4: Run the whole unit suite** and confirm it passes.
- [ ] **Step 5: Commit** — `Launcher: status and controller surface for many regions`.

### Task 5: Supervising programs

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/GridController.cs`, `Grid/GridConsoleCommands.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/GridConsoleCommandsTests.cs`, `Launcher/DreamGrid.Launcher.Tests/RegionAlertRoutingTests.cs`, integration `Launcher/DreamGrid.Launcher.IntegrationTests/GridBootTests.cs`

**Interfaces:**
- Consumes: Tasks 1–4.
- Produces:
  - One `Component` per program, keyed `program-<Instance>`, its spec built like today's `MakeRegion` from `ProgramSettings` (ports `[HttpPort, ConsolePort, …region UDP ports]`, key including its loadable region set so a changed set rebuilds a stopped program).
  - `StartAsync(ct)` (boot and adoption, unchanged meaning): MariaDB → Robust → every program with loadable regions, one after another; a program that fails is recorded and the next still starts; adopted programs are never restarted.
  - `GridConsoleCommands.LoadedRegionsAsync(RestConsoleClient console, TimeSpan settle, CancellationToken ct) → Task<IReadOnlyList<string>>`: region names from the console's `show regions` output.
  - Per-region login state from `OnRegionAlert`, matched by `RegionId`, else by `RegionName` (ignoring case); an alert matching no region is logged at Debug and ignored. An adopted program's regions get their state from `LoadedRegionsAsync` plus the per-region `login status` answer.
  - `GetStatus()` fills `Programs` and `Regions` with the state rules: program not running → its process state mapped (`Starting`, `Stopping`, `Failed`; `Crashed` → `Failed`; otherwise `Stopped`); parked → `Parked`; running program but region held or not loaded → `Stopped`; loaded with logins enabled → `Running`; loaded without → `Starting`.
  - `StopAllAsync`: every program (reverse of start order), then Robust, then MariaDB.

- [ ] **Step 1: Write the failing tests**
  - `Show_regions_output_is_parsed`: the console text from OpenSim's `show regions` (copy a real sample into the test) → the region names.
  - `Alert_for_unknown_region_is_ignored` and `Alert_matches_by_id_then_name` (route through a small `RegionLogins` class extracted from the controller: `bool Apply(RegionAlert a, IReadOnlyList<RegionSettings> regions)`, `bool Enabled(Guid id)`).
  - `Adopted_program_reports_unloaded_region_as_stopped` (unit, on the state-derivation helper `GetStatus` uses: `public static class RegionStates { public static RegionState Derive(RegionSettings r, ComponentStatus? program, bool held, IReadOnlyCollection<string>? loaded, bool loginsEnabled); }`, where `loaded` is null when unknown and a running program then counts the region as loaded).
  - Integration `Two_programs_boot_and_stop` in `GridBootTests`: seed settings with two programs and one region each (no content is loaded; OpenSim's default terrain is fine), call `StartAsync`; both programs and both regions reach Running; `StopAllAsync` leaves no `OpenSim.dll` process.
- [ ] **Step 2: Run and confirm they fail.** Unit: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*GridConsoleCommandsTests" --filter-class "*RegionAlertRoutingTests"`. Integration (needs `DREAMGRID_MARIADB_BIN` and a built runtime, as in CI): `dotnet test --project Launcher/DreamGrid.Launcher.IntegrationTests --filter-class "*GridBootTests"`.
- [ ] **Step 3: Implement.** The existing single-region paths (`StartRegionAsync`, `WaitForLoginsAsync`, `RestartComponentAsync("database")`) become per-program and per-region.
- [ ] **Step 4: Run the unit suite and the integration tests** and confirm they pass (the existing `Fresh_grid_boots_owner_and_region` and `Launcher_adopts_a_running_grid_twice` included).
- [ ] **Step 5: Commit** — `Launcher: supervise one OpenSim program per region group`.

### Task 6: Adding a region

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/GridController.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/AddRegionValidationTests.cs`, integration `GridBootTests.cs`

**Interfaces:**
- Consumes: Tasks 1–5.
- Produces:
  - `AddRegionAsync(NewRegion r, …)`:
    1. Validates before anything changes, throwing `ArgumentException` with these messages (the website shows them as they are):
       - an invalid name → the existing `IsValidRegionName` message;
       - a name in use → `There is already a region called {name}.`;
       - a size outside 1–16 → `A region is 1 to 16 squares on each side.`;
       - an overlap → `That spot overlaps {other}.`;
       - `ShareWith` not a program → `There is no region group {instance}.`;
       - unknown content → the existing content message.
    2. Allocates ports (Task 2 rules), saves the program and region through `UpdateSettings`, writes the program's log config.
    3. Starts it: on its own, the new program starts; shared, the program restarts (it is stopped and started with the new region file).
    4. Waits for that region's logins, loads its content after `change region <name>` on the program's console (`island` terrain or the starter OAR, as today), then waits for logins again. Progress stages are the existing three (`SetupProgress.CreatingRegion`, `LoadingContent`, `WaitingForLogins`).
    5. Returns the saved `RegionSettings`.
  - `CreateRegionAsync(name, content, …)` (setup) keeps its signature and stages. While `Setup.RegionCreated` is false it removes the regions and programs an earlier attempt left (stopping their program), keeping the UUID when the name is the same, then calls `AddRegionAsync(new NewRegion(name, 1000, 1000, 1, content, null), …)` and sets `RegionCreated`.

- [ ] **Step 1: Write the failing tests**
  - Unit (validation runs before any process work, so a controller with a fake `SettingsStore` directory suffices): `Duplicate_name_ignoring_case_is_refused`, `Overlapping_spot_is_refused`, `Size_17_is_refused`, `Unknown_share_target_is_refused`. Each asserts the exact message and that the settings file is unchanged.
  - Integration `Second_region_shared_and_third_on_its_own`: boot, create the owner, `CreateRegionAsync("Welcome","island")`, then `AddRegionAsync(new("Beach", 1001, 1000, 1, "island", "Welcome"))` and `AddRegionAsync(new("Isle", 1010, 1010, 1, "island", null))`; `GetStatus()` shows three Running regions, two programs, `Beach.RunsWith == ["Welcome"]`; `LoginsEnabled` is true.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the unit suite and `GridBootTests`** and confirm they pass.
- [ ] **Step 5: Commit** — `Launcher: add regions, on their own or sharing a program`.

### Task 7: Power actions and parking

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/GridController.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/RegionPowerPlanTests.cs`, integration `GridBootTests.cs`

**Interfaces:**
- Consumes: Tasks 1–6.
- Produces:
  - A pure planner the controller follows, so the rules are unit-tested: `public static class RegionPowerPlan { public static PowerSteps For(LauncherSettings s, IReadOnlySet<Guid> held, IReadOnlySet<string> runningPrograms, Guid region, RegionPower action); }` with `public sealed record PowerSteps(LauncherSettings Settings, IReadOnlySet<Guid> Held, IReadOnlyList<string> Restart, IReadOnlyList<string> Stop, IReadOnlyList<string> Start, IReadOnlyList<string> Affected);` — `Affected` is the names of other regions that go offline.
  - Rules (spec §6.8):
    - **Start:** remove the region from held; its program starts if stopped, else restarts (to load it).
    - **Stop:** add it to held; its program restarts without it, or stops when no loadable region is left.
    - **Restart:** the program restarts.
    - **Park:** set `Parked`, remove it from held; the program restarts without it, or stops when none is left.
    - **Unpark:** clear `Parked`; nothing starts.
    - **UnparkAndStart:** Unpark, then Start.
    - Parking or starting a deleted region throws `InvalidOperationException("That region has been deleted.")`.
  - `RegionPowerAsync` applies the steps under the operation lock, saves `Parked` through `UpdateSettings`, logs `"{action} {region}: also affected {names}"` when `Affected` isn't empty, and rewrites the config with the held set.
  - `StartWorldAsync(everything)`: clears held; with `everything`, clears `Parked` on every region; then `StartAsync` and restarts each running program whose loaded regions (from `LoadedRegionsAsync`) miss a loadable region.
  - World Restart (dashboard): `StopAllAsync` then `StartAsync`, keeping held and parked regions off.

- [ ] **Step 1: Write the failing tests**
  - `Stop_in_shared_program_restarts_it_without_the_region` (`Affected == ["Home"]`).
  - `Stopping_last_region_stops_its_program`.
  - `Park_is_saved_and_unpark_starts_nothing`.
  - `World_start_restarts_program_missing_a_region` (planner input: Beach not in the loaded list).
  - `Start_everything_unparks`.
  - `Deleted_region_cannot_be_started`.
  - Integration `Park_and_unpark_a_shared_region`: Welcome and Beach share; Park Beach → Beach `Parked`, Welcome Running again; a new controller on the same data dir (launcher restart) keeps Beach parked; `StartWorldAsync(everything: true)` → both Running.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the unit suite and `GridBootTests`.**
- [ ] **Step 5: Commit** — `Launcher: region power actions and parking`.

### Task 8: Status API and the dashboard's power menu

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Endpoints/StatusApi.cs`, `Endpoints/GridActions.cs`
- Modify: `Launcher/DreamGrid.Launcher/Pages/Index.cshtml`, `Pages/Index.cshtml.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/DashboardTests.cs`, `StatusApiTests.cs`

**Interfaces:**
- Consumes: Tasks 4–7 (`GridStatus`, `StartWorldAsync`, `RegionPowerAsync`).
- Produces:
  - `/api/status` → `grid.regions: [{ name, state, stateText, runsWith, loginsEnabled }]` (anonymous callers get name and state only) in place of `grid.region`; `grid.loginsEnabled` unchanged.
  - `Problem(GridStatus)`: a failed region gives `"{name} isn't running."`; the `"Your region isn't running."` text goes.
  - `GridActions` actions (POST `/api/grid/{action}` and the dashboard form): `start` → `StartWorldAsync(false)`, `start-everything` → `StartWorldAsync(true)`, `restart`, `stop`, `restart-database`, `restart-robust`. Notices: `"Starting your world."`, `"Starting everything, parked regions too."`, and the existing ones. `restart-region` goes.
  - Who's online shows each person's region: `"{name} in {region}"`, matching `OnlineAvatar.RegionId` to a region's UUID (no match: the name alone).
  - Dashboard: one row per region (name, state badge, "runs with …" in muted text) after the database and grid service rows; the power menu replaces the Start all / Stop all / Restart buttons. It's a `<details>` element styled as a Bootstrap dropdown, so it works without JavaScript; each item is a submit button with a one-line explanation (copy from spec §6.8). The live update (`render` in `Index.cshtml`) renders region rows from `grid.regions`.
- [ ] **Step 1: Write the failing tests** — `Online_list_names_each_persons_region`, `Dashboard_lists_each_region_with_its_state`, `Power_menu_offers_start_everything`, `Start_everything_calls_StartWorldAsync_true`, `Status_api_lists_regions_without_details_for_anonymous`, `Failed_region_problem_names_it`.
- [ ] **Step 2: Run and confirm they fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*DashboardTests" --filter-class "*StatusApiTests"`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the whole unit suite.**
- [ ] **Step 5: Commit** — `Website: list every region and add the world power menu`.

### Task 9: Port-8001 API, Advanced → Network and setup's port fix

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Api/LegacyCommandApi.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/SettingsValidator.cs`
- Modify: `Launcher/DreamGrid.Launcher/Pages/Advanced/Network.cshtml(.cs)`, `Pages/Setup/Index.cshtml.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/LegacyCommandApiTests.cs`, `AdvancedSettingsTests.cs`, `SetupFlowTests.cs`

**Interfaces:**
- Consumes: Tasks 2, 4, 7.
- Produces:
  - Legacy API:
    - `regionlist` lists every live region.
    - `RegionStatus` answers for the region named by `RegionName` or `RegionUuid`, required when there's more than one region (`NAK Name the region.`).
    - `StartRegion`, `StopRegion` and `RestartRegion` call `RegionPowerAsync` with `Start`, `Stop` and `Restart`.
    - An unknown region → `NAK Unknown region {name}` (unchanged).
  - `SettingsValidator.Network(...)` gains a final `IReadOnlySet<int> assigned` parameter (the program and region ports from `PortAllocator.Taken` minus the eight configured ones); a configured port that equals an assigned one → `"{label} is used by region {name}."` (the caller maps port → region name).
  - Network page: the labels become "First region port" and "First region console port", with the hint "New regions take the next free ports from here. Regions you already have keep theirs." Below the form, a read-only table: region, runs with, UDP port, web port, console port.
  - Setup port fix (`/setup?handler=Port`): when the taken port is a program or region assignment, `PortAllocator.Replace` moves it; otherwise the existing setting-key mapping applies.
- [ ] **Step 1: Write the failing tests** — `Regionlist_lists_all_regions`, `Region_status_needs_a_name_when_there_are_several`, `Stop_region_is_a_stop_not_a_park` (fake records `RegionPower.Stop`), `Network_refuses_a_port_a_region_uses`, `Network_page_lists_region_ports`, `Setup_port_fix_moves_a_region_port`.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the whole unit suite.**
- [ ] **Step 5: Commit** — `Launcher: port-8001 API, Network page and setup port fix for many regions`.

### Task 10: End to end — upgrade, adoption and a login to the second region

**Files:**
- Create: `Launcher/DreamGrid.Launcher.IntegrationTests/XmlRpcLogin.cs`
- Modify: `Launcher/DreamGrid.Launcher.IntegrationTests/LauncherEndToEndTests.cs`
- Modify: `docs/superpowers/specs/2026-10-24-crossplatform-launcher-v1-design.md` ("Recorded during plan 4a" notes under §6.8: schema 2, programs and regions, port allocation, `.ini.off`, region states)

**Interfaces:**
- Produces: `internal static class XmlRpcLogin { public static Task<LoginResult> LoginAsync(HttpClient http, string loginUri, string first, string last, string password, string start, CancellationToken ct); }`, `internal sealed record LoginResult(bool Ok, int? SimPort, string Message);` — POSTs `login_to_simulator` (`first`, `last`, `passwd` = `$1$` + MD5 hex of the password, `start` = `uri:<Region>&128&128&25`, `channel`, `version`, `platform`, `mac`, `id0`) and reads `login`, `sim_port` and `message` from the response struct.
- [ ] **Step 1: Write the failing tests**
  - `Login_to_second_region_returns_its_port`: a launcher host boots, setup creates the owner and Welcome, `AddRegionAsync` adds Beach sharing Welcome's program; `XmlRpcLogin.LoginAsync(… start: "uri:Beach&128&128&25")` → `Ok` and `SimPort == Beach.UdpPort`. Then log out (the session expires; no UDP is needed).
  - `Upgraded_v1_grid_is_adopted_unchanged`: boot a version 1-shaped settings file (write the schema 1 JSON before the first start), let the region reach logins, record its PID, stop only the launcher host, start a new host on the same data dir: the region PID is the same, settings are schema 2, ports 8004/8011 and the `Welcome` folder are unchanged.
  - `Adopts_two_programs_after_launcher_restart`.
- [ ] **Step 2: Run and confirm they fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.IntegrationTests --filter-class "*LauncherEndToEndTests"`.
- [ ] **Step 3: Implement** the helper and the spec notes.
- [ ] **Step 4: Run every suite** — unit, integration (`dotnet test --project Launcher/DreamGrid.Launcher.IntegrationTests`), then push and confirm CI is green, including the Debian and Windows install smokes (their upgrade test is the real-world version 1 → 4a path).
- [ ] **Step 5: Commit** — `Launcher: end-to-end tests for many regions; record plan 4a in the spec`.
