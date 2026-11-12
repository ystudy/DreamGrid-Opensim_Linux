# Starter avatars Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** New residents pick one of the owner's finished avatars on a second sign-up screen and log in wearing it; the owner loads,
dresses, checks and arranges those avatars on one page.

**Architecture:** A starter list (`StarterAvatarList`, JSON state) names model accounts. Sign-up passes the picked model's name to
Robust's `create user` (8th argument), which runs OpenSim's own `EstablishAppearance`; GridGate then compares the copy with the model
in the background. The owner's page loads avatars from an IAR with the IAR-backups machinery (strict reader, `Rewrite`, banned
filter, `RunIarJobAsync`), checks a model's stored appearance in Robust's `Avatars` table, and re-encodes snapshots with SkiaSharp.

**Tech Stack:** C# / .NET 10, ASP.NET Core Razor Pages + minimal APIs, MySqlConnector, SkiaSharp, xUnit v3 + AngleSharp +
`LauncherFactory`.

**Spec:** `docs/superpowers/specs/2026-11-11-starter-avatars-design.md`

## Global Constraints

- Branch `feature/starter-avatars` in `../DreamGrid-starter-avatars`, based on `feature/iar-backups` (PR 70, unmerged).
- Owner page `/security/starter-avatars`: OwnerPolicy, guest side only (`GuestSideOnly` convention like `/security/iar`), under Guest pages.
- Model account names `Starter<NN> Avatar`, NN two digits from 01; level 0; random password; no email.
- Snapshots: PNG, JPEG or WebP in, at most 5 MB; out WebP, at most 800 px wide, metadata stripped; served at `/starter-avatars/<id>.webp` (anonymous).
- State: `<data>/starter-avatars/avatars.json` (JsonStateFile), folder owner-only (`LauncherPaths.CreateOwnerOnly`).
- The form carries the avatar's GridGate id (GUID), never a model name; unknown/not-ready id → default → plain avatar, never an error.
- The copy check after sign-up polls up to 30 s; decoys run it too.
- Audit actions: `starter-avatar-added`, `-order`, `-default`, `-renamed`, `-hidden`, `-shown`, `-removed`, `-checked`, `-snapshot`, `-login`, `-failed`.
- Resident words in all six `ResidentText*.resx` (en, de, es, fr, pt, ru); avatar names shown as typed.
- `CreateDefaultAvatarEntries=true` must stay in the written `Robust.HG.ini`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Test command for a task: `dotnet test Launcher/DreamGrid.Launcher.Tests --filter <Class>`; whole suite at the end of each task: `dotnet test Launcher/DreamGrid.Launcher.Tests`.

## Review Focus

1. A starter avatar that becomes not-ready (hidden, removed, check failed) between a resident opening step 2 and pressing Create → they get the default or the plain avatar and the sign-up succeeds (Task 4 test `Stale_pick_falls_back_to_default`).
2. A name a resident types that matches the model pattern (`Starter07 Avatar`) → refused as taken, so nobody can squat or impersonate a model (Task 3 test `Model_names_are_refused_at_sign_up`).
3. Two owner imports at once picking the same `StarterNN` → second gets the next free number, never two models with one name (Task 7 test `Two_imports_never_share_a_model_name`).
4. A snapshot upload that is not an image, or an image bomb (huge dimensions) → refused before decoding the pixels (Task 6 test `Huge_dimensions_are_refused_before_decoding`).
5. Robust's copy is still running when the check first looks → it waits rather than flagging a working avatar (Task 4 test `Check_waits_for_a_slow_copy`).

---

