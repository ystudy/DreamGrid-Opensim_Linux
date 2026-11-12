# Inventory Backups (IAR) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Residents download their inventory as an IAR (only full-permission items, object contents included) and upload
an IAR into a new folder (taken-down content removed). Four switches and all limits are settings. The owner and admins
save and load any avatar's inventory with no limits, and every use is audited with a typed reason.

**Architecture:**
- **Archive work in Core, with no OpenSim code.** `IarArchive` reads and rewrites a gzip tar with
  `System.Formats.Tar`. `IarPermissionFilter` (download) and `IarBannedFilter` (upload) decide which inventory entries
  stay. A rewrite keeps only the assets still reachable from the kept items. OpenSim's own `--perm` is **not** used: the
  launcher's filter is the one rule, and it also produces the "left out" list.
- **OpenSim does the inventory part.** `GridController.RunIarJobAsync` runs `save iar` / `load iar` on a running region
  program's console and follows the region log for the end, the way `SaveOarAsync` does. It shares that program's job
  slot (`_jobs`).
- **Rules are pure.** `IarRules.Refusal(...)` decides whether a resident may start a job: switches, takedown-named,
  limits, disk. Stores keep state in `JsonStateFile`s.
- **The web project orchestrates.** `IarJobs` runs jobs in the background (save → filter → ready, or upload → check →
  clean → load), writes the audit log and sends the email. Pages only draw and post.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages and minimal APIs, `System.Formats.Tar`, `System.IO.Compression`,
MySqlConnector, xUnit v3, AngleSharp. Integration tests run against the real MariaDB, Robust and a region
(`DreamGrid.Launcher.IntegrationTests`, CI only).

**Spec:** `docs/superpowers/specs/2026-11-11-iar-backups-design.md`

## Global Constraints

**Access**
- Resident pages are under `/guest` (GuestPolicy, `GuestSideOnly`, `TermsGate`, the resident language). The resident's
  identity is `GuestSignIn.Read(User)!.Value.Principal`, never a form field.
- Staff IAR actions are for the **owner and admins only**: `StaffSignIn.RoleOf(User) is StaffRole.Owner or StaffRole.Admin`,
  otherwise 403, rechecked on every post. Supervisors never see the buttons. The settings page is under `/security`
  (owner only).
- Posts use the antiforgery token. CSP is unchanged: no inline script or style.
- IAR exists only where `AdminRule.HasStaffPages(s)` is true (the grid machine). Elsewhere every page and endpoint
  returns 404.

**Files**
- `LauncherPaths` gets `Iars` = `<Data>/iars`, with `IarsIncoming` (`.incoming`), `IarsSaving` (`.saving`),
  `IarsReady` (`ready`) and `IarsStaff` (`staff`). All are created with `LauncherPaths.CreateOwnerOnly` at start-up
  (Program.cs, next to Guests). `.incoming` and `.saving` are emptied at start-up.
- File names are `<guid N>.iar` only. Avatar names never appear in a path.

**Values** (spec §8; these are the defaults of `IarSettings`)
- `DownloadsOn` false, `UploadsOn` false
- `MaxUploadMb` 500, `UnpackFactor` 4, `MaxUploadItems` 20000, `MaxDownloadMb` 2048
- `DownloadsPerDay` 2, `UploadsPerDay` 2 (rolling 24 hours, per account)
- `JobsAtOnce` 1 (whole grid), `DownloadKbPerSecond` 2048, `LinkHours` 24, `MinFreeDiskGb` 5
- Per-resident overrides are allowed for `MaxUploadMb`, `MaxDownloadMb`, `DownloadsPerDay`, `UploadsPerDay` and
  `DownloadKbPerSecond`. A null override means "use the grid's".
- Full permission is `Copy | Modify | Transfer` = `0x8000 | 0x4000 | 0x2000` in an item's `CurrentPermissions`.
- The upload folder is named `Imported yyyy-MM-dd` (UTC), then `Imported yyyy-MM-dd (2)`, `(3)`, and so on if the name
  is taken in My Inventory.
- A staff reason is required: trimmed, at least 10 characters, at most 1000. The case number is optional and must match
  `TD-\d{4}-\d{3,}` when given.

**Audit** (`AuditLog`, targets through `LogText.Clean(..., 140)`)
- Resident (role `guest`): `iar-download-asked`, `iar-download-ready`, `iar-download-fetched`, `iar-upload-asked`,
  `iar-upload-loaded`, `iar-refused` (target `First Last: <reason key>`), `iar-failed`, `iar-banned-removed` (target
  `First Last: N items`).
- Staff: `iar-staff-save`, `iar-staff-load`, `iar-staff-fetched`, `iar-staff-deleted`, `iar-rights-changed`,
  `iar-settings`. Each has a short target `Actor -> First Last` **and** a full detail record (Task 9).
- Free text goes only into the detail record, never into the one-line target.

**Words**
- Every resident-facing string is a `ResidentText` key with entries in all six `.resx` files (`en` source, `de`, `es`,
  `fr`, `pt`, `ru`). Staff pages are English, as the other staff pages are.
