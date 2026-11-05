# Inventory Takedown Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The owner and admins browse a resident's inventory and take down the items a notice names. Each takedown is a case that keeps the record and an evidence copy, finds copies held by others, can put items back, holds every account it names against delete, and prints a page for the sender.

**Architecture:**
- **Database in Core.** `InventoryStore` reads and writes `robust.inventoryitems` / `inventoryfolders` (and reads `fsassets`) with direct SQL as robustuser, like `AccountRemover`.
- **Cases in Core.** `TakedownStore` keeps one folder per case under `<data>/state/takedowns/`, with `case.json` and the evidence files. `EvidenceCopier` copies an asset's stored file there.
- **Rules.** Pure rules in Core: `PeopleRules` gets an `Inventory` task, and `TakedownRules` gets the sender view and the creator masking.
- **Actions.** `TakedownActions` in the web project does each step, sends the emails and writes the audit log. `AccountHolds` asks the store.
- **Pages.** The pages under `Pages/People/` only draw and post.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, MySqlConnector, xUnit v3, AngleSharp; integration tests against the real MariaDB + Robust (`DreamGrid.Launcher.IntegrationTests`, CI only).

**Spec:** `docs/superpowers/specs/2026-11-04-inventory-takedown-design.md`

## Global Constraints

**Database and code**
- No changes to OpenSim code, config or schema. No new index.
- SQL with parameters only. Item search is a `LIKE` value with `\`, `%` and `_` escaped and `ESCAPE '\\'`, trimmed, at most 64 characters (as `PeopleDirectory`).
- Principals and ids go to SQL in OpenSim's lower-case `D` form.
- Folder types (OpenSim `FolderType`): My Inventory (root) `8`, Trash `14`, Lost And Found `16`.

**Access**
- Every page and handler is under `/people`, so `StaffPolicy` and `StaffPagesOnly` apply. Each page model also refuses anyone who isn't the owner or an admin with 403 (`StaffSignIn.RoleOf(User) is StaffRole.Owner or StaffRole.Admin`).
- Posts use the antiforgery token, never GET. The rules are rechecked on every post.
- CSP is unchanged: no inline script or style. The new icon is a `box` symbol in `wwwroot/img/people-icons.svg`.

**Values**
- Inventory page size 50. Find copies shows at most 200 holdings.
- Case numbers are `TD-<year>-<count:D3>`. The count restarts each UTC year, so the first one is `TD-2026-001`.
- Text limits: the notice text 4000 characters; every other free-text field 1000. Each is trimmed. Sender name and the Put back / Close "why" are required.
- Reasons, with their words: `Copyright` "Copyright (DMCA)", `Illegal` "Illegal content", `Rules` "Breaks the grid's rules", `Other` "Other". Tell the resident defaults to on for Copyright and Rules, and is forced off for Illegal.
- In-world refusal: `PeopleActions.InWorld` ("Sign them out first: they are in-world.").

**Audit and email**
- Audit actions:
  - `inventory-viewed` (target the name)
  - `takedown-opened`, `takedown-added`, `takedown-copies`, `takedown-note`, `takedown-closed` (target `First Last: TD-2026-001`, or just `TD-2026-001` where there is no one person)
  - `takedown-removed`, `takedown-restored` (target `First Last: TD-2026-001 <item name>`)
- Every target goes through `LogText.Clean(..., 140)`. Free text never goes into the audit log.
- New email templates: `takedown-removed` and `takedown-restored`, added to `EmailTemplates.All`.
- English only until worklist 1.5.

**Rulings made while planning** (the spec leaves them open):
- **Stored asset file.** FSAssets keeps it at `<FsAssetsData>/<hash[0..2]>/<hash[2..4]>/<hash[4..6]>/<hash[6..10]>/<hash>.gz`. That is the default layout; the grid never sets `UseOsgridFormat`. A plain `<hash>` with no `.gz` is also accepted, since it may still be in the spool. The copy is named `assets/<assetId>` plus the same extension.
- **Record before the database.** The evidence copy and the `case.json` save happen before the rows are deleted. If the save fails, nothing is deleted. If the delete fails, the new items are taken back out of the case and saved again, and the page says `PeopleActions.DatabaseDown`.
- **Links.** Link items (`assetType` 24, 25) are left alone. A link to a removed item shows broken in the viewer. Find copies leaves out links.
- **Unreadable cases.** `TakedownStore` reads every `case.json` at start. If one can't be read or parsed, it is left where it is (not moved aside like `JsonStateFile` does), and `Unreadable` is true until restart. Then every delete is refused.
- **Hold API.** `AccountHolds` gets `virtual string? Refusal(Guid principal)`, and `IsHeld` becomes `Refusal(principal) is not null` (no longer virtual). `PeopleActions.DeleteRefusal` uses `Refusal`. The test fake `HoldAll` in `PeopleDeleteTests` overrides `Refusal`.
- **Copies.** A copy is any non-link item with the same `assetID`, in any account, that isn't already on the case.

## Review Focus

1. **Two staff opening cases at once.** Both get different numbers; neither overwrites the other's case. → `TakedownStoreTests.Two_cases_opened_at_once_get_different_numbers`.
2. **An item whose old folder was deleted.** It goes back into Lost And Found; with none, into My Inventory; never into nothing. → `InventoryStoreIntegrationTests.Put_back_without_its_folder_goes_to_lost_and_found` and `..._without_lost_and_found_goes_to_my_inventory`.
3. **A resident who made the item.** The sender page shows "a resident", not their name. → `TakedownRulesTests.Sender_view_masks_a_creator_who_is_a_holder`.
4. **The database is down mid-takedown.** The case doesn't claim an item was removed when it is still in the inventory. → `TakedownActionsTests.Database_down_leaves_no_removed_item_in_the_case`.
5. **An illegal-content case.** No email reaches the resident, even if the form was tampered with to tick Tell. → `TakedownActionsTests.Illegal_content_never_emails_even_when_tell_is_posted`.

---

### Task 1: Inventory in the database

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Database/InventoryStore.cs`
- Create: `Launcher/DreamGrid.Launcher.IntegrationTests/InventoryStoreIntegrationTests.cs`
- Modify: `Launcher/DreamGrid.Launcher.Tests/FakeGridController.cs` (add `FakeInventoryStore`), `Launcher/DreamGrid.Launcher.Tests/LauncherFactory.cs` (register it, expose `Inventory`)
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (register `IInventoryStore` → `InventoryStore`)

