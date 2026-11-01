# Plan 11: Sign-up approval (pending accounts), implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Staff can approve or deny new sign-ups from a dashboard card and a sign-ups list. Both paths stay open: the person's email link, or staff approval. The owner switches when approval is needed (grids without email) and who may approve. Staff are told by an hourly email and a menu badge.

**Architecture:**
- The launcher's pending sign-ups list (`PendingSignUps`, `<data>/guests/pending-signups.json`) grows into the **sign-ups board**. One entry per sign-up holds the PrincipalID, name, made time, last-link time, state, and when and by whom it changed. It's still the one thing that tells "waiting" from "suspended", and it's still the only list the clean-up deletes from.
- A new `SignUpApprovals` service owns Approve and Deny.
- `SignUps` and `SignUpCleanup` move entries between states. The clean-up also sends the hourly staff email.
- The pages are a dashboard card, a list page under `/People`, a menu badge, two Bots-page switches, and an opt-out on the staff profile.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, xUnit v3, AngleSharp (tests), MySqlConnector (existing). No new packages.

**Spec:** `docs/superpowers/specs/2026-11-01-sign-up-approval-design.md`. The sign-up, email, password-recovery and audiences designs stay binding where it doesn't change them.

## Global Constraints

- Windows, Debian 12 and Debian 13. CI `build-and-smoke` stays green.
- Everything exists only where there is a guest side (`AdminRule.HasStaffPages(s)`, `GuestSideOnly`). On a Region Server the card, list, badge and switches are absent (404 for the pages).
- **Lifetimes:** confirm link **24 hours** (`SignUps.LinkLifetime`, unchanged). A waiting entry is deleted **30 days** after it was made (`SignUpBoard.WaitLimit`). A finished row leaves the board **14 days** after its state changed (`SignUpBoard.ShowFinishedFor`). An unlisted (decoy or upgraded) entry is deleted 24 hours after its last link, as today.
- **States** (`SignUpState`): `WaitingForEmail`, `WaitingForStaff`, `Unlisted`, `SelfApproved`, `Approved`, `LetIn`, `Denied`. "Waiting" means the first two. "On the list at −1" (`Contains`, used by sign-in, People and `AccountChanges`) means waiting or `Unlisted`.
- **Unlisted entries are never shown** on the card, list, badge or staff email, and can't be approved or denied.
- **Who may approve** (`SignUpApprovers`): `Owner`, `OwnerAndAdmins` (default), `OwnerAdminsAndSupervisors`. It's checked on the server on every Approve or Deny post. The dashboard stays `AdminPolicy`. Supervisors approve only from `/people/sign-ups` (`StaffPolicy`).
- **The no-email switch** is `BotSettings.AdminApproval`, default **off**. Label, word for word: "When email isn't set up, new sign-ups wait for staff approval." Approvers label: "Who can approve new sign-ups". Choices: "The owner only", "The owner and admins", "The owner, admins and supervisors". No settings schema bump: new record fields arrive with their defaults (as schema 5 to 6 did).
- **Copy, word for word:**
  - Sign-up "check your email" page, an added line: "Can't find the email? Staff can approve you too."
  - Waiting for staff after sign-up: "Thanks. The grid's staff will look at your sign-up. Once you're approved, you can sign in here with the name and password you just chose."
  - Guest sign-in, waiting for email: the existing `ConfirmFirst` sentence, plus " Or wait for staff to approve you."
  - Guest sign-in, waiting for staff: "Your account is waiting for approval by the grid's staff."
  - Confirm link after a staff approval: "You're already approved. Sign in."
  - First sign-in after a staff approval: the How to connect page greets "You're approved. Log in as {name}."
  - Deny confirmation: "Deny {name}?". Note label: "Note for the email (optional)", up to **500** characters.
  - Action on a finished entry: "Already done by {name}." (or "Already done." when no staff name). Deny delete failed: "Couldn't deny right now. Try again in a few minutes." Approve level failed: "Couldn't approve right now. Try again in a few minutes."
  - Card title "New sign-ups", with a badge "{n} waiting". Menu badge "{n} waiting".