### Task 1: The starter list

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Starter/StarterAvatarList.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/LauncherPaths.cs` (add `StarterAvatars => Path.Combine(<data>, "starter-avatars")`)
- Test: `Launcher/DreamGrid.Launcher.Tests/StarterAvatarListTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record StarterAvatar(Guid Id, string Name, Guid Model, string ModelFirst, string ModelLast, int Order, bool Hidden, string? Snapshot, DateTimeOffset? CheckedAt, bool CheckPassed, string? Warning)` with `bool IsReady => !Hidden && Snapshot is not null && CheckPassed && Warning is null`.
  - `public sealed record StarterPick(Guid Principal, Guid Avatar, string Name, DateTimeOffset At)`.
  - `public sealed class StarterAvatarList(string file, TimeProvider time, ILogger? log = null)`:
    `IReadOnlyList<StarterAvatar> All()` (by Order), `IReadOnlyList<StarterAvatar> Ready()`, `Guid? DefaultId`, `StarterAvatar? Default()` (DefaultId if ready, else first ready),
    `StarterAvatar? Resolve(Guid? picked)` (picked if ready, else `Default()`), `StarterAvatar Add(string name, Guid model, string first, string last)`,
    `bool Update(Guid id, Func<StarterAvatar, StarterAvatar> change)`, `bool Remove(Guid id)`, `bool Move(Guid id, int delta)`, `bool SetDefault(Guid id)`,
    `bool IsModel(Guid principal)`, `void Picked(StarterPick pick)`, `StarterPick? PickOf(Guid principal)`.
  - `public static partial class StarterNames`: `bool IsModelName(string first, string last)` (regex `^Starter\d{2}$` + last `Avatar`, case-insensitive), `(string First, string Last) Next(IEnumerable<string> takenFirstNames)`.
  - `LauncherPaths.StarterAvatars`.
- One lock around load-change-save; all mutators save through `JsonStateFile<T>`.

- [ ] **Step 1: Write the failing tests** in `StarterAvatarListTests`:
  - `Resolve_returns_the_pick_when_ready`, `Resolve_falls_back_to_default_then_first_ready_then_null` (hidden, no snapshot, failed check, warning each make it not ready).
  - `Default_is_first_ready_when_none_chosen`; `Move_swaps_order_and_survives_reload` (new instance on same file).
  - `Next_skips_taken_numbers`: `Next(["Starter01","Starter03"]) == ("Starter02","Avatar")`; `IsModelName("starter07","AVATAR")` true; `IsModelName("Starter7","Avatar")` false.
  - `Picks_are_kept_per_principal`.
- [ ] **Step 2: Run** `dotnet test Launcher/DreamGrid.Launcher.Tests --filter StarterAvatarListTests` — Expected: compile FAIL (types missing).
- [ ] **Step 3: Implement** the types above.
- [ ] **Step 4: Run** the same filter — Expected: PASS.
- [ ] **Step 5: Commit** `starter: the starter avatar list (starter task 1)`.

### Task 2: Reading a stored appearance and comparing copies

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Database/AppearanceStore.cs`, `Launcher/DreamGrid.Launcher.Core/Starter/StarterCheck.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/StarterCheckTests.cs`; add a case to `InventoryStoreIntegrationTests` (skipped without MariaDB, like its neighbours).

**Interfaces:**
- Consumes: `IInventoryStore.ItemsAsync(owner, ids, ct)`, `FoldersAsync`, `InventoryItemRow` (Task-free, existing).
- Produces:
  - `public sealed record WornItem(string Slot, Guid ItemId)` — Slot is `"Wearable i:j"` or `"_ap_<point>"`.
  - `public interface IAppearanceStore { Task<IReadOnlyList<WornItem>?> WornAsync(Guid principal, CancellationToken ct); }` — null when the account has no `Avatars` rows; reads `robust.Avatars` (`PrincipalID`, `Name`, `Value`): `Wearable i:j` value `item:asset` (skip `UUID.Zero` items), `_ap_N` value comma-separated item ids. Throws `StaffDirectoryUnavailableException` like `InventoryStore.Run`.
  - `public sealed record CheckResult(bool Passed, IReadOnlyList<string> Missing, IReadOnlyList<string> PermissionWarnings)`.
  - `public static class StarterCheck`:
    - `CheckResult Model(IReadOnlyList<WornItem>? worn, IReadOnlyList<InventoryItemRow> modelItems)` — fails with "No outfit stored: log in and choose Replace Outfit" when worn is null/empty; every worn item must be in modelItems (missing listed by slot); warns, without failing, for each worn item whose NextPermissions lacks copy (0x8000) or modify (0x4000): `"<item name>: residents can't copy"` / `"can't modify"`.
    - `CheckResult Copy(IReadOnlyList<WornItem> model, IReadOnlyList<InventoryItemRow> modelItems, IReadOnlyList<WornItem>? resident, IReadOnlyList<InventoryItemRow> residentItems)` — for every model slot, the resident has an item in the same slot whose row has the model item's AssetId; anything else is listed missing.