**Interfaces:**
- Produces:
  - `sealed record InventoryFolder(Guid Id, Guid? Parent, string Name, int Type)`.
  - `sealed record InventoryItemRow(string? AssetId, int? AssetType, string? Name, string? Description, uint? NextPermissions, uint? CurrentPermissions, int? InvType, string CreatorId, uint BasePermissions, uint EveryOnePermissions, int SalePrice, sbyte SaleType, int CreationDate, string GroupId, sbyte GroupOwned, uint Flags, Guid ItemId, Guid Owner, Guid Folder, uint GroupPermissions)`. One field per `inventoryitems` column, so a put-back writes back exactly what was there.
  - `sealed record InventoryItemView(InventoryItemRow Row, string CreatorName, string FolderPath)`. `CreatorName` is the local account's "First Last". For a hypergrid `uuid;url;First Last` it is the name part plus " (" + url host + ")". When it can't be found it is "unknown".
  - `sealed record InventoryHolding(Guid Owner, string OwnerName, InventoryItemView Item)`.
  - `interface IInventoryStore`. Every method throws `StaffDirectoryUnavailableException` when the database can't be asked.
    - `Task<IReadOnlyList<InventoryFolder>> FoldersAsync(Guid owner, CancellationToken ct)` — all of them
    - `Task<IReadOnlyList<InventoryItemView>> ItemsInAsync(Guid owner, Guid folder, int skip, int take, CancellationToken ct)` — sorted by name
    - `Task<IReadOnlyList<InventoryItemView>> SearchAsync(Guid owner, string text, int skip, int take, CancellationToken ct)`
    - `Task<IReadOnlyList<InventoryItemView>> ItemsAsync(Guid owner, IReadOnlyCollection<Guid> items, CancellationToken ct)` — only those owned by `owner`
    - `Task<IReadOnlyList<InventoryHolding>> CopiesAsync(string assetId, int max, CancellationToken ct)`
    - `Task RemoveAsync(Guid owner, IReadOnlyCollection<Guid> items, CancellationToken ct)` — one transaction: delete the rows, then `version = version + 1` on each distinct parent folder
    - `Task<Guid> PutBackAsync(InventoryItemRow row, CancellationToken ct)` — inserts every column. The folder is `row.Folder` if it still exists for `row.Owner`, else their type-16 folder, else their type-8 folder. That folder's version is raised in the same transaction, and the folder used is returned. Throws `InvalidOperationException` when the owner has no folders at all (deleted).
    - `Task<string?> AssetHashAsync(string assetId, CancellationToken ct)` — `fsassets.hash`, or null when there is no row or no such table
  - `FakeInventoryStore : IInventoryStore`, in memory, with `Down`, and `Add(owner, folder, row)` / `AddFolder(owner, InventoryFolder)` helpers.

