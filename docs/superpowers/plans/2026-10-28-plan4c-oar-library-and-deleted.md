# Plan 4c — OAR library, OAR jobs and Recently deleted Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The admin keeps OAR files in a library they can upload to, download from and tidy. They can turn any library OAR into a custom region type, Import an OAR into a region or Save a region as one, and delete a region into "Recently deleted", from where it can be restored or deleted forever.

**Architecture:**
- The Core gains:
  - the library folder and a small `OarLibrary` (safe names, listing, staging folders);
  - custom region types in the settings;
  - three OAR jobs on `GridController` (Import, Save, Make the land again). These run under a lock per program, not the global operation lock.
  - Delete, Restore and Delete forever on the controller;
  - a `RegionStore` that talks to MariaDB to measure, deregister and erase a region's rows.
- The host gains:
  - a library page with a streamed upload;
  - an `OarJobs` service (background, one job per program, progress the page polls);
  - the info panel's Import, Save as OAR, Make the land again and Delete items;
  - a Recently deleted page.

**Tech Stack:** C# / .NET 10, ASP.NET Core Razor Pages and minimal APIs, `Microsoft.AspNetCore.WebUtilities.MultipartReader`, `System.Formats.Tar` with `GZipStream`, MySqlConnector, Bootstrap 5.3 (bundled), xUnit v3 (Microsoft Testing Platform), OpenSim REST console.

**Spec:** `docs/superpowers/specs/2026-10-27-regions-design.md`: §5.4 (Import, Save, Delete), §5.5, §5.6, §5.7 (custom types), §6.6, and §8's library tests.

## Where this plan sits

Plans 4a (2a299e8) and 4b (312a2c6) are merged into V7. The settings model already has `RegionSettings.DeletedAt`, and the rules already reserve a deleted region's name, map spot and ports (`RegionModel`, `RegionPlacement`, `PortAllocator`). The map already draws a deleted region as "reserved" (`/api/regions`). Nothing sets `DeletedAt` yet; this plan does. Plan 4d (settings tabs) follows.

This plan also takes the three items 4b left for it. None of them is in the spec, so each is a **my call** ruling:

- **Ruling A (failed add):** a region whose add failed before its land loaded gets **Make the land again** in its panel: it loads the region's type again (an OAR job). `RegionSettings` gains `NeedsContent`, set when the region is saved and cleared once its content loads, so the item survives a restart. *Cost if wrong:* one extra menu item that a person might ignore.
- **Ruling B (deleted region whose program was removed):** a deleted region keeps its program's folder name reserved; no new program may take it. Restore then puts the region back in its old program when that program still exists. When the program is gone, Restore makes a new program of its own under the same folder name, so the region's logs and files are where they were. *Cost if wrong:* a folder name like `Beach` stays taken until the deleted region is erased; the next "Beach" program becomes `Beach_2`.
- **Ruling C (removed program still running):** a folder name is not free while a live process holds that folder's PID file (`PidFile.ReadLive(<pids>/<name>.pid, OpenSim.dll)`). Without this, a new program would adopt the old process as its own. *Cost if wrong:* an unusual `_2` folder name.

## Global Constraints

- .NET 10; Linux (Debian 12/13) and Windows; no changes under `OpenSim/` (spec §3).
- Every settings change goes through `IGridController.UpdateSettings`.
- The website loads nothing from the internet (`LayoutTests`).
- Every page works without JavaScript except the map. Without JavaScript the upload is a plain form post (the browser shows its own progress) and job progress refreshes with `<meta http-equiv="refresh" content="3">`.
- The bare `<footer>DreamGrid <version></footer>` stays (the install smokes read it).
- The website never says "program" or "instance". It says "runs on its own", "runs with Home, Beach", or "the regions that run with it" (spec §4).
- Only an admin reaches these pages and APIs; APIs answer 401, never a redirect.
- Anything that restarts other regions names them and asks first (spec §2).
- The library folder is `<data>/oars`, owner-only like the rest of the data directory (spec §5.5). Only `*.oar` files directly in it are the library; its subfolders `.incoming` (uploads) and `.saving` (saves) are staging, never listed, and emptied when the launcher starts.
- A library name is a plain file name: no `/`, `\` or `"`, not starting with `.`, ending in `.oar` (any case), at most 120 characters. The name is resolved with `Path.GetFullPath`, and that path's directory must equal the library's.
- A file the upload or a save makes is an OAR only if it is a gzip tar whose first entry is `archive.xml`. Anything else is removed and refused: "That file isn't an OAR."
- Saving writes `<RegionName>-<yyyy-MM-dd>.oar`. A name already taken in the library gets `-2`, `-3`, …, and so does an upload.
- An OAR job runs only on a region that is running and accepts logins. Only one job runs at a time in each program. While a job runs, that program's regions refuse power actions, Move, How it runs and Delete with: "<Job text> is still running. Try again when it finishes." The world's Start, Stop and shutdown still go ahead; the job then fails with "The region stopped before the <import|save> finished."
- Job texts: "Importing <file> into <Region>", "Saving <Region> as an OAR", "Making the land of <Region> again".
- Deleted regions keep their name, map spot, ports and program folder name until they are deleted forever. Nothing is deleted automatically (spec §5.6).

## Review Focus