- **Badges on rows:** "Waiting for email", "Waiting for approval", "Self-approved (email link)", "Approved by {staff}", "Signed up (no approval needed)", "Denied by {staff}".
- **New templates:** `signup-denied` (title "Sign-up not approved"; words `{GridName}`, `{AvatarName}`, `{Note}`; subject "Your sign-up on {GridName}"; body "Hello {AvatarName},\n\nYour sign-up on {GridName} wasn't approved.\n\n{Note}"). `{Note}` renders as empty when there is none. `signups-waiting` (title "Sign-ups waiting"; words `{GridName}`, `{Count}`, `{Names}`, `{Link}`; required `{Link}`; subject "{Count} waiting for approval on {GridName}"; body "{Count} people are waiting for approval on {GridName}:\n\n{Names}\n\n{Link}"). The approved email reuses `account-approved`, with `{Link}` = `{site}/guest/sign-in`.
- **Audit actions:** `signup-approved`, `signup-denied`, `signup-expired` (existing), `approval-settings`. Never audit or log a password, token, typed email or the deny note.
- No inline script, no `on…=` handlers, no `style="…"` (`InlineScriptScanTests`). Approve and Deny are form posts with antiforgery tokens. The deny confirmation is a second page or step, not a JavaScript `confirm()`.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Run tests with `dotnet test Launcher/DreamGrid.Launcher.Tests --filter <Class>`.

## Review Focus

1. **A link click and a staff Approve at the same moment** → exactly one wins. The level is raised once, the other side sees "already", and the audit has one approval. (Task 3 `Approve_and_confirm_race_one_wins`.)
2. **A decoy sign-up (email already on the grid)** → never on the card, list, badge or staff email, and a forged Approve post for it is refused. (Task 3 `Unlisted_cannot_be_approved`, Task 5 `Unlisted_not_in_staff_email`.)
3. **A supervisor posts Approve while only admins may** → refused, nothing changes. A supervisor never gets the dashboard. (Task 6 `Supervisor_post_refused_when_admins_only`, `Supervisor_cannot_open_dashboard`.)
4. **An upgrade with an old `pending-signups.json`** (a Guid-to-time map) → its entries become `Unlisted` and keep today's 24-hour rule, their links still confirm, and nothing appears on the board. (Task 1 `Old_format_reads_as_unlisted`.) This **changes spec §11**. Old files can't tell decoys from real sign-ups, and those entries are at most 24 hours old anyway. Task 1 edits §11 to match.
5. **Sign-ups arrive in a burst** → staff get one email that hour, not one per sign-up, and no email when nobody new arrived since the last one. (Task 5 `One_email_per_hour`, `No_email_without_new_waiting`.)

---