- [ ] **Step 1: Write the failing integration tests** in `InventoryStoreIntegrationTests` (same set-up as `AccountRemoverIntegrationTests`: start the grid, make the owner first, then residents; `create user` gives each resident their default folders). Insert test items with SQL.
  - `Remove_deletes_only_the_named_items_and_raises_their_folder_versions`: two items in one folder and one elsewhere; remove one. Afterwards its row is gone, the other two remain, and the folder's `version` is one higher.
  - `Put_back_restores_every_column_into_its_old_folder`: save the `InventoryItemRow` from `ItemsAsync`, remove, put back. `ItemsAsync` gives a row equal (record equality) to the saved one.
  - `Put_back_without_its_folder_goes_to_lost_and_found`: delete the old folder with SQL first. The returned folder is the resident's type-16 folder.
  - `Put_back_without_lost_and_found_goes_to_my_inventory`: delete the type-16 folder too. The returned folder is the type-8 one.
  - `Copies_finds_the_same_asset_in_other_inventories_and_skips_links`: Ann and Bob hold the same asset id, and Bob also has a link (type 24) with it. The result is two holdings with owner names, and no link.
  - `Search_escapes_like_wildcards`: items "100% Silk" and "100 Silk". Searching `100%` returns only the first.
  - `Hypergrid_creator_shows_its_name_and_host`: `creatorID` = `"<uuid>;http://other.grid:8002/;Hal Gee"` gives `CreatorName` "Hal Gee (other.grid)".
  - `Asset_hash_is_null_for_an_unknown_asset`.

- [ ] **Step 2: Build** — `dotnet build Launcher/DreamGrid.Launcher.IntegrationTests`. Expected: compile errors for `InventoryStore` (the tests can only run in CI; locally a build is the red step).

- [ ] **Step 3: Implement `InventoryStore(Func<LauncherSettings> settings) : IInventoryStore`** with the connection pattern and exception filter of `AccountRemover`, and a 10-second timeout. The creator name comes from a `LEFT JOIN robust.UserAccounts` on the creator id, when it is a plain uuid. `FolderPath` is built from `FoldersAsync` as "My Inventory / Clothes / Hats". Paging is `LIMIT @take OFFSET @skip`.

- [ ] **Step 4: Add `FakeInventoryStore`, register both, build the solution** — `dotnet build Launcher`. Expected: 0 errors. Then run `dotnet test Launcher/DreamGrid.Launcher.Tests`. Expected: all pass (nothing uses it yet).

- [ ] **Step 5: Commit** — `Takedown: inventory reads, remove and put back on Robust's database`.