1. **A multi-gigabyte upload, or one cut off halfway:** it is never held in memory; a cut-off or non-OAR upload leaves nothing in the library and nothing in `.incoming`. Pinned by `Upload_streams_without_buffering`, `Cut_off_upload_leaves_no_file` and `Upload_of_non_oar_is_refused` (Task 2).
2. **Path tricks on download, delete, import and use-as-type** (`..`, `a/b.oar`, `..\x.oar`, `.hidden.oar`, `x.txt`, a percent-encoded slash): these answer 404 or 400 and never touch a file outside the library. Pinned by `Library_refuses_unsafe_names` (Task 1) and `Library_endpoints_refuse_unsafe_names` (Task 2).
3. **The region's program stops or restarts during a long save:** the job fails in plain words, no half file reaches the library, and the page can start a new job. Meanwhile that region's own power menu is refused rather than cutting the save off. Pinned by `Save_fails_cleanly_when_program_stops` and `Power_action_refused_while_job_runs` (Task 4).
4. **Deleting a broken region** (a failed program whose console is down): the delete still works at once, the region leaves Robust's map, and its land and objects stay in the database. Pinned by `Delete_works_when_program_failed` (Task 6) and `Delete_restore_and_erase_on_a_real_grid` (Task 8).
5. **Restoring after things moved on:** the region's program was removed (Ruling B), or a new region now runs in its old program. Restore brings it back with its land, at its old spot, and restarts nothing without asking. Pinned by `Restore_after_program_removed_recreates_its_folder` and `Restore_into_running_program_comes_back_stopped` (Task 6).

---

### Task 1: The library folder, safe names and custom types in the Core

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/LauncherPaths.cs`: `Oars`, `OarsIncoming`, `OarsSaving`; `EnsureCreated` creates them under the owner-only data directory.
- Create: `Launcher/DreamGrid.Launcher.Core/Grid/OarLibrary.cs`
- Create: `Launcher/DreamGrid.Launcher.Core/Grid/OarFile.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs`: `CustomRegionType`, `LauncherSettings.CustomTypes`, `RegionSettings.NeedsContent`
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/RegionTypes.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/OarLibraryTests.cs`, `OarFileTests.cs`, `RegionTypesTests.cs`, `SettingsStoreTests.cs`, `LauncherPathsTests.cs`

**Interfaces:**
- Produces:
  - `public string Oars`, `OarsIncoming` (`Oars/.incoming`), `OarsSaving` (`Oars/.saving`) on `LauncherPaths`.
  - `public sealed record OarEntry(string Name, long Bytes, DateTimeOffset Modified);`
  - `public sealed class OarLibrary(LauncherPaths paths)` with:
    - `IReadOnlyList<OarEntry> List()`: newest first.
    - `string? Resolve(string name)`: the full path, or null for an unsafe or missing name.
    - `static bool IsSafeName(string name)`
    - `static string CleanName(string uploaded)`: turns a browser's file name into a safe one. Characters outside letters, digits, space, `-_.'()` become `_`, the name is trimmed to 120 characters, and `.oar` is added if it's missing.
    - `string FreeName(string name)`: `name`, else `name-2.oar`, `name-3.oar`, ….
    - `void Delete(string name)`
    - `void ClearStaging()`: empties `.incoming` and `.saving`.
  - `public static class OarFile { bool LooksLikeOar(string path); }`: true when the file is gzip, a tar, and its first entry is `archive.xml`. It reads only the start of the file.
  - `public sealed record CustomRegionType(string Id, string Name, string File);`. `Id` is `"c-"` plus 8 lowercase hex characters; `File` is a library name.
  - `public List<CustomRegionType> CustomTypes { get; init; } = new();` on `LauncherSettings`. No schema bump: a missing list reads as empty.
  - `RegionSettings` gains a trailing `bool NeedsContent = false`.
  - On `RegionTypes`:
    - `IReadOnlyList<RegionType> All(LauncherSettings s)`: built-in first, then custom types in the order they were added.
    - `RegionType? Find(LauncherSettings s, string id)`
    - `bool IsContent(LauncherSettings s, string content)`
    - `string? Oar(LauncherPaths paths, LauncherSettings s, string id)`: a custom type's library file, or a built-in's content file.
    - `bool IsCustom(string id)`
  - `RegionType` gains a trailing `bool Custom = false`. A custom type's `Shape` is `Island` (its land when the file is gone) and its `Description` is "Made from <file>."
- Consumes: `LauncherPaths.CreateOwnerOnly`; the existing `RegionTypes.BuiltIn`, `Find(id)`, `Oar(paths, id)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("../x.oar")] [InlineData("a/b.oar")] [InlineData("..\\x.oar")] [InlineData(".hidden.oar")]
[InlineData("x.txt")] [InlineData("")] [InlineData("a\"b.oar")]
public void Library_refuses_unsafe_names(string name)
{
    Assert.False(OarLibrary.IsSafeName(name));
    Assert.Null(new OarLibrary(_paths).Resolve(name));
}

[Fact] public void List_shows_only_top_level_oars_newest_first() // a.oar (older), b.OAR, notes.txt, .incoming/c.oar
    => Assert.Equal(["b.OAR", "a.oar"], new OarLibrary(_paths).List().Select(e => e.Name));

[Fact] public void FreeName_counts_up() // Home-2026-10-28.oar and -2 exist
    => Assert.Equal("Home-2026-10-28-3.oar", new OarLibrary(_paths).FreeName("Home-2026-10-28.oar"));

[Theory] [InlineData("my world?.OAR", "my world_.OAR")] [InlineData("C:\\x\\land", "land.oar")]
public void CleanName_makes_a_safe_name(string uploaded, string expected) => Assert.Equal(expected, OarLibrary.CleanName(uploaded));