### Task 1: The sign-ups board (Core)

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Guests/PendingSignUps.cs`. Rename the class to `SignUpBoard`, keep the file name `pending-signups.json`, and update every user (`Program.cs:241`, `SignUps`, `SignUpCleanup`, `Pages/Guest/SignIn.cshtml.cs`, `Pages/People/Index.cshtml.cs`, `Guests/AccountChanges.cs`, tests).
- Modify: `docs/superpowers/specs/2026-11-01-sign-up-approval-design.md` §11 (upgraded entries become `Unlisted`, per Review Focus 4).
- Test: rename `PendingSignUpsTests.cs` to `SignUpBoardTests.cs`.

**Interfaces:**
- Produces:
  - `public enum SignUpState { WaitingForEmail, WaitingForStaff, Unlisted, SelfApproved, Approved, LetIn, Denied }`
  - `public sealed record SignUpEntry(Guid Principal, string Name, DateTimeOffset Made, DateTimeOffset LastLink, SignUpState State, DateTimeOffset Changed, string? By, bool Greeted = false) { public bool Waiting => State is SignUpState.WaitingForEmail or SignUpState.WaitingForStaff; }`
  - `public sealed class SignUpBoard(string file, TimeProvider time, ILogger<SignUpBoard>? log = null)`. It keeps today's file handling: atomic write, a damaged file read as empty and set aside on the next write, an unreadable file throws. `public static readonly TimeSpan WaitLimit = TimeSpan.FromDays(30), ShowFinishedFor = TimeSpan.FromDays(14);`
    - `void Add(Guid principal, string name, SignUpState state)`: Made, LastLink and Changed are now. Replaces an earlier entry.
    - `void Linked(Guid principal)`: LastLink is now. Made stays.
    - `bool Contains(Guid principal)`: waiting or `Unlisted`.
    - `SignUpEntry? Get(Guid principal)`
    - `bool TryFinish(Guid principal, SignUpState to, string? by)`: only from a waiting state (or from `Unlisted` when `to` is `SelfApproved`). Sets State, Changed and By. Returns false otherwise. Atomic under the lock: this is the race guard.
    - `void SetName(Guid principal, string name)`, `void MarkGreeted(Guid principal)`, `void Remove(Guid principal)`
    - `IReadOnlyList<SignUpEntry> Shown()`: every entry except `Unlisted`.
    - `int WaitingCount()`
    - `IReadOnlyList<SignUpEntry> ToDelete(TimeSpan linkLifetime)`: waiting entries with Made older than `WaitLimit`, and `Unlisted` entries with LastLink older than `linkLifetime`.
    - `void PruneFinished()`: drops finished entries with Changed older than `ShowFinishedFor`. Denied entries too.
  - Old format: a JSON object of Guid to time reads as `Unlisted` entries, with Name "", and Made, LastLink and Changed set to that time. The next write saves the new format.
- [ ] **Step 1: Failing tests:** `Add_and_get_round_trip`; `Linked_keeps_made`; `Contains_waiting_and_unlisted_only`; `TryFinish_once_only` (a second call is false and the entry is unchanged); `TryFinish_unlisted_only_to_self_approved`; `Shown_hides_unlisted`; `ToDelete_waiting_after_30_days_from_made` (with `FakeTimeProvider`: a link resent on day 29 doesn't extend it); `ToDelete_unlisted_after_24_hours_from_last_link`; `PruneFinished_after_14_days`; `Old_format_reads_as_unlisted`; `Damaged_file_set_aside` (kept from the old tests); `Survives_restart`.
- [ ] **Step 2: Run** `--filter SignUpBoardTests`. Expect FAIL. **Step 3: Implement** and rename the users. **Step 4: Run** `--filter "SignUpBoardTests|SignUps|SignUpCleanup|People|AccountChanges"`. Expect PASS. Until Task 3, every caller adds `Unlisted` (today's 24-hour behaviour), confirming finishes as `SelfApproved`, and the no-email path finishes as `LetIn`. So no existing test changes meaning.
- [ ] **Step 5: Commit** `Approval: the sign-ups board`.

### Task 2: The two switches and the templates

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs` (`BotSettings` gets `SignUpApprovers Approvers = SignUpApprovers.OwnerAndAdmins`), `Core/Email/EmailTemplates.cs`, `Pages/Security/Bots.cshtml(.cs)`
- Create: `Launcher/DreamGrid.Launcher.Core/Guests/ApprovalRules.cs`
- Test: `ApprovalRulesTests.cs`, `BotsPageTests.cs` (exists, extend), `EmailTemplatesTests.cs`

**Interfaces:**
- Produces:
  - `public enum SignUpApprovers { Owner, OwnerAndAdmins, OwnerAdminsAndSupervisors }`
  - `public static class ApprovalRules { public static bool CanApprove(StaffRole role, LauncherSettings s); public static bool WaitsForStaff(LauncherSettings s, bool emailReady); }`. `CanApprove`: Owner always; Admin unless `Owner`; Supervisor only for `OwnerAdminsAndSupervisors`; None never. `WaitsForStaff`: `!emailReady && s.Bots.AdminApproval`.
  - `EmailTemplates.SignUpDenied = "signup-denied"`, `EmailTemplates.SignUpsWaiting = "signups-waiting"` (Global Constraints wording), both added to `All`.
  - The Bots page: the `AdminApproval` switch shows again with its new label, and an `Approvers` select. Save audits a change of either as `approval-settings` with target `admin-approval on|off` / `approvers {value}`. Remove the "kept as it is" special case. Warning, when sign-up is open, email isn't ready and `AdminApproval` is off: "Anyone can make an account and use it at once: email isn't set up and staff approval is off."
- [ ] **Step 1: Failing tests:** `CanApprove_matrix` (every role × every setting); `WaitsForStaff_only_without_email_and_switch_on`; `Old_settings_file_gets_default_approvers` (JSON without `approvers` loads `OwnerAndAdmins`); `Bots_page_shows_both_switches`; `Saving_approval_switch_audited`; `Saving_approvers_audited`; `Warning_when_open_without_email_or_approval`; `New_templates_in_All`; `Denied_renders_empty_note`.
- [ ] **Step 2: Run.** Expect FAIL. **Step 3: Implement.** **Step 4: Run.** Expect PASS.
- [ ] **Step 5: Commit** `Approval: the owner's switches and the new emails`.

