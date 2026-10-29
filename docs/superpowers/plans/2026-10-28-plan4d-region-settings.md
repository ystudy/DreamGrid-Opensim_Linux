# Plan 4d — Region settings tabs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each region's info panel gets a Settings page with tabs (Basics, Options, Map, Physics, Scripts, Permissions, Access, CPU). The admin can rename and resize a region, set its limits, how its map tile is drawn, its physics and scripts, god permissions, who may enter, and the CPU its program uses. Every change says when it applies and offers "Restart now", naming each region that restarts.

**Architecture:**
- The Core gains:
  - `RegionOptions` on each region, and `ProgramOptions` plus a nullable `Physics` on each program. Grid defaults for physics and scripts go on `LauncherSettings`. Schema 3 migrates the schema 2 physics value.
  - A `RegionSettingsRules` class that validates each tab and lists the program settings that differ between two programs.
  - `GridConfigWriter` writes the new keys into region files and program `OpenSim.ini` files.
  - `CpuTuning`, which sets cores and priority on a running program (Windows: process-wide; Linux: every thread in `/proc/<pid>/task`).
  - On `GridController`: rename, resize, save-options, CPU applied live, and a per-program "settings changed since it started" flag.
- The host gains:
  - `/regions/region/{id}/settings/{tab}`: one page with tabs as links (no JavaScript). Each tab posts its own handler.
  - The panel's pending-restart notice with "Restart now".
  - An Advanced → Regions page for the grid defaults.
  - The "How it runs" page naming every program setting that will change.

**Tech Stack:** C# / .NET 10, ASP.NET Core Razor Pages, Bootstrap 5.3 (bundled), xUnit v3 (Microsoft Testing Platform), OpenSim INIs through `IniDocument`, libc P/Invoke (`sched_setaffinity`, `setpriority`) on Linux.

**Spec:** `docs/superpowers/specs/2026-10-27-regions-design.md`: §5.4 (settings tabs, "Restart now"), §6.1 (program and region settings), §6.7 (all of it), and the "Share with" warnings in §6.7.

## Where this plan sits