- [ ] **Step 1: Write the failing tests**: `Model_without_appearance_fails`, `Model_missing_a_worn_item_names_its_slot`, `Model_no_copy_hair_warns_but_passes`, `Copy_matches_slot_by_asset`, `Copy_with_an_empty_slot_fails` (the half-dressed case), `Copy_with_no_appearance_fails`; a parser test `Avatars_rows_parse_wearables_and_attachments` on an internal `AppearanceStore.Parse(IEnumerable<(string Name, string Value)>)`.
- [ ] **Step 2: Run** `--filter StarterCheckTests` — Expected: compile FAIL.
- [ ] **Step 3: Implement** `AppearanceStore` (`internal static IReadOnlyList<WornItem> Parse(...)` + MySQL read) and `StarterCheck`.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `starter: read a stored appearance and compare copies (starter task 2)`.

### Task 3: Making an account from a model

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/GridConsoleCommands.cs` (`CreateUserAsync` gains `string? model`), `Grid/ResidentAvatars.cs` (`CreateAsync(..., string? model = null)`), `Grid/IGridController.cs` + `Grid/GridController.cs` (`CreateResidentAsync(string first, string last, string? email, int level, CancellationToken ct, string? model = null)`), `Guests/SignUpRules.cs` (model names are Taken), `Launcher/DreamGrid.Launcher.Tests/Fakes/FakeGridController.cs` (records the model).
- Test: `GridConsoleCommandsTests`, `ResidentAvatarsTests`, `SignUpRulesTests`, `GridConfigWriterTests`.

**Interfaces:**
- Consumes: `StarterNames.IsModelName` (Task 1), `GridConsoleCommands.ValidateAvatarName`.
- Produces: the `model` argument above; `FakeGridController.LastModel` (string?).
- The model goes in quotes as the 8th argument: `create user F L P "email" id "Starter01 Avatar"`; null/empty stays `""`. Validate it as `First Last` with `ValidateAvatarName` on its two parts, else `ArgumentException`.

- [ ] **Step 1: Failing tests**: `Create_user_sends_the_model_quoted` (transcript line ends `"Starter01 Avatar"`), `A_model_with_three_words_is_refused`, `Model_names_are_refused_at_sign_up` (`SignUpRules.NameCheck("Starter07","Avatar",...) == NameFault.Taken`), `Written_robust_ini_keeps_default_avatar_entries` (`GridConfigWriter` output contains `CreateDefaultAvatarEntries=true` or ` = true`).
- [ ] **Step 2: Run** the four classes — Expected: FAIL (the ini test may pass already: if so it is a pin, note it in the ledger).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — Expected: PASS; whole suite green.
- [ ] **Step 5: Commit** `starter: make an account from a model (starter task 3)`.

### Task 4: Sign-up passes the pick, and checks the copy

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Guests/SignUps.cs` (`SignUpForm` gains `Guid? Avatar = null`; constructor gains `StarterAvatarList starters, StarterCopyCheck check`), `Program.cs` (DI).
- Create: `Launcher/DreamGrid.Launcher/Starter/StarterCopyCheck.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/StarterSignUpTests.cs`

**Interfaces:**
- Consumes: `StarterAvatarList.Resolve/Picked/Update` (Task 1), `IAppearanceStore`, `StarterCheck.Copy` (Task 2), `CreateResidentAsync(..., model)` (Task 3), `BackgroundWork.Run`, `AuditLog`.
- Produces: `public sealed class StarterCopyCheck(IAppearanceStore looks, IInventoryStore inventory, StarterAvatarList starters, AuditLog audit, TimeProvider time, ILogger<StarterCopyCheck> log)` with
  `public Task RunAsync(Guid resident, StarterAvatar avatar, CancellationToken ct)` and `public TimeSpan Wait { get; init; } = 30 s`, `Poll = 2 s`.
- `SubmitAsync`: `var avatar = starters.Resolve(form.Avatar)`; pass `avatar is null ? null : $"{avatar.ModelFirst} {avatar.ModelLast}"` to `CreateResidentAsync`; after the account is made (decoy or not) `background.Run("Starter avatar check", ct => check.RunAsync(account.PrincipalId, avatar, ct))`; for non-decoys `starters.Picked(...)`. The reply never waits for the check.
- `RunAsync` polls until `StarterCheck.Copy` passes or `Wait` passes; on failure: `starters.Update(id, a => a with { Warning = $"{a.Name} wasn't copied to a new resident; check its model" })` and audit `starter-avatar-failed` (target = avatar name, result = first missing slot). A database error is logged and leaves the avatar alone.