[Fact] public void ClearStaging_empties_incoming_and_saving() { /* files in both → both empty, library files untouched */ }

[Fact] public void LooksLikeOar_wants_archive_xml_first() // built with TarWriter in a GZipStream: archive.xml first → true;
    // terrains/x.r32 first → false; plain zip bytes → false; empty file → false
{ }

[Fact] public void All_lists_built_in_then_custom()
{
    var s = TestSettings.Default() with { CustomTypes = [new("c-0a1b2c3d", "My Castle", "castle.oar")] };
    Assert.Equal(["island", "beach", "forest", "meadow", "c-0a1b2c3d"], RegionTypes.All(s).Select(t => t.Id));
    Assert.True(RegionTypes.IsContent(s, "c-0a1b2c3d"));
    Assert.False(RegionTypes.IsContent(TestSettings.Default(), "c-0a1b2c3d"));
}

[Fact] public void Custom_type_oar_is_its_library_file() { /* castle.oar in Oars → its path; file gone → null */ }

[Fact] public void Settings_without_custom_types_or_needs_content_load() { /* schema-2 JSON from 4b → CustomTypes empty, NeedsContent false */ }

[Fact] public void EnsureCreated_makes_the_library_folders() { /* Oars, OarsIncoming, OarsSaving exist */ }
```

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*OarLibraryTests" --filter-class "*OarFileTests" --filter-class "*RegionTypesTests"`
Expected: build errors (`OarLibrary`, `OarFile`, `CustomTypes` not defined).

- [ ] **Step 3: Implement the interfaces above.** `Resolve` returns null unless `IsSafeName(name)`, the file exists, and `Path.GetDirectoryName(Path.GetFullPath(Path.Combine(Oars, name)))` equals `Path.GetFullPath(Oars)` (compared case-insensitively on Windows). `LooksLikeOar` catches `InvalidDataException`, `IOException` and `EndOfStreamException` and returns false.

- [ ] **Step 4: Run the tests and see them pass** (the same command). Expected: PASS. Then run the whole unit suite: `dotnet test --project Launcher/DreamGrid.Launcher.Tests`. Expected: all pass.

- [ ] **Step 5: Commit:** `Launcher: OAR library folder, safe names and custom region types in the Core`

### Task 2: The library page

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Regions/Library.cshtml`, `Library.cshtml.cs` (route `/regions/library`)
- Create: `Launcher/DreamGrid.Launcher/Endpoints/LibraryApi.cs` (upload and download)
- Create: `Launcher/DreamGrid.Launcher/wwwroot/js/upload.js` (a progress bar through `XMLHttpRequest.upload.onprogress`; it sends the antiforgery token in the `RequestVerificationToken` header)
- Modify: `Program.cs` (map `LibraryApi`; call `OarLibrary.ClearStaging()` once at start, before the host runs), `Pages/Regions/Index.cshtml` (an "OAR library" link beside the "Region types" heading)
- Test: `Launcher/DreamGrid.Launcher.Tests/LibraryPageTests.cs`

**Interfaces:**
- Consumes: `OarLibrary`, `OarFile.LooksLikeOar`, `IGridController.UpdateSettings`, `RegionTypes.All`.
- Produces:
  - `GET /regions/library`: the page.
  - `POST /regions/library/upload` (multipart, the file in a field named `oar`): redirects to `/regions/library` with a notice. When the `X-Requested-With: fetch` header is sent it answers JSON `{ ok, name, text }` instead.
  - `GET /regions/library/file/{name}`: the download (`application/octet-stream`, attachment, range requests on).
  - Page handlers: `OnPostDelete(string name, bool confirmed)`, `OnPostUseAsType(string name, string typeName)` and `OnPostRemoveType(string id)`.

The page lists each file with its name, size (`RegionAdvice.Size`) and date, and these actions:
- **Download.**
- **Delete.** It asks first: "Delete <name>? This can't be undone." When custom types use the file, the question adds "It is also the region type <names>, which goes too."
- **Use as a region type.** A small form takes a type name of 2 to 32 characters, unique ignoring case among all types. It saves `CustomRegionType("c-" + 8 hex, name, file)`.

A "Your region types" list shows each custom type with a **Remove from the list** button, and says "The file stays in the library." A short note says that very large files can also be copied straight into `<the library path>`, for example over SFTP (spec §5.5).

**Ruling (planning): the upload is a minimal API endpoint that reads the body with `MultipartReader`.** It turns off the 30 MB limit for this endpoint only (`IHttpMaxRequestBodySizeFeature.MaxRequestBodySize = null`), streams the `oar` section into `.incoming/<guid>.part`, checks it with `LooksLikeOar`, and moves it to `FreeName(CleanName(section file name))`. The antiforgery check must not make ASP.NET read the form, because that would buffer the whole file. With JavaScript, the token comes in the header. Without it, the form puts the token's hidden field before the file field; the endpoint reads that first section, copies its value into the `RequestVerificationToken` request header, and then calls `IAntiforgery.ValidateRequestAsync`. When that header is present, the antiforgery check reads only the header, never the form. *Cost if wrong:* a no-JavaScript upload fails its antiforgery check. `Upload_without_javascript_form_works` pins this.

- [ ] **Step 1: Write the failing tests** (using `LauncherFactory` with a temp data directory and `TestAuth`, as `RegionsPageTests` does)

```csharp
[Fact] public async Task Library_lists_files_with_size_and_date() { /* two OARs → two rows; "1.0 MB" shown */ }
[Fact] public async Task Library_needs_admin() { /* anonymous GET /regions/library → login redirect; GET file → 401 */ }
[Fact] public async Task Upload_with_header_token_saves_the_oar() { /* fetch-style post → JSON ok, file in library */ }
[Fact] public async Task Upload_without_javascript_form_works() { /* token field first, then file → redirect, notice "Uploaded castle.oar." */ }
[Fact] public async Task Upload_streams_without_buffering() { /* a 64 MB body through a stream that counts reads: the server never holds more than 1 MB at once (assert on peak bytes in flight via a custom HttpContent) and no Kestrel 413 */ }
[Fact] public async Task Cut_off_upload_leaves_no_file() { /* body stream throws halfway → library and .incoming are empty */ }
[Fact] public async Task Upload_of_non_oar_is_refused() { /* a text file → "That file isn't an OAR." and nothing saved */ }
[Fact] public async Task Upload_with_a_taken_name_gets_a_number() { /* castle.oar exists → castle-2.oar; notice names it */ }
[Theory] [InlineData("..%2Fsettings.json")] [InlineData(".hidden.oar")] [InlineData("x.txt")]
public async Task Library_endpoints_refuse_unsafe_names(string name) { /* download → 404; delete → 404; use-as-type → 404 */ }
[Fact] public async Task Delete_asks_first_and_names_its_types() { /* without confirmed → question with "region type My Castle"; with → file gone, type gone */ }
[Fact] public async Task Use_as_type_adds_it_and_refuses_a_taken_name() { /* "My Castle" → in CustomTypes; "meadow" (ignoring case) → "There is already a region type called Meadow." */ }
[Fact] public async Task Remove_type_keeps_the_file() { }
[Fact] public async Task Regions_page_links_the_library() { }
```

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*LibraryPageTests"`
Expected: FAIL (404 for `/regions/library`).