Plans 4a, 4b and 4c are merged into V7 (4c is PR #9, 3330d26). The panel still says "Region settings are coming next."; this plan replaces that line. `ProgramSettings.Physics` exists and is written to `[Startup] physics`. "How it runs" already warns when physics changes (`RunsModel.PhysicsNote`). After this plan, regions step 3 (the status dashboard with graphs) is the next design.

**What OpenSim reads where** (checked against the sources while planning, as the spec asks). A region file can't override program settings: `RegionLoaderFileSystem` (`OpenSim/ApplicationPlugins/LoadRegions/RegionLoaderFileSystem.cs:91-93`) treats every section of a region file as a region.
- **Per region, in the region file** (`RegionInfo.ReadNiniConfig`, `OpenSim/Framework/RegionInfo.cs`):
  - `MaxAgents` (:668)
  - `MaxPrims` (:647; the viewer shows it, OpenSim doesn't enforce it)
  - `NonPhysicalPrimMax` (:635; an int, used when > 0; `Scene.cs:968-974` caps it at 65536)
  - `PhysicalPrimMax` (:641; capped at 2048, `Scene.cs:984-991`)
  - `ClampPrimSize` (:644; OR-ed with the program's value, so a region can only turn it on)
- **Per region, keyed by name in the program's `OpenSim.ini`:**
  - `[AuthorizationService] Region_<Name with spaces as _> = DisallowResidents` (`AuthorizationService.cs:72`). `LocalAuthorizationServicesConnector` is already on in `Grid.ini`.
  - `[Concierge] enabled` and `regions`, a case-insensitive regex on the region name (`ConciergeModule.cs:80-134`).
- **Per program**, in `OpenSim.ini`:
  - `[Startup] physics` / `meshing` and `[BulletSim] UseSeparatePhysicsThread`
  - `[YEngine] Enabled` and `MinTimerInterval`
  - `[Startup] FrameTime`
  - `[Map] GenerateMaptiles`, `MapImageModule`, `TextureOnMapTile`, `DrawPrimOnMapTile`, `TexturePrims`, `RenderMeshes`
  - `[Permissions] allow_grid_gods`, `region_owner_is_god`, `region_manager_is_god`
- **CPU** is not in OpenSim at all; it is set by the operating system.

Rulings this plan makes (none is in the spec; each is **my call**):
- **Ruling D (Hybrid physics):** the spec lists "Hybrid". This OpenSim has no engine of that name, only `ubODE`, `BulletSim`, `basicphysics` and `POS`. The Physics tab offers grid default, ubODE, BulletSim, and "BulletSim in its own thread". *Cost if wrong:* one missing choice that would have meant ubODE with the BulletSim mesher.
- **Ruling E (when changes apply):** saving a tab never restarts anything. A change applies at the program's next restart, and the panel offers "Restart now" (spec §5.4). The exception is CPU, which the operating system applies at once to a running program. *Cost if wrong:* one extra click compared with an automatic restart.
- **Ruling F (grid default physics on upgrade):** schema 3 reads a schema 2 program's `"ubODE"` as "grid default". Nobody could choose physics before, and the grid default starts as ubODE, so nothing changes on upgrade. *Cost if wrong:* none in behaviour.
- **Ruling G (Linux priorities):** Linux has nice values, not priority classes. The mapping is Below normal = 10, Normal = 0, Above normal = −5, High = −10, Real time = −20. All three above Normal need root. When the launcher isn't root they are shown as unavailable: "On Linux only the root user can raise a program's priority." Real time on Linux is nice −20, not a real-time scheduler, and the tab says so. *Cost if wrong:* a Linux admin with `CAP_SYS_NICE` but not root sees those choices greyed out.
- **Ruling H (map has no grid default):** the spec adds grid defaults only for physics and scripts. Map defaults to "Simple", which is what the prototype `[Map]` section already does.

## Global Constraints

- .NET 10; Linux (Debian 12/13) and Windows; no changes under `OpenSim/` (spec §3).
- Every settings change goes through `IGridController.UpdateSettings`; INIs are generated, never hand-edited.
- The website loads nothing from the internet (`LayoutTests`), and every settings page works without JavaScript.
- The bare `<footer>DreamGrid <version></footer>` stays.
- The website never says "program" or "instance". A program setting says "<Home and Beach> share this setting because they run together." (spec §6.7, with §4's wording rule).
- Only an admin reaches these pages; APIs answer 401, never a redirect.
- Anything that restarts other regions names them and asks first (spec §2). "Restart now" uses the panel's existing Restart confirmation.
- While a program runs an OAR job, rename, resize and "Restart now" for its regions are refused with the 4c busy text: "<Job text> is still running. Try again when it finishes." Saving a tab is allowed: it only changes what the next start uses.
- "Saved. It applies when <regions> restart." is the notice after every save except CPU. CPU says "Saved and applied." when its program runs, and "Saved. It applies when <regions> start." otherwise.
- "High" and "Real time" priority carry: "This can make the whole computer slow to respond while these regions are busy." (spec §6.7).

## Review Focus

1. **A region name with spaces or punctuation in Access settings:** "My Place" must give `Region_My_Place`. A name such as `Joe's (Beach)` must reach the Concierge regex escaped, so it doesn't match other regions or break the regex. Pinned by `Access_keys_use_underscored_names` and `Concierge_regex_escapes_names` (Task 2).
2. **Rename edge cases:** a case-only rename (`home` → `Home`) is allowed. A name that a deleted region still holds is refused with the add form's wording. A rename while the region is loaded is refused. Pinned by `Rename_case_only_is_allowed`, `Rename_to_a_deleted_regions_name_is_refused` and `Rename_refused_while_loaded` (Task 4).
3. **Shrinking a region that has content:** the page warns first and offers to save it as an OAR. When that save fails, the size stays as it was. Pinned by `Shrink_save_first_failure_keeps_size` (Task 6).
4. **The upgrade from schema 2:** a running program is adopted and shows no pending restart, its INIs come out the same, and physics reads as grid default. Pinned by `Schema2_physics_ubODE_becomes_grid_default` (Task 1) and `Adopted_program_has_no_pending_restart` (Task 4).
5. **CPU settings copied from another computer:** cores beyond this computer's count are dropped. When none are left the program uses every core, and nothing throws at start. Pinned by `Cores_beyond_this_computer_are_ignored` (Task 3).

---

### Task 1: The settings model, schema 3 and the rules

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/SettingsStore.cs`: the 2→3 migration
- Create: `Launcher/DreamGrid.Launcher.Core/Settings/RegionSettingsRules.cs`
- Test: `SettingsStoreTests.cs`, `RegionSettingsRulesTests.cs` (new)

**Interfaces:**
- Produces:
  - `public sealed record RegionOptions(int? MaxAgents = null, int? MaxPrims = null, int? MaxNonPhysicalSize = null, int? MaxPhysicalSize = null, bool ClampSize = false, bool KeepResidentsOut = false, bool AnnounceVisitors = false);`
    - Null means OpenSim's own default, and the key is not written.
  - `RegionSettings` gains a trailing `RegionOptions? Options = null`, read as `new RegionOptions()`.
  - `public enum MapQuality { None, Simple, Good, Better, Best }`
  - `public enum CpuPriority { BelowNormal, Normal, AboveNormal, High, RealTime }`
  - `public sealed record ProgramOptions(bool? Scripts = null, double? MinTimerInterval = null, int? FramesPerSecond = null, MapQuality Map = MapQuality.Simple, bool AllowGods = true, bool OwnerIsGod = true, bool ManagerIsGod = false, IReadOnlyList<int>? Cores = null, CpuPriority Priority = CpuPriority.Normal);`
    - `Scripts`: null means grid default.
    - `Cores`: null or empty means every core.
  - `ProgramSettings` becomes `(string Instance, int HttpPort, int ConsolePort, string? Physics = null, ProgramOptions? Options = null)`.
    - `Physics` is null (grid default), `"ubODE"`, `"BulletSim"` or `"BulletSimThreaded"`.
  - `LauncherSettings` gains:
    - `public string DefaultPhysics { get; init; } = "ubODE";`: `"ubODE"`, `"BulletSim"` or `"BulletSimThreaded"`.
    - `public bool DefaultScripts { get; init; } = true;`
    - `CurrentSchema = 3`.
  - `SettingsStore` migrates 2 → 3: a program's `Physics == "ubODE"` becomes null (Ruling F). It still migrates 1 → 2 → 3 in one load.
  - `public static class RegionSettingsRules`:
    - `ValidationResult Options(RegionOptions o)`: error keys are the property names. The ranges are:
      - MaxAgents 1–500
      - MaxPrims 1–1,000,000
      - MaxNonPhysicalSize 1–65536
      - MaxPhysicalSize 1–2048

      The messages say "Use a number from <a> to <b>, or leave it empty for OpenSim's default."
    - `ValidationResult Program(string? physics, ProgramOptions o, int processorCount)`, with these limits:
      - physics: one of the three values, or null.
      - MinTimerInterval 0.05–10 s.
      - FramesPerSecond 5–55.
      - Cores: each from 0 to `processorCount - 1`, with no duplicates.
    - `string EffectivePhysics(LauncherSettings s, ProgramSettings p)` and `bool EffectiveScripts(LauncherSettings s, ProgramSettings p)`.
    - `IReadOnlyList<string> Differences(LauncherSettings s, ProgramSettings from, ProgramSettings to)`: one plain line per program setting whose effective value differs, in tab order. Lines:
      - "Physics will change from ubODE to BulletSim."
      - "Scripts will be turned off." / "Scripts will be turned on."
      - "The script timer rate will change from 0.5 to 0.2 seconds."
      - "The frame rate will change from 11 to 20 per second."
      - "The map will be drawn Better instead of Simple."
      - "God permissions will change."
      - "It will use cores 0–3 instead of every core."
      - "Its priority will change from Normal to High."
    - `IReadOnlyList<int> UsableCores(IReadOnlyList<int>? cores, int processorCount)`: cores within range, in order. Empty means every core.
    - `string? PriorityUnavailable(CpuPriority p)`: Ruling G's text on Linux when the launcher isn't root (`Environment.IsPrivilegedProcess`) and `p` is above Normal; null otherwise.
- Consumes: `ValidationResult` (`DreamGrid.Launcher.Core.Modules`); `GridController.IsValidRegionName`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Schema2_physics_ubODE_becomes_grid_default() { /* schema 2 JSON with programs[0].physics "ubODE" and [1] "BulletSim" → Physics null and "BulletSim"; SchemaVersion 3 */ }
[Fact] public void Schema1_file_migrates_through_to_3() { }
[Fact] public void Missing_options_read_as_defaults() { /* region/program JSON without "options" → Options null; Rules treat as new() */ }
[Fact] public void Options_out_of_range_name_the_field() { /* MaxAgents 0 → "Use a number from 1 to 500, or leave it empty for OpenSim's default." */ }
[Fact] public void Program_rejects_unknown_physics_and_bad_cores() { /* "Hybrid"; Cores [0,0]; Cores [8] with 8 processors */ }
[Fact] public void Differences_lists_each_changed_setting_in_tab_order() { }
[Fact] public void Differences_compare_effective_values() { /* null physics vs "ubODE" with DefaultPhysics ubODE → no line */ }
[Fact] public void Cores_beyond_this_computer_are_ignored() { /* UsableCores([2,9,15], 8) == [2]; ([9], 8) == [] */ }
[Fact] public void High_priority_unavailable_on_linux_without_root() { /* Assert.SkipUnless(OperatingSystem.IsLinux() && !Environment.IsPrivilegedProcess, …) */ }
```

- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*SettingsStoreTests" --filter-class "*RegionSettingsRulesTests"`. Expected: build errors (`RegionOptions` not defined).

- [ ] **Step 3: Implement the interfaces above.** Every caller that builds `ProgramSettings` with a physics string keeps compiling. `ChangeRunsWithAsync`'s "with the old program's physics" becomes "with the old program's `Physics` and `Options`".

- [ ] **Step 4: Run the tests and see them pass**, then the whole unit suite: `dotnet test --project Launcher/DreamGrid.Launcher.Tests`. Expected: all pass.

- [ ] **Step 5: Commit:** `Launcher: region and program options, grid defaults and schema 3`

### Task 2: Writing the options into the INIs

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Config/GridConfigWriter.cs` (`WriteProgram`)
- Test: `GridConfigWriterTests.cs`

**Interfaces:**
- Consumes: Task 1's records and `RegionSettingsRules.EffectivePhysics` / `EffectiveScripts`.
- Produces: no new API. The INIs carry the keys below.

What is written:
- **Region file**, per loadable region. Each key is written only when set:
  - `MaxAgents`, `MaxPrims`
  - `NonPhysicalPrimMax` (from MaxNonPhysicalSize), `PhysicalPrimMax` (from MaxPhysicalSize)
  - `ClampPrimSize = True`, when ClampSize is on
- **Program `OpenSim.ini`:**
  - Physics:

    | Effective physics | `[Startup] physics` | `[Startup] meshing` | `[BulletSim] UseSeparatePhysicsThread` |
    |---|---|---|---|
    | ubODE | `ubODE` | `ubODEMeshmerizer` | (not written) |
    | BulletSim | `BulletSim` | `Meshmerizer` | `false` |
    | BulletSimThreaded | `BulletSim` | `Meshmerizer` | `true` |

  - Scripts: `[YEngine] Enabled` is `true` or `false`, from EffectiveScripts.
  - `[YEngine] MinTimerInterval`, when set.
  - `[Startup] FrameTime = 1/FramesPerSecond`, when set, written with the invariant culture and 4 decimals.
  - `[Map]`. Every key in the list is written each time, so no prototype value leaks through. Any key a level doesn't name is `false`.
    - None: `GenerateMaptiles = false`.
    - Simple: `GenerateMaptiles = true`, `MapImageModule = "MapImageModule"`.
    - Good: `GenerateMaptiles = true`, `MapImageModule = "Warp3DImageModule"`.
    - Better: Good, plus `TextureOnMapTile = true` and `DrawPrimOnMapTile = true`.
    - Best: Better, plus `TexturePrims = true` and `RenderMeshes = true`.
  - `[Permissions] allow_grid_gods`, `region_owner_is_god`, `region_manager_is_god`.
  - `[AuthorizationService] Region_<name with ' ' → '_'> = DisallowResidents` for each loadable region with KeepResidentsOut. A region without it gets no key.
  - `[Concierge]`. When any loadable region has AnnounceVisitors: `enabled = true` and `regions = "^(?:<Regex.Escape(name)>|…)$"`. Otherwise: `enabled = false`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Region_file_gets_only_the_options_that_are_set() { }
[Fact] public void Physics_rows_match_the_table() { /* the three effective values, and null with DefaultPhysics BulletSimThreaded */ }
[Fact] public void Scripts_off_disables_yengine() { }
[Fact] public void Frame_rate_writes_frame_time() { /* 20 → FrameTime = 0.0500 */ }
[Theory, InlineData(MapQuality.None), InlineData(MapQuality.Simple), InlineData(MapQuality.Good), InlineData(MapQuality.Better), InlineData(MapQuality.Best)]
public void Map_levels_write_every_map_key(MapQuality q) { }
[Fact] public void Permissions_are_written() { }
[Fact] public void Access_keys_use_underscored_names() { /* "My Place" → [AuthorizationService] Region_My_Place = DisallowResidents */ }
[Fact] public void Concierge_regex_escapes_names() { /* "Joe's (Beach)" and "Home" → regions = "^(?:Joe's\ \(Beach\)|Home)$"; Regex matches both names, not "Homestead" */ }
[Fact] public void Programs_get_only_their_own_regions_access_keys() { }
[Fact] public void Default_options_write_the_same_files_as_before() { /* a schema 3 settings with no options: region files unchanged from today's; OpenSim.ini differs only by the [Map]/[Permissions]/[YEngine] Enabled/[Concierge] lines */ }
```

- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*GridConfigWriterTests"`. Expected: FAIL.

- [ ] **Step 3: Implement.** Region names allow letters, digits, spaces and the punctuation `IsValidRegionName` permits. Check that `IniDocument.Save` keeps a backslash in a quoted value as it is; Nini reads it literally.

- [ ] **Step 4: Run the tests and see them pass**, then the whole unit suite. Expected: all pass.

- [ ] **Step 5: Commit:** `Launcher: region limits, map, physics, scripts, permissions and access in the generated INIs`

### Task 3: CPU cores and priority

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Processes/CpuTuning.cs`
- Test: `CpuTuningTests.cs` (new)

**Interfaces:**
- Produces: `public static class CpuTuning`:
  - `void Apply(int pid, IReadOnlyList<int> cores, CpuPriority priority)`. `cores` comes from `RegionSettingsRules.UsableCores`; empty means every core.
    - **Windows:** `Process.ProcessorAffinity` (a bit mask; empty sets every core) and `Process.PriorityClass` (`Idle` is never used; the enum names map one to one, RealTime → `ProcessPriorityClass.RealTime`).
    - **Linux:** every thread id in `/proc/<pid>/task` gets `sched_setaffinity(tid, …)` and `setpriority(PRIO_PROCESS, tid, nice)`, with Ruling G's nice values. This is libc P/Invoke in a `LinuxCpu` nested class. New threads inherit from the thread that creates them, so applying to every existing thread covers the process.
    - It throws `InvalidOperationException` with plain words on failure: "This computer refused the priority: <errno text>." A thread that ends in the middle is skipped.
  - `(IReadOnlyList<int> Cores, CpuPriority Priority)? Read(int pid)`: what the process has now; on Linux, read from the main thread. Used by tests and the integration test.
- Consumes: Task 1's `CpuPriority`.

- [ ] **Step 1: Write the failing tests.** Each test starts a long-running child (`ping -n 60 127.0.0.1` on Windows, `sleep 60` on Linux) and kills it at the end.

```csharp
[Fact] public void Applies_cores_to_a_running_process() { /* cores [0] → Read(pid).Cores == [0] (skip when ProcessorCount == 1) */ }
[Fact] public void Empty_cores_means_every_core() { }
[Fact] public void Below_normal_is_applied() { }
[Fact] public void Applies_to_every_thread_on_linux() { /* SkipUnless Linux: each /proc/<pid>/task/<tid>/stat nice field == 10 */ }
[Fact] public void A_process_that_exited_throws_plain_words() { }
```

- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*CpuTuningTests"`. Expected: build errors.

- [ ] **Step 3: Implement.**

- [ ] **Step 4: Run the tests and see them pass**, then the whole unit suite. Expected: all pass.

- [ ] **Step 5: Commit:** `Launcher: CPU cores and priority for a running program on Windows and Linux`

### Task 4: Settings changes in the controller

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/IGridController.cs`, `GridController.cs`
- Modify: `Launcher/DreamGrid.Launcher.Tests/FakeGridController.cs`
- Test: `GridControllerWiringTests.cs`

**Interfaces:**
- Produces, on `IGridController`:
  - `Task RenameRegionAsync(Guid regionId, string name, CancellationToken ct);`
    - The region must not be loaded by a running program: "Stop <Name> first; a region is renamed while it is offline."
    - The name follows `RegionRequestError`'s name rules and wording: valid characters, and not taken by another live or deleted region (case-insensitive).
    - A case-only change of its own name is allowed.
    - The busy check uses `ThrowIfBusy`.
    - Only the region's `Name` is saved. The writer's keep logic turns the old region file into `.off`. The region registers under its new name at its next start.
  - `Task ResizeRegionAsync(Guid regionId, int size, CancellationToken ct);`
    - The size must be 1..`RegionPlacement.MaxSize`.
    - The new area must be free: `RegionPlacement.Check(s, r.X, r.Y, size, ignore: r.Uuid)`, refused with `RegionAdvice.Placement(...)` as `ArgumentException`.
    - The busy check applies. It saves `Size`; nothing restarts (Ruling E).
  - `Task SaveRegionOptionsAsync(Guid regionId, RegionOptions options, CancellationToken ct);`: it validates (`ArgumentException` carrying the first error) and saves.
  - `Task<bool> SaveProgramOptionsAsync(string instance, string? physics, ProgramOptions options, CancellationToken ct);`
    - It validates with `Environment.ProcessorCount` and saves.
    - When `Cores` or `Priority` changed and the program runs, it calls `CpuTuning.Apply` at once and returns true ("applied now"); otherwise it returns false.
  - `bool NeedsRestart(string instance);`: true while a running program was started from different settings than the current ones.
- Behaviour:
  - Each method above holds `_ops` only to check and save, like `MoveRegionAsync`. They save through `UpdateSettings`, so the INIs are rewritten.
  - **Pending restart:** `string StartKey(LauncherSettings s, ProgramSettings p)` covers everything a start reads from these settings:
    - effective physics and scripts, and `Options` minus Cores and Priority;
    - each loadable region's `Name`, `Size` and `Options`.

    `StartProgramAsync`, and adoption at launcher start, record it in `_startedWith[instance]`. `NeedsRestart` compares it with the current key. Adoption records the current key, so an upgrade or a launcher restart shows nothing pending.
  - **CPU at start:** when a program becomes `Running`, after a start, a restart or an adoption, the controller applies its CPU settings when they aren't the defaults. A failure is logged and doesn't stop the program.
- Consumes: Tasks 1–3.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Rename_saves_the_name_and_turns_the_old_file_off() { }
[Fact] public async Task Rename_refused_while_loaded() { /* "Stop Home first; a region is renamed while it is offline." */ }
[Fact] public async Task Rename_case_only_is_allowed() { }
[Fact] public async Task Rename_to_a_deleted_regions_name_is_refused() { /* the add form's taken-name text */ }
[Fact] public async Task Rename_refused_while_job_runs() { }
[Fact] public async Task Resize_refuses_a_taken_area() { /* RegionAdvice.Placement text */ }
[Fact] public async Task Resize_saves_and_restarts_nothing() { }
[Fact] public async Task Saving_options_marks_the_running_program_for_restart() { /* NeedsRestart false → SaveRegionOptionsAsync → true → RegionPowerAsync(Restart) → false */ }
[Fact] public async Task Cpu_change_applies_at_once_without_restart() { /* returns true; NeedsRestart stays false; the fake process saw Apply */ }
[Fact] public async Task Adopted_program_has_no_pending_restart() { }
[Fact] public async Task Grid_default_physics_change_marks_programs_that_use_it() { /* UpdateSettings DefaultPhysics BulletSim → NeedsRestart true for a null-physics program, false for an explicit ubODE one */ }
[Fact] public async Task Own_program_copies_physics_and_options() { /* ChangeRunsWithAsync(id, null) keeps the old program's Physics and Options */ }
```

`CpuTuning.Apply` goes through a `Func<int, IReadOnlyList<int>, CpuPriority, …>` the controller takes (default `CpuTuning.Apply`), so wiring tests can see the calls without real processes.

- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*GridControllerWiringTests"`. Expected: build errors (`RenameRegionAsync` not defined).

- [ ] **Step 3: Implement it as described.** `FakeGridController` gains the five members. It records requests, and `NeedsRestart` returns a settable set.

- [ ] **Step 4: Run the tests and see them pass**, then the whole unit suite. Expected: all pass.

- [ ] **Step 5: Commit:** `Launcher: rename, resize and save region settings; CPU applied live; pending restart per program`

### Task 5: The settings page and its tabs

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Regions/Settings.cshtml`, `Settings.cshtml.cs`
- Create: the partials `Pages/Regions/_SettingsOptions.cshtml`, `_SettingsMap.cshtml`, `_SettingsPhysics.cshtml`, `_SettingsScripts.cshtml`, `_SettingsPermissions.cshtml`, `_SettingsAccess.cshtml`, `_SettingsCpu.cshtml`
- Modify: `Pages/Regions/Region.cshtml`, `Region.cshtml.cs`
- Test: `RegionSettingsPageTests.cs` (new), `RegionPanelTests.cs`

**Interfaces:**
- Consumes: Task 4's controller methods, `RegionSettingsRules`, `RegionActions.SharingProgram`.
- Produces:
  - `@page "/regions/region/{id:guid}/settings/{tab?}"`, where `tab` is `basics` (the default), `options`, `map`, `physics`, `scripts`, `permissions`, `access` or `cpu`. Any other tab gives 404.
  - The tabs are a Bootstrap `nav nav-tabs` of plain links. Each tab is one form that posts `?handler=<tab>` with antiforgery. A wrong value comes back at its field, and nothing is saved.
  - On a program tab (map, physics, scripts, permissions, cpu), when others run with it, the tab shows "<Home and Beach> share this setting because they run together." above the form.
  - After a save, the page redirects back to the same tab with the notice from Global Constraints.
  - The panel: when `grid.NeedsRestart(instance)` is true and the region runs, it shows "Some settings apply when <regions> restart." with a "Restart now" button. The button posts the existing power `Restart`, which already asks about other regions. The line "Region settings are coming next." becomes a "Settings…" link.
  - Field details:
    - **Options:** number boxes; empty means OpenSim's default, shown as a placeholder (100 visitors; 1024 m and 128 m prim sizes; MaxPrims has no default and stays empty).
    - **Physics:** radio buttons. "Grid default (<DefaultPhysics label>)", "ubODE", "BulletSim", "BulletSim in its own thread".
    - **Scripts:** "Grid default (On/Off)", "On", "Off"; then the timer rate in seconds and the frame rate per second, empty for the default.
    - **Permissions:** three checkboxes.
    - **Access:** the "Keep residents out" checkbox, with the help text "Only you and the estate managers can enter." and the "Announce visitors in chat" checkbox.
    - **CPU:** one checkbox per core ("Core 0" … "Core N−1"; none ticked means every core) and the priority radios. Unavailable priorities are disabled, with the reason from `PriorityUnavailable`, and High and Real time carry the warning.

- [ ] **Step 1: Write the failing tests** (`LauncherFactory` with `TestAuth` and `FakeGridController`, like `RegionPanelTests`)

```csharp
[Fact] public async Task Each_tab_renders_and_unknown_tab_is_404() { }
[Fact] public async Task Settings_need_admin() { /* anonymous GET → login redirect; anonymous POST → 400/redirect like the other region pages */ }
[Fact] public async Task Options_save_calls_controller_and_says_when_it_applies() { /* "Saved. It applies when Home restarts." */ }
[Fact] public async Task Bad_number_shows_at_its_field_and_saves_nothing() { }
[Fact] public async Task Program_tab_names_the_regions_that_share_it() { /* "Home and Beach share this setting because they run together." */ }
[Fact] public async Task Physics_tab_offers_no_hybrid() { }
[Fact] public async Task Cpu_save_on_running_program_says_applied() { /* "Saved and applied." */ }
[Fact] public async Task Unavailable_priority_is_disabled_with_reason() { /* fake rules hook returns a reason */ }
[Fact] public async Task Panel_shows_restart_now_when_settings_pending() { }
[Fact] public async Task Pages_work_without_javascript() { /* no <script> needed: forms post plain; LayoutTests' no-CDN check covers the new pages */ }
```

- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*RegionSettingsPageTests" --filter-class "*RegionPanelTests"`. Expected: FAIL.

- [ ] **Step 3: Implement.** Use the layout and form patterns of `Pages/Regions/Move.cshtml` and `Pages/Advanced/Network.cshtml`. The priority check goes through an injectable `Func<CpuPriority, string?>`, registered as `RegionSettingsRules.PriorityUnavailable`, so tests don't depend on the OS.

- [ ] **Step 4: Run the tests and see them pass**, then the whole unit suite. Expected: all pass.

- [ ] **Step 5: Commit:** `Launcher: region settings page with Options, Map, Physics, Scripts, Permissions, Access and CPU tabs`

### Task 6: The Basics tab and the "How it runs" warnings

**Files:**
- Create: `Pages/Regions/_SettingsBasics.cshtml`
- Modify: `Pages/Regions/Settings.cshtml.cs` (the rename and resize handlers), `Pages/Regions/Runs.cshtml.cs` (replace `PhysicsNote`)
- Test: `RegionSettingsPageTests.cs`, `RunsChangePageTests.cs`

**Interfaces:**
- Consumes: Task 4's `RenameRegionAsync` and `ResizeRegionAsync`; Task 1's `Differences`; `OarJobs.TryStart(request, text, followUp)` from 4c.
- Produces, on the Basics tab:
  - **Name:** a text box. While the region is loaded, it is disabled with the note "Stop <Name> first to rename it."
  - **Size:** a select of 1..16 with `RegionAdvice.SizeRisk`.
    - Growing saves after the placement check.
    - Shrinking first shows: "Land and objects outside the new edge are lost." It then offers "Save as OAR first, then shrink" and "Shrink without saving".
      - "Save first" starts a Save job whose follow-up is `ResizeRegionAsync`, so a failed save leaves the size alone. It then goes to the job page.
  - **Map spot:** "X, Y" with a "Move…" link to the existing Move page.
  - **Region UUID:** read-only text.
  - "How it runs": each group's note becomes `RegionSettingsRules.Differences(current program, target program)`, one line each. This replaces `PhysicsNote`. When there are no differences, there is no note.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Rename_posts_to_controller_and_returns_to_basics() { }
[Fact] public async Task Rename_is_disabled_while_loaded() { }
[Fact] public async Task Grow_saves_after_placement_check() { }
[Fact] public async Task Shrink_asks_first_and_offers_save() { }
[Fact] public async Task Shrink_save_first_starts_a_save_with_resize_follow_up() { }
[Fact] public async Task Shrink_save_first_failure_keeps_size() { /* job fails → ResizeRegionAsync never called */ }
[Fact] public async Task Uuid_is_shown_read_only() { }
[Fact] public async Task Runs_page_lists_every_program_setting_that_changes() { /* physics and map and priority differ → three lines */ }
```

- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*RegionSettingsPageTests" --filter-class "*RunsChangePageTests"`. Expected: FAIL.

- [ ] **Step 3: Implement.**

- [ ] **Step 4: Run the tests and see them pass**, then the whole unit suite. Expected: all pass.

- [ ] **Step 5: Commit:** `Launcher: rename and resize a region; How it runs lists every setting that changes`

### Task 7: Grid defaults on Advanced Settings

**Files:**
- Create: `Pages/Advanced/Regions.cshtml`, `Regions.cshtml.cs`
- Modify: `Pages/Advanced/Index.cshtml` (a "Regions" entry after Database)
- Test: `AdvancedSettingsTests.cs`

**Interfaces:**
- Consumes: `LauncherSettings.DefaultPhysics` and `DefaultScripts`; `IGridController.UpdateSettings`.
- Produces: `/advanced/regions`, with the physics radios (ubODE, BulletSim, BulletSim in its own thread) and the scripts On/Off radios. On save, the page lists the regions that use the grid default: "Saved. It applies when Home and Beach restart." When none do: "Saved. No region uses the grid default right now."

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Regions_defaults_page_saves_physics_and_scripts() { }
[Fact] public async Task Defaults_save_names_the_regions_that_use_them() { }
[Fact] public async Task Advanced_menu_links_to_regions_defaults() { }
```

- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*AdvancedSettingsTests"`. Expected: FAIL.

- [ ] **Step 3: Implement** with the shape of `Pages/Advanced/Network.cshtml(.cs)` and its `_Saved` partial.

- [ ] **Step 4: Run the tests and see them pass**, then the whole unit suite. Expected: all pass.

- [ ] **Step 5: Commit:** `Launcher: grid default physics and scripts on Advanced Settings`

### Task 8: End-to-end on a real grid

**Files:**
- Create: `Launcher/DreamGrid.Launcher.IntegrationTests/RegionSettingsTests.cs`, following the existing integration tests' grid fixture.

- [ ] **Step 1: Write the tests**

```csharp
[Fact] public async Task Options_and_access_apply_after_restart_now() { /* save MaxAgents 20 and Keep residents out on a second region; NeedsRestart true; Restart; the region accepts logins again, its file has MaxAgents = 20, its OpenSim.ini has Region_<name> = DisallowResidents, its log has no [ERROR] line from that start, and NeedsRestart is false */ }
[Fact] public async Task BulletSim_threaded_region_accepts_logins() { }
[Fact] public async Task Scripts_off_region_starts_and_accepts_logins() { }
[Fact] public async Task Map_none_and_best_both_start() { }
[Fact] public async Task Renamed_region_logs_in_by_its_new_name() { /* stop, rename, start; XML-RPC login_to_simulator with the new name as the start location */ }
[Fact] public async Task Grown_region_registers_its_new_size() { /* Robust's map row SizeX == 512 after a resize to 2 and a restart */ }
[Fact] public async Task Cpu_settings_apply_to_the_running_program() { /* core [0] and BelowNormal; CpuTuning.Read(pid) shows them, on Windows and Linux */ }
```

- [ ] **Step 2: Run them in the background** (about 15 minutes): `DREAMGRID_MARIADB_BIN=C:\Users\ystud\AppData\Local\DreamGridDev\mariadb-11.4.9-winx64\bin DREAMGRID_REQUIRE_INTEGRATION=1 dotnet test --project Launcher/DreamGrid.Launcher.IntegrationTests`. Expected: all pass.

- [ ] **Step 3: Fix what the real grid shows.** Each fix is a red test in the unit suite first. In particular, check that `[YEngine] Enabled = false` and `[Map] GenerateMaptiles = false` let OpenSim boot. If one doesn't, the fix goes in the writer, never under `OpenSim/`. Expected: the integration and unit suites all pass.

- [ ] **Step 4: Commit:** `Launcher: end-to-end tests for region settings`