- New email template `iar-ready`, added to `EmailTemplates.All`, in the six languages.

**Rulings made while planning** (the spec leaves them open)
- **Counting.** A day's count is used when a job **starts**. It is given back when the job fails for a grid reason (no
  region, region stopped, launcher restart). It is kept when the job fails because of the file (not an IAR, too big
  unpacked, too many items). This stops junk being uploaded over and over.
- **Download size.** `MaxDownloadMb` is checked against the finished, filtered file. A bigger one is deleted, and the
  resident is told to ask staff.
- **Folder for a load.** OpenSim's `load iar` needs the target folder to exist
  (`InventoryArchiveReadRequest`: "Inventory path {0} does not exist"). The launcher creates it first with
  `IInventoryStore.CreateFolderAsync`, then loads into that path.
- **Which program.** The running region program on this machine whose `BusyWith` is null, with the fewest regions loaded.
  Ties go to the lowest instance name.
- **Download link.** `/guest/inventory/file/{token}`, where the token is 32 random bytes in base64url. It's valid while
  the file exists, the token matches, the signed-in principal is the file's owner, the session stamp equals the stamp
  stored with the file, and the link hasn't expired.

## Review Focus

1. **Double click on "Back up".** Two posts arrive together: one job starts and one count is used. →
   `IarJobsTests.Two_starts_at_once_make_one_job_and_one_count` (Task 8).
2. **An inventory where everything is left out.** The download is still offered (an IAR with no items), and the list
   shows every item. → `IarPermissionFilterTests.All_left_out_still_writes_a_valid_archive` (Task 3).
3. **Resident suspended after the job finished.** The link no longer works, and the file is swept at expiry. →
   `IarDownloadTests.Link_refused_after_stamp_changes` (Task 10).
4. **"Imported 2026-11-11" already exists from an earlier upload that day.** The second goes into `(2)`. →
   `IarJobsTests.Second_upload_same_day_goes_to_numbered_folder` (Task 8).
5. **A small file that unpacks huge (a gzip bomb).** It's refused while being read, before the disk fills. →
   `IarArchiveTests.Inspect_stops_at_unpacked_limit` (Task 3).

---

### Task 1: Settings, the owner's page, and the files folder

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs` (new `IarSettings` record and `Iar` property,
  null-safe like `Bots`)
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/SettingsValidator.cs` (`Iar`)
- Modify: `Launcher/DreamGrid.Launcher.Core/LauncherPaths.cs` (`Iars`, `IarsIncoming`, `IarsSaving`, `IarsReady`, `IarsStaff`)
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (owner-only folders, staging cleared at start-up)
- Create: `Launcher/DreamGrid.Launcher/Pages/Security/Iar.cshtml(.cs)` (`/security/iar`, "Inventory backups")
- Modify: `Launcher/DreamGrid.Launcher/Navigation/StaffMenu.cs` (owner entry)
- Test: `Launcher/DreamGrid.Launcher.Tests/IarSettingsTests.cs`, `IarSettingsPageTests.cs`

**Interfaces:**
- Produces: `public sealed record IarSettings(bool DownloadsOn = false, bool UploadsOn = false, int MaxUploadMb = 500, int UnpackFactor = 4, int MaxUploadItems = 20000, int MaxDownloadMb = 2048, int DownloadsPerDay = 2, int UploadsPerDay = 2, int JobsAtOnce = 1, int DownloadKbPerSecond = 2048, int LinkHours = 24, int MinFreeDiskGb = 5)`;
  `LauncherSettings.Iar`; `SettingsValidator.Iar(IarSettings) -> IReadOnlyList<string>`.

- [ ] **Step 1: Write the failing tests.**
  - `Defaults_match_the_spec`: `new LauncherSettings().Iar` equals the values in Global Constraints.
  - `Old_settings_file_without_Iar_loads_defaults`: JSON without `iar` loads as `new IarSettings()`.
  - `Validator_refuses_out_of_range`: `MaxUploadMb` 0 or above 102400, `UnpackFactor` below 1 or above 20,
    `JobsAtOnce` below 1 or above 8, `LinkHours` below 1 or above 168, any per-day value below 0 or above 100,
    `DownloadKbPerSecond` below 64 → one message each.
  - `Page_saves_and_audits_each_change`: posting new values saves them through `UpdateSettings(..., writeConfig: false)`
    and writes one `iar-settings` audit line per changed field (`"MaxUploadMb 500 -> 800"`).
  - `Page_is_owner_only`: an admin gets 403.
  - `Iar_folders_are_owner_only_and_staging_is_cleared`: after start-up, `.incoming` and `.saving` are empty, and all
    five folders exist.
- [ ] **Step 2: Run them and confirm they fail.** `dotnet test Launcher/DreamGrid.Launcher.Tests --filter "FullyQualifiedName~IarSettings"`
- [ ] **Step 3: Implement.** Copy the `Bots` pattern for the record, the property, the validator and the page
  (`Pages/Security/Bots.cshtml.cs`). The page shows the two switches first, with the note "Set the limits before turning
  these on."
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit.** `iar: settings, owner page and folders (IAR task 1)`