### Task 2: Cases and evidence

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Security/TakedownCase.cs` (the records), `Launcher/DreamGrid.Launcher.Core/Security/TakedownStore.cs`, `Launcher/DreamGrid.Launcher.Core/Security/EvidenceCopier.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/LauncherPaths.cs` (`public string Takedowns => Path.Combine(Data, "state", "takedowns");`)
- Test: `Launcher/DreamGrid.Launcher.Tests/TakedownStoreTests.cs`, `Launcher/DreamGrid.Launcher.Tests/EvidenceCopierTests.cs`

**Interfaces:**
- Consumes: `InventoryItemRow`, `InventoryItemView` (Task 1).
- Produces:
  - `enum TakedownReason { Copyright, Illegal, Rules, Other }`, and `enum PersonOnCase { Named, CopyHolder }`.
  - `sealed record TakedownNotice(TakedownReason Reason, string SenderName, string? Organisation, string? Address, string? Reference, DateOnly Received, string Text)`.
  - `sealed record CasePerson(Guid Principal, string Name, PersonOnCase Kind)`.
  - `sealed record CaseItem(Guid ItemId, Guid Holder, string Name, string? Description, int? AssetType, string CreatorId, string CreatorName, string FolderPath, string? AssetId, InventoryItemRow Row, string? EvidenceFile, string? EvidenceSha256, string? NotKept, DateTimeOffset RemovedAt, DateTimeOffset? PutBackAt)`.
  - `sealed record CaseEvent(DateTimeOffset At, string Staff, Guid? StaffId, string What, string? Why)`. `What` is one of: `opened`, `removed`, `copies-searched`, `put-back`, `note`, `emailed`, `closed`.
  - `sealed record TakedownCase(string Number, bool Open, TakedownNotice Notice, bool TellResident, IReadOnlyList<CasePerson> People, IReadOnlyList<CaseItem> Items, IReadOnlyList<CaseEvent> Events, string? Outcome)`.
  - `sealed class TakedownStore(string root, TimeProvider time, ILogger<TakedownStore> log)`:
    - `bool Unreadable { get; }`
    - `IReadOnlyList<TakedownCase> All()` — open first, then newest number first
    - `TakedownCase? Get(string number)`
    - `TakedownCase Open(TakedownNotice notice, bool tell, Func<string, TakedownCase> build)` — under one lock: picks the next number, calls `build(number)`, saves it, and returns it. Throws `IOException` when it can't be saved.
    - `bool Save(TakedownCase c)`
    - `string CaseFolder(string number)`
    - `IReadOnlyList<TakedownCase> Naming(Guid principal)`
  - `sealed class EvidenceCopier(string fsAssetsData)` with `EvidenceResult Copy(string hash, string assetId, string caseFolder)`. `sealed record EvidenceResult(string? File, string? Sha256, string? NotKept)`.
  - `NotKept` uses these exact words:
    - "the asset store has no record of it" (null hash)
    - "its stored file is missing"
    - "it couldn't be copied (<ExceptionType>)"

- [ ] **Step 1: Write the failing tests**
  - `TakedownStoreTests`:
    - `First_case_of_the_year_is_001`: `TD-2026-001`, and the next one `TD-2026-002`. When the fake time moves to 2027, it's `TD-2027-001`.
    - `Two_cases_opened_at_once_get_different_numbers`: `Parallel.For` 20 opens give 20 distinct numbers and 20 folders.
    - `Cases_survive_a_restart`: a new store on the same root gives back an equal `All()`.
    - `A_damaged_case_file_makes_the_store_unreadable_and_is_left_in_place`: garbage in one `case.json` → `Unreadable` is true and the file's bytes are unchanged.
    - `Naming_finds_only_cases_that_list_the_person`.
    - `Case_folder_is_private_on_linux`: skipped on Windows; mode is `UserRead|UserWrite|UserExecute`.
  - `EvidenceCopierTests` (a temp fsassets tree):
    - `Copies_the_gz_file_byte_for_byte_with_its_sha256`: path `ab/cd/ef/0123/abcdef0123….gz`, copy at `assets/<assetId>.gz`, `Sha256` = lower-case hex of the bytes.
    - `Copies_a_plain_spool_file_when_there_is_no_gz`.
    - `Missing_file_says_why`, and `Null_hash_says_no_record`.

- [ ] **Step 2: Run** — `dotnet test Launcher/DreamGrid.Launcher.Tests --filter "FullyQualifiedName~TakedownStoreTests|FullyQualifiedName~EvidenceCopierTests"`. Expected: build fails, because the types are missing.

- [ ] **Step 3: Implement.**
  - `TakedownStore` writes with `AtomicFile`, using the web JSON options of `JsonStateFile`.
  - The next count is 1 + the highest count among folder names `TD-<year>-*`.
  - `Open` creates the folder and its `assets/` folder, then sets mode 0700 on non-Windows.
  - Register in `Program.cs`: `new TakedownStore(paths.Takedowns, TimeProvider.System, …)` and `new EvidenceCopier(paths.FsAssetsData)`.

- [ ] **Step 4: Run** the same filter. Expected: all pass.

- [ ] **Step 5: Commit** — `Takedown: case files and evidence copies`.

### Task 3: Rules

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Security/PeopleRules.cs` (`PersonTask.Inventory`)
- Create: `Launcher/DreamGrid.Launcher.Core/Security/TakedownRules.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/PeopleRulesTests.cs` (add cases), `Launcher/DreamGrid.Launcher.Tests/TakedownRulesTests.cs`