### Task 3: Sign-up states and the approvals service

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Guests/SignUps.cs`, `Pages/SignUp.cshtml(.cs)`, `Pages/Guest/Confirm.cshtml(.cs)`, `Pages/Guest/SignIn.cshtml(.cs)`, `Pages/Guest/Connect.cshtml(.cs)`
- Create: `Launcher/DreamGrid.Launcher/Guests/SignUpApprovals.cs`. Modify `Program.cs` (register) and `LauncherFactory.cs`.
- Test: `SignUpsTests.cs` (extend), `SignUpApprovalsTests.cs`, `SignUpPageTests.cs` (extend)

**Interfaces:**
- Consumes: Tasks 1–2.
- `SignUps` changes:
  - `SignUpOutcome` gains `WaitingForStaff`.
  - With email: `Add(…, WaitingForEmail)`. A decoy is `Add(…, Unlisted)`. `SendLink` calls `Linked`, not `Add`.
  - Without email: when `ApprovalRules.WaitsForStaff`, the account stays at −1, the entry is `WaitingForStaff`, and the outcome is `WaitingForStaff`. Otherwise the level goes to 0, the entry is `LetIn`, and the outcome is `SignedUp`. The `Remove` after level 0 becomes `LetIn`.
  - `ConfirmAsync` now returns `Task<(ConfirmResult Result, RobustAccount? Account)>` with `public enum ConfirmResult { Confirmed, AlreadyApproved, Bad }`. The entry is `Approved` → redeem the token, `AlreadyApproved`. Waiting or `Unlisted` → raise to 0, then `TryFinish(SelfApproved, null)`. When `TryFinish` is false because staff won meanwhile → `AlreadyApproved`. Missing → `Bad`. It keeps the `_one` lock.
  - `ResendAsync` sends only for `WaitingForEmail` entries.
- Produces:
  - `public enum ApprovalResult { Done, AlreadyDone, NotAllowed, NotFound, Busy }`
  - `public sealed record ApprovalOutcome(ApprovalResult Result, string? Message = null);`
  - `public sealed class SignUpApprovals(Func<LauncherSettings> settings, IGridController grid, IRobustClient robust, IAccountRemover remover, SignUpBoard board, EmailSender sender, AuditLog audit, ILogger<SignUpApprovals> log)`
    - `Task<ApprovalOutcome> ApproveAsync(Guid principal, string staffName, StaffRole role, IPAddress? ip, CancellationToken ct)`. Order: `CanApprove` false → `NotAllowed`. Entry missing or `Unlisted` → `NotFound`. Not waiting → `AlreadyDone` with "Already done by {By}.". Robust account missing or not at −1 → remove the entry, `NotFound`. Raise to 0 (`SetUserLevelAsync`, under the same `_one` lock as `SignUps.ConfirmAsync`: share one `SignUpLock` singleton, a `SemaphoreSlim(1,1)` both take). Then `TryFinish(Approved, staffName)`. Queue `account-approved` when email is ready and the account has an email. Audit `signup-approved`, `Done`. A level failure → `Busy` with the approve-failed copy, and the entry stays waiting.
    - `Task<ApprovalOutcome> DenyAsync(Guid principal, string staffName, StaffRole role, string? note, IPAddress? ip, CancellationToken ct)`. The same first three checks. The note is trimmed to 500 characters. `remover.DeleteAsync`; on failure → `Busy` with the deny-failed copy, the entry stays waiting, nothing is sent. Then `TryFinish(Denied, staffName)`, queue `signup-denied` when email is ready and the account has an email, audit `signup-denied` (no note), `Done`.
- Pages:
  - The sign-up page shows the waiting-for-staff copy for `WaitingForStaff`, and the added "Can't find the email?" line on the check-email reply. That covers decoy replies too, since they're the same reply.
  - Confirm shows the already-approved copy.
  - Sign-in uses `board.Get`: the waiting-for-email or waiting-for-staff copy, only after the right password (as today). On a successful sign-in where the entry is `Approved` and not `Greeted`: `MarkGreeted` and redirect to `/guest/connect?approved=1`, which shows the greeting.
- [ ] **Step 1: Failing tests:** SignUps — `With_email_waits_for_email`; `Decoy_is_unlisted`; `No_email_switch_on_waits_for_staff` (level stays −1); `No_email_switch_off_lets_in`; `Resend_does_not_extend_made`; `Confirm_after_staff_approval_is_already_approved`; `Confirm_unlisted_upgraded_entry_still_works`. Approvals — `Approve_raises_level_and_sends_email`; `Approve_without_email_sends_nothing`; `Approve_twice_already_done_by`; `Approve_and_confirm_race_one_wins` (both started together on a fake whose level change waits on a gate: one `SetUserLevelAsync` call, one audit approval); `Unlisted_cannot_be_approved`; `Not_allowed_role_refused_nothing_changes`; `Approve_level_failure_stays_waiting`; `Deny_deletes_and_emails_note`; `Deny_without_note_email_has_no_reason`; `Deny_note_not_audited`; `Deny_delete_failure_stays_waiting_no_email`; `Deny_note_cut_at_500`. Pages — `Waiting_for_staff_copy`; `Check_email_has_staff_line`; `Sign_in_waiting_for_staff_copy`; `Sign_in_waiting_for_email_copy`; `First_sign_in_after_approval_greets_once`.
- [ ] **Step 2: Run** `--filter "SignUpsTests|SignUpApprovalsTests|SignUpPageTests"`. Expect FAIL. **Step 3: Implement.** **Step 4: Run.** Expect PASS.
- [ ] **Step 5: Commit** `Approval: sign-up states and the approve and deny service`.

### Task 4: The clean-up

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Guests/SignUpCleanup.cs`
- Test: `SignUpCleanupTests.cs` (extend)

