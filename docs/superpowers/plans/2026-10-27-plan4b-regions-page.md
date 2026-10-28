# Plan 4b — Regions page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Regions page where the admin sees their world on Robust's map (with every region the launcher knows drawn as a named marker), drags a region type onto an empty spot, adds the region with a chosen size and "on its own" or "share with", and opens any region's info panel for its status, its power menu, Move and how it runs.

**Architecture:** The Core gains region types with generated stand-in land of four shapes, the memory and size advice the add form shows, placement sentences, and two controller operations (move a region; change how it runs). The host gains a map-tile proxy to Robust, a small regions JSON API for the map, and Razor pages for the map, the add form (a background job with progress, like setup's region step) and the info panel. All of these pages work without JavaScript, except the map itself. The map is a hand-written script in `wwwroot/js/map.js` with no library and nothing from a CDN.

**Tech Stack:** C# / .NET 10, ASP.NET Core Razor Pages, Bootstrap 5.3 (bundled), plain JavaScript, xUnit v3 (Microsoft Testing Platform), OpenSim REST console, Robust `MapGetServiceConnector`.

**Spec:** `docs/superpowers/specs/2026-10-27-regions-design.md` (§5.1–5.4, §5.7, §6.5, §6.8; and "Recorded during plan 4a" in the version 1 spec, `2026-10-24-crossplatform-launcher-v1-design.md`).

## Where this plan sits

Plan 4a (merged into V7 as 2a299e8) built the engine: `IGridController.AddRegionAsync`, `RegionPowerAsync`, `StartWorldAsync`, `GridStatus.Regions`, `RegionPlacement`, `PortAllocator`. This plan puts the website in front of it.

**Ruling (planning):** the per-region settings tabs (spec §6.7) move out of 4b into their own plan, **4d**. That covers Basics, Options, Map, Physics, Scripts, Permissions, Access and CPU, the region-versus-program split, and the grid-wide physics and script defaults. They mostly touch INI generation and process launch (CPU affinity and priority on two OSs), not the page. Without them 4b ships a complete page, and the tabs slot into the info panel later. *Cost if wrong:* the info panel ships without settings for one plan's length. It shows the line "Region settings are coming next" with no controls.

- **4b (this plan):** the map, tile proxy, region types, the add form, the info panel (status, power menu, Move, how it runs), navigation.
- **4c:** the OAR library, Import / Save as OAR, custom region types, Recently deleted, Restore, Delete forever. The info panel's Import, Save and Delete items arrive with it.
- **4d:** the settings tabs (spec §6.7).

## Global Constraints

- .NET 10; Linux (Debian 12/13) and Windows; no changes under `OpenSim/` (spec §3).
- Every settings change goes through `IGridController.UpdateSettings`.
- The website loads nothing from the internet (`LayoutTests`). Map tiles come through the launcher (spec §5.2), never from Robust's port in the browser.
- Every page works without JavaScript except the map, which says plainly when JavaScript is off: "The map needs JavaScript. You can still add a region by its map position below." (spec §2 anti-goals).
- The bare `<footer>DreamGrid <version></footer>` stays (the install smokes read it).
- The website never says "program" or "instance". It says "runs on its own", or "runs with Home, Beach" (spec §4).
- Region names follow `GridController.IsValidRegionName`, unique ignoring case across all regions, deleted ones included.
- Region sizes run 1 to 16 squares per side; 1×1 is preselected (spec §5.3).
- No spot may overlap any region, whether parked, stopped, failed, not yet drawn or deleted. A spot is connected exactly when it shares an edge with a region (`RegionPlacement.Check`).
- Map positions are region coordinates (one per 256 m square), Y grows north, and valid positions are 0 to 65535 minus the size.
- Anything that restarts other regions names them and asks first (spec §2 anti-goals, §5.3, §5.4).
- Only an admin can reach these pages and APIs; the fallback policy already requires the admin role, and APIs answer 401 (never a redirect).
- The built-in region types are Default Island, Sandy Beach, Forest and Meadow, in that order, with ids `island`, `beach`, `forest` and `meadow`. Each one's OAR lives at `<program root>/content/region-types/<id>.oar` and its picture at `<id>.png` (spec §5.7). `starter` stays the setup checklist's content.

## Review Focus

1. **Robust is down, booting, or has no tile for a spot:** the tile proxy answers 404 (never 500, never a hang past 5 s), and the map still draws every region's marker over plain water. Pinned by `Tile_is_404_when_robust_is_down` and `Tile_is_404_when_robust_has_none` (Task 3).
2. **A spot taken by a region the map can't show** (parked, stopped, failed, no tile yet): it can't be chosen, and the page names who holds it. Pinned by `Placement_refuses_spot_of_parked_region` (Task 3) and `Add_refuses_spot_taken_by_stopped_region` (Task 6).
3. **Two adds at once** (a double click, two tabs): the second gets the running job's progress, and no second region is created. Pinned by `Second_add_while_one_runs_shows_its_progress` (Task 6).
4. **A larger region (2×2 and up) from a built-in type:** the generated land fills the whole region, and a type's OAR loads in the middle. Pinned by `Size_2_region_gets_512_terrain` and `Size_3_oar_loads_in_the_middle` (Task 1), and by `Two_by_two_beach_region_boots` (Task 10).
5. **Sharing with a running program, or moving or changing a region in one:** the page names every region that will restart and does nothing until the admin confirms. Pinned by `Share_with_running_needs_confirmation` (Task 6), `Move_in_shared_program_asks_first` (Task 8) and `Runs_change_names_both_groups` (Task 9).

---

### Task 1: Region types and generated land

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Grid/RegionTypes.cs`
- Create: `Launcher/DreamGrid.Launcher.Core/Grid/TerrainShapes.cs` (the shape maths moves here; `IslandTerrain.WriteR32` stays as a one-line wrapper so setup is unchanged)
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/GridController.cs`: content validation in `CreateRegionAsync` and `AddRegionAsync`, and `LoadContentCoreAsync`
- Test: `Launcher/DreamGrid.Launcher.Tests/TerrainShapesTests.cs`, `RegionTypesTests.cs`, `AddRegionValidationTests.cs`, `GridControllerWiringTests.cs`

**Interfaces:**
- Produces:
  - `public enum TerrainShape { Island, Beach, Forest, Meadow }`
  - `public sealed record RegionType(string Id, string Name, string Description, TerrainShape Shape);`
  - `public static class RegionTypes { IReadOnlyList<RegionType> BuiltIn; RegionType? Find(string id); string? Oar(LauncherPaths paths, string id); string? Picture(LauncherPaths paths, string id); bool IsContent(string content); }`. `IsContent` is true for `starter` and each built-in id.
  - `public static class TerrainShapes { void WriteR32(string path, TerrainShape shape, int size = 256, int seed = 1); }`
- Consumes: `StarterContent.ProgramRoot(paths)`; `GridConsoleCommands.IslandTerrainLoadedAsync` (it requires at least 10 m of relief, so every shape needs that much).

Descriptions, as shown beside the map:
- Default Island: "A round island with a gentle hill."
- Sandy Beach: "A low, flat island with wide sandy shores."
- Forest: "Hilly ground for a forest."
- Meadow: "Gently rolling open land."

The shapes, all above the 20 m water level in the middle and below it at the edges:
- **Island:** today's island.
- **Beach:** centre about 23 m, with a wide flat band near 20.5 m before the drop.
- **Forest:** centre about 30 m, with ripples of about 4 m.
- **Meadow:** centre about 26 m, with long gentle swells of about 2 m.

The exact numbers are the implementer's.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory, InlineData(TerrainShape.Island), InlineData(TerrainShape.Beach), InlineData(TerrainShape.Forest), InlineData(TerrainShape.Meadow)]
public void Every_shape_is_land_in_the_middle_and_sea_at_the_edge(TerrainShape shape)
{
    var h = Heights(shape, 256);
    Assert.True(h[128 * 256 + 128] > 20f);
    Assert.True(h[0] < 20f && h[255 * 256 + 255] < 20f);
    Assert.True(h.Max() - h.Min() >= 10f); // IslandTerrainLoadedAsync's check
}

[Fact] public void Forest_is_hillier_than_meadow() => Assert.True(Relief(TerrainShape.Forest) > Relief(TerrainShape.Meadow));

[Fact] public void Size_512_writes_512_by_512() => Assert.Equal(512 * 512 * 4, FileLength(TerrainShape.Beach, 512));

[Fact] public void Island_wrapper_writes_the_same_bytes_as_before() // golden: today's IslandTerrain output for seed 1
```

```csharp
// RegionTypesTests
[Fact] public void Built_in_types_in_order() =>
    Assert.Equal(["Default Island", "Sandy Beach", "Forest", "Meadow"], RegionTypes.BuiltIn.Select(t => t.Name));
[Fact] public void Oar_is_null_until_the_file_exists() // then content/region-types/beach.oar under the program root
[Fact] public void Starter_and_built_in_ids_are_content() // "starter", "island", "beach", "forest", "meadow" true; "../x", "" false
```

```csharp
// AddRegionValidationTests
[Fact] public async Task Add_accepts_built_in_type_content()   // NewRegion(..., Content: "beach", ...) is not refused
[Fact] public async Task Add_refuses_unknown_content()         // "lava" → ArgumentException
// GridControllerWiringTests
[Fact] public async Task Size_2_region_gets_512_terrain()       // AddRegion size 2, content "meadow": island.r32 is 512*512*4 bytes; console saw "terrain load"
[Fact] public async Task Size_3_oar_loads_in_the_middle()       // with content/region-types/forest.oar present: console saw
                                                                // load oar ... --displacement <256,256,0> "…/forest.oar"
```

The displacement is `128 × (Size − 1)` metres on X and Y, which puts a 256 m OAR in the middle of the region. The middle square holds the OAR. The squares around it keep the terrain OpenSim makes, flat land at its default height. That is the spec's "rest left as water or plain land" (§5.3, my call).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*TerrainShapesTests" --filter-class "*RegionTypesTests" --filter-class "*AddRegionValidationTests" --filter-class "*GridControllerWiringTests"`
Expected: FAIL (compile errors: `TerrainShape` and `RegionTypes` don't exist).

- [ ] **Step 3: Implement**
  - `TerrainShapes.WriteR32` keeps `IslandTerrain`'s format rules: row-major, y outer, little-endian float32, size a multiple of 256.
  - `LoadContentCoreAsync(console, region, content, ct)`:
    - picks the OAR: the starter OAR for `starter`, else `RegionTypes.Oar`;
    - with an OAR and `Size > 1`, adds `--displacement <d,d,0>`;
    - without an OAR, writes `256 × Size` terrain of the type's shape (`starter` without its OAR is an Island).
  - Content validation in both controller methods uses `RegionTypes.IsContent`, and its message lists the ids.

- [ ] **Step 4: Run the tests to verify they pass**

Run: the Step 2 command.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Launcher
git commit -m "Launcher: region types with generated land of four shapes, sized to the region"
```

### Task 2: Advice for the add form (memory, size, sharing, placement)

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Grid/SystemMemory.cs`
- Create: `Launcher/DreamGrid.Launcher.Core/Grid/RegionAdvice.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/RegionAdviceTests.cs`, `SystemMemoryTests.cs`

**Interfaces:**
- Produces:
  - `public static class SystemMemory { long? AvailableBytes(); }`. On Linux this is `MemAvailable` from `/proc/meminfo`; on Windows, `GlobalMemoryStatusEx.ullAvailPhys`; null when unreadable. Also `internal static long? ParseMemInfo(string text)`, for the test.
  - `public sealed record OwnAdvice(long EstimateBytes, long? FreeBytes, int? MoreRegions, bool Short, string Text);`
  - `public static class RegionAdvice`:
    - `OwnAdvice OnItsOwn(GridStatus status, long? freeBytes, int size)`
    - `string Share(IReadOnlyList<string> groupRegions, string newName)`
    - `string? SizeRisk(int size)` (null for 1)
    - `string Placement(PlacementCheck check)`
    - `string GroupLabel(IReadOnlyList<string> regionNames)`
- Consumes: `GridStatus.Programs` (each one's `Process.MemoryBytes`, `Regions`), `PlacementCheck`, `Neighbour`.

Rules (my call, spec §5.3 and §8):
- **The estimate:** 400 MB for a 1×1, plus 150 MB for each extra square (`size² − 1`). Once any running program reports memory, the estimate is the mean memory of the running programs, scaled the same way.
- **`MoreRegions`:** free memory divided by the estimate, rounded down.
- **`Short`:** free memory is below 1.5 × the estimate.
- **Unknown free memory:** the text drops the "This computer has…" clause and `Short` is false.

- [ ] **Step 1: Write the failing tests (exact copy pinned)**

```csharp
[Fact] public void Own_with_plenty_of_memory() => Assert.Equal(
    "Uses about 400 MB more memory. This computer has 5.2 GB free, enough for about 13 more regions run this way. If it crashes, only this region goes offline.",
    RegionAdvice.OnItsOwn(NoPrograms, 5_583_457_484, 1).Text); // MB and GB are 2^20 and 2^30 bytes: 5.2 GB / 400 MB = 13

[Fact] public void Own_when_memory_is_short() // free 500 MB, 1×1 → Short; Text ends
    // "This computer may not have enough free memory for another region run on its own. Sharing saves memory."

[Fact] public void Estimate_follows_running_programs() // two running programs at 600 MB and 800 MB → EstimateBytes 700 MB for 1×1

[Fact] public void Share_names_the_group() => Assert.Equal(
    "Saves memory, but these regions start, stop, restart and crash together. Restarting Sunset Shore also restarts Home and Beach, and drops everyone in them.",
    RegionAdvice.Share(["Home", "Beach"], "Sunset Shore"));

[Fact] public void Size_risk_for_4() => Assert.Equal(
    "A 4×4 region is 16 squares of land in one region. It uses several times the memory of a 1×1 and can feel slower to visitors. Many viewers handle very large regions poorly.",
    RegionAdvice.SizeRisk(4));
// SizeRisk(2): "A 2×2 region is 4 squares of land. It uses more memory than a 1×1." SizeRisk(1): null.

[Fact] public void Placement_connected() => Assert.Equal("This region will join Beach on its east side.",
    RegionAdvice.Placement(new(true, null, [new("Beach", "east")])));
// two neighbours: "This region will join Beach on its east side and Home on its north side."
[Fact] public void Placement_island() => Assert.Equal("This will be a separate island; visitors reach it by teleport.", …);
[Fact] public void Placement_taken() => Assert.Equal("That spot is taken by Beach.", …);

[Fact] public void Group_label() => Assert.Equal("Home, Beach (2 regions)", RegionAdvice.GroupLabel(["Home", "Beach"]));
// one region: "Home"

[Fact] public void MemInfo_reads_MemAvailable() // "MemTotal: 8000000 kB\nMemAvailable:  5078125 kB\n" → 5_200_000_000
```

The size-risk wording departs from the spec's example in one word: the spec says "in one program". The website must not say "program", so the copy says "in one region". **Ruling (planning)**; *cost if wrong:* one string.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*RegionAdviceTests" --filter-class "*SystemMemoryTests"`
Expected: FAIL (the types don't exist).

- [ ] **Step 3: Implement both classes.** Sizes print in GB with one decimal from 1 GB up, else in MB rounded to 10. The Windows call is a P/Invoke of `GlobalMemoryStatusEx` (`kernel32`); any other OS returns null.

- [ ] **Step 4: Run the tests to verify they pass.** Expected: PASS.

- [ ] **Step 5: Commit** — `git commit -m "Launcher: memory, size, sharing and placement advice for adding a region"`

### Task 3: Map tile proxy and the regions API

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Endpoints/RegionsApi.cs`
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (`RegionsApi.Map(app)`, and a named `HttpClient` "robust-map" with a 5 s timeout)
- Test: `Launcher/DreamGrid.Launcher.Tests/RegionsApiTests.cs`

**Interfaces:**
- Produces, all admin only:
  - `GET /api/map/tile/{z:int}/{x:int}/{y:int}`. It answers 400 unless 1 ≤ z ≤ 8, 0 ≤ x, y ≤ 65535, and x and y are multiples of 2^(z−1). Otherwise it fetches `http://127.0.0.1:{RobustPublicPort}/map-{z}-{x}-{y}-objects.jpg` and passes on the bytes, with `image/jpeg` or `image/png` and `Cache-Control: private, max-age=60`. It answers 404 when Robust fails, times out, or returns a non-image or empty body.
  - `GET /api/regions` returns `{ regions: [MapRegion] }`, every region in the settings, deleted ones included:
    - `public sealed record MapRegion(Guid Id, string Name, int X, int Y, int Size, string State, string StateText, IReadOnlyList<string> RunsWith, bool Reserved);`
    - `State` is the `RegionState` name in lower case, or `"reserved"` for a deleted region. `StateText` is the dashboard's words for it.
  - `GET /api/regions/placement?x=&y=&size=&ignore=` returns `{ ok, text }`, where `text` is from `RegionAdvice.Placement`. It answers 400 for a size outside 1–16 or a spot off the map, with text "That spot is off the map."
- Consumes: `IGridController.CurrentSettings()`, `GetStatus()`; `RegionAdvice.Placement` (Task 2); the dashboard's state words. `StatusApi` builds them privately today, through `AsProcessState` and `StateText`. This task makes that one public helper, `public static string StatusApi.RegionStateText(RegionState state)`, which `/api/status`, this API and the Task 7 panel all use, and adds the word "Parked" for `Parked` and "Reserved" for a deleted region.

- [ ] **Step 1: Write the failing tests.** These use `LauncherFactory` and `TestAuth`. The Robust side is a `StubHandler` swapped in for the "robust-map" client through `ExtraServices`.

```csharp
[Fact] public async Task Tile_needs_sign_in()                // anonymous GET /api/map/tile/1/1000/1000 → 401
[Fact] public async Task Tile_passes_on_robust_jpeg()        // stub answers image/jpeg bytes for "/map-1-1000-1000-objects.jpg" → 200, same bytes, image/jpeg
[Fact] public async Task Tile_is_404_when_robust_is_down()   // stub throws HttpRequestException → 404
[Fact] public async Task Tile_is_404_when_robust_has_none()  // stub answers 200 text/plain empty → 404
[Fact] public async Task Tile_refuses_misaligned_spot()      // z=2, x=1001 → 400
[Fact] public async Task Tile_refuses_zoom_out_of_range()    // z=9 → 400
[Fact] public async Task Regions_lists_every_region_with_state() // Welcome running, Isle parked → states "running", "parked"
[Fact] public async Task Placement_says_connected()          // Welcome at 1000,1000; x=1001 → ok, "This region will join Welcome on its east side."
[Fact] public async Task Placement_refuses_spot_of_parked_region() // parked Isle at 1010,1010 → ok false, "That spot is taken by Isle."
[Fact] public async Task Placement_refuses_spot_off_the_map()      // x=65535, size=2 → 400
```

- [ ] **Step 2: Run them to verify they fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*RegionsApiTests"`. Expected: FAIL (404 for every route).
- [ ] **Step 3: Implement `RegionsApi.Map(WebApplication app)`.** The tile URL is built from validated integers only. Robust's port comes from `CurrentSettings()` per request, so a changed port applies at once.
- [ ] **Step 4: Run them to verify they pass.** Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "Launcher: map tiles through the launcher, and a regions API for the map"`

### Task 4: The Regions page, navigation and dashboard links

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Regions/Index.cshtml(.cs)`
- Modify: `Pages/Shared/_Layout.cshtml` (Regions between Home and Advanced), `Pages/Index.cshtml` (region names link to `/regions?region=<id>`; "More regions" leaves Coming soon), `wwwroot/css/site.css`
- Create: the endpoint `GET /regions/types/{id}.png` in `RegionsApi.cs`. It serves `RegionTypes.Picture`, or 404; the page then shows a coloured square for the type, from CSS.
- Test: `Launcher/DreamGrid.Launcher.Tests/RegionsPageTests.cs`, plus updates to `DashboardTests` and `LayoutTests`

**Interfaces:**
- Produces:
  - The page `/regions`, with element hooks for Task 5: `#region-types` (one `li[data-type=<id>]` per type, with its name and description), `#map` (`data-tiles="/api/map/tile"`, `data-regions="/api/regions"`, `data-placement="/api/regions/placement"`, `data-add="/regions/add"`, `data-panel="/regions/region/"`), `#region-panel`, `#back-to-mine`, `#zoom-in`, `#zoom-out`, `#placement-text`.
  - A `<noscript>` block with the constraint's sentence.
  - A plain table `#region-list` (name, state text, "runs on its own" / "runs with …", map position), each name linking to `/regions/region/<id>`.
  - An "Add a region" link to `/regions/add`.
  - `?region=<id>` marks that region selected; the map opens its panel, and without JavaScript the table row is highlighted.
- Layout (spec §5.2): types on the left (`col-lg-2`), map in the middle, panel on the right (`col-lg-3`, hidden until a region is selected). On phones they stack, and the panel opens as a Bootstrap offcanvas over the map.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Nav_has_regions_between_home_and_advanced() // the nav links' text order: Home, Regions, Advanced, Logs
[Fact] public async Task Page_lists_built_in_types_in_order()      // #region-types li names
[Fact] public async Task Page_says_map_needs_javascript()           // noscript contains "The map needs JavaScript."
[Fact] public async Task Region_list_links_each_region()            // a[href="/regions/region/<WelcomeId>"] with text "Welcome"
[Fact] public async Task Region_list_never_says_program()           // the page text contains neither "program" nor "instance" (case-insensitive)
[Fact] public async Task Type_picture_is_404_without_file()         // GET /regions/types/beach.png → 404; with the file → image/png
[Fact] public async Task Type_picture_refuses_unknown_id()          // GET /regions/types/..%2Fsettings.png → 404
// DashboardTests
[Fact] public async Task Coming_soon_no_longer_lists_more_regions()
[Fact] public async Task Dashboard_region_links_to_regions_page()   // a[href="/regions?region=<id>"]
```

- [ ] **Step 2: Run them to verify they fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*RegionsPageTests" --filter-class "*DashboardTests" --filter-class "*LayoutTests"`. Expected: the new tests FAIL; LayoutTests pass.
- [ ] **Step 3: Implement the page, the nav link, the dashboard changes and the picture endpoint.** The page loads `/js/live.js` and `/js/map.js` (Task 5; until then an empty file).
- [ ] **Step 4: Run them to verify they pass.** Expected: PASS, LayoutTests included.
- [ ] **Step 5: Commit** — `git commit -m "Website: Regions page with region types and a region list; Regions in the top bar"`

### Task 5: The map script

**Files:**
- Create: `Launcher/DreamGrid.Launcher/wwwroot/js/map.js`
- Modify: `wwwroot/css/site.css` (the tile layer, markers, the ghost marker, striped "no picture yet" squares)
- Test: `Launcher/DreamGrid.Launcher.Tests/LayoutTests.cs` (map.js contains no `http://` or `https://`), plus a manual check in the browser pane

**Interfaces:**
- Consumes: Task 4's element hooks and Task 3's APIs; `poll()` and `signInAgain()` from `live.js`.
- Produces: navigation to `/regions/add?type=<id>&x=<x>&y=<y>`, the panel loaded from `/regions/region/<id>?partial=1` into `#region-panel`, and the Move pick mode (Task 8) entered by `/regions?move=<id>`.

Behaviour, with the drawing approach left to the builder (spec §8):
- **Tiles** are absolutely positioned `<img>` elements in a layer moved by a CSS transform. A zoom level is a Robust zoom, 1–8. A failed image is hidden, which leaves water colour.
- **Movement:** drag (mouse and touch) pans; the wheel and + / − zoom about the pointer or centre. "Back to my regions" fits every live region's box.
- **Markers:** one per region from `/api/regions`, polled every 5 s. A square sized `Size` squares, with the name always on top. Stripes when there is no tile; a state class (`state-running`, `state-parked`, …, `reserved`).
- **Placing:**
  - Drag a type `li` onto the map, or click one (it becomes selected) and then click a spot.
  - A ghost square follows the pointer, snapped to whole squares.
  - On a new square the script fetches `/api/regions/placement`, at most 4 requests a second, and shows its `text` in `#placement-text`, red when not ok.
  - A drop or click on an ok spot navigates to the add page.
- **Clicking a marker** loads its panel. Escape or the panel's close button closes it and clears `?region`.
- **A 401 from any fetch** calls `signInAgain()`.

- [ ] **Step 1: Write the failing test** — `[Fact] public void Map_script_loads_nothing_external()` in LayoutTests, reading `wwwroot/js/map.js`. It fails while the file is empty, because the test also asserts the file defines `function startMap(`.
- [ ] **Step 2: Run it to verify it fails.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*LayoutTests"`. Expected: FAIL.
- [ ] **Step 3: Write `map.js`.** `startMap(element)` runs on DOMContentLoaded for `#map`.
- [ ] **Step 4: Run it to verify it passes,** then check by hand. Start the launcher against a dev data directory with a running grid (the `run` skill), sign in, open `/regions`, and check each point:
  - tiles or water appear, and Welcome's marker sits at 1000,1000;
  - drag, wheel and both buttons work;
  - "Back to my regions" recentres;
  - dragging Sandy Beach east of Welcome shows "This region will join Welcome on its east side.";
  - dropping it there opens the add page with x=1001, y=1000;
  - clicking Welcome opens its panel;
  - at phone width (`resize_window` mobile), the panel opens over the map.
  Expected: every point as described; the browser console shows no errors.
- [ ] **Step 5: Commit** — `git commit -m "Website: the world map (tiles, markers for every region, drag-to-place)"`

### Task 6: Add a region (form and job)

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Regions/Add.cshtml(.cs)`
- Create: `Launcher/DreamGrid.Launcher/RegionJob.cs`. One add at a time: it owns its own `SetupProgress` instance (the type is reused for its stages and percentages), the region name, and the saved region's id once it is known.
- Modify: `Program.cs` (register `RegionJob`); `RegionsApi.cs` (`GET /api/regions/progress`, the job's snapshot plus `regionId`)
- Test: `Launcher/DreamGrid.Launcher.Tests/AddRegionPageTests.cs`

**Interfaces:**
- Consumes:
  - `IGridController.AddRegionAsync(NewRegion, ct, progress)`, `GetStatus()`, `CurrentSettings()`;
  - `RegionAdvice` (Task 2), `RegionTypes` (Task 1), `SystemMemory`;
  - `BackgroundWork.Run`;
  - `LogReader`, for the failure's "Show details" excerpt (the last 40 lines of the new region's program log; the same source and call as setup's region step).
- Produces: `public sealed class RegionJob { bool TryStart(NewRegion request, Func<NewRegion, CancellationToken, IProgress<string>, Task<RegionSettings>> add); SetupProgressSnapshot Snapshot(); string? Name; Guid? RegionId; }`

**The form** (GET reads `type`, `x`, `y` and `size` from the query; everything is editable, so it works without the map):
- **Name:** setup's error copy.
- **Type:** radio cards with each picture.
- **Map position:** X and Y number fields, with the `RegionAdvice.Placement` sentence under them (computed on the server, so it updates on submit).
- **Size:** a `select` of 1×1 … 16×16 with `SizeRisk` under it. A small script refreshes the sentence and the risk on change; without it they refresh on submit.
- **How it runs:**
  - "On its own" with `OnItsOwn` text. It's preselected unless `Short`, in which case the first share option is preselected and the short text shows.
  - One "Runs with {GroupLabel}" radio per program, with `Share` text.
- **Confirmation:** when the chosen group's program is running, a checkbox "I understand {names} will restart" is required.

**POST** checks, in this order, each with the error shown at its field:
1. the name rule;
2. the name is unique;
3. the type is known;
4. size 1–16;
5. the spot is on the map;
6. the spot is free: "That spot is taken by X.";
7. the group exists;
8. the confirmation, when needed;
9. the world is running: Robust must be up, else "Start your world first: the grid service isn't running." with a link to the dashboard.

Then `RegionJob.TryStart`, and a redirect to `/regions/add?working=1`, which:
- while it runs, shows the stage and a bar (polled from `/api/regions/progress`; `<noscript>` meta refresh 3 s);
- on success, shows "{Name} is ready." with "Show it on the map" (`/regions?region=<id>`);
- on failure, shows the plain message, "Show details" with the log excerpt, and "Open {Name}" to retry from its info panel.

**Ruling (planning):** a region whose add failed stays saved (4a ruling 10). So "retry" is that region's Start in its panel, not this form; the form refuses the name. Changing the choices of a failed region needs Delete (plan 4c). *Cost if wrong:* until 4c, a failed add with a wrong choice is fixed with Move or "How it runs", or kept parked.

- [ ] **Step 1: Write the failing tests** (the fake controller's `AddRegionAsync` is driven like `OnCreateRegion`)

```csharp
[Fact] public async Task Get_prefills_from_the_map()                 // ?type=beach&x=1001&y=1000 → Sandy Beach checked, X 1001, Y 1000, size 1 selected
[Fact] public async Task Own_preselected_with_plenty_of_memory()
[Fact] public async Task Share_preselected_when_memory_is_short()     // SystemMemory swapped via a Func<long?> service
[Fact] public async Task Groups_are_named_by_their_regions()          // "Runs with Home, Beach (2 regions)"
[Fact] public async Task Add_refuses_spot_taken_by_stopped_region()   // "That spot is taken by Isle."
[Fact] public async Task Add_refuses_duplicate_name_ignoring_case()
[Fact] public async Task Share_with_running_needs_confirmation()      // no checkbox → error, AddRegionAsync not called
[Fact] public async Task Add_when_world_is_stopped_says_start_first()
[Fact] public async Task Add_starts_the_job_and_shows_progress()      // redirect to ?working=1; calls AddRegionAsync once with the NewRegion asked for
[Fact] public async Task Second_add_while_one_runs_shows_its_progress() // second POST → same redirect; AddRegionAsync called once
[Fact] public async Task Failed_add_shows_message_and_details()
[Fact] public async Task Add_page_never_says_program()
```

- [ ] **Step 2: Run them to verify they fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*AddRegionPageTests"`. Expected: FAIL (404).
- [ ] **Step 3: Implement `RegionJob`, the page and the progress endpoint.** The job catches the exceptions the way `SetupRunner` does: `ArgumentException` messages are shown as they are, and anything else shows "Your region could not be created." with the log excerpt. `SystemMemory.AvailableBytes` reaches the page through a registered `Func<long?>`, so tests can swap it.
- [ ] **Step 4: Run them to verify they pass.** Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "Website: add a region (type, spot, size, how it runs) with progress"`

### Task 7: The region info panel and its power menu

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Regions/Region.cshtml(.cs)` (route `/regions/region/{id:guid}`; `?partial=1` renders with no layout, for the map's panel)
- Create: `Launcher/DreamGrid.Launcher/Endpoints/RegionActions.cs` (like `GridActions`: runs `RegionPowerAsync` in the background, logs, and has notices)
- Test: `Launcher/DreamGrid.Launcher.Tests/RegionPanelTests.cs`

**Interfaces:**
- Consumes: `GetStatus()` (the region's state, its program's `Process` uptime and memory), `CurrentSettings()`, `IOnlineUsers.ListAsync` (avatars whose `RegionId` is this region), `RegionPlacement.Check(s, r.X, r.Y, r.Size, ignore: r.Uuid).Neighbours`, `RegionStatus.RunsWith`, with the states of those regions (the regions an action affects are the ones in `RunsWith` that are running or starting while this region's program is active; the page can't see the controller's held set, so it doesn't call `RegionPowerPlan.For`), `StatusApi.RegionStateText` (Task 3), and `LogReader` (a failed region's last lines).
- Produces: `public sealed class RegionActions { bool TryRun(Guid id, RegionPower action); static string Notice(RegionPower action, string name); }`

What the panel shows (spec §5.4):
- **Status:** the state in words; for a failed region, the message and the log excerpt.
- **Who's here:** names, or "Nobody is here right now."; "Who's here isn't available right now." when the list is unavailable.
- **Running time** and **memory** (of its program; when shared, the line says "shared by Home and Beach").
- **Map position, size, neighbours** ("Next to Beach (east)"), and "Runs on its own" or "Runs with Home, Beach".
- **The power menu** (`<details class="power-menu">`, as on the dashboard):
  - running or starting: Restart, Stop, Park;
  - stopped or failed: Start, Park;
  - parked: Unpark and start, Unpark.
- **Move…** and **How it runs…** links (Tasks 8 and 9).
- **Settings:** the line "Region settings are coming next."

A power POST goes to the page's `OnPostPower(action, confirmed)`. When the affected list isn't empty and `confirmed` isn't set, the page shows a confirmation view instead of acting: "Restarting Sunset Shore also restarts Home and Beach, and drops everyone in them. Go ahead?" with "Yes, restart" and Cancel. The wording is built per action. Otherwise it runs and redirects back with the notice.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Panel_shows_state_size_position_and_neighbours()
[Fact] public async Task Panel_lists_who_is_here()
[Fact] public async Task Panel_says_runs_with_others()            // "Runs with Home, Beach"
[Fact] public async Task Running_region_menu_has_restart_stop_park()
[Fact] public async Task Parked_region_menu_has_unpark_items()
[Fact] public async Task Failed_region_shows_message_and_log()
[Fact] public async Task Power_on_region_of_its_own_runs_at_once()  // POST Restart → fake saw "RegionPower Restart <id>"
[Fact] public async Task Power_in_shared_group_asks_first()          // POST Stop without confirmed → confirmation naming Home and Beach; no call
[Fact] public async Task Power_confirmed_runs()                       // confirmed=true → the call
[Fact] public async Task Partial_has_no_layout()                      // ?partial=1 → no <nav>, no <footer>
[Fact] public async Task Unknown_region_is_404()
[Fact] public async Task Panel_never_says_program()
```

- [ ] **Step 2: Run them to verify they fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*RegionPanelTests"`. Expected: FAIL (404).
- [ ] **Step 3: Implement the page and `RegionActions`.** Register `RegionActions` in `Program.cs`. The fake grid controller needs `RegionPowerAsync` recorded (it already is) and status with several regions per program (its `Status` property).
- [ ] **Step 4: Run them to verify they pass.** Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "Website: region info panel with status, who's here and its power menu"`

### Task 8: Move a region

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/IGridController.cs`, `GridController.cs`; `Launcher/DreamGrid.Launcher.Tests/FakeGridController.cs`
- Create: `Launcher/DreamGrid.Launcher/Pages/Regions/Move.cshtml(.cs)` (route `/regions/region/{id:guid}/move`)
- Modify: `wwwroot/js/map.js` (in `?move=<id>` mode, a click on an ok spot goes to the move page with x and y)
- Test: `Launcher/DreamGrid.Launcher.Tests/GridControllerWiringTests.cs`, `MoveRegionPageTests.cs`

**Interfaces:**
- Produces: `Task MoveRegionAsync(Guid regionId, int x, int y, CancellationToken ct);` on `IGridController`, under the operation lock.
  - It throws `ArgumentException` with "That spot is taken by X." or "That spot is off the map.", and `InvalidOperationException` for a deleted region.
  - It saves the new X and Y (the INIs are rewritten by `UpdateSettings`).
  - When the region's program `IsActive` and loads the region, it restarts that program (logging the others it affected).
  - Otherwise the position applies at the next start.
- Consumes: `RegionPlacement.Check(..., ignore: id)`, `RegionPowerPlan.IsActive`, `RestartProgramAsync`.

The page: X and Y (prefilled from the query), the placement sentence, and "Choose on the map" (`/regions?move=<id>`). When the region runs with others, a required checkbox "I understand {names} will restart". The move runs in the background through `BackgroundWork`, then the page redirects to the panel with "Moving {Name}…".

- [ ] **Step 1: Write the failing tests**

```csharp
// GridControllerWiringTests (the rig from plan 4a)
[Fact] public async Task Move_saves_spot_and_restarts_running_program()
[Fact] public async Task Move_of_stopped_region_restarts_nothing()
[Fact] public async Task Move_onto_another_region_is_refused()        // settings unchanged
[Fact] public async Task Move_next_to_itself_is_allowed()             // overlapping its own old square: ignore = itself
// MoveRegionPageTests
[Fact] public async Task Move_in_shared_program_asks_first()
[Fact] public async Task Move_posts_and_returns_to_panel()
```

- [ ] **Step 2: Run them to verify they fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*GridControllerWiringTests" --filter-class "*MoveRegionPageTests"`. Expected: FAIL (compile: no `MoveRegionAsync`).
- [ ] **Step 3: Implement the controller method, the fake's recording, the page and the map's move mode.**
- [ ] **Step 4: Run them to verify they pass.** Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "Launcher: move a region on the map"`

### Task 9: Change how a region runs

**Files:**
- Modify: `IGridController.cs`, `GridController.cs`, `FakeGridController.cs`
- Create: `Launcher/DreamGrid.Launcher/Pages/Regions/Runs.cshtml(.cs)` (route `/regions/region/{id:guid}/runs`)
- Test: `GridControllerWiringTests.cs`, `RunsChangePageTests.cs`

**Interfaces:**
- Produces: `Task ChangeRunsWithAsync(Guid regionId, string? shareWith, CancellationToken ct);` on `IGridController`, under the operation lock. `shareWith` null means "on its own".
  - **On its own:** a new program (`UniqueFolderName`, ports from `PortAllocator`, and the old program's `Physics`, so nothing about the region changes).
  - **Share:** the region's `Instance` becomes `shareWith`. It throws `ArgumentException` for an unknown group, or for its own group.
  - **Afterwards:**
    - the old program stops if it has no loadable regions left (and its `ProgramSettings` entry is removed), or restarts without the region if it was active;
    - the new program starts, or restarts if it was active, when the region is loadable;
    - the region keeps its UDP port.
- Consumes: `PortAllocator.Next`, `RegionModel.Loadable`, `StopProgramAsync`, `RestartProgramAsync`, `StartProgramAsync`.

**Ruling (planning):** the change applies at once, after the admin confirms, instead of "at the next restart, with Restart now offered" (spec §5.4). A region whose settings name a new program while the old one still runs it would show the wrong state until something restarted. Applying at once avoids that state. *Cost if wrong:* an admin who wanted to queue the change for later can't; they can do it later instead.

The page uses the add form's "How it runs" choices and texts (Task 6 renders them through a shared partial, `_RunsChoices.cshtml`). It lists every region that will restart, from both groups, behind a required checkbox. When the group differs in `Physics`, it names the setting: "Physics will change from ubODE to BulletSim to match Home." (spec §6.7: the target group's values win).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Share_moves_region_and_stops_emptied_program()   // Isle on its own → shares Welcome's: Isle's program stopped and removed, Welcome's restarted
[Fact] public async Task Own_makes_a_new_program_keeping_udp_port()
[Fact] public async Task Own_of_parked_region_starts_nothing()
[Fact] public async Task Share_with_own_group_is_refused()
// RunsChangePageTests
[Fact] public async Task Runs_change_names_both_groups()                  // "Home, Beach and Isle will restart" before confirming
[Fact] public async Task Runs_change_names_physics_difference()
[Fact] public async Task Runs_page_never_says_program()
```

- [ ] **Step 2: Run them to verify they fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*GridControllerWiringTests" --filter-class "*RunsChangePageTests"`. Expected: FAIL (compile).
- [ ] **Step 3: Implement.** Extract `_RunsChoices.cshtml` from Task 6's form, so both pages share the texts.
- [ ] **Step 4: Run them to verify they pass,** then run the whole unit suite: `dotnet test --project Launcher/DreamGrid.Launcher.Tests`. Expected: all pass.
- [ ] **Step 5: Commit** — `git commit -m "Launcher: change how a region runs (on its own or with others)"`

### Task 10: End-to-end through the website

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.IntegrationTests/LauncherEndToEndTests.cs`, `GridBootTests.cs`

**Interfaces:**
- Consumes: the real launcher process (`StartLauncher`), its website on the web port, and an admin sign-in. Use the same route the existing end-to-end tests use for an admin cookie; if none exists, sign in through `/login` with the owner avatar created by the test.

- [ ] **Step 1: Write the tests**

```csharp
// LauncherEndToEndTests
[Fact] public async Task Website_adds_a_second_region_and_it_accepts_logins()
// sign in; POST /regions/add (Beach, beach, 1001,1000, size 1, on its own); poll /api/regions/progress until not running;
// /api/regions lists Beach "running"; XmlRpcLogin to uri:Beach returns Beach's UDP port.
[Fact] public async Task Map_tile_proxy_answers_for_a_running_grid()
// GET /api/map/tile/1/1000/1000 → 200 image or 404, never 5xx.
// GridBootTests
[Fact] public async Task Two_by_two_beach_region_boots()   // AddRegionAsync size 2 "beach": accepts logins; `terrain stats` relief ≥ 10
[Fact] public async Task Move_then_runs_change_keep_the_region_reachable() // move Isle, then share with Welcome: both accept logins
```

These check work Tasks 1–9 built, so they are expected to pass on their first run (as in plan 4a). The RED step is that they don't compile until Tasks 8 and 9 exist.

- [ ] **Step 2: Run them.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.IntegrationTests --filter-class "*LauncherEndToEndTests" --filter-class "*GridBootTests"` (needs the staged OpenSim and MariaDB, as in plan 4a). Expected: PASS.
- [ ] **Step 3: Commit** — `git commit -m "Launcher: end-to-end tests for the Regions page"`