### Task 2: Per-resident rights and limits

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Guests/IarRights.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Security/PeopleRules.cs` (`PersonTask.IarDownload`, `IarUpload`,
  `IarLimits`, and their refusal rule)
- Modify: `Launcher/DreamGrid.Launcher/Security/PeopleActions.cs` (cases and handlers)
- Modify: `Launcher/DreamGrid.Launcher/Pages/People/Person.cshtml(.cs)` (an "Inventory backups" panel)
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (DI: `<Guests>/iar-rights.json`)
- Test: `Launcher/DreamGrid.Launcher.Tests/IarRightsTests.cs`, `PersonIarTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record IarPersonRights(Guid Person, bool DownloadOff, bool UploadOff, int? MaxUploadMb, int? MaxDownloadMb, int? DownloadsPerDay, int? UploadsPerDay, int? DownloadKbPerSecond)`
  - `public sealed class IarRights { IarPersonRights For(Guid person); void Set(IarPersonRights rights); }`.
    `For` returns all-allowed, no-override rights for an unknown person.
  - `public static IarSettings Effective(IarSettings grid, IarPersonRights person)`: the grid's values with the
    person's overrides applied. It's in `IarRights`.

- [ ] **Step 1: Write the failing tests.**
  - `Unknown_person_is_allowed_with_grid_limits`.
  - `Override_replaces_only_its_own_value`: `UploadsPerDay = 10` changes that value alone in `Effective`.
  - `Damaged_file_means_defaults_not_everyone_blocked`: a corrupt file loads as empty (all allowed, grid limits), and
    `JsonStateFile` moves it aside.
  - `Owner_and_admin_can_set_supervisor_cannot`: a supervisor posting `IarDownload` gets the `PeopleRules` refusal.
  - `Rights_change_is_audited`: `iar-rights-changed`, target `"Ann Lee: download off -> on"` or
    `"Ann Lee: UploadsPerDay grid -> 10"`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement** on the `UploadRights` pattern (`Guests/UploadRights.cs`) and the `UploadRefusal` rule (the
  owner may set these for anyone; an admin for residents and for themself). The panel shows the two switches and the
  five overrides, each with "Use the grid's (N)".
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit.** `iar: per-resident switches and limits (IAR task 2)`

### Task 3: Reading and rewriting an IAR

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Iar/IarArchive.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/IarArchiveTests.cs`, plus a small test helper `IarBuilder` that makes
  archives in memory

**Interfaces:**
- Produces:
  - `public sealed record IarItem(string EntryPath, Guid ItemId, string Name, string FolderPath, Guid AssetId, int AssetType, int InvType, uint CurrentPermissions)`.
    It's parsed from each `inventory/**.xml` item entry (OpenSim's `InventoryItemBase` XML: `<ID>`, `<Name>`,
    `<AssetID>`, `<AssetType>`, `<InvType>`, `<CurrentPermissions>`). `FolderPath` is the folder names from the entry
    path, with the `__<uuid>` suffixes removed.
  - `public sealed record IarContents(IReadOnlyList<IarItem> Items, IReadOnlyDictionary<Guid, string> AssetEntries, long UnpackedBytes)`
  - `public static class IarArchive`:
    - `bool LooksLikeIar(string path)`: gzip tar, first entry `archive.xml`.
    - `IarContents Inspect(string path, long maxUnpackedBytes, int maxItems)`: throws `IarTooBigException` (with
      `Limit` = "unpacked" or "items") as soon as a limit is passed.
    - `Stream OpenAsset(string path, Guid assetId)`: the asset entry's bytes.
    - `void Rewrite(string source, string target, ISet<string> dropItemEntries)`: copies everything except the dropped
      item entries, and keeps an `assets/` entry only when it's reachable (below).