**Interfaces:**
- Consumes: Task 2 records.
- Produces:
  - `PersonTask.Inventory`, appended last. `PeopleRules.Refusal` treats it like `Edit`: owner refused, waiting refused, self refused, supervisors get `SupervisorOnly`, and staff get `StaffFirst`. Suspended is allowed.
  - `static class TakedownRules`:
    - `string ReasonWords(TakedownReason r)` — the words in Global Constraints
    - `bool DefaultTell(TakedownReason r)`
    - `bool MayTell(TakedownReason r)` — false only for `Illegal`
    - `string TypeWords(int? assetType)`
    - `string? NoticeProblem(TakedownNotice n)` — the limits; "Who sent the notice is needed."; "The notice text can be at most 4000 characters."; and "Each field can be at most 1000 characters."
    - `IReadOnlyList<SenderItem> SenderView(TakedownCase c)`
  - `TypeWords` gives:

    | Asset type | Words |
    |---|---|
    | 0 | texture |
    | 1 | sound |
    | 2 | calling card |
    | 3 | landmark |
    | 5 | clothing |
    | 6 | object |
    | 7 | notecard |
    | 10 | script |
    | 13 | body part |
    | 20 | animation |
    | 21 | gesture |
    | 49 | mesh |
    | 56 | settings |
    | 57 | material |
    | anything else | item |

  - `sealed record SenderItem(string Name, string Type, string Creator, DateTimeOffset RemovedAt, DateTimeOffset? PutBackAt)`. `Creator` is "a resident" when `CreatorId` starts with any case person's principal (`D` form, ignoring case).
  - `static bool TakedownRules.IsTakedownAudit(string action)` — true for `inventory-viewed` and anything starting `takedown-`.

- [ ] **Step 1: Write the failing tests.**
  - The new cases in `PeopleRulesTests`: Inventory on a resident by an admin → null; by a supervisor → `SupervisorOnly`; on an admin → `StaffFirst`; on self → `Self`; on the owner → `StaffChanges.OwnerRefusal`; on a suspended resident → null.
  - `TakedownRulesTests`:
    - `Sender_view_masks_a_creator_who_is_a_holder` (creator id = holder's id → "a resident"; a stranger's creator → their `CreatorName`)
    - `Sender_view_has_no_holder_name_or_folder`
    - `Illegal_may_not_tell`
    - `Defaults_tell_for_copyright_and_rules`
    - `Notice_without_sender_is_refused`
    - `Notice_text_over_4000_is_refused`
    - `Type_words_cover_the_table_and_unknown_is_item`
    - `Takedown_audit_actions_are_recognised`

- [ ] **Step 2: Run** — `dotnet test Launcher/DreamGrid.Launcher.Tests --filter "FullyQualifiedName~PeopleRulesTests|FullyQualifiedName~TakedownRulesTests"`. Expected: FAIL (no `Inventory`, no `TakedownRules`).

- [ ] **Step 3: Implement** both.

- [ ] **Step 4: Run** the same filter. Expected: all pass.

- [ ] **Step 5: Commit** — `Takedown: who may look at an inventory, and what the sender sees`.

