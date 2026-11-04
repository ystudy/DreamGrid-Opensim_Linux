# People Admin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Staff find anyone on the grid by part of their name and, from a paged list with task icons or the person's own page, edit their name and email, send a reset link that also clears two-factor, sign them out everywhere (in-world too), suspend or unsuspend them, and delete a suspended resident completely.

**Architecture:**
- **Data access in Core.** A reader (`PeopleDirectory`) queries `robust.UserAccounts` with `GridUser` and `Presence` directly, the way `StaffDirectory` and `OnlineUsers` do. A writer (`RobustNames`) does the same for names, and `AccountRemover` grows into a full delete.
- **Rules.** One pure class (`PeopleRules`) answers "may this actor do this task to this person, and if not, why", on top of `StaffChanges`.
- **Actions.** One web service (`PeopleActions`) does each task, writes the audit log and sends the emails. The pages only draw and post.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, MySqlConnector 2.6.2, xUnit v3, AngleSharp; integration tests against the real MariaDB + Robust (`DreamGrid.Launcher.IntegrationTests`).

**Spec:** `docs/superpowers/specs/2026-11-03-people-admin-design.md`

## Global Constraints

**Database and code**
- No changes to OpenSim code or config.
- SQL with parameters only. Search text is a `LIKE` value, with `\`, `%` and `_` escaped and `ESCAPE '\\'`. It is trimmed and limited to 64 characters.
- Page size 25. Sort: last name, then first name.

**Pages and access**
- `/people` and everything under it stays behind `StaffPolicy` and `StaffPagesOnly`, as today.
- Every task is a POST with the antiforgery token. The rules are rechecked on the post, never trusted from the page.
- CSP is unchanged: no inline script or style (`InlineScriptScanTests`). Icons come from one SVG sprite file, `wwwroot/img/people-icons.svg`, used with `<use href="/img/people-icons.svg#…">`.
- Every icon is a `<button>` with `aria-label` and `title`. A disabled icon has `disabled`, and its `title` is the refusal sentence.

**Who may do what**
- **Owner and admins:** edit, reset link, sign out everywhere and delete, on residents only (`StaffRole.None`, judged by `RoleView.Judged`).
- **Suspend and unsuspend:** exactly as `StaffChanges` says today.
- **Supervisors:** suspend and unsuspend only.
- Nobody uses a task on themself, and the owner can't be changed.

**Fixed values**
- Names follow `SignUpRules.NameProblem`; email follows the `SignUpRules` email rules and `NormalEmail`.
- Held names last 30 days.
- A staff reset link lasts 30 minutes, as `PasswordResets.LinkLifetime`.
- Delete works only when `UserLevel < 0` and the person isn't a waiting sign-up.

**Audit actions**
- `name-changed` (target `"Old Name -> New Name"`)
- `email-changed` (target the name; never the addresses)
- `reset-sent`
- `signed-out`
- `deleted`
- the existing `suspended` / `unsuspended`
- Results are `ok`, `refused` or `failed`.

**Language**
- English only until worklist 1.5.

Rulings made while planning (the spec leaves them open):
- **Name change on the database.** It is `UPDATE robust.UserAccounts SET FirstName=@f, LastName=@l WHERE PrincipalID=@id`. Robust's `UserAccountService` has no cache, and regions pick up the name at the next login (the spec's warning covers the rest).
- **"Online" means a `Presence` row** whose `RegionID` isn't all zeros. Kicking needs that region to be one of this launcher's running regions; when it isn't, the page says so and only the website sessions end.
- **Kicking** is the region console's `kick user <First> <Last> <message>`. The names are already limited to letters, digits and `-`. The message is fixed: `Your session was ended by staff.`
- **A staff reset link is its own token purpose,** `staff-reset-password`. `OpenAsync` accepts both purposes. For the staff one it ignores the Off rule and never asks for a code, and `ResetOneAsync` turns two-factor off after the password is written.
- **The person page's history** is the last 20 audit lines whose `target` starts with the person's current name. It reads the audit files (current plus rotated) newest first, at most 2 MB in all.
- **Leftover estate removal is SQL on the `opensim` database:** `estate_managers`, `estate_users`, `estate_groups`, `estateban`, then `estate_settings`. It runs only when `estate_map` has no region in that estate. Before that, delete calls `RegionRights.Remove` for each region they hold. While `RegionRights.PendingMoves()` still has one of them, delete answers "Their regions are moving back to the grid's estate. Try again in a minute." and changes nothing else.

## Review Focus

1. **A name with `%` or `_`** (an avatar called `Ann_B`, or a search for `50%`) must match literally, not as a wildcard. Test in Task 1 (integration).
2. **A suspended admin is still treated as an admin.** Judged by `RoleView.Judged`, an admin can't edit, reset, sign out or delete them. Test in Task 2.
3. **A rename to a name that differs only in case** from another account's: the taken check must call it taken (MariaDB's collation does). Test in Task 3 (integration) and Task 5.
4. **A delete that fails halfway** (the opensim database is down after the robust transaction committed): the page says the account is gone but the estate wasn't removed. The audit result is `failed` with that detail, and a second delete attempt doesn't 500. Test in Task 6.
5. **A staff reset link opened after recovery is turned Off for residents:** it still works and asks no code. A resident's own Forgot-password link opened at the same time is still refused. Test in Task 4.

---

### Task 1: People directory (search, paging, last seen)

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Database/PeopleDirectory.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/PeopleDirectoryTests.cs` (the escaping and the fake), `Launcher/DreamGrid.Launcher.IntegrationTests/PeopleDirectoryIntegrationTests.cs` (the real tables)
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (register `IPeopleDirectory`), `Launcher/DreamGrid.Launcher.Tests/LauncherFactory.cs` (a `FakePeopleDirectory` built over `f.Robust`)