- [ ] **Step 3: Implement the page, the endpoints and `upload.js`.** Use the layout and Bootstrap patterns of `Pages/Regions/Move.cshtml`. Every file name shown goes through Razor encoding. The download's `Content-Disposition` uses the library name.

- [ ] **Step 4: Run the tests and see them pass**, then run the whole unit suite. Expected: all pass.

- [ ] **Step 5: Commit:** `Launcher: OAR library page with streamed upload, download, delete and custom types`

### Task 3: Custom types on the map and in the add form

**Files:**
- Modify: `Pages/Regions/Index.cshtml(.cs)` and `Pages/Regions/Add.cshtml(.cs)`: use `RegionTypes.All(grid.CurrentSettings())`
- Modify: `Endpoints/RegionsApi.cs`: `/regions/types/{id}.png` serves a built-in picture; a custom type has none (the page shows `wwwroot/img/custom-type.svg`, a plain generic swatch)
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/GridController.cs`:
  - `CreateRegionAsync` and `AddRegionAsync` validate content with `RegionTypes.IsContent(s, …)`.
  - `AddCoreAsync` saves `NeedsContent = true` and clears it after content loads.
  - `LoadContentCoreAsync` takes the settings and loads a custom type's file without `--displacement`.
- Test: `RegionsPageTests.cs`, `AddRegionPageTests.cs`, `GridControllerWiringTests.cs`

**Interfaces:**
- Consumes: Task 1's `RegionTypes.All`, `Find(s, id)`, `Oar(paths, s, id)`, `IsCustom`, `NeedsContent`.
- Produces: `LoadContentCoreAsync(RestConsoleClient console, RegionSettings region, string content, LauncherSettings s, CancellationToken ct)`. Task 4 uses it.

**Ruling (planning): only the built-in types go in the middle of a larger region.** A custom type, like an Import, loads at the OAR's own size and place, because it may be a larger region's OAR. *Cost if wrong:* a 256 m custom OAR in a 2×2 region sits in its south-west square instead of the middle.

**Ruling (planning): a custom type whose file has gone** (the file was deleted outside the page) **makes the Default Island's land.** The add form shows "The file castle.oar is no longer in the library; this region will start as plain island land." *Cost if wrong:* none worth noting.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Regions_page_lists_custom_types_after_built_in() { }
[Fact] public async Task Add_form_accepts_a_custom_type() { /* type=c-… preselected; posting it calls AddRegionAsync with Content "c-…" */ }
[Fact] public async Task Add_form_warns_when_custom_file_is_gone() { }
[Fact] public async Task Custom_type_loads_its_file_without_displacement() { /* wiring: size-2 region from c-… → console got load oar --force-terrain --force-parcels "<oars>/castle.oar", no --displacement */ }
[Fact] public async Task Added_region_needs_content_until_it_loads() { /* the console fails the load → saved region has NeedsContent true; a good add → false */ }
```

- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*RegionsPageTests" --filter-class "*AddRegionPageTests" --filter-method "*Custom_type*" --filter-method "*Needs_content*"`. Expected: FAIL.

- [ ] **Step 3: Implement.** Leave the setup checklist's `starter` path unchanged. `CreateRegionAsync` also sets and clears `NeedsContent`.

- [ ] **Step 4: Run the tests and see them pass**, then run the whole unit suite. Expected: all pass.

- [ ] **Step 5: Commit:** `Launcher: custom region types on the map and in the add form`