**Reachable assets** (the one algorithm the signatures don't fix): start from the `AssetId` of every kept item.
Repeatedly, for each reached asset whose type is text-based (object 6, notecard 7, clothing 5, bodypart 13, gesture 21,
material 57, settings 56, LSL text 10), scan its bytes as UTF-8 for UUIDs (`[0-9a-f]{8}-…-[0-9a-f]{12}`, any case), and
add each one that names an `assets/` entry. Every other asset entry is left out. `archive.xml` and every non-item,
non-asset entry are copied unchanged. Write in `TarEntryFormat.Gnu` so long names use `././@LongLink`, which OpenSim's
`TarArchiveReader` reads.

- [ ] **Step 1: Write the failing tests.**
  - `LooksLikeIar_true_for_iar_false_for_oar_text_and_empty`. An OAR also starts with `archive.xml`, but it has no
    `inventory/` entries, so `Inspect` reports 0 items. The upload check (Task 8) refuses an archive with 0 items and 0
    folders as "not an inventory archive".
  - `Inspect_reads_items_folders_and_permissions`.
  - `Inspect_stops_at_unpacked_limit`: a 1 MB entry of zeros with `maxUnpackedBytes` 512 KB throws
    `IarTooBigException { Limit: "unpacked" }`, having read at most about 512 KB.
  - `Inspect_stops_at_item_limit`.
  - `Rewrite_drops_item_and_its_only_asset`.
  - `Rewrite_keeps_asset_still_used_by_a_kept_item`: two items share an asset, one is dropped, and the asset stays.
  - `Rewrite_drops_texture_reached_only_through_dropped_object`: a dropped object's XML names a texture, and the
    texture is gone.
  - `Rewrite_keeps_texture_reached_through_kept_notecard`.
  - `Rewrite_round_trips_long_names`: a 150-character folder name survives a rewrite and an `Inspect`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement `IarArchive`.** Stream entry by entry, and never hold the whole archive in memory.
  `UnpackedBytes` counts the bytes actually read from the gzip stream.
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit.** `iar: read and rewrite inventory archives (IAR task 3)`

### Task 4: The full-rights rule for downloads

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Iar/IarPermissionFilter.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/IarPermissionFilterTests.cs`

**Interfaces:**
- Consumes: `IarArchive` (Task 3).
- Produces:
  - `public enum LeftOutReason { NoCopy, NoModify, NoTransfer, ContentsNotFullRights, PartsNotFullRights }`
  - `public sealed record LeftOutItem(string Name, string FolderPath, LeftOutReason Reason)`
  - `public static class IarPermissionFilter { IReadOnlyList<LeftOutItem> Apply(string source, string target); }`:
    rewrites `source` into `target` with every item that isn't full rights left out, and returns the list.

**Rule:** an item stays only when its `CurrentPermissions` has Copy, Modify and Transfer. For an object item
(`AssetType` 6), the object asset must also pass. Every `<SceneObjectPart>` (root and children, in both a single
`<SceneObjectGroup>` and a coalesced `<CoalescedObject>`) must have `<OwnerMask>` with all three bits. Every
`<TaskInventoryItem>` in every part must have `<CurrentPermissions>` with all three bits. A part that fails gives
`PartsNotFullRights`; contents that fail give `ContentsNotFullRights`. Check the first failing bit in the order Copy,
Modify, Transfer for the item's own reason. An object asset that's missing or can't be parsed is left out as
`PartsNotFullRights` (fail closed).

- [ ] **Step 1: Write the failing tests.**
  - `Full_rights_item_stays`.
  - `No_transfer_item_left_out_with_reason`, plus the same for no-copy and no-modify.
  - `Full_rights_box_with_no_transfer_contents_left_out`: the spec's anti-goal, pinned.
  - `Full_rights_linkset_with_no_copy_child_prim_left_out`.
  - `Coalesced_object_with_one_restricted_object_left_out`.
  - `Unparsable_object_left_out`.
  - `All_left_out_still_writes_a_valid_archive`: `LooksLikeIar(target)` is true and `Inspect` shows 0 items.
  - `Assets_of_left_out_items_are_not_in_the_file`: the texture of a left-out no-transfer texture item is gone.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.** Use `XmlReader` with DTD processing off and `XmlResolver` null, and read only the elements
  named above.
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit.** `iar: only full-rights items leave the grid, object contents included (IAR task 4)`

### Task 5: Banned content on upload, and notes on closed cases

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Iar/IarBannedFilter.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Security/TakedownStore.cs` (`BannedAssets`, `AppendUploadMatch`)
- Modify: `Launcher/DreamGrid.Launcher.Core/Security/TakedownCase.cs` (event kind `UploadMatch`, and
  `MatchedSinceClosed`)
- Modify: `Launcher/DreamGrid.Launcher/Pages/People/Takedowns.cshtml` (the case list marks "match since closed")
- Test: `Launcher/DreamGrid.Launcher.Tests/IarBannedFilterTests.cs`, `TakedownStoreUploadMatchTests.cs`

**Interfaces:**
- Consumes: `IarArchive` (Task 3), `IInventoryStore.AssetHashAsync`.
- Produces:
  - `public sealed record BannedAsset(Guid AssetId, string? Hash, string CaseNumber)`
  - `TakedownStore.BannedAssets(IInventoryStore inv, CancellationToken ct) -> Task<IReadOnlyList<BannedAsset>>`: every
    `CaseItem` in **every** case, open or closed, with `PutBackAt` null. `Hash` is the FSAssets hash (lowercase hex
    SHA-256 of the raw data, as `Util.SHA256Hash`), read once and cached per asset ID.
  - `TakedownStore.AppendUploadMatch(string caseNumber, Guid uploader, string uploaderName, int items, DateTimeOffset at)`:
    the **only** write allowed on a closed case. It adds an `UploadMatch` event and never reopens the case.
  - `public sealed record BannedMatch(string ItemName, string CaseNumber)`
  - `public static class IarBannedFilter { IReadOnlyList<BannedMatch> Find(string iar, IReadOnlyList<BannedAsset> banned); void Remove(string source, string target, IReadOnlyList<BannedMatch> matches); }`:
    an item matches when its own asset is banned (by ID, or by the hash of its bytes in the archive), or when it's an
    object whose reachable assets (Task 3's walk) include a banned one.
- Also: `IarBannedFilter.Find` refuses (throws `TakedownsUnreadableException`) when `TakedownStore.Unreadable` is true,
  so uploads fail closed.

- [ ] **Step 1: Write the failing tests.**
  - `Match_by_asset_id`, and `Match_by_content_hash_with_a_new_id`: the same bytes under another UUID still match.
  - `Object_holding_a_banned_texture_matches`.
  - `Put_back_item_is_not_banned`.
  - `Closed_case_items_are_banned`.
  - `Upload_match_on_closed_case_adds_event_and_stays_closed`, and the case list shows "match since closed".
  - `Other_changes_to_closed_case_still_refused`: `TakedownActions` refuses as before.
  - `Unreadable_takedowns_refuse_upload_check`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit.** `iar: taken-down content can't come back in an upload (IAR task 5)`

### Task 6: Running save iar and load iar on a region

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Grid/IarJob.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/IGridController.cs`, `GridController.cs` (new `// ---- IAR jobs` region
  beside the OAR jobs)
- Modify: `Launcher/DreamGrid.Launcher.Core/Database/InventoryStore.cs` (`CreateFolderAsync`, `FreeFolderNameAsync`)
- Test: `Launcher/DreamGrid.Launcher.Tests/GridControllerIarTests.cs` (fake programs and logs, as the OAR tests do),
  `Launcher/DreamGrid.Launcher.IntegrationTests/IarEndToEndTests.cs`

**Interfaces:**
- Produces:
  - `public enum IarJobKind { Save, Load }`
  - `public sealed record IarJobRequest(Guid Principal, IarJobKind Kind, string File, string? Folder = null)`: `File` is
    a full path under `Iars`; `Folder` is the target folder name for a load, directly under My Inventory.
  - `public sealed record IarJobProgress(string Stage, long? BytesWritten)`
  - `IGridController.RunIarJobAsync(IarJobRequest request, CancellationToken ct, IProgress<IarJobProgress>? progress = null) -> Task<int>`:
    returns the item count from the log line. It throws `InvalidOperationException` in plain words for "no region is
    running" (`IarJobErrors.NoRegion`), "the region stopped", and a failed save or load.
  - `IInventoryStore.CreateFolderAsync(Guid owner, string name, CancellationToken ct) -> Task<Guid>` (under My
    Inventory, type -1, version 1); `FreeFolderNameAsync(Guid owner, string baseName, CancellationToken ct) -> Task<string>`.
- `File` must resolve inside `paths.Iars`; anything else throws `ArgumentException`.

**Commands and log lines** (`InventoryArchiverModule.cs`):
- Save: `save iar --home=<GridInfo HG address> First Last / "<file>"` (no `--perm`). It's done at
  `[INVENTORY ARCHIVER]: Saved archive with {n} items for {First} {Last}`, and failed at
  `[INVENTORY ARCHIVER]: Archive save for {First} {Last} failed`. Progress is the staging file's growing size.
- Load: `load iar First Last "<Folder>" "<file>"`. It's done at `[INVENTORY ARCHIVER]: Loaded {n} items from archive`,
  and failed at `Archive load for {First} {Last} failed` or `Inventory path {Folder} does not exist`.
- The avatar's name comes from Robust by principal. It's checked with `GridConsoleCommands.ValidateAvatarName` before it
  goes on a console line. Paths go through `Fwd`. A folder name may only contain characters from
  `Imported yyyy-MM-dd (n)` and staff-typed names checked to `[\p{L}\p{N} ._()-]{1,64}`.
- The program is chosen as in the Rulings. It takes `_jobs[instance]` with the text `"Inventory backup for First Last"`,
  so OAR jobs on that program are refused meanwhile, and the reverse. `OnRegionConsoleAsync` is used so the command
  runs in a region's context.
- Use the OAR waits: 2 hours at most, and 60 s for the save to start.

- [ ] **Step 1: Write the failing tests.**
  - `Save_sends_command_and_returns_count_from_log`.
  - `Save_failure_line_throws_and_removes_file`.
  - `Load_creates_folder_first_then_loads`.
  - `No_running_region_throws_NoRegion`.
  - `Takes_the_program_slot_and_oar_job_is_refused_meanwhile`.
  - `Picks_idle_program_with_fewest_regions`.
  - `File_outside_iars_folder_is_refused`.
  - `FreeFolderName_adds_numbers`: `Imported 2026-11-11` taken → `Imported 2026-11-11 (2)`; both taken → `(3)`.
  - Integration, CI: `Save_then_load_round_trips_an_item` makes a test avatar with one full-perm notecard, saves, loads
    into a second avatar, and finds the notecard in `Imported …`. `Launcher_rewritten_iar_loads_in_opensim` puts the save
    through `IarPermissionFilter`, and OpenSim then loads the result.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement** on the `SaveOarAsync` / `ImportOarAsync` pattern, with `LogFollower`.
- [ ] **Step 4: Run the tests again.** Expected: unit tests pass; the integration tests pass in CI.
- [ ] **Step 5: Commit.** `iar: save and load inventory archives on a region (IAR task 6)`

### Task 7: The resident rules, daily counts and download files

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Iar/IarRules.cs`, `IarCounts.cs`, `IarFiles.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/IarRulesTests.cs`, `IarCountsTests.cs`, `IarFilesTests.cs`

**Interfaces:**
- Produces:
  - `public enum IarRefusal { GridOff, PersonOff, NamedInCase, DailyLimit, Busy, GridBusy, LowDisk, NoRegion, TakedownsUnreadable }`
  - `public static class IarRules { IarRefusal? Refusal(IarJobKind kind, IarSettings effective, bool gridOn, bool personOn, bool namedInOpenCase, int usedToday, bool personHasJob, int jobsRunning, long freeDiskBytes, bool takedownsReadable); }`.
    Checks run in the enum's order. `effective` comes from `IarRights.Effective`. `TakedownsUnreadable` applies to
    `Load` only, so downloads go ahead (spec 5.1). `NoRegion` is never returned here: it comes from the controller when
    the job runs, and is shown with the same text.
  - `TakedownStore.NamedInOpenCase(Guid principal) -> bool`: only `PersonOnCase.Named` on an open case, never copy
    holders, and never true just because the store is unreadable.
  - `public sealed class IarCounts { int UsedToday(Guid p, IarJobKind k); void Use(Guid p, IarJobKind k); void GiveBack(Guid p, IarJobKind k); }`:
    a rolling 24 hours from `TimeProvider`, kept in `<Security>/iar-counts.json` with principals as SHA-256 keys (as
    `ResetLimits`).
  - `public sealed record IarReadyFile(Guid Owner, string File, string Token, string Stamp, DateTimeOffset Expires, IReadOnlyList<LeftOutItem> LeftOut, int Items, long Bytes)`
  - `public sealed class IarFiles { IarReadyFile Publish(Guid owner, string stamp, string file, TimeSpan life, IReadOnlyList<LeftOutItem> leftOut, int items); IarReadyFile? Find(Guid owner); IarReadyFile? ForDownload(string token, Guid principal, string stamp); void Sweep(); }`.
    One ready file per owner: `Publish` deletes the previous one. The state is in `<Guests>/iar-ready.json`, and the files
    are in `IarsReady`. `Sweep` deletes expired files, runs at start-up and every 10 minutes (`BackgroundWork`), and
    never touches `IarsStaff`.
  - Each refusal has a resident text key `Iar.Refused.<Name>` (Task 10). `DailyLimit` gets the number:
    "You can back up {0} times a day. Try again tomorrow."

- [ ] **Step 1: Write the failing tests.**
  - One `Refusal_*` test per enum value, plus `Allowed_when_all_pass`.
  - `Order_grid_off_wins_over_daily_limit`.
  - `Copy_holder_is_not_named`.
  - `Counts_roll_after_24_hours`, `GiveBack_restores_one`.
  - `Publish_replaces_previous_file`.
  - `ForDownload_refuses_other_principal_wrong_stamp_expired_and_unknown_token`.
  - `Sweep_removes_expired_and_keeps_staff_files`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.** The token comes from `RandomNumberGenerator.GetBytes(32)` and is compared with
  `CryptographicOperations.FixedTimeEquals`.
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit.** `iar: rules, daily counts and download files (IAR task 7)`

### Task 8: Running resident jobs in the background

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Iar/IarJobs.cs`
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (DI), `Launcher/DreamGrid.Launcher.Core/Email/EmailTemplates.cs`
  (`iar-ready`)
- Test: `Launcher/DreamGrid.Launcher.Tests/IarJobsTests.cs`

**Interfaces:**
- Consumes: Tasks 2–7.
- Produces:
  - `public sealed record IarJobSnapshot(Guid Principal, IarJobKind Kind, string Stage, long? Bytes, bool Running, string? ErrorKey, int? Items, IReadOnlyList<LeftOutItem> LeftOut, int BannedRemoved, string? Folder)`
  - `IarJobs.TryStartDownload(Guid principal, string name, string stamp, IPAddress? ip) -> IarRefusal?`
  - `IarJobs.TryStartUpload(Guid principal, string name, string stagedFile, IPAddress? ip) -> IarRefusal?`
  - `IarJobs.For(Guid principal) -> IarJobSnapshot?`
  - `IarJobs.RunStaffAsync(...)` (Task 9 adds it).

**Download flow:** check the rules → `Use` the count → `RunIarJobAsync(Save)` into `IarsSaving` →
`IarPermissionFilter.Apply` into a second staging file → delete the unfiltered file at once → check `MaxDownloadMb` →
`IarFiles.Publish` → audit `iar-download-ready` → email `iar-ready` if email is set up.

**Upload flow:** check the rules → `Use` the count → `IarArchive.Inspect` with `MaxUploadMb × UnpackFactor` and
`MaxUploadItems` → refuse 0 items as "not an inventory archive" → `IarBannedFilter.Find`, then `Remove` if there are
matches (`AppendUploadMatch` for each case, and audit `iar-banned-removed`) → `FreeFolderNameAsync` →
`RunIarJobAsync(Load)` → audit `iar-upload-loaded` → delete every staging file.

Count give-back follows the Rulings. The grid-wide cap is `JobsAtOnce`, and one job per resident. Starting is under one
lock, so a double click makes one job. Jobs run on `BackgroundWork` with `UseStaffCulture()`. The snapshot keeps text
keys, not words, so the page renders them in the resident's language.

- [ ] **Step 1: Write the failing tests** (fake controller, in-memory stores).
  - `Two_starts_at_once_make_one_job_and_one_count`.
  - `Download_filters_then_publishes_and_removes_unfiltered_file`.
  - `Download_too_big_after_filtering_is_refused_and_deleted`.
  - `Grid_failure_gives_the_count_back`, `Bad_file_keeps_the_count`.
  - `Upload_with_banned_item_removes_it_notes_case_and_loads_the_rest`.
  - `Upload_not_an_inventory_archive_refused`.
  - `Second_upload_same_day_goes_to_numbered_folder`.
  - `Staging_files_deleted_after_success_and_failure`.
  - `Ready_email_sent_only_when_email_is_set_up`.
  - `Jobs_at_once_cap_refuses_GridBusy`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit.** `iar: resident backup and upload jobs (IAR task 8)`

### Task 9: The owner's and admins' side

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Iar/IarStaffRecords.cs`
- Create: `Launcher/DreamGrid.Launcher/Pages/People/Iar.cshtml(.cs)` (`/people/{id}/iar`)
- Create: `Launcher/DreamGrid.Launcher/Endpoints/IarStaffApi.cs` (streamed upload with no cap; download of a staff file)
- Modify: `Launcher/DreamGrid.Launcher/Iar/IarJobs.cs` (`RunStaffAsync`), `Launcher/DreamGrid.Launcher/Security/AccountHolds.cs`
  (a staff-evidence reason), `Launcher/DreamGrid.Launcher/Pages/People/Person.cshtml` (link to the page, and the history
  shows these entries)
- Test: `Launcher/DreamGrid.Launcher.Tests/IarStaffTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record IarStaffRecord(string Id, string Action, string Actor, Guid ActorId, string Role, string? Ip, DateTimeOffset At, Guid Subject, string SubjectName, string Reason, string? CaseNumber, string? File, long? Bytes, string? Sha256, int? Items, int? Folders, string? Folder, IReadOnlyList<string> BannedMatches, string Result)`
  - `IarStaffRecords.Add(IarStaffRecord r)`, `For(Guid subject)`, `Files(Guid subject)`. Records are kept in
    `<Data>/iars/staff/records.jsonl`, append-only, and never deleted.
  - `IarJobs.RunStaffAsync((string Name, Guid Id, string Role) actor, IPAddress? ip, Guid subject, IarJobKind kind, string reason, string? caseNumber, string? file, string? folder) -> Task<IarStaffRecord>`

**Rules:** no `IarRules` checks, no counts, no permission filter, no banned removal, and the resident's switches are
ignored. But `JobsAtOnce` and the program's job slot still apply: those protect the grid, they aren't limits on the
person. On a staff load, banned matches are found and shown on a confirm step ("These items match taken-down content:
…; load anyway?"). They are written into the record, not removed. A staff save stays in `IarsStaff` as `<guid>.iar` with
its SHA-256. A save that names a case adds an `AccountHolds` reason, "kept inventory evidence (TD-…)", that stops delete
only. Deleting a staff file needs its own reason and writes a record. The hold goes when the file is gone.

**Detail:** one `IarStaffRecord` for each of: asked, started, finished or failed, fetched, deleted. Each also writes a
one-line `AuditEntry` (`iar-staff-save` and so on, target `Actor -> First Last`, result). The `/people/{id}/iar` page
lists the records in full, newest first.

**Upload with no cap:** `IarStaffApi` sets `MaxRequestBodySize = null` for this endpoint only, reads with
`MultipartReader`, validates antiforgery first (as `LibraryApi.UploadAsync`), streams into `IarsIncoming`, and requires
`LooksLikeIar`. It still refuses when the disk would drop below `MinFreeDiskGb`. The page also offers "pick a kept staff
save" from `Files(subject)` and from any subject's files (for moving evidence).

- [ ] **Step 1: Write the failing tests.**
  - `Supervisor_gets_403_on_page_and_endpoints`.
  - `Reason_required_and_case_number_checked`.
  - `Staff_save_ignores_switches_limits_and_permissions`: the person is switched off, the item is no-transfer, and it's
    in the file.
  - `Staff_save_with_case_holds_delete_but_not_backups`.
  - `Staff_load_shows_banned_matches_and_loads_them_when_confirmed`.
  - `Every_step_writes_a_full_record_and_an_audit_line`.
  - `Staff_upload_has_no_size_cap_but_needs_an_iar`.
  - `Staff_file_delete_needs_reason_and_lifts_hold`.
  - `Sweep_never_deletes_staff_files`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit.** `iar: owner and admin save and load, fully recorded (IAR task 9)`

### Task 10: The resident's page, upload and download

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Guest/Inventory.cshtml(.cs)` (`/guest/inventory`)
- Create: `Launcher/DreamGrid.Launcher/Endpoints/IarGuestApi.cs` (`POST /guest/inventory/upload`,
  `GET /guest/inventory/file/{token}`, `GET /guest/inventory/status`)
- Create: `Launcher/DreamGrid.Launcher/Iar/ThrottledStream.cs`
- Create: `Launcher/DreamGrid.Launcher/Iar/IarPagesOnly.cs` (404 when both switches are off for this person, as
  `UploadPagesOnly`)
- Modify: `Launcher/DreamGrid.Launcher/Pages/Guest/Index.cshtml` (link), all six `Resources/ResidentText*.resx`,
  `wwwroot/js/` (reuse `upload.js` and the job polling)
- Test: `Launcher/DreamGrid.Launcher.Tests/IarGuestPagesTests.cs`, `IarDownloadTests.cs`, `ThrottledStreamTests.cs`,
  `IarPagesKeysTests.cs` (the `*KeysTests` pattern)

**Interfaces:**
- Consumes: `IarJobs` (Task 8), `IarFiles` (Task 7), `IarRights` (Task 2).
- Produces: `public sealed class ThrottledStream(Stream inner, int bytesPerSecond, TimeProvider time) : Stream`. It's
  seekable when `inner` is, so `Results.File(stream, enableRangeProcessing: true)` keeps range requests working.

**Page:** "Back up my inventory" (button, then progress, then the Download button with when it expires, then the "Left
out" table: name, folder, reason). "Bring in inventory" (file picker, progress bar, then the summary: N items loaded
into "Imported …", and M items left out because they matched removed content; if they're in-world: "Relog to see the new
folder"). Each part shows only when allowed. A
refusal shows its `Iar.Refused.*` text. If the person is `NamedInOpenCase`, both parts show "Inventory backups are not
available on your account right now."

**Upload endpoint:** `MaxRequestBodySize` = the effective `MaxUploadMb` plus 1 MB for the form. The byte count is
enforced while streaming into `IarsIncoming`, and it stops at the cap and deletes the part file. `LooksLikeIar` is
checked before `TryStartUpload`. It's audited `iar-upload-asked` or `iar-refused`.

**Download endpoint:** `IarFiles.ForDownload(token, principal, stamp)`, otherwise 404 (never 403, so a token's existence
isn't confirmed). It's served as `inventory-yyyy-MM-dd.iar` through `ThrottledStream` at the effective
`DownloadKbPerSecond`, and audited `iar-download-fetched`.

- [ ] **Step 1: Write the failing tests.**
  - `Page_404_when_both_off`, `Only_allowed_part_shows`, `Named_in_case_sees_not_available`.
  - `Upload_over_cap_cut_off_and_part_file_removed`.
  - `Upload_not_iar_refused_before_job`.
  - `Link_refused_for_other_resident`, `Link_refused_after_stamp_changes`, `Link_refused_after_expiry`: all 404.
  - `Download_supports_ranges`.
  - `ThrottledStream_takes_about_one_second_per_rate_of_bytes`: with a fake `TimeProvider`, 2 × rate bytes take 2 s of
    fake time.
  - `Left_out_table_lists_name_folder_reason`.
  - `Every_key_has_six_languages_and_no_raw_keys`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.** Translate the new keys into `de`, `es`, `fr`, `pt` and `ru` in the same plain tone as the
  existing entries.
- [ ] **Step 4: Run the tests again.** Expected: all pass.
- [ ] **Step 5: Commit.** `iar: resident inventory page, upload and throttled download (IAR task 10)`

### Task 11: Docs, worklist and the owner's check

**Files:**
- Modify: `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` (2.11 ticked, with a pointer to this plan;
  the owner's VM check added; and, while there, tick the items already done that are still unchecked: 2.6's acceptance
  table and 2.14)
- Create: `docs/inventory-backups.md` (owner docs: the switches, the limits and what each stops, the full-rights rule,
  banned uploads, the staff powers and their records)

- [ ] **Step 1: Write the docs and the worklist changes.** The owner's check, on the Debian 12 VM in Firestorm:
  1. Turn downloads on, back up a resident, and read the left-out list. It must include a full-perm box holding a
     no-transfer item.
  2. Upload that IAR into a second test resident, and see `Imported …` in their inventory.
  3. Upload an IAR holding a taken-down item: it's left out, and the case shows the match.
  4. Hit the daily limit once, and try someone else's download link.
  5. As an admin, save a switched-off resident with a reason and a case number. See the record and the delete hold. Then
     load FULLAVATARS into a test account from a browser upload.
- [ ] **Step 2: Run the full test suite.** `dotnet test Launcher/DreamGrid.Launcher.Tests`. Expected: all pass.
- [ ] **Step 3: Commit.** `docs: inventory backups for owners; worklist 2.11 (IAR task 11)`