**Interfaces:**
- Produces:
  - `enum PeopleFilter { Everyone, Staff, Suspended, Waiting }`
  - `sealed record PersonRow(RobustAccount Account, DateTimeOffset? LastLogin, DateTimeOffset? LastLogout, Guid? OnlineRegion)`
  - `sealed record PeoplePage(IReadOnlyList<PersonRow> Rows, int Total, int Page, int PageSize)`
  - `interface IPeopleDirectory`:
    - `Task<PeoplePage> SearchAsync(string? text, PeopleFilter filter, IReadOnlyCollection<Guid> waiting, int staffLevel, int page, CancellationToken ct)`
    - `Task<PersonRow?> GetAsync(Guid principal, CancellationToken ct)`
  - `sealed class PeopleDirectory(Func<LauncherSettings> settings) : IPeopleDirectory`
    - `const int PageSize = 25, MaxSearch = 64`
    - `static string LikePattern(string? text)`: trimmed, cut to 64, escaped, wrapped in `%…%`. Empty gives `"%"`.
    - Failures throw `PeopleDirectoryUnavailableException`.
  - Filters: Staff is `UserLevel >= staffLevel`. Suspended is `UserLevel < 0 AND PrincipalID NOT IN waiting`. Waiting is `PrincipalID IN waiting`; with an empty list it returns an empty page without querying.
  - The text matches `CONCAT(FirstName,' ',LastName)`.