### Task 4: OAR jobs in the controller

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/IGridController.cs`, `GridController.cs`
- Create: `Launcher/DreamGrid.Launcher.Core/Grid/OarJob.cs` (the request and progress records)
- Modify: `Launcher/DreamGrid.Launcher.Tests/FakeGridController.cs`
- Test: `GridControllerWiringTests.cs`, `RegionAlertRoutingTests.cs`

**Interfaces:**
- Produces:
  - `public enum OarJobKind { Import, Save, MakeLand }`
  - `public sealed record OarJobRequest(Guid RegionId, OarJobKind Kind, string? File = null);`. `File` is the library name for Import; it is null for the other kinds.
  - `public sealed record OarJobProgress(string Stage, long? BytesWritten);`
  - On `IGridController`:
    - `Task<string?> RunOarJobAsync(OarJobRequest request, CancellationToken ct, IProgress<OarJobProgress>? progress = null);`. It returns the library name a Save wrote, and null otherwise. It throws `ArgumentException` (plain words) for a bad request, and `InvalidOperationException` for "Start it first", "already running" and a failure.
    - `string? BusyWith(string instance);`: the job text running in that program, or null.
- Consumes: Task 3's `LoadContentCoreAsync`; `OarLibrary`, `OarFile`; the region log `<logs>/region-<instance>.log`, the file `LogReader` serves as `region-<instance>`.

Behaviour:
- **Starting a job.** Under `_ops`, briefly:
  - The region must be live.
  - Its program must be running and have loaded it, and `_logins.Enabled(region)` must be true. Otherwise the job throws "Start <Region> first; an OAR needs it running."
  - The program must have no job already (otherwise "<job text> is still running. Try again when it finishes.").
  - The job is recorded in a `ConcurrentDictionary<string, string> _jobs` keyed by instance, then `_ops` is released.

  The rest runs under no global lock, so other programs and the world keep working. The finally block always removes `_jobs[instance]`.
- **Refusals while a job runs.** `RegionPowerAsync`, `MoveRegionAsync`, `ChangeRunsWithAsync` and `DeleteRegionAsync` (Task 6) refuse a region whose program, or target program, has a job, with the busy text. The `_jobs` check runs under `_ops`.
- **The console switch.** In a shared program, every job sends `change region <Name>` first and `change region root` last, like `LoadContentAsync`.
- **Import.** The job sends `load oar --force-terrain --force-parcels "<path>"` and waits for that region's `oarloaded` alert. The alert's `RegionId` must equal the region's.
  - **Ruling (planning):** `_oarLoaded` becomes a `ConcurrentDictionary<Guid, TaskCompletionSource<string>>` keyed by region, because two programs can load at once. *Cost if wrong:* none.
  - The job then clears `NeedsContent`. Progress stages: "Loading the OAR", then "Waiting for the region to accept logins".
- **Make the land again.** The job runs `LoadContentCoreAsync` with the region's `Content` (Ruling A), then clears `NeedsContent`.
- **Save.** The job notes the region log's length, then sends `save oar "<OarsSaving>/<guid>.oar"`. Then, about every 2 s:
  - It reports `BytesWritten` (the staging file's length).
  - It reads the log lines added since then.

  It succeeds on `[ARCHIVER]: Finished writing out OAR for <Name>` when no `[ARCHIVER]: Error` line came before it and `LooksLikeOar` is true. The file then moves to `FreeName("<Name>-<yyyy-MM-dd>.oar")`, with the date from the `TimeProvider`'s local date.

  It fails with "The region stopped before the save finished." when the program leaves `Running`. It fails with "The save did not finish; see the region log." when 2 hours pass without the finished line, or when an error line appears. On every failure the staging file is removed.

  **Ruling (planning):** OpenSim reports a finished save only in its log (`ArchiveWriteRequest.CloseArchive`); nothing under `OpenSim/` changes. *Cost if wrong:* a reworded log line in a future OpenSim breaks saves, and the integration test catches it.
- **Cancellation.** When the launcher stops, the job ends without waiting for OpenSim. An OpenSim save still running keeps writing into `.saving`, and the next start's `ClearStaging` removes the file.

- [ ] **Step 1: Write the failing tests** (wiring tests: the existing fake processes and the stub console of `GridControllerWiringTests`)

```csharp
[Fact] public async Task Import_switches_region_loads_and_waits_for_its_alert() { /* shared program: change region Beach, load oar --force-terrain --force-parcels ".../oars/castle.oar", change region root; completes on Beach's oarloaded, not Home's */ }
[Fact] public async Task Import_refuses_a_stopped_region() { /* "Start Beach first; an OAR needs it running." */ }
[Fact] public async Task Import_refuses_an_unsafe_or_missing_file() { /* ArgumentException "That file is not in the OAR library." */ }
[Fact] public async Task Second_job_in_same_program_is_refused() { }
[Fact] public async Task Jobs_in_two_programs_run_together() { }
[Fact] public async Task Power_action_refused_while_job_runs() { /* RegionPowerAsync(Stop) on a region sharing the busy program → "Saving Home as an OAR is still running. Try again when it finishes." */ }
[Fact] public async Task Save_moves_a_finished_oar_into_the_library() { /* stub writes the staging file (archive.xml first) and appends the finished line to region-Home.log → returns "Home-2026-10-28.oar" */ }
[Fact] public async Task Save_reports_bytes_written() { }
[Fact] public async Task Save_fails_cleanly_when_program_stops() { /* program state → Stopped mid-save → "The region stopped before the save finished."; .saving empty; BusyWith null */ }
[Fact] public async Task Save_fails_on_archiver_error_line() { }
[Fact] public async Task Make_land_again_loads_the_type_and_clears_needs_content() { }
```

- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*GridControllerWiringTests" --filter-class "*RegionAlertRoutingTests"`. Expected: build errors (`RunOarJobAsync` not defined).