**Interfaces:**
- Consumes: Task 1. The pass becomes:
  1. For each `ToDelete(SignUps.LinkLifetime)` entry: delete it when the account is at −1, audit `signup-expired`, `Remove` (as today).
  2. For each shown waiting entry: the account is gone or not at −1 → `Remove` (changed outside this flow). A Name of "" → `SetName` from Robust.
  3. `PruneFinished()`.
  4. Task 5's notice step.
- [ ] **Step 1: Failing tests:** `Deletes_waiting_after_30_days`; `Keeps_waiting_at_29_days`; `Deletes_unlisted_after_24_hours`; `Drops_entry_raised_elsewhere_without_deleting`; `Fills_missing_name`; `Prunes_finished_after_14_days_account_untouched`; `Database_down_keeps_entries` (kept).
- [ ] **Step 2: Run.** Expect FAIL. **Step 3: Implement.** **Step 4: Run.** Expect PASS.
- [ ] **Step 5: Commit** `Approval: the clean-up keeps waiting sign-ups 30 days`.

### Task 5: Telling staff by email

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Guests/ApprovalNotices.cs` (the store), `Launcher/DreamGrid.Launcher/Guests/SignUpNotices.cs` (the sender), `Pages/Profile/Emails.cshtml(.cs)` (`/profile/emails`, StaffPolicy by the `/Profile` convention; a nav link "Emails" next to Sessions)
- Modify: `SignUpCleanup.cs` (calls `SignUpNotices.RunAsync` at the end of each pass), `Program.cs`, `LauncherFactory.cs`
- Test: `SignUpNoticesTests.cs`, `ProfileEmailsPageTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class ApprovalNotices(string file, TimeProvider time)` at `<data>/guests/approval-notices.json`, in the board's file pattern. Members: `bool OptedOut(Guid staff)`, `void SetOptedOut(Guid staff, bool value)`, `DateTimeOffset? LastSent`, `DateTimeOffset NewestNoticed`, `void Sent(DateTimeOffset now, DateTimeOffset newestMade)`.
  - `public sealed class SignUpNotices(Func<LauncherSettings> settings, SignUpBoard board, ApprovalNotices notices, IStaffDirectory directory, EmailSender sender, TimeProvider time, ILogger<SignUpNotices> log)` with `Task RunAsync(CancellationToken ct)`. It sends only when: email is ready; `LastSent` is null or at least 1 hour ago; and a waiting (shown) entry has Made after `NewestNoticed`. Recipients: `directory.ListAtOrAboveAsync(s.Security.SupervisorLevel)`, plus the owner, filtered by `ApprovalRules.CanApprove(AdminRule.RoleOf(…))`, a non-empty email, and not opted out. `{Count}` is every waiting entry. `{Names}` is their names, one per line. `{Link}` is `{site}/` for the owner and admins, and `{site}/people/sign-ups` for supervisors. Then `Sent`. Database down → log and try next pass.
  - The profile page: one switch, "Email me when new sign-ups are waiting for approval", shown only to staff for whom `CanApprove` is true. Otherwise it says "You can't approve sign-ups on this grid."
- [ ] **Step 1: Failing tests:** `One_email_per_hour` (two passes 10 minutes apart with a new sign-up between: one email; a third pass 61 minutes later: a second email); `No_email_without_new_waiting`; `Only_approvers_get_it` (per setting); `Opted_out_gets_none`; `Unlisted_not_in_staff_email`; `No_applicant_email_in_message`; `Supervisor_link_is_sign_ups_list`; `No_email_when_email_not_set_up`; `Directory_down_tries_next_pass`. Page: `Opt_out_saved`; `Non_approver_sees_cannot_text`.
- [ ] **Step 2: Run.** Expect FAIL. **Step 3: Implement.** **Step 4: Run.** Expect PASS.
- [ ] **Step 5: Commit** `Approval: the hourly email to approvers`.

### Task 6: The card, the sign-ups list, the badge

**Files:**
- Create: `Pages/Shared/_SignUpRows.cshtml` (rows plus buttons, shared by the card and the list), `Pages/People/SignUps.cshtml(.cs)` (`/people/sign-ups`), `Pages/People/Deny.cshtml(.cs)` (`/people/sign-ups/deny?id=`: the confirm step with the note box)
- Modify: `Pages/Index.cshtml(.cs)` (the card), `Pages/People/Index.cshtml(.cs)` ("Waiting for approval" badge next to the existing "Waiting for email"; a link to the list), `Pages/Shared/_Layout.cshtml` (the badge)
- Test: `SignUpBoardPagesTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3, 5.
- Posts: `/people/sign-ups?handler=approve` with `id` and `returnUrl` (`/` or `/people/sign-ups`, local only), and the deny page's post with `id` and `note`. Both call `SignUpApprovals` with the signed-in staff member's name and role, then redirect back with the outcome's message as a notice.
- Order: waiting rows first, oldest first; then the rest, newest first. The card shows the first **10** and the link "See all sign-ups". The list page has `q` (case-insensitive name contains), `sort` (`name`, `made`, `state`), `dir` (`asc`/`desc`), and `page` (**25** per page). It's server-side only, with no script.
- Buttons are drawn only when `CanApprove`. Rows show "signed up {relative time}" (an existing helper if there is one, otherwise "x minutes/hours/days ago").
- Badge: the layout asks `board.WaitingCount()` for staff who can approve. It's next to Home for the owner and admins, and next to People for supervisors. Hidden at 0.
- [ ] **Step 1: Failing tests:** `Card_shows_waiting_first_and_ten_rows`; `Card_has_buttons_for_approvers_only`; `Card_hides_unlisted`; `Card_absent_on_region_server`; `List_search_by_name`; `List_sort_by_each_column`; `List_pages_25`; `Approve_from_card_returns_to_dashboard`; `Deny_asks_first_then_deletes`; `Supervisor_post_refused_when_admins_only`; `Supervisor_can_approve_from_list_when_allowed`; `Supervisor_cannot_open_dashboard`; `Post_without_antiforgery_rejected`; `Return_url_must_be_local`; `Badge_counts_waiting`; `Badge_links_people_for_supervisor`; `People_page_shows_waiting_for_approval`; `No_inline_script`.
- [ ] **Step 2: Run** `--filter SignUpBoardPagesTests`. Expect FAIL. **Step 3: Implement.** **Step 4: Run.** Expect PASS.
- [ ] **Step 5: Commit** `Approval: the dashboard card, the sign-ups list and the badge`.