- [ ] **Step 1: Write the failing tests.**
  - `PeopleDirectoryTests`, a `[Theory]` `Like_pattern`:
    - `null` → `"%"`
    - `" ann "` → `"%ann%"`
    - `"50%"` → `"%50\\%%"`
    - `"Ann_B"` → `"%Ann\\_B%"`
    - `"a\\b"` → `"%a\\\\b%"`
    - 70 × `"x"` → 64 `x` inside `%…%`
  - `PeopleDirectoryIntegrationTests` (`[Collection(GridCollection.Name)]`, like `GridGroupsTests`). Make 30 residents on a started grid, plus `Ann_B Test`, `Annie Rose` (level −1) and `Sam Staff` (level 100). Then:
    - `Search_finds_part_of_a_name` (`"ann"` finds both Ann accounts)
    - `Underscore_is_literal` (`"n_B"` finds only `Ann_B Test`)
    - `Pages_are_25_and_total_counts_all`
    - `Staff_filter`
    - `Suspended_filter_leaves_out_waiting` (pass Annie's id as waiting → not in Suspended, only in Waiting)
    - `Last_login_comes_from_GridUser` (after a real `XmlRpcLogin`)
    - `Online_region_comes_from_Presence`
- [ ] **Step 2: Run them and see them fail.**
  - Run `dotnet build Launcher/DreamGrid.Launcher.Tests`, then the built `DreamGrid.Launcher.Tests.exe -class "DreamGrid.Launcher.Tests.PeopleDirectoryTests"` (the runner takes `-class`).
  - Expected: build fails, because `PeopleDirectory` doesn't exist.
- [ ] **Step 3: Implement `PeopleDirectory`, the fake and the registration.**
  - Shape it like `StaffDirectory`: one connection per call, a 3 s connect timeout.
  - Run one `COUNT(*)` query and one page query, with `LEFT JOIN GridUser g ON g.UserID = u.PrincipalID`.
  - Get the online region with `(SELECT p.RegionID FROM Presence p WHERE p.UserID = u.PrincipalID AND p.RegionID <> '00000000-0000-0000-0000-000000000000' LIMIT 1)`.
  - `GridUser.Login` and `Logout` are Unix seconds as text; `"0"` or an empty value means none.
- [ ] **Step 4: Run the unit tests, then the integration class.**
  - Unit tests: as above.
  - Integration: `DreamGrid.Launcher.IntegrationTests.exe -class "DreamGrid.Launcher.IntegrationTests.PeopleDirectoryIntegrationTests"`.
  - Expected: all pass.
- [ ] **Step 5: Commit** — `People: search, page and last-seen reader`.

### Task 2: Who may do which task

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Security/PeopleRules.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/PeopleRulesTests.cs`

**Interfaces:**
- Consumes: `StaffChanges.Refusal(StaffRole actor, StaffRole target, bool targetIsOwner, StaffChange change, bool targetIsActor)`, `StaffRole`.
- Produces:
  - `enum PersonTask { Edit, ResetLink, SignOut, Suspend, Unsuspend, Delete }`
  - `sealed record PersonFacts(StaffRole Judged, bool IsOwner, bool IsActor, bool Suspended, bool Waiting, bool HasEmail, bool EmailReady)`
  - `static class PeopleRules`:
    - `static string? Refusal(StaffRole actor, PersonFacts target, PersonTask task)`
    - Suspend and Unsuspend delegate to `StaffChanges.Refusal`. Waiting sign-ups get `"Approve or deny them on the Sign-ups page."` for every task.
  - Exact refusal sentences, in check order:
    - `"The grid owner can't be changed here."`
    - `"Use your own account page."`
    - `"Supervisors can only suspend or unsuspend."`
    - `"Change staff on the Staff page first."` (Edit, ResetLink, SignOut, Delete on a judged Supervisor or Admin)
    - ResetLink: `"Email isn't set up."`, `"No email address."`, `"Suspended."`
    - Delete: `"Suspend them first."`

- [ ] **Step 1: Write the failing tests.**
  - A `[Theory]` table covering each sentence above.
  - `Admin_can_do_everything_to_a_resident` (Delete only when `Suspended`).
  - `Suspended_admin_is_judged_as_admin` (Judged = Admin, Suspended = true → Edit/Delete refused with the Staff page sentence).
  - `Supervisor_can_suspend_a_resident_and_nothing_else`.
  - `Unsuspend_follows_StaffChanges`.
- [ ] **Step 2: Run them and see them fail.** Run with `-class "DreamGrid.Launcher.Tests.PeopleRulesTests"`. Expected: build fails.
- [ ] **Step 3: Implement `PeopleRules.Refusal`.**
- [ ] **Step 4: Run them and see them pass.**
- [ ] **Step 5: Commit** — `People: one rule table for every task`.

### Task 3: Names: writing, holding the old one, and the notices

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher.Core/OpenSim/RobustNames.cs`
  - `Launcher/DreamGrid.Launcher.Core/Guests/HeldNames.cs`
- Modify:
  - `Launcher/DreamGrid.Launcher.Core/Guests/SignUpRules.cs`: `NameProblem` takes an optional `Func<string, bool>? held` that counts as taken.
  - `Launcher/DreamGrid.Launcher/Guests/SignUps.cs`: passes `HeldNames.IsHeld`.
  - `Launcher/DreamGrid.Launcher.Core/Email/EmailTemplates.cs`: four new templates.
  - `Program.cs`: registrations, with `HeldNames` at `<data>/guests/held-names.json`.
- Test:
  - `HeldNamesTests.cs`
  - `SignUpRulesTests.cs` (add)
  - `EmailTemplatesTests.cs` (add)
  - integration `RobustNamesIntegrationTests.cs`

**Interfaces:**
- Produces:
  - `interface IRobustNames { Task SetAsync(Guid principal, string first, string last, CancellationToken ct); }`
    - `RobustNames(Func<LauncherSettings>)`. It throws `RobustNamesUnavailableException`, and `NameTakenException` when the UPDATE breaks the unique name index (MySQL error 1062).
  - `sealed class HeldNames(string path, TimeProvider time, ILogger<HeldNames>? log)` on `JsonStateFile`:
    - `static readonly TimeSpan HoldFor = TimeSpan.FromDays(30)`
    - `bool Hold(string fullName, Guid formerOwner)`
    - `bool IsHeld(string fullName)` (case-insensitive; expired entries don't count and are dropped on the next save)
    - `bool Release(Guid formerOwner)` (used by delete)
  - `EmailTemplates`:
    - `NameChanged = "name-changed"`: words `{GridName} {OldName} {AvatarName}`.
    - `EmailChangedByStaff = "email-changed-by-staff"`, to the old address: words `{GridName} {AvatarName} {Link}`, where `{Link}` is the Forgot-password page.
    - `EmailSetByStaff = "email-set-by-staff"`, to the new address: words `{GridName} {AvatarName}`.
    - `StaffResetPassword = "staff-reset-password"`: words `{GridName} {AvatarName} {Link} {ExpiresIn}`; required `{Link} {ExpiresIn}`. Its text says two-factor will be turned off when they set the new password.
    - All four are added to `All`.

- [ ] **Step 1: Write the failing tests.**
  - `HeldNamesTests`:
    - `Held_name_is_taken_for_30_days` (a `FakeTimeProvider` +29 days held, +31 not)
    - `Case_does_not_matter`
    - `Release_drops_the_owners_names`
    - `Survives_a_restart`
  - `SignUpRulesTests.Held_name_counts_as_taken`.
  - `EmailTemplatesTests`: words and required lists for the four templates.
  - `RobustNamesIntegrationTests`:
    - `Rename_writes_UserAccounts` (then `FindAccountAsync` finds the new name and not the old)
    - `Rename_to_a_case_variant_of_another_name_is_taken` (`NameTakenException`)
- [ ] **Step 2: Run them and see them fail.** Expected: build fails.
- [ ] **Step 3: Implement `RobustNames`, `HeldNames`, the `SignUpRules` overload and the templates' default text.** `RobustNames` copies `RobustEmails`' shape.
- [ ] **Step 4: Run them and see them pass** (unit tests, then the integration class).
- [ ] **Step 5: Commit** — `People: rename writer, held names and the staff notices`.

### Task 4: Staff reset link that clears two-factor

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Guests/PasswordResets.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/PasswordResetTests.Staff.cs` (new partial, beside `PasswordResetTests.Reset.cs`)

**Interfaces:**
- Consumes: `EmailTokens.Issue/Find/TryRedeem`, `TwoFactorStore.Disable(Guid)`, `ResetLimits.SendAllowed/CountSend`, `EmailTemplates.StaffResetPassword`.
- Produces:
  - `public const string StaffPurpose = "staff-reset-password"`
  - `ResetLink` gains `bool ByStaff` as its last member, so `new ResetLink(p, a, role, needsCode, ByStaff: false)` is used at the existing call site.
  - `enum StaffResetResult { Sent, NoEmail, Limited, EmailOff }`
  - `StaffResetResult SendByStaff(RobustAccount account)`:
    - It issues a `StaffPurpose` token (30 min) and revokes any earlier staff token for the same person.
    - It counts against `limits.SendAllowed/CountSend`, so the per-account limit applies; no per-IP limit.
    - It queues `StaffResetPassword`. The audit is written by `PeopleActions` (Task 5), not here.
  - `OpenAsync`: tries `Purpose`, then `StaffPurpose`. For a staff link it skips the Off check and sets `NeedsCode = false`.
  - `ResetOneAsync`: redeems the purpose the link came from. For `ByStaff` it calls `StoreFor(role).Disable(principal)` after the password is written, when its state isn't `Off`. It audits `password-reset` with target `"two-factor cleared"` when it did.

- [ ] **Step 1: Write the failing tests.**
  - `Staff_link_needs_no_code_and_clears_two_factor` (a resident with two-factor on, `RecoveryRule.EmailLinkAndCode`).
  - `Staff_link_works_when_recovery_is_off_for_residents`.
  - `Own_link_is_still_refused_when_recovery_is_off` (Review Focus 5).
  - `Unused_staff_link_changes_nothing` (two-factor still on after `SendByStaff` alone).
  - `Staff_send_keeps_the_per_account_limit` (the fourth send in an hour is `Limited`).
  - `Second_staff_link_replaces_the_first`.
  - `No_email_address` → `NoEmail`.
- [ ] **Step 2: Run them and see them fail.** Run with `-class "DreamGrid.Launcher.Tests.PasswordResetTests"`.
- [ ] **Step 3: Implement it.**
- [ ] **Step 4: Run them and see them pass,** together with the existing `PasswordResetTests`.
- [ ] **Step 5: Commit** — `Password reset: staff-sent links skip the code and clear two-factor`.

### Task 5: People actions (edit, reset, sign out, suspend), with the in-world kick

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Security/PeopleActions.cs`
- Modify:
  - `Launcher/DreamGrid.Launcher.Core/Grid/IGridController.cs` and `GridController.cs`: `KickAsync`.
  - The test fake grid controller in `LauncherFactory.cs`: it records `"Kick <region> <First> <Last>"`.
  - `Program.cs`.
- Test: `Launcher/DreamGrid.Launcher.Tests/PeopleActionsTests.cs`

**Interfaces:**
- Consumes:
  - `IPeopleDirectory`, `PeopleRules`, `StaffActions.RoleOf/ApplyAsync`
  - `IRobustNames`, `IRobustEmails`, `HeldNames`, `PasswordResets.SendByStaff`
  - `StaffSessions.EndAll`, `GuestStamps.Rotate`, `SignUpBoard`, `EmailSender`, `AuditLog`
- Produces:
  - `Task KickAsync(Guid regionId, string first, string last, string message, CancellationToken ct)` on `IGridController`.
    - It runs `kick user {first} {last} {message}` through `OnRegionConsoleAsync`.
    - It throws `InvalidOperationException` when the region isn't one of ours or isn't running.
  - `sealed record PersonView(PersonRow Row, PersonFacts Facts, RoleView Role)`.
  - `sealed class PeopleActions(...)`:
    - `Task<PersonView?> ViewAsync(ClaimsPrincipal actor, Guid principal, CancellationToken ct)`
    - `Task<IReadOnlyList<PersonView>> ViewsAsync(ClaimsPrincipal actor, PeoplePage page, CancellationToken ct)`, for the list.
    - `string? Refusal(ClaimsPrincipal actor, PersonView person, PersonTask task)`
    - `Task<ChangeOutcome> EditAsync(ClaimsPrincipal actor, string? ip, Guid principal, string? first, string? last, string? email, CancellationToken ct)`
      - **Rename:** refused while online with `"Sign them out first: they are in-world."`. It holds the old name (`HeldNames.Hold`), sends `NameChanged` when they have an email, and audits `name-changed`.
      - **Email:** refused when the address is in use by another account (`IStaffDirectory.FindByEmailAsync`). It sends `EmailChangedByStaff` to the old address and `EmailSetByStaff` to the new one, and audits `email-changed`.
      - **The taken check:** a new name is checked with `SignUpRules.NameProblem` with `HeldNames.IsHeld`, so a held name counts as taken, and then with `FindAccountAsync`.
      - Unchanged fields are skipped. With nothing changed, the outcome is `(false, "Nothing was changed.")`.
    - `Task<ChangeOutcome> SendResetAsync(...)`: audits `reset-sent`.
    - `Task<ChangeOutcome> SignOutAsync(...)`:
      - It ends the sessions and rotates the stamp.
      - When `OnlineRegion` is set, it calls `KickAsync`. If that throws, the message says `"Signed out of the website. They are still in-world: the region they are in couldn't be reached."` and the audit result is `failed`.
    - `Task<ChangeOutcome> SuspendAsync(...)` and `UnsuspendAsync(...)` call `StaffActions.ApplyAsync`. A successful suspend then calls the same kick as `SignOutAsync`.
  - Every method rechecks `Refusal` first and audits a refusal as `refused`.

- [ ] **Step 1: Write the failing tests.** Use the fake Robust and the fake grid in `LauncherFactory`.
  - `Rename_writes_holds_old_name_emails_and_audits`.
  - `Rename_to_a_taken_name_is_refused` (also a case variant through the fake).
  - `Rename_while_online_is_refused`.
  - `Email_change_tells_old_and_new_address` (two queued emails, the right templates).
  - `Email_in_use_by_another_account_is_refused`.
  - `Clearing_email_tells_the_old_address`.
  - `Sign_out_kicks_when_online` (`"Kick <region> Gail Guest"` recorded, stamp rotated).
  - `Sign_out_when_kick_fails_says_so`.
  - `Suspend_also_kicks`.
  - `Supervisor_posting_edit_is_refused_and_audited`.
  - `Nothing_changed`.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement `KickAsync` and `PeopleActions`.**
- [ ] **Step 4: Run them and see them pass.** Also run `StaffChangesTests` and `StaffPagesTests`.
- [ ] **Step 5: Commit** — `People: edit, reset link, sign out everywhere, suspend with kick`.

### Task 6: Delete a suspended resident completely

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Database/AccountRemover.cs`.
  - Today it serves sign-ups only. It gains a full delete beside the current one, and the current `DeleteAsync` stays for sign-ups.
- Create:
  - `Launcher/DreamGrid.Launcher.Core/Database/EstateRemover.cs`
  - `Launcher/DreamGrid.Launcher/Security/AccountHolds.cs`
- Modify: `PeopleActions.cs` (`DeleteAsync`), `Program.cs`.
- Test:
  - integration `AccountRemoverIntegrationTests.cs`
  - unit `PeopleDeleteTests.cs`

**Interfaces:**
- Produces:
  - `IAccountRemover`:
    - `Task<IReadOnlyList<string>> DeleteEverythingAsync(Guid principal, CancellationToken ct)`. It is one transaction on `robust` and returns the tables it removed rows from.
    - `static IReadOnlyList<string> EverythingStatements`.
  - The statement list for `robust`:
    - `inventoryitems`, `inventoryfolders`
    - `Friends` (`PrincipalID = @id` **or** `Friend = @id`)
    - `os_groups_membership`, `os_groups_rolemembership`, `os_groups_invites`, `os_groups_principals`
    - `userprofile`, `userpicks`, `userclassifieds`, `usernotes` (`useruuid` and `targetuuid`), `usersettings`
    - `im_offline` (`PrincipalID`)
    - `MuteList`, `Avatars`, `GridUser`, `Presence`, `tokens`, `auth`, `UserAccounts`
    - Step 1 checks each name and column against the real migrated tables. A table that doesn't exist in this Robust is left out of the list, with a code comment saying so. It is never skipped at run time.
  - `interface IEstateRemover { Task<bool> RemoveIfEmptyAsync(int estateId, CancellationToken ct); }`
    - It works on the `opensim` database. It returns false and touches nothing when `estate_map` still has a region in that estate.
  - `sealed class AccountHolds { bool IsHeld(Guid principal) => false; }`. This is the one place 2.5b and 2.5c will later make refuse; it says so in its summary.
  - `PeopleActions`:
    - `Task<DeletePreview?> PreviewDeleteAsync(ClaimsPrincipal actor, Guid principal, CancellationToken ct)`
      - `sealed record DeletePreview(PersonView Person, IReadOnlyList<string> RegionsMoving, string? Refusal)`.
    - `Task<ChangeOutcome> DeleteAsync(ClaimsPrincipal actor, string? ip, Guid principal, CancellationToken ct)`, in this order:
      1. Recheck `Refusal`, then `AccountHolds.IsHeld` (`"This account is on hold."`).
      2. For each `RegionRights.OwnedBy` region, call `RegionRights.Remove`. If any of their regions is in `PendingMoves()`, return the moving-back sentence from the rulings.
      3. Kick if online.
      4. `DeleteEverythingAsync`.
      5. `EstateRemover.RemoveIfEmptyAsync` for `RegionRights.EstateOf(owner)`, then drop the `OwnerEstate` entry. A new `RegionRights.ForgetOwner(Guid owner)` removes their `Estates` entry and their remaining rights.
      6. `HeldNames.Release`, `SuspendedStore.Forget`, `GuestStamps` and `StaffSessions.EndAll`.
      7. Audit `deleted` with the name.
    - When step 5 fails after step 4, the outcome says `"{Name} was deleted, but their empty estate couldn't be removed. Check the log."` and the audit result is `failed`.

- [ ] **Step 1: Check the table list.**
  - On a started test grid, run `SHOW TABLES` and `SHOW COLUMNS` for each table in the list (an ad-hoc integration test, or the `mariadb` client the tests start).
  - Put the confirmed list in `EverythingStatements`, with a one-line comment on any change.
- [ ] **Step 2: Write the failing tests.**
  - `AccountRemoverIntegrationTests.Delete_everything_leaves_no_row_for_the_avatar`:
    - Make the avatar on a started grid.
    - Give it a friend both ways, a group membership, a profile and pick, an offline IM and an inventory.
    - Delete it, then query every listed table for its id and expect zero rows.
    - The friend's own account and the group still exist.
  - `Estate_with_a_region_is_not_removed`, and `Empty_estate_is_removed_with_its_managers_and_bans`.
  - `PeopleDeleteTests`:
    - `Not_suspended_is_refused` (`"Suspend them first."`)
    - `Held_account_is_refused` (with a test `AccountHolds` that says true)
    - `Regions_go_back_first_and_delete_waits`
    - `Delete_after_moves_removes_everything_and_audits`
    - `Estate_failure_after_delete_says_so` (Review Focus 4)
    - `Second_delete_of_a_gone_account_is_a_message_not_an_error`
- [ ] **Step 3: Run them and see them fail.**
- [ ] **Step 4: Implement it.**
- [ ] **Step 5: Run them and see them pass.** Run the unit class and the integration classes, plus `SignUpCleanupTests` (the old `DeleteAsync` is unchanged).
- [ ] **Step 6: Commit** — `People: delete a suspended resident and everything tied to them`.

### Task 7: The pages

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Pages/People/Index.cshtml` and `.cshtml.cs` (the list).
- Create:
  - `Pages/People/Person.cshtml` and `.cs`: `@page "/people/{id:guid}"`, the details, the task buttons and the recent history.
  - `Pages/People/Edit.cshtml` and `.cs`: `/people/{id:guid}/edit`.
  - `Pages/People/Delete.cshtml` and `.cs`: `/people/{id:guid}/delete`, the "Are you sure?" page.
  - `Pages/People/_TaskIcons.cshtml`: the row of five icon buttons, used by the list and the person page.
  - `wwwroot/img/people-icons.svg`: symbols `edit`, `key`, `signout`, `pause`, `play`, `trash`.
  - `Launcher/DreamGrid.Launcher.Core/Logs/AuditLog.cs`: `IReadOnlyList<AuditEntryRead> Recent(Func<AuditEntryRead, bool> match, int max)`, newest first. It reads the current and rotated files, at most 2 MB, with `FileShare.ReadWrite | FileShare.Delete`. `AuditEntryRead` is the entry plus `DateTimeOffset At`.
- Test: `Launcher/DreamGrid.Launcher.Tests/PeoplePagesTests.cs` (AngleSharp, on the real host like `StaffPagesTests`); `AuditLogTests.cs` (add `Recent`).

**Interfaces:**
- Consumes: `PeopleActions` (Tasks 5 and 6), `IPeopleDirectory.SearchAsync`, `SignUpBoard` (the waiting ids), `PeopleRules`.
- The list:
  - It is a GET form with `q`, `filter` and `page`. The table has the columns Name, Role, Email, Last seen and Tasks, plus the badges from the spec.
  - Last seen reads `"Online now"`, `"Today"`, `"Yesterday"`, `"d MMMM yyyy"` or `"Never"`.
  - Below the table: `"1–25 of 132"`, then Previous and Next links.
- Each task icon is a small `<form method="post">`:
  - Edit and Delete are links styled as icon buttons, going to their pages.
  - Reset, Sign out, Suspend and Unsuspend post to `/people?handler=Task` with `id`, `task`, and `q`/`filter`/`page` to come back to.
  - Disabled icons are `<button disabled title="{refusal}" aria-label="{task}: {refusal}">`.
- Supervisors see the list and the person page. Edit and Delete answer 403 for them on GET and POST.
- The person page shows the spec's details: name, role, level, email, created, last seen, the online region's name, region rights (`RegionRights.OwnedBy`) and their estate (`RegionRights.OwnerEstateOf`). It lists `"Recent changes"` from `AuditLog.Recent(e => e.Target?.StartsWith(name) == true, 20)`.
- The edit page:
  - When online, it shows the spec's warnings and `"Sign them out first: they are in-world."` with a Sign out button.
  - When the name changes, it says it may show the old name until they log in again, and longer on other grids.
- The delete page lists what goes (the spec's list) and the regions that will move back. It has an `"Are you sure?"` heading and one red `Delete {Name}` button, posting `confirm=yes`. A post without it is refused.

- [ ] **Step 1: Write the failing tests.**
  - `List_searches_and_pages` (30 fake accounts, `?q=a&page=2`)
  - `Row_icons_disabled_with_reason` (the owner row, an admin row, a not-suspended resident's delete title `"Suspend them first."`)
  - `Supervisor_sees_only_suspend_enabled`
  - `Supervisor_gets_403_on_edit_and_delete`
  - `Reset_post_sends_and_returns_to_the_same_page`
  - `Edit_page_renames`
  - `Delete_without_confirm_is_refused`
  - `Delete_confirmed_removes`
  - `Person_page_shows_recent_changes`
  - `No_inline_script_or_style` (`InlineScriptScanTests` must stay green)
  - `Icons_have_accessible_names`
  - `AuditLogTests.Recent_reads_newest_first_across_rotated_files`
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement the pages, the partial, the sprite and `AuditLog.Recent`.** Remove the old exact-name card from `Index.cshtml`.
- [ ] **Step 4: Run the whole launcher test project.** The `StaffPagesTests` People cases are updated to the new list's markup. Expected: all pass, with the usual 9 Linux-only skips on Windows.
- [ ] **Step 5: Commit** — `People: the list with task icons, the person, edit and delete pages`.

### Task 8: Docs and the VM check

**Files:**
- Modify:
  - `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`:
    - Under 2.5, split out 2.5a, mark its items done and point to this plan.
    - Add 2.5b inventory takedown and 2.5c history for legal requests as open items, with the hold.
    - Change 2.9's first line to "The owner, admins and supervisors add or remove hypergrid links (owner, 2026-11-03)".
  - The user manual page for People, if `docs/` has one (`grep -ril "people page" docs`).

- [ ] **Step 1: Make the doc edits.**
- [ ] **Step 2: Build the package** (`gh workflow run release.yml --ref feature/people-admin`) and install it on the Debian 12 VM.
- [ ] **Step 3: The owner's check in Firestorm**, as listed at the end of the spec:
  - search and page
  - rename and log in again
  - email change with both notices
  - reset link with two-factor on
  - sign out someone in-world
  - suspend, then delete; their name is free and they can't log in
- [ ] **Step 4: Commit** — `Docs: 2.5a people admin done; 2.5b and 2.5c on the list`.