- [ ] **Step 1: Failing tests**: `Picked_avatar_model_reaches_create_user`, `Stale_pick_falls_back_to_default` (hidden after page load), `No_ready_avatar_sends_no_model`, `Decoy_gets_the_model_and_the_check_too`, `Check_waits_for_a_slow_copy` (fake store returns nothing for first two polls, then a full copy; FakeTimeProvider; no warning), `Failed_copy_takes_the_avatar_off_sign_up` (Ready() no longer contains it; audit has `starter-avatar-failed`).
- [ ] **Step 2: Run** `--filter StarterSignUpTests` — Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — Expected: PASS; whole suite green (existing SignUps tests compile with the new constructor via test factory).
- [ ] **Step 5: Commit** `starter: sign-up passes the pick and checks the copy (starter task 4)`.

### Task 5: The two-screen sign-up page

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Pages/SignUp.cshtml(.cs)` (`[BindProperty] public Guid? Avatar`, `public IReadOnlyList<StarterAvatar> Starters`, `public Guid? Selected`), `Resources/ResidentText*.resx` (6 files), `Program.cs` (anonymous `GET /starter-avatars/{id:guid}.webp` → file under `LauncherPaths.StarterAvatars`, 404 unless the avatar is ready, `Cache-Control: public, max-age=300`).
- Create: `Launcher/DreamGrid.Launcher/wwwroot/js/signup-steps.js`, `Endpoints/StarterSnapshotApi.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/SignUpStepsPageTests.cs`; `TranslationsTests` count update.

**Interfaces:**
- Consumes: `StarterAvatarList.Ready()/Default()` (Task 1); `SignUpForm.Avatar` (Task 4).
- Keys: `SignUp.Next` "Next", `SignUp.Back` "Back", `SignUp.ChooseAvatar` "Choose your avatar", `SignUp.ChooseAvatarHint` "You can change clothes later from your inventory.", `SignUp.Step` "Step {0} of {1}".
- Markup: step 2 is a `<fieldset id="step2">` of radio inputs `name="Avatar" value="<id>"`, each with `<img src="/starter-avatars/<id>.webp" alt="<name>" loading="lazy">` and the name; default checked. Without JS both fieldsets show; the script hides step 2, swaps Next/Create, runs `form.reportValidity()` on step 1 before showing step 2, and Back returns. A server-refused post renders step 1 visible (`data-step="1"`) with the posted `Avatar` still checked. No starters → no step 2, button `SignUp.CreateAvatar` as now.

- [ ] **Step 1: Failing tests** (AngleSharp via `LauncherFactory`): `No_starters_no_step_two`, `Ready_starters_are_radios_with_default_checked` (not-ready ones absent), `Refused_post_keeps_the_pick_and_shows_step_one`, `Snapshot_served_only_for_ready_avatars` (200 webp vs 404), `Post_with_avatar_reaches_signups` (fake grid `LastModel`).
- [ ] **Step 2: Run** `--filter SignUpStepsPageTests` — Expected: FAIL.
- [ ] **Step 3: Implement**, with the five keys in all six resx files.
- [ ] **Step 4: Run** — Expected: PASS; `TranslationsTests` green.
- [ ] **Step 5: Commit** `starter: two-screen sign-up page (starter task 5)`.

### Task 6: The owner's page — list, arrange, snapshots

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Security/StarterAvatars.cshtml(.cs)`, `Launcher/DreamGrid.Launcher/Starter/StarterSnapshots.cs`
- Modify: `Pages/GuestPages/Index.cshtml` (link), `Navigation/StaffMenu.cs` if guest pages are listed there, `Program.cs` (`GuestSideOnly` for `/Security/StarterAvatars`, DI, `LauncherPaths.CreateOwnerOnly(StarterAvatars)`).
- Test: `Launcher/DreamGrid.Launcher.Tests/StarterAvatarsPageTests.cs`, `StarterSnapshotsTests.cs`; update `GuestPagesHubTests`, `StaffMenuTests`.

