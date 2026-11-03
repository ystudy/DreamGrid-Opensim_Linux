# Groups Admin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Owner and admins manage the grid's groups on the website (rename, charter, members, delete) and keep one default group that new residents join, without changing OpenSim.

**Architecture:** A Core class writes OpenSim's core `os_groups_*` tables in the `robust` MariaDB database directly, one transaction per action, the way `GroupsService` writes them. A launcher state file (`guests/group-joins.json`) holds the default group and, per group, everyone the launcher knows has been in it. A web service (`GroupsAdmin`) joins the two, writes the audit log and is what the pages and the sign-up code call.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, MySqlConnector 2.6.2, xUnit v3, AngleSharp; integration tests against the real MariaDB + Robust.

**Spec:** `docs/superpowers/specs/2026-11-02-groups-admin-design.md`

## Global Constraints

- No changes to OpenSim code or config. `1DivaOpenSimServices` stays (Phase 3 removes it).
- SQL with parameters only. Every action that writes is one MariaDB transaction.
- Owner and admins only: `Pages/Groups/` gets `AdminPolicy` from the folder rule; add `GuestSideOnly` to the folder (404 on a Region Server or Connected Server).
- CSP: no inline scripts or styles (`InlineScriptScanTests`). Every form has the anti-forgery token (Razor's form tag helper does it).
- Group name: trimmed, 1–35 characters, unique (the table's `Name` is UNIQUE). Charter: at most 4000 characters (ruling below).
- The Everyone role is `RoleID = 00000000-0000-0000-0000-000000000000`. Adding never touches `os_groups_principals.ActiveGroupID`.
- A member ID that is a plain UUID is a local resident; anything else (`uuid;url;name`) is a hypergrid visitor: shown and removable, never added.
- The "already added" list only grows. It is written **before** the group tables; if writing it fails, nothing is added.
- Changes reach regions within about a minute (`GROUPS_CACHE_TIMEOUT = 60 s`); every success notice says "It can take about a minute to show in the viewer."
- English only until worklist 1.5.

Rulings made while planning (the spec leaves them open):
- Charter limit 4000 characters: the column is `text`; the viewer's own box is smaller, so this only stops a runaway paste.
- The automatic join is audited with actor `DreamGrid` and role `StaffRole.None.AuditName()`, target `"<group id> <resident name>"`.
- Confirmations are server-side: Delete needs a ticked `confirm` box; removing the last owner first answers with a warning and a second Remove button carrying `confirm=true`.

## Review Focus

1. A hypergrid member ID (`uuid;https://grid.example:8002/;Ann Smith`) posted back to Remove must match the stored row exactly — test in Task 2 (integration) and Task 4 (page).
2. A rename to a name that differs only in case from another group's — MariaDB's collation calls it a duplicate; the page must say the name is taken, not 500 — test in Task 2.
3. The database stops half-way through **Add everyone now** — the page says how many were added before it stopped; people already remembered stay remembered and are skipped next time — test in Task 3.
4. The default group is deleted (on the website or in the viewer) — the default is cleared or treated as none, and a new sign-up then joins nothing and doesn't fail — test in Task 3 and Task 5.
5. A supervisor posting straight to a group handler — refused like any admin page — test in Task 4.

---

### Task 1: Group rules and the joins file

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Groups/GroupRules.cs`
- Create: `Launcher/DreamGrid.Launcher.Core/Groups/GroupJoins.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/GroupRulesTests.cs`, `Launcher/DreamGrid.Launcher.Tests/GroupJoinsTests.cs`

**Interfaces:**
- Produces:
  - `static class GroupRules { const int MaxName = 35, MaxCharter = 4000; static readonly Guid Everyone = Guid.Empty; static string? NameProblem(string? name); static string? CharterProblem(string? charter); static bool IsLocal(string principalId, out Guid id); static string DisplayName(string principalId, string? first, string? last); }`
    - `NameProblem` returns null or `"Type a group name."` / `"A group name can be at most 35 characters."`; it checks the trimmed value and refuses control characters with `"A group name can't contain line breaks or tabs."`.
    - `CharterProblem` returns null or `"The charter can be at most 4000 characters."`.
    - `DisplayName`: local → `"First Last"` (or the ID when the account is gone); hypergrid → the third `;` part plus `" (visitor)"`.
  - `sealed record GroupJoinsState(Guid? DefaultGroup, Dictionary<Guid, HashSet<string>> Seen)`
  - `sealed class GroupJoins(string path, ILogger<GroupJoins>? log)`, on `JsonStateFile<GroupJoinsState>`, thread-safe:
    - `Guid? Default()`
    - `bool SetDefault(Guid group, IEnumerable<string> currentMembers)` — sets the default and adds the members to `Seen[group]` in one save.
    - `bool ClearDefault()`
    - `bool Has(Guid group, string principalId)` (case-insensitive on the ID)
    - `bool Remember(Guid group, IEnumerable<string> principalIds)` — returns false (and changes nothing in memory) when the save fails.
    - `bool GroupDeleted(Guid group)` — drops `Seen[group]`; clears the default when it was that group.

- [ ] **Step 1: Write the failing tests**

`GroupRulesTests`: `[Theory]` `Name_problem` with `""`/`"  "` → `"Type a group name."`, 36 × `"a"` → the 35 message, `"a\tb"` → the line-break message, `"  Builders  "` → null; `Charter_over_4000_is_refused`; `Plain_uuid_is_local` / `Hypergrid_id_is_not_local` (`"…;https://grid.example:8002/;Ann Smith"`); `Display_name_of_a_visitor` → `"Ann Smith (visitor)"`; `Display_name_of_a_missing_account_is_its_id`.

`GroupJoinsTests` (temp directory per test): `Starts_with_no_default`; `Set_default_records_current_members` (then `Has` is true for each, survives a new `GroupJoins` on the same file); `Remember_is_case_insensitive`; `Clear_default_keeps_the_list`; `Group_deleted_drops_its_list_and_clears_the_default`; `Failed_save_remembers_nothing` (make the path a directory so the save fails; `Remember` returns false and `Has` stays false).

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build Launcher/DreamGrid.Launcher.Tests`, then the built `DreamGrid.Launcher.Tests.exe -class "DreamGrid.Launcher.Tests.GroupRulesTests" -class "DreamGrid.Launcher.Tests.GroupJoinsTests"` (the runner takes `-class`, not `--filter-class`)
Expected: build fails — `GroupRules` and `GroupJoins` don't exist.

- [ ] **Step 3: Implement `GroupRules` and `GroupJoins`**

`GroupJoins` copies the shape of the other `guests/` stores (`TermsAcceptances`, `RegionRights`): load once, mutate a copy, `Save`, swap in the copy only when `Save` returns true.

- [ ] **Step 4: Run them to see them pass**

Same command. Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Launcher/DreamGrid.Launcher.Core/Groups Launcher/DreamGrid.Launcher.Tests/GroupRulesTests.cs Launcher/DreamGrid.Launcher.Tests/GroupJoinsTests.cs
git commit -m "Groups: name and charter rules, and the joins file"
```

### Task 2: The group tables (`GridGroups`)

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Groups/GridGroups.cs`
- Test: `Launcher/DreamGrid.Launcher.IntegrationTests/GridGroupsTests.cs`

**Interfaces:**
- Consumes: `GroupRules.Everyone`, `GroupRules.IsLocal`, `GroupRules.DisplayName`.
- Produces:
  - `sealed class GroupsUnavailableException(string message, Exception? inner = null) : Exception`
  - `sealed record GroupSummary(Guid Id, string Name, string Founder, int Members, bool OpenToJoin)`
  - `sealed record GroupMember(string PrincipalId, string Name, bool Local, IReadOnlyList<string> Roles, bool Owner)`
  - `sealed record GroupDetail(Guid Id, string Name, string Charter, string Founder, bool OpenToJoin, Guid OwnerRoleId, IReadOnlyList<GroupMember> Members)`
  - `enum RenameResult { Done, NoGroup, NameTaken }`, `enum AddResult { Added, AlreadyMember, NoGroup }`
  - `interface IGridGroups` (every method throws `GroupsUnavailableException` when the database can't be asked):
    - `Task<IReadOnlyList<GroupSummary>> ListAsync(CancellationToken ct)` — by name.
    - `Task<GroupDetail?> GetAsync(Guid group, CancellationToken ct)` — members by name.
    - `Task<RenameResult> UpdateAsync(Guid group, string name, string charter, CancellationToken ct)`
    - `Task<AddResult> AddMemberAsync(Guid group, Guid principal, CancellationToken ct)`
    - `Task<bool> RemoveMemberAsync(Guid group, string principalId, CancellationToken ct)` — false when they weren't a member.
    - `Task<bool> DeleteAsync(Guid group, CancellationToken ct)` — false when there was no such group.
    - `Task<IReadOnlyList<string>> MemberIdsAsync(Guid group, CancellationToken ct)`
    - `Task<IReadOnlyList<Guid>> ActiveResidentsAsync(CancellationToken ct)` — `UserAccounts` with `UserLevel >= 0`.
  - `sealed class GridGroups(Func<LauncherSettings> settings) : IGridGroups`

What each write does, in one transaction (`conn.BeginTransactionAsync`), IDs passed as strings:
- **Update:** `UPDATE os_groups_groups SET Name=@n, Charter=@c WHERE GroupID=@g`; a `MySqlErrorCode.DuplicateKeyEntry` is `NameTaken`; zero matched rows is `NoGroup` (use `UseAffectedRows=false` in the connection string, or check existence first).
- **Add:** the group must exist (`SELECT … FOR UPDATE`); a membership row already there is `AlreadyMember`; else insert `os_groups_membership (GroupID, PrincipalID, SelectedRoleID, Contribution, ListInProfile, AcceptNotices, AccessToken)` = (g, p, Everyone, 0, 1, 1, new GUID), insert `os_groups_rolemembership (g, Everyone, p)`, delete `os_groups_invites` for (g, p).
- **Remove:** delete from `os_groups_rolemembership`, `os_groups_membership`, `os_groups_invites` for (g, p); `UPDATE os_groups_principals SET ActiveGroupID=Everyone WHERE PrincipalID=@p AND ActiveGroupID=@g`.
- **Delete:** delete from `os_groups_rolemembership`, `os_groups_membership`, `os_groups_roles`, `os_groups_invites`, `os_groups_notices` for g; `UPDATE os_groups_principals SET ActiveGroupID=Everyone WHERE ActiveGroupID=@g`; delete the `os_groups_groups` row.

Reads: founder name and local member names by joining `UserAccounts` on the ID; role names from `os_groups_rolemembership` ⨝ `os_groups_roles`; `Owner` is "has the group's `OwnerRoleID`"; `OpenToJoin` is `OpenEnrollment` = `"1"`. Wrap failures exactly as `StaffDirectory.ReadAsync` does, as `GroupsUnavailableException`. Timeout 3 s like `StaffDirectory`.

- [ ] **Step 1: Write the failing integration tests**

In the `GridCollection`, with the same setup as `OnlineUsersTests` (start the grid, create the owner, `Robust` makes the `os_groups_*` tables). Seed groups with plain SQL inserts in a helper `SeedGroup(name, founder)` that writes the group row, the Everyone and Owners roles and the founder's membership and role rows, as `GroupsService.CreateGroup` would. One `[Fact]` per behaviour, sharing one started grid via a class fixture if the collection allows, else one `[Fact]` that runs them in order:
- `List_and_get_show_founder_members_and_roles`
- `Update_renames_and_sets_charter`; `Update_to_a_name_taken_in_another_case_is_NameTaken` (`"Builders"` vs `"builders"`); `Update_of_a_missing_group_is_NoGroup`
- `Add_writes_membership_and_everyone_role_and_drops_the_invite` (seed an invite first; assert `os_groups_principals` has no row for them)
- `Add_twice_is_AlreadyMember`
- `Remove_clears_roles_and_active_group`
- `Remove_a_hypergrid_member_by_its_full_id`
- `Delete_removes_every_row_and_clears_active_groups` (count rows in all six tables for the group = 0)
- `Active_residents_skip_pending_accounts` (an account at −1 is left out)
- `A_stopped_database_throws_GroupsUnavailable_quickly` (< 10 s, as in `OnlineUsersTests`)

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build Launcher/DreamGrid.Launcher.IntegrationTests -c Release`, then the built `DreamGrid.Launcher.IntegrationTests.exe -class "DreamGrid.Launcher.IntegrationTests.GridGroupsTests"`
Expected: build fails — `GridGroups` doesn't exist. (Without MariaDB and `bin/` locally the tests skip; CI sets `DREAMGRID_REQUIRE_INTEGRATION=1` and runs them for real.)

- [ ] **Step 3: Implement `GridGroups`**

- [ ] **Step 4: Run them to see them pass**

Same command. Expected: all pass (or skip locally with the reason "MariaDB …"; then they must pass in CI before Task 6 is called done).

- [ ] **Step 5: Commit**

```bash
git add Launcher/DreamGrid.Launcher.Core/Groups/GridGroups.cs Launcher/DreamGrid.Launcher.IntegrationTests/GridGroupsTests.cs
git commit -m "Groups: read and change the grid's group tables"
```

### Task 3: `GroupsAdmin`, the default group and the audit

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Groups/GroupsAdmin.cs`
- Create: `Launcher/DreamGrid.Launcher.Tests/FakeGridGroups.cs`
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (register `IGridGroups` → `GridGroups`, `GroupJoins` at `Path.Combine(paths.Guests, "group-joins.json")`, `GroupsAdmin`)
- Modify: `Launcher/DreamGrid.Launcher.Tests/LauncherFactory.cs` (a `Groups` fake replacing `IGridGroups`)
- Test: `Launcher/DreamGrid.Launcher.Tests/GroupsAdminTests.cs`

**Interfaces:**
- Consumes: Task 1 and Task 2 types; `AuditLog`, `AuditActor.Of(ClaimsPrincipal)`, `IRobustClient.FindAccountByIdAsync` (names for the audit).
- Produces:
  - `sealed record GroupOutcome(bool Done, string Message)`
  - `sealed record AddEveryoneOutcome(bool Done, int Added, int Skipped, string Message)`
  - `sealed class GroupsAdmin(IGridGroups groups, GroupJoins joins, AuditLog audit, ILogger<GroupsAdmin> log)` — one `SemaphoreSlim` around every default-group change and every add (so the button and a new sign-up never race):
    - `Task<GroupOutcome> UpdateAsync(ClaimsPrincipal actor, string? ip, Guid group, string? name, string? charter, CancellationToken ct)` — rules first; audits `group-renamed` (target `"<id> <new name>"`) when the name changed and `group-charter-changed` (target `"<id>"`) when the charter changed.
    - `Task<GroupOutcome> AddMemberAsync(ClaimsPrincipal actor, string? ip, Guid group, RobustAccount who, CancellationToken ct)` — remembers first (`joins.Remember`; false → `"Couldn't save the change. Try again."`, nothing added), then adds; audits `group-member-added`.
    - `Task<GroupOutcome> RemoveMemberAsync(ClaimsPrincipal actor, string? ip, Guid group, string principalId, bool confirmed, CancellationToken ct)` — when they are the group's only owner and not `confirmed`: `Done=false`, message `"<name> is the group's last owner. Removing them leaves the group with no owner. Press Remove again to confirm."`; audits `group-member-removed`.
    - `Task<GroupOutcome> DeleteAsync(ClaimsPrincipal actor, string? ip, Guid group, CancellationToken ct)` — then `joins.GroupDeleted(group)`; audits `group-deleted` (target `"<id> <name>"`).
    - `Task<GroupOutcome> MakeDefaultAsync(ClaimsPrincipal actor, string? ip, Guid group, CancellationToken ct)` — `joins.SetDefault(group, await groups.MemberIdsAsync(…))`; audits `group-default-set`.
    - `Task<GroupOutcome> ClearDefaultAsync(ClaimsPrincipal actor, string? ip, CancellationToken ct)` — audits `group-default-cleared`.
    - `Task<AddEveryoneOutcome> AddEveryoneAsync(ClaimsPrincipal actor, string? ip, CancellationToken ct)` — no default → `"There is no default group."`; skips current members and remembered IDs, remembers the current members and the ones it is about to add **in one `Remember` call**, then adds them one by one; a `GroupsUnavailableException` half-way stops and reports `"Added N before the database stopped answering. Press the button again once the world is running."`; success message `"Added N residents to <name>; skipped M who are already in it or were in it before. It can take about a minute to show in the viewer."`; one `group-add-everyone` audit line with target `"<id> added N skipped M"`.
    - `Task JoinNewResidentAsync(RobustAccount resident, CancellationToken ct)` — never throws: no default, a deleted default (`AddResult.NoGroup` → `joins.ClearDefault()`), an ID already remembered, a failed `Remember`, or `GroupsUnavailableException` are logged at warning (except "no default") and return. On success audits `group-auto-joined` (actor `DreamGrid`).
  - Every database failure in the page actions is `Done=false` with `"The grid's database isn't answering. Start the world, then try again."` (constant `GroupsAdmin.DatabaseDown`).

`FakeGridGroups` keeps groups, members (with role names and owner flag), invites and active-group in memory with the same results as `GridGroups`, and has a `FailAfter(int calls)` switch that makes later calls throw `GroupsUnavailableException`. It takes the `FakeRobustAccounts` for names and `ActiveResidentsAsync`.

- [ ] **Step 1: Write the failing tests** (`GroupsAdminTests`, on the fake, an admin `ClaimsPrincipal` built the way `StaffActionsTests` builds one, and a real `AuditLog` in a temp directory)
- `Update_refuses_a_bad_name_without_touching_the_group`
- `Update_audits_rename_and_charter_separately`
- `Add_remembers_before_adding` (make the joins file unwritable → `Done=false`, the fake has no new member)
- `Removing_the_last_owner_asks_first` then `…_with_confirm_removes`
- `Delete_clears_the_default`
- `Make_default_records_current_members` — a member who then leaves (remove them from the fake directly) is skipped by `AddEveryoneAsync`
- `Add_everyone_skips_members_and_remembered_and_counts` (3 active residents, 1 member, 1 remembered, 1 pending at −1 → Added 1, Skipped 2)
- `Add_everyone_twice_adds_nobody_the_second_time`
- `Add_everyone_stops_when_the_database_does` (`FailAfter`) — message names the count; the ones remembered stay remembered
- `Add_everyone_without_a_default_says_so`
- `New_resident_joins_the_default` (audit line `group-auto-joined`); `New_resident_with_no_default_does_nothing`; `New_resident_when_the_default_was_deleted_in_the_viewer_clears_it` (delete the group in the fake behind the service's back); `New_resident_when_the_database_is_down_does_not_throw`

- [ ] **Step 2: Run them to see them fail**

Run: the Launcher.Tests project with `-class "DreamGrid.Launcher.Tests.GroupsAdminTests"`.
Expected: build fails — `GroupsAdmin` doesn't exist.

- [ ] **Step 3: Implement `GroupsAdmin`, `FakeGridGroups`, the registrations**

- [ ] **Step 4: Run them to see them pass**

Same command. Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Launcher/DreamGrid.Launcher/Groups Launcher/DreamGrid.Launcher/Program.cs Launcher/DreamGrid.Launcher.Tests/FakeGridGroups.cs Launcher/DreamGrid.Launcher.Tests/LauncherFactory.cs Launcher/DreamGrid.Launcher.Tests/GroupsAdminTests.cs
git commit -m "Groups: admin actions, the default group and the audit"
```

### Task 4: The Groups pages and the menu

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Groups/Index.cshtml(.cs)` — `/groups`
- Create: `Launcher/DreamGrid.Launcher/Pages/Groups/Group.cshtml(.cs)` — `/groups/group?id=<guid>`
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (`AddFolderApplicationModelConvention("/Groups", m => m.Filters.Add(new GuestSideOnly()))`, next to the `/Splash` line)
- Modify: `Launcher/DreamGrid.Launcher/Pages/Shared/_Layout.cshtml` (a "Groups" link after "Splash page", inside the same `hasStaffPages` block)
- Modify: `Launcher/DreamGrid.Launcher.Tests/RegionsPageTests.cs` (the menu order now ends `…, "Splash page", "Groups", …`)
- Test: `Launcher/DreamGrid.Launcher.Tests/GroupsPagesTests.cs`

**Interfaces:**
- Consumes: `IGridGroups` (reads), `GroupJoins.Default()`, `GroupsAdmin` (every write), `IRobustClient.FindAccountAsync` and `AvatarName.Parse` for Add (the strict lookup as on the Staff page: `"No avatar called …"`, `StaffActions.RobustDown` when Robust doesn't answer).
- Produces: page handlers `OnGetAsync(string? q)` (Index), and on Group: `OnGetAsync(Guid id)`, `OnPostSaveAsync(Guid id, string? name, string? charter)`, `OnPostAddAsync(Guid id, string? avatar)`, `OnPostRemoveAsync(Guid id, string? member, bool confirm)`, `OnPostDeleteAsync(Guid id, bool confirm)`, `OnPostDefaultAsync(Guid id)`, `OnPostNoDefaultAsync(Guid id)`, `OnPostEveryoneAsync(Guid id)`. Each post redirects nowhere: it re-reads the group and shows `Notice` or `Error` (as `Pages/Staff/Index`), except a successful Delete, which redirects to `/groups` with the notice in `TempData`.

What the pages show:
- **Index:** heading "Groups"; a name filter (`q`, case-insensitive contains, kept in the box); a table — name (link), founder, members, open to join, and a "Default" badge; "No groups yet. Groups are made in the viewer." when there are none; `GroupsAdmin.DatabaseDown` text when `GroupsUnavailableException`.
- **Group:** name and charter form (Save); "This is the default group" with **Stop being the default** and **Add everyone now**, or **Make this the default group** with one line of what that means ("New residents join it when their sign-up is done. People who leave it aren't put back."); the members table (name, visitor mark, roles, Remove button posting `member` = the exact stored ID); the Add box (hypergrid visitors can't be added: the hint says so); the Delete form with a required checkbox "Delete <name> and remove its <N> members" (`confirm`); the last-owner warning shows a second Remove button with `confirm=true`. Unknown `id` → 404.

- [ ] **Step 1: Write the failing tests** (`GroupsPagesTests`, `LauncherFactory` with a public grid that has staff pages, as `SplashEditorPageTests` does; sign in with `/__test/signin?role=…`; post with the page's anti-forgery token)
- `[Theory] Supervisor_and_guest_cannot_open_or_post` over `GET /groups`, `GET /groups/group?id=…`, `POST /groups/group?handler=Delete…` → not 200 and the fake unchanged
- `Not_there_without_a_guest_side` (Region Server settings → 404)
- `Menu_has_groups_for_admins_only`
- `List_shows_groups_founder_count_and_default_badge`; `Filter_matches_part_of_the_name`; `Database_down_says_so`
- `Save_renames`; `Save_with_a_taken_name_says_taken_and_keeps_typed_text`
- `Add_by_name_adds`; `Add_an_unknown_name_says_no_avatar`
- `Remove_a_visitor_by_full_id` (`"…;https://grid.example:8002/;Ann Smith"`)
- `Remove_last_owner_warns_then_confirm_removes`
- `Delete_without_the_box_ticked_does_nothing`; `Delete_with_the_box_redirects_to_the_list_with_a_notice`
- `Make_default_then_add_everyone_reports_counts`
- `Unknown_group_is_404`
- `Posts_without_a_token_are_refused`

- [ ] **Step 2: Run them to see them fail**

Run: Launcher.Tests with `-class "DreamGrid.Launcher.Tests.GroupsPagesTests"`.
Expected: build fails or 404s — the pages don't exist.

- [ ] **Step 3: Implement the pages, the folder filter and the menu link**

- [ ] **Step 4: Run them, `RegionsPageTests` and `InlineScriptScanTests` to see them pass**

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Launcher/DreamGrid.Launcher/Pages/Groups Launcher/DreamGrid.Launcher/Program.cs Launcher/DreamGrid.Launcher/Pages/Shared/_Layout.cshtml Launcher/DreamGrid.Launcher.Tests/GroupsPagesTests.cs Launcher/DreamGrid.Launcher.Tests/RegionsPageTests.cs
git commit -m "Groups: the Groups pages and menu item"
```

### Task 5: New residents join the default group

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Guests/SignUps.cs` (constructor takes `GroupsAdmin`; `SubmitAsync`'s "let in" branch, after the `signup-made` audit and only when `owner is null` and the account is at level 0; `ConfirmAsync` after the `signup-confirmed` audit)
- Modify: `Launcher/DreamGrid.Launcher/Guests/SignUpApprovals.cs` (constructor takes `GroupsAdmin`; `ApproveAsync` after the `signup-approved` audit)
- Test: `Launcher/DreamGrid.Launcher.Tests/SignUpDefaultGroupTests.cs`

**Interfaces:**
- Consumes: `GroupsAdmin.JoinNewResidentAsync(RobustAccount, CancellationToken)` — awaited with `CancellationToken.None` (the work runs to the end, as the rest of sign-up does); it never throws.

- [ ] **Step 1: Write the failing tests** (through the real pages with `LauncherFactory`, as `SignUpFlowTests` and `SignUpApprovalsTests` do; a default group set in the fake first)
- `Open_sign_up_joins_the_default_group`
- `Email_sign_up_joins_only_after_the_link` (in the group after `/guest/confirm`, not before)
- `Staff_approved_sign_up_joins_on_approval`; `Denied_sign_up_never_joins`
- `Decoy_sign_up_never_joins` (an address already on the grid)
- `Restored_suspended_account_is_not_joined` (a level change through the People page)
- `Sign_up_still_works_when_the_groups_database_is_down` (`FailAfter(0)`)
- `Sign_up_with_the_default_deleted_in_the_viewer_joins_nothing`

- [ ] **Step 2: Run them to see them fail**

Run: Launcher.Tests with `-class "DreamGrid.Launcher.Tests.SignUpDefaultGroupTests"`.
Expected: the join assertions fail (nobody is added).

- [ ] **Step 3: Call `JoinNewResidentAsync` at the three places**

- [ ] **Step 4: Run them and every `SignUp*Tests` class to see them pass**

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Launcher/DreamGrid.Launcher/Guests Launcher/DreamGrid.Launcher.Tests/SignUpDefaultGroupTests.cs
git commit -m "Groups: new residents join the default group"
```

### Task 6: Worklist and the whole suite

**Files:**
- Modify: `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` (2.8: tick the admin view and the "decided" line, add a line for the default group and plan 13; leave "Delete `1DivaOpenSimServices`" for Phase 3; add an unticked "The owner's check on the VM, in Firestorm" line)

- [ ] **Step 1: Update the worklist**
- [ ] **Step 2: Run the whole unit suite**

Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests -c Release`
Expected: no failures.

- [ ] **Step 3: Run the integration suite (if MariaDB and `bin/` are here; CI otherwise)**

Run: `dotnet test --project Launcher/DreamGrid.Launcher.IntegrationTests -c Release`
Expected: no failures; `GridGroupsTests` ran, not skipped, in CI.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md
git commit -m "Worklist: the groups admin is built"
```