### Task 4: Takedown actions, emails and the hold

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Security/TakedownActions.cs`
- Modify:
  - `Launcher/DreamGrid.Launcher/Security/AccountHolds.cs`
  - `Launcher/DreamGrid.Launcher/Security/PeopleActions.cs` (`DeleteRefusal` uses `holds.Refusal`)
  - `Launcher/DreamGrid.Launcher.Core/Email/EmailTemplates.cs`
  - `Launcher/DreamGrid.Launcher/Program.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/TakedownActionsTests.cs`, `Launcher/DreamGrid.Launcher.Tests/PeopleDeleteTests.cs` (update `HoldAll`, add hold cases)

**Interfaces:**
- Consumes: `IInventoryStore` (Task 1), `TakedownStore` and `EvidenceCopier` (Task 2), `TakedownRules` and `PersonTask.Inventory` (Task 3), and `PeopleActions.LookAsync` / `Refusal`, `EmailSender`, `AuditLog`, `AuditActor`.
- Produces:
  - `AccountHolds(TakedownStore store)`:
    - `public virtual string? Refusal(Guid principal)`. It returns "The takedown records couldn't be read, so nobody can be deleted now." when `store.Unreadable`; else `"This account is on hold: takedown " + number` for the first open case naming them; else null.
    - `public bool IsHeld(Guid principal) => Refusal(principal) is not null`.
    - `public IReadOnlyList<string> OpenCasesOf(Guid principal)`.
  - `TakedownActions`. Each method returns `ChangeOutcome` or a record with `Done`/`Message`:
    - `Task<TakedownOutcome> TakeDownAsync(ClaimsPrincipal actor, string? ip, Guid person, IReadOnlyCollection<Guid> items, string? intoCase, TakedownNotice? notice, bool tell, CancellationToken ct)`. A new case when `intoCase` is null (then `notice` is required); else added to that open case. `sealed record TakedownOutcome(bool Done, string Message, string? Number)`.
    - `Task<IReadOnlyList<CopyRow>> FindCopiesAsync(ClaimsPrincipal actor, string? ip, string number, CancellationToken ct)`. `sealed record CopyRow(InventoryHolding Holding, string? Refusal)`: refused when in-world (`InWorld`), staff, or owner (the `PeopleRules` sentences).
    - `Task<TakedownOutcome> TakeCopiesAsync(ClaimsPrincipal actor, string? ip, string number, IReadOnlyCollection<(Guid Owner, Guid Item)> picks, CancellationToken ct)`. Those holders are added as `CopyHolder`, and each one is removed and told as in `TakeDownAsync`.
    - `Task<TakedownOutcome> PutBackAsync(ClaimsPrincipal actor, string? ip, string number, Guid item, string? why, CancellationToken ct)`.
    - `TakedownOutcome AddNote(ClaimsPrincipal actor, string? ip, string number, string? text)`.
    - `TakedownOutcome Close(ClaimsPrincipal actor, string? ip, string number, string? outcome)`.
  - Email templates, both with words `{GridName}`, `{AvatarName}`, `{CaseNumber}`, `{Items}`, `{Reason}`, and `{CaseNumber}` and `{Items}` required. `{Items}` is the item names joined with ", ".
    - `TakedownRemoved = "takedown-removed"`, title "Item removed from your inventory".
      - Subject: "Items removed from your {GridName} inventory"
      - Body: "Hello {AvatarName},\n\nStaff on {GridName} removed these items from your inventory after a notice ({Reason}): {Items}\n\nThe case number is {CaseNumber}. If you think this is a mistake, reply to this email and give the case number."
    - `TakedownRestored = "takedown-restored"`, title "Item put back in your inventory".
      - Subject: "Items put back in your {GridName} inventory"
      - Body: "Hello {AvatarName},\n\nStaff on {GridName} put these items back in your inventory: {Items}\n\nThe case number is {CaseNumber}."

Behaviour every method shares:
- The person is checked with `PeopleActions.LookAsync` and `Refusal(actor, person, PersonTask.Inventory)`. When it refuses, the action is audited `refused` and the sentence is returned.
- An offline check (`Row.OnlineRegion is null`) comes before any write, and refuses with `PeopleActions.InWorld`.
- A closed case refuses every change with "This case is closed."
- An unknown case is "There is no such case."
- An actor who isn't the owner or an admin gets `PeopleRules.SupervisorOnly`.
- Order in `TakeDownAsync`:
  1. read the items (`ItemsAsync`; an id the person doesn't hold is "That item isn't in their inventory any more.")
  2. copy the evidence (`AssetHashAsync` then `EvidenceCopier.Copy`)
  3. save the case
  4. `RemoveAsync`; on `StaffDirectoryUnavailableException`, take the items back out of the case, save it, and return `DatabaseDown`
  5. email when `tell && MayTell(reason)` and they have an address
  6. audit
- `tell` is stored as `tell && MayTell(reason)`.
- `PutBackAsync` emails `takedown-restored` when the case's `TellResident` is true. When the holder has been deleted (`PutBackAsync` throws `InvalidOperationException`, or `LookAsync` finds no one), it answers "They have been deleted, so it can't be put back." and changes nothing.
- Each email sent and each Find copies run adds a case event (`emailed`, `copies-searched`).
- Messages on success:
  - "Removed N item(s) from First Last. Case TD-2026-001."
  - "Put back <item> in <folder name>."
  - "Note added."
  - "Case TD-2026-001 is closed."

- [ ] **Step 1: Write the failing tests** in `TakedownActionsTests`. Use `LauncherFactory` with `FakeInventoryStore`, a temp takedowns root, and a fake time. Get the service from `factory.Services`.
  - `Taking_down_removes_the_items_saves_the_case_and_copies_evidence`
  - `In_world_person_is_refused_and_nothing_changes`
  - `Supervisor_is_refused_and_audited`
  - `Database_down_leaves_no_removed_item_in_the_case`
  - `Copyright_case_emails_the_resident_once`
  - `Illegal_content_never_emails_even_when_tell_is_posted`
  - `Adding_to_an_open_case_keeps_its_number`
  - `Find_copies_lists_others_and_refuses_staff_and_in_world`
  - `Taking_copies_adds_copy_holders`
  - `Put_back_restores_and_records_why`
  - `Put_back_needs_a_why`
  - `Close_needs_an_outcome_and_makes_the_case_read_only`
  - `Audit_targets_never_contain_the_notice_text`
  - In `PeopleDeleteTests`:
    - `Open_takedown_refuses_delete_with_its_number`
    - `Closed_takedown_lets_delete_go_ahead`
    - `Unreadable_takedowns_refuse_every_delete`
    - The existing `HoldAll` overrides `Refusal` and returns `PeopleActions.OnHold`, so the old held test keeps its words.

- [ ] **Step 2: Run** — `dotnet test Launcher/DreamGrid.Launcher.Tests --filter "FullyQualifiedName~TakedownActionsTests|FullyQualifiedName~PeopleDeleteTests"`. Expected: build fails, because `TakedownActions` and `Refusal` are missing.

- [ ] **Step 3: Implement** the hold, the templates and `TakedownActions`, and register them as singletons.

- [ ] **Step 4: Run** the same filter. Expected: all pass. Then run the whole `dotnet test Launcher/DreamGrid.Launcher.Tests`. Expected: all pass. The email template tests that walk `EmailTemplates.All` must accept the two new ones.

- [ ] **Step 5: Commit** — `Takedown: take down, find copies, put back, close, and the hold`.

### Task 5: Pages

**Files:**
- Create in `Launcher/DreamGrid.Launcher/Pages/People/`:
  - `Inventory.cshtml(.cs)` — `@page "/people/{id:guid}/inventory"`
  - `Takedowns.cshtml(.cs)` — `@page "/people/takedowns"`
  - `TakedownNew.cshtml(.cs)` — `@page "/people/{id:guid}/takedown"`
  - `Takedown.cshtml(.cs)` — `@page "/people/takedowns/{number:regex(^TD-\\d{{4}}-\\d{{3,}}$)}"`
  - `TakedownSender.cshtml(.cs)` — the same route plus `/sender`
- Modify:
  - `TaskIcons.cs` (sixth icon `PersonTask.Inventory`, symbol `box`, label "Inventory", `LinkOf` → `/people/{id}/inventory`)
  - `Person.cshtml(.cs)` (the Takedowns and On hold lines; supervisors' history drops `TakedownRules.IsTakedownAudit` lines)
  - `Index.cshtml` (a "Takedowns" link)
  - `wwwroot/img/people-icons.svg` (the `box` symbol)
- Test: `Launcher/DreamGrid.Launcher.Tests/TakedownPagesTests.cs`, `Launcher/DreamGrid.Launcher.Tests/PeoplePagesTests.cs` (icon count 6)

**Interfaces:**
- Consumes: everything above.
- Produces the pages:
  - **Inventory.** `?folder=<guid>&q=<text>&page=<n>`. It shows the breadcrumb, subfolders (links), and items (name, type words, creator, created date from `CreationDate` unix seconds, checkbox `items`). The form posts GET to `/people/{id}/takedown` with the ticked `items` (a form GET that only shows a form, so no write). It writes `inventory-viewed` once per load.
  - **TakedownNew.** GET shows the chosen items, the notice fields (`Reason` select, `SenderName`, `Organisation`, `Address`, `Reference`, `Received` date, `Text` textarea, `Tell` checkbox), and an "Add to open case" select of open case numbers. POST calls `TakeDownAsync`, then redirects to the case on success.
  - **Takedowns.** A table: number (link), opened date, reason words, sender name, item count, people count, Open/Closed.
  - **Takedown.** The notice, people (name links to the person, with "named" / "holds a copy"), items (with Put back form + why, or "Put back <date>"), events oldest first, Find copies (POST, shows `CopyRow`s with checkboxes `picks` as `owner:item`, then a "Take these down too" POST), Add a note, Close (outcome). It ends with the note "Objects already placed in regions aren't covered. Return them in-world with the viewer's region tools." When the case is closed it shows the outcome and no forms.
  - **TakedownSender.** Only `SenderView`, the grid name, the notice sender / reference / received date, and the line "Objects already placed in regions are not covered by this list." No layout menu (`Layout = "_Print"` if one exists, otherwise a minimal layout with the site stylesheet).
  - **Person page.** "Takedowns: named in N (M open); held a copy in K" linking to `/people/takedowns`, and "On hold: takedown TD-…" linking to the case. Both are shown only to the owner and admins, and only when non-zero.

- [ ] **Step 1: Write the failing page tests** (AngleSharp, as `PeoplePagesTests`):
  - `Inventory_page_lists_folders_and_items_and_audits_the_view`
  - `Inventory_search_finds_items_in_trash`
  - `Supervisor_gets_403_on_every_takedown_page` (all five routes)
  - `Take_down_form_posts_and_redirects_to_the_case`
  - `Case_page_shows_put_back_and_find_copies_while_open_and_no_forms_when_closed`
  - `Sender_page_has_no_holder_name`
  - `Person_page_shows_the_hold_and_counts_to_admins_only`
  - `Supervisor_history_hides_takedown_lines`
  - `Inventory_icon_is_disabled_for_staff_with_the_reason`
  - `Takedown_pages_have_no_inline_script` (or extend `InlineScriptScanTests` if it walks all pages already)

- [ ] **Step 2: Run** — `dotnet test Launcher/DreamGrid.Launcher.Tests --filter "FullyQualifiedName~TakedownPagesTests|FullyQualifiedName~PeoplePagesTests"`. Expected: FAIL, with 404s and missing icons.

- [ ] **Step 3: Implement** the pages and the icon, in the markup style of `Delete.cshtml` and `Person.cshtml`.

- [ ] **Step 4: Run** the whole `dotnet test Launcher/DreamGrid.Launcher.Tests`. Expected: all pass.

- [ ] **Step 5: Commit** — `Takedown: inventory, case, list and sender pages`.

### Task 6: Docs

**Files:**
- Modify: `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` (2.5b done, with what it covers and what it leaves to 2.5c)

- [ ] **Step 1:** Mark 2.5b done. Note that the hold is in place for 2.5c to extend, and that evidence export is 2.5c's.
- [ ] **Step 2:** `dotnet build Launcher` and `dotnet test Launcher/DreamGrid.Launcher.Tests`. Expected: 0 errors, all pass.
- [ ] **Step 3: Commit** — `Docs: 2.5b inventory takedown done`.