- [ ] **Step 3: Implement it as described.** `FakeGridController` gains `RunOarJobAsync` (records the requests and returns a settable result) and `BusyWith`.

- [ ] **Step 4: Run the tests and see them pass**, then run the whole unit suite. Expected: all pass.

- [ ] **Step 5: Commit:** `Launcher: Import, Save as OAR and Make the land again as per-program jobs`

### Task 5: OAR jobs on the website

**Files:**
- Create: `Launcher/DreamGrid.Launcher/OarJobs.cs`
- Create: `Pages/Regions/Import.cshtml(.cs)` (route `/regions/region/{id}/import`) and `Pages/Regions/Job.cshtml(.cs)` (route `/regions/region/{id}/job`)
- Modify: `Pages/Regions/Region.cshtml(.cs)`: an "OAR" group under the power menu
- Modify: `Endpoints/RegionsApi.cs`: `GET /api/regions/{id}/job`
- Modify: `Program.cs`: register `OarJobs`
- Test: `Launcher/DreamGrid.Launcher.Tests/OarJobPagesTests.cs`, `RegionPanelTests.cs`

**Interfaces:**
- Consumes: Task 4's `RunOarJobAsync`, `BusyWith`, `OarJobRequest`, `OarJobProgress`; `BackgroundWork`; `OarLibrary.List`.
- Produces:
  - `public sealed record OarJobSnapshot(Guid RegionId, string Text, string Stage, long? BytesWritten, TimeSpan Elapsed, bool Running, string? Error, string? SavedAs);`
  - `public sealed class OarJobs(IGridController grid, BackgroundWork background, TimeProvider time, ILogger<OarJobs> log)` with:
    - `bool TryStart(OarJobRequest request, string text)`: false when that region's program already has a job.
    - `OarJobSnapshot? For(Guid regionId)`: the running job, or the last finished one until the next job in that program.
    - `void Then(Guid regionId, Func<CancellationToken, Task> after)`: used by Task 7's save-then-delete.
  - `GET /api/regions/{id}/job`: the snapshot as JSON, or 404.

The panel's "OAR" items:
- **Import OAR…** opens `/regions/region/{id}/import`. That page lists the library, with a radio for each file and a link to the library page when the list is empty. It asks: "Import <file> into <Region>? Its land and objects are replaced by the OAR's. This can't be undone; Save as OAR first if you may want them back." There is a "Yes, import" button, and a "Save as OAR first" link that starts a Save.
- **Save as OAR** starts at once (it replaces nothing).
- **Make the land again** shows only while `NeedsContent` is set: "Its land didn't finish loading when it was added."

While the region isn't running, the items are disabled and show "Start it first; an OAR needs it running." When `BusyWith` is set, the panel says what is running and links to the job page.

The job page shows the text, the stage, the elapsed time and, while saving, "<size> written so far". Without JavaScript it refreshes every 3 s; with JavaScript a few lines of script poll `/api/regions/{id}/job`. When the job finishes it shows "Done.", plus a download link for `SavedAs`, or the error. The map's side panel shows the same panel fragment, so the items appear there too.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Panel_shows_oar_items_for_a_running_region() { }
[Fact] public async Task Panel_disables_oar_items_when_stopped() { /* "Start it first; an OAR needs it running." */ }
[Fact] public async Task Panel_shows_make_land_again_only_when_needed() { }
[Fact] public async Task Import_page_asks_before_replacing() { /* GET lists files; POST without confirmed → question text; with → TryStart(Import, "castle.oar") */ }
[Fact] public async Task Import_page_with_empty_library_links_to_it() { }
[Fact] public async Task Save_starts_and_job_page_shows_progress() { /* fake reports BytesWritten 1048576 → "1.0 MB written so far"; meta refresh present */ }
[Fact] public async Task Finished_save_links_its_file() { }
[Fact] public async Task Failed_job_shows_its_reason() { }
[Fact] public async Task Second_job_in_busy_program_shows_the_running_one() { }
[Fact] public async Task Job_api_needs_admin() { /* anonymous → 401 */ }
```

- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*OarJobPagesTests" --filter-class "*RegionPanelTests"`. Expected: FAIL.

- [ ] **Step 3: Implement.** Follow `RegionJob`'s shape: background work, a lock-guarded snapshot, and refusals passed through as plain words (an `ArgumentException`'s message without its parameter suffix).

- [ ] **Step 4: Run the tests and see them pass**, then run the whole unit suite. Expected: all pass.

- [ ] **Step 5: Commit:** `Launcher: Import, Save as OAR and job progress in the region panel`