**Interfaces:**
- Consumes: Task 1 list; `AuditLog`, `AuditEntry`.
- Produces: `public static class StarterSnapshots { public const long MaxBytes = 5 * 1024 * 1024; public const int MaxWidth = 800; public const int MaxPixels = 40_000_000; public static string? Save(Stream input, string folder, Guid id, out string? problem); }` — reads the header with `SKCodec.Create`, refuses formats other than PNG/JPEG/WebP and `width*height > MaxPixels` before decoding, decodes, scales down to MaxWidth, encodes WebP quality 85 to `<id>.webp` (new file, so no metadata), returns the file name.
- Page handlers (all OwnerPolicy, antiforgery): `OnPostSnapshotAsync(Guid id, IFormFile file)`, `OnPostMove(Guid id, int delta)`, `OnPostDefault(Guid id)`, `OnPostRename(Guid id, string name)` (1–64 chars, trimmed), `OnPostHide/OnPostShow(Guid id)`, `OnPostRemoveAsync(Guid id, bool deleteModel)`. Each writes its audit action. The page shows per row: snapshot, name, model name, checklist (Snapshot ✓/✗, Worn and checked ✓/✗ with time, Ready), the warning if any, and the buttons.
- Remove with `deleteModel` calls `IAccountRemover.DeleteAsync(model)` directly (not the People delete, which is for suspended residents) and audits `starter-avatar-removed` with result "model deleted".

- [ ] **Step 1: Failing tests**: `Owner_sees_the_page_admin_does_not` (admin 403/redirect), `Not_on_a_region_server` (404), `Snapshot_png_becomes_webp_800_wide` (1600×1200 PNG made with SkiaSharp in test → 800×600 WebP), `Non_image_is_refused`, `Huge_dimensions_are_refused_before_decoding` (PNG header claiming 20000×20000), `Arrange_actions_are_audited` (move/default/rename/hide/show each one entry), `Remove_with_delete_model_deletes_the_account`.
- [ ] **Step 2: Run** both classes — Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — Expected: PASS; whole suite green.
- [ ] **Step 5: Commit** `starter: owner page, arranging and snapshots (starter task 6)`.