### Task 7: Worklist and the end-to-end check

**Files:**
- Modify: `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`. Tick 2.2's first item. Mark the second "pending accounts can't log in" as done (level −1, plan 9; no Wifi accounts to migrate, since Wifi's pending marking isn't used by v7). Tick 1.3's "Admin approval of new accounts" and 2.1's "Modes chosen by the admin" with "(plan 11)".
- Test: `ApprovalFlowTests.cs` (through `LauncherFactory`, with email captured by `FakeSmtp` as in `SignUpFlowTests`)

- [ ] **Step 1: Failing tests:**
  - `Staff_approve_before_link`: sign up with email, an admin approves from the card, the approved email is captured, the link page says already approved, and guest sign-in works and greets.
  - `Link_then_board_shows_self_approved`
  - `No_email_waits_then_approved`: switch on, the sign-up waits, sign-in shows the waiting copy, approve, sign-in works.
  - `Deny_frees_name`: deny with a note, the email has the note, and the same name can sign up again.
  - `Supervisor_flow`: approvers set to include supervisors; a supervisor approves from `/people/sign-ups`, and `/` is refused.
- [ ] **Step 2: Run.** Expect FAIL. **Step 3: Implement** (fixes only, no new behaviour). **Step 4: Run** the whole test project `dotnet test Launcher/DreamGrid.Launcher.Tests`. Expect PASS.
- [ ] **Step 5: Commit** `Approval: the end-to-end check and the worklist`.