### Task 6: Delete, Restore and Delete forever in the controller

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Database/RegionStore.cs`
- Modify: `IGridController.cs`, `GridController.cs` (and `UniqueFolderName`, for Rulings B and C), `FakeGridController.cs`
- Test: `GridControllerWiringTests.cs`, `Launcher/DreamGrid.Launcher.IntegrationTests/RegionStoreTests.cs` (real MariaDB, like the existing database tests)

**Interfaces:**
- Produces:
  - `public interface IRegionStore`. Its methods: `Task<IReadOnlyDictionary<Guid, long>> SpaceAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)`, `Task DeregisterAsync(Guid id, CancellationToken ct)` and `Task EraseAsync(Guid id, CancellationToken ct)`. `RegionStore(LauncherSettings s)` implements it with `MariaDbServer.ConnectionString(s)`. The controller takes a `Func<LauncherSettings, IRegionStore>`, with a default, so tests can swap it.
  - On `IGridController`:
    - `Task DeleteRegionAsync(Guid regionId, CancellationToken ct);`
    - `Task RestoreRegionAsync(Guid regionId, CancellationToken ct);`
    - `Task EraseRegionAsync(Guid regionId, CancellationToken ct);`
    - `Task<IReadOnlyDictionary<Guid, long>?> DeletedSpaceAsync(CancellationToken ct);`: null while the database isn't running.
- Consumes: Task 4's `_jobs` refusal.

**Delete** (under `_ops`; instant and works even for a broken region, spec §5.6):
- It refuses only a busy program (Task 4).
- It saves `DeletedAt = now` and `Parked = false`.
- When the program has no live region left, the program leaves `Programs`; the deleted region keeps the program's folder name (Ruling B).
- When the program is active, has loaded the region and has other loadable regions, it restarts without the region (the others are logged). When nothing else in it is loadable, it stops; a removed program stops with `CancellationToken.None`, as in `ChangeRunsWithAsync`. A failed or stopped program is left alone.
- The config rewrite moves the region's `.ini` to `.ini.off` (the existing `GridConfigWriter` behaviour).
- Finally `DeregisterAsync` removes the region's row from Robust's `regions` table, so a region that crashed leaves the map too. It is skipped, with a log line, when the database isn't running.

**Restore:**
- It refuses a live region with "That region isn't deleted."
- It clears `DeletedAt`. When a program named by the region's `Instance` exists, the region goes back into it. Otherwise it gets a program of its own under that same folder name, with `HttpPort = Next(RegionPort)`, `ConsolePort = Next(RegionConsolePort, {HttpPort})` and `UdpPort = HttpPort`.
- **Ruling (planning):** a restored region comes back **stopped**, and keeps its old `Parked`. The page says "Start it when you're ready." Its Start (the power menu) asks before restarting a shared program. So Restore itself never restarts anything. *Cost if wrong:* the admin clicks Start after Restore.

**Delete forever:**
- It requires a deleted region and a running database (otherwise "Start the database first.").
- `EraseAsync` deletes in one transaction from the `opensim` database:
  - `primitems` and `primshapes` for the region's prims (`prims.RegionUUID`), then `prims`;
  - `terrain`, `bakedterrain`, `land` and the `landaccesslist` rows of its parcels;
  - `regionsettings`, `regionwindlight`, `regionenvironment`, `spawn_points`, `regionextra`, `regionban` and `estate_map`.

  It also deletes the region's row in `robust.regions`. Then the region leaves `Regions`, which frees its name, spot, ports and folder name. The folder itself stays (it holds the logs).

  The implementer checks each table's region column name against `OpenSim/Data/MySQL/Resources/RegionStore.migrations` and `EstateStore.migrations`. The names seen while planning: `RegionUUID`, `regionUUID`, `region_id`, `RegionID`.

**Space:** `SpaceAsync` estimates each region's bytes. It counts the region's rows in `prims`, `primshapes` and `primitems` and multiplies each count by that table's `AVG_ROW_LENGTH` from `information_schema.TABLES`, then adds the `LENGTH` of its `terrain` and `bakedterrain` blobs. **Ruling (planning):** an estimate is enough for "how much space it still holds". *Cost if wrong:* the number is off by the tables' overhead.

**Ruling C:** `UniqueFolderName` skips a name when any of these hold:
- a program has it;
- a deleted region's `Instance` is that name;
- `PidFile.ReadLive(<pids>/<name>.pid, <runtime>/OpenSim.dll)` names a live process.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Delete_alone_in_program_stops_and_removes_it() { /* DeletedAt set; program gone from Programs; region keeps Instance; .ini → .ini.off; DeregisterAsync called */ }
[Fact] public async Task Delete_in_shared_program_restarts_it_without_the_region() { }
[Fact] public async Task Delete_works_when_program_failed() { /* failed program, console down → completes, no restart attempted, DeregisterAsync called */ }
[Fact] public async Task Delete_refused_while_its_program_has_a_job() { }
[Fact] public async Task Deleted_region_reserves_its_folder_name() { /* add "Be ach" after deleting "Be_ach"'s region → new program folder "Be_ach_2" */ }
[Fact] public async Task Folder_of_a_live_removed_program_is_not_reused() { /* live PID file for "Beach" with no program → "Beach_2" */ }
[Fact] public async Task Restore_after_program_removed_recreates_its_folder() { /* program "Beach" re-added with fresh ports; region UdpPort = HttpPort; stopped */ }
[Fact] public async Task Restore_into_running_program_comes_back_stopped() { /* no restart, no stop sent to the shared program */ }
[Fact] public async Task Erase_needs_a_deleted_region_and_the_database() { }
[Fact] public async Task Erase_removes_the_region_and_frees_its_name() { /* EraseAsync called; the name can be added again */ }
// RegionStoreTests (integration, real MariaDB with the opensim and robust schemas created by OpenSim's migrations,
// or by a tiny seed of the same tables for the test)
[Fact] public async Task Erase_removes_only_that_regions_rows() { }
[Fact] public async Task Space_estimates_bytes_per_region() { }
[Fact] public async Task Deregister_removes_robust_row() { }
```

- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*GridControllerWiringTests"`. Expected: build errors (`DeleteRegionAsync` not defined).

- [ ] **Step 3: Implement it as described.** Reuse `ChangeRunsWithAsync`'s stop-or-restart steps. If they come out the same shape, pull them into one private helper.