### Task 7: Loading avatars from an archive

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Starter/StarterArchive.cs`, `Launcher/DreamGrid.Launcher/Starter/StarterImport.cs`, `Launcher/DreamGrid.Launcher/Endpoints/StarterApi.cs` (`POST /security/starter-avatars/upload`, OwnerPolicy, `IarUpload.ReceiveAsync(ctx, antiforgery, paths, maxBytes: null)`, redirect `?pending=<name>`)
- Modify: `Pages/Security/StarterAvatars.cshtml(.cs)` (pending archive: list candidates with checkboxes and editable names; `OnPostImport(string pending, string[] folders, string[] names)`; add-from-account form `OnPostAddExistingAsync(string first, string last, string name)`; progress per job).
- Test: `Launcher/DreamGrid.Launcher.Tests/StarterArchiveTests.cs`, `StarterImportTests.cs` (use `IarBuilder`).

**Interfaces:**
- Consumes: `IarArchive.Inspect/Rewrite/ReadAssets`, `IarItem`, `IarBannedFilter.Find/Clean`, `TakedownStore.BannedAssetsAsync/AppendUploadMatch`, `IInventoryStore.CreateFolderAsync`, `IGridController.CreateResidentAsync` (level 0, email null), `IGridController.RunIarJobAsync(new IarJobRequest(model, first, last, IarJobKind.Load, file, folderName))`, `IRobustAccounts`/robust lookup for add-existing, `StarterNames.Next`, `StarterAvatarList.Add`.
- Produces:
  - `public sealed record StarterCandidate(string FolderPath, string Name, int Items, Guid? PictureAsset)`.
  - `public static class StarterArchive`: `IReadOnlyList<StarterCandidate> Candidates(string iar)` — folders exactly two levels down (`A/B`) holding at least one item with AssetType 13 (body part); Name = last folder segment; PictureAsset = a texture item (AssetType 0) directly in it whose name equals the avatar's first word, case-insensitive (e.g. "Lyra" in "Lyra complete female avatar"). `void OneAvatar(string iar, string folderPath, string target)` — `Rewrite` dropping every item entry not under folderPath.
  - `public sealed class StarterImport(...)`: `string Start(IarStaffActor actor, string pendingFile, IReadOnlyList<(string Folder, string Name)> picks)` — one background job, avatars one after another: lock, pick name with `StarterNames.Next` over taken first names (list + Robust lookup), create the model, `OneAvatar` → banned `Find` → `Clean` (always) → `AppendUploadMatch` per match → `CreateFolderAsync(model, Name)` → `RunIarJobAsync` → `starters.Add`; when a picture texture exists, save it as a snapshot *suggestion* (`ReadAssets` → `StarterSnapshots.Save` to `<id>.suggested.webp`; the page offers "Use this picture", which renames it). `StarterImportProgress For(string jobId)`. Audit `starter-avatar-added` per avatar; a failed avatar is reported and the next goes on; the model of a failed load is deleted.
- Model-name choice and account creation happen under one `SemaphoreSlim` so two imports never share a number.

- [ ] **Step 1: Failing tests**: `Candidates_are_folders_with_body_parts` (18-style layout of 3 avatars + a folder with only a notecard → 3), `Picture_texture_is_found_by_first_word`, `One_avatar_archive_keeps_only_its_folder` (and its assets), `Import_makes_model_loads_folder_and_adds` (fake grid sees `create user` for `Starter01 Avatar` and a Load into folder "Lyra"), `Banned_item_is_left_out_and_noted` (case gets `UploadMatch`), `Two_imports_never_share_a_model_name`, `Failed_load_deletes_its_model_and_goes_on`, `Add_existing_account_needs_no_archive`.
- [ ] **Step 2: Run** both classes — Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — Expected: PASS; whole suite green.
- [ ] **Step 5: Commit** `starter: load avatars from an archive (starter task 7)`.

### Task 8: Dressing and checking a model; model accounts elsewhere; docs

**Files:**
- Modify: `Pages/Security/StarterAvatars.cshtml(.cs)` (`OnPostLoginAsync(Guid id)` shows a one-time password; `OnPostCheckAsync(Guid id)`), `Security/AccountHolds.cs` (refuse delete while on the list: "This account is a starter avatar model"), `Pages/People/Index.cshtml` + `Person.cshtml` ("Starter avatar" tag; person page shows the resident's `PickOf`), `Core/Dashboard/WorldCounter.cs` (`Person` also excludes `LastName = 'Avatar' AND FirstName REGEXP '^Starter[0-9]{2}$'`), guest sign-in (`Pages/Guest/SignIn.cshtml.cs`: a model account gets the same reply as a wrong password), `Program.cs`.
- Create: `docs/starter-avatars.md`; worklist `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` gets the VM check (sign up with Lyra, log in, wearing her, pieces in Clothing).
- Test: `Launcher/DreamGrid.Launcher.Tests/StarterModelTests.cs`; update `WorldCounter` tests if present.

**Interfaces:**
- Consumes: `IRobustPasswords.SetAsync`, `IAppearanceStore.WornAsync`, `IInventoryStore.ItemsAsync`, `StarterCheck.Model` (Task 2), `StarterAvatarList.Update/IsModel`.
- Login: new 16-char password from `RandomNumberGenerator` (letters+digits), `SetAsync`, shown once with the model's full name and the spec's words ("Log in with a viewer, open the folder <Name> in your inventory, choose Replace Outfit, wait until you look right, then log out."); audit `starter-avatar-login`; sets `CheckPassed = false` until the next check.
- Check: `StarterCheck.Model`; stores `CheckedAt`, `CheckPassed`, clears `Warning` on pass, shows Missing and PermissionWarnings; on pass sets a fresh unseen random password; audit `starter-avatar-checked` with result.

- [ ] **Step 1: Failing tests**: `Login_sets_a_password_shown_once_and_unreadies`, `Check_pass_makes_ready_and_rotates_password`, `Check_fail_lists_missing_slots`, `Check_clears_an_old_warning`, `Model_cannot_be_deleted_from_people`, `Model_cannot_sign_in_to_resident_pages`, `People_list_tags_models`, `Dashboard_does_not_count_models` (the SQL filter string test, or the integration test if the counter has one).
- [ ] **Step 2: Run** `--filter StarterModelTests` — Expected: FAIL.
- [ ] **Step 3: Implement**, then write `docs/starter-avatars.md` (the six owner steps from spec §7, what each checklist line means, what residents get, the warning) and the worklist entry.
- [ ] **Step 4: Run** — Expected: PASS; whole suite green.
- [ ] **Step 5: Commit** `starter: dressing and checking models; docs (starter task 8)`.