- [ ] **Step 4: Run the tests and see them pass.** Then run the unit suite, and the integration tests in the background (they take about 13 minutes): `DREAMGRID_MARIADB_BIN=… DREAMGRID_REQUIRE_INTEGRATION=1 dotnet test --project Launcher/DreamGrid.Launcher.IntegrationTests --filter-class "*RegionStoreTests"`. Expected: all pass.

- [ ] **Step 5: Commit:** `Launcher: delete regions to Recently deleted, restore them, or delete them forever`

### Task 7: Delete and Recently deleted on the website

**Files:**
- Modify: `Pages/Regions/Region.cshtml(.cs)`: **Delete…** with its confirmation (the `Asking` pattern) and the "Also save it to the OAR library" checkbox
- Create: `Pages/Regions/Deleted.cshtml(.cs)` (route `/regions/deleted`)
- Modify: `Endpoints/RegionActions.cs`: `TryDelete`, `TryRestore` and `TryErase`, in the background like `TryMove`
- Modify: `Pages/Regions/Index.cshtml(.cs)`: a "Recently deleted (N)" link when N > 0
- Test: `Launcher/DreamGrid.Launcher.Tests/DeletePageTests.cs`

**Interfaces:**
- Consumes: Task 6's `DeleteRegionAsync`, `RestoreRegionAsync`, `EraseRegionAsync` and `DeletedSpaceAsync`; Task 5's `OarJobs.TryStart` and `Then`.

The Delete question:
- "Delete <Region>? It goes to Recently deleted, where you can restore it. Its land and objects are kept until you delete it forever."
- When other regions run with it, it adds "<Names> restart without it."
- The checkbox says "Also save it to the OAR library first (from seconds to many minutes; it needs the region running)". It is off by default, and disabled while the region isn't running.
- With the box ticked, the page starts a Save and then calls `Then(id, delete)`. The delete runs only when the save succeeds; a failed save leaves the region alone and shows why.

The Recently deleted page lists each region with:
- its name, map spot, size and when it was deleted;
- the space it holds ("about 12 MB", or "Unknown while the database is stopped");
- **Restore**, with the notice "<Region> is back, stopped. Start it when you're ready.";
- **Delete forever…**, which asks "Delete <Region> forever? Its land and objects are erased from the database. This can't be undone." and is refused in plain words while the database is stopped.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Delete_asks_and_names_regions_that_restart() { }
[Fact] public async Task Delete_confirmed_runs_and_goes_to_regions_page() { /* TryDelete called; notice "Beach is in Recently deleted." */ }
[Fact] public async Task Delete_with_save_first_waits_for_the_save() { /* fake save fails → DeleteRegionAsync never called; job page shows the reason */ }
[Fact] public async Task Save_first_box_disabled_when_not_running() { }
[Fact] public async Task Deleted_page_lists_with_space_and_date() { }
[Fact] public async Task Deleted_page_shows_unknown_space_when_db_stopped() { }
[Fact] public async Task Restore_brings_it_back_stopped() { }
[Fact] public async Task Erase_asks_first() { }
[Fact] public async Task Regions_page_links_recently_deleted_only_when_some() { }
[Fact] public async Task Deleted_region_panel_is_404() { /* the existing rule still holds */ }
```

- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests --filter-class "*DeletePageTests"`. Expected: FAIL.

- [ ] **Step 3: Implement.**

- [ ] **Step 4: Run the tests and see them pass**, then run the whole unit suite. Expected: all pass.

- [ ] **Step 5: Commit:** `Launcher: Delete and Recently deleted pages`

### Task 8: End-to-end on a real grid

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.IntegrationTests/GridBootTests.cs`, `LauncherEndToEndTests.cs`
- Test: those two files

**Interfaces:**
- Consumes: everything above; `GridTestSupport` (seeded ports in free blocks).

- [ ] **Step 1: Write the tests**

```csharp
// GridBootTests (in process, real OpenSim and MariaDB)
[Fact] public async Task Save_then_import_round_trips_a_region()
{   // boot; rez nothing (an empty region still saves); Save → library holds "<Name>-<date>.oar" that LooksLikeOar;
    // Import it back → completes on oarloaded; BusyWith null afterwards
}
[Fact] public async Task Delete_restore_and_erase_on_a_real_grid()
{   // add a second region on its own; Delete → its program stops, robust.regions has no row for it, opensim.terrain still has its rows;
    // Restore → Start → logins enabled again at the same spot; Delete + Erase → opensim.terrain has no rows for it; name reusable
}
// LauncherEndToEndTests (the real launcher over HTTP)
[Fact] public async Task Upload_import_and_download_through_the_website()
{   // log in; upload the Save_then_import OAR (or the starter OAR) through /regions/library/upload; import it into the region
    // through /regions/region/{id}/import; poll /api/regions/{id}/job until done; download it and compare bytes
}
```

- [ ] **Step 2: Run them in the background** (about 15 minutes): `DREAMGRID_MARIADB_BIN=C:\Users\ystud\AppData\Local\DreamGridDev\mariadb-11.4.9-winx64\bin DREAMGRID_REQUIRE_INTEGRATION=1 dotnet test --project Launcher/DreamGrid.Launcher.IntegrationTests`. Expected: all pass.

- [ ] **Step 3: Fix what the real grid shows.** Each fix is a red test in the unit suite first. Expected: the integration and unit suites all pass.

- [ ] **Step 4: Commit:** `Launcher: end-to-end tests for the OAR library, OAR jobs and Recently deleted`
