# Plan 10 — Resident account page — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A signed-in resident (or staff member, at the guest door) sees their own details, changes their password, and changes their email by a link sent to the new address, with a notice to the old one.

**Architecture:** Two small database writers/readers in `DreamGrid.Launcher.Core` (the email write, every account with an address) and a launcher-owned list of waiting email changes. The website gets one `AccountChanges` service that owns both changes (credential check, rules, limits, token, emails, sessions, audit), the account page and the confirm-new-email page on top of it, and two new email templates.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, xUnit v3, AngleSharp (tests), MySqlConnector (existing). No new packages.

**Spec:** `docs/superpowers/specs/2026-10-31-resident-account-design.md`. The audiences, email, password-recovery and sign-up designs stay binding where the spec doesn't change them.

## Global Constraints

- Windows, Debian 12 and Debian 13; CI `build-and-smoke` stays green.
- The pages exist only where there is a guest side (`GuestSideOnly`; `AdminRule.HasStaffPages(s)`). `/guest/account` is GuestPolicy (the `/Guest` folder convention) and behind `TermsGate`. `/guest/confirm-email` is anonymous (open-page list in `Program.cs`) and outside `TermsGate` (add to `TermsGate.Open`). Both already exempt from Admin networks (`/guest` in `AdminNetworkFilter.PublicPaths`; check, don't add).
- Wrong current password, word for word: "That isn't your current password." Locked: "Too many attempts. Try again in a few minutes." Wrong code: `TwoFactorModel.WrongCode` ("That code isn't right. Try again.").
- Same password as now: "Choose a password you haven't been using." Write failed: "Couldn't change the password right now. Try again in a few minutes." Done: "Your password is changed."
- No email set up, word for word: "Changing your email needs the grid's email to be set up. Ask the grid owner."
- Same address: "That's already your email address." Ask reply, word for word, whatever happened after the credential and address checks: "We sent a link to the new address. Your email stays the same until you open it. It works once, for 24 hours."
- Link page: expired "This link has expired or was already used." (link to `/guest/account`); taken "That address is used by another avatar on this grid."; write failed "Couldn't change your email right now. Try again in a few minutes."; done "Your email address is now {address}."
- Token: purpose `change-email`, lifetime **24 hours**, `EmailTokens`; `{ExpiresIn}` = "24 hours"; email queued with `validFor: 24 hours`. The link carries the token only.
- Limits: **3** change-email links per account per hour, **3** per typed address per hour, **10** posts per IP per hour: a third `ResetLimits` instance on its own file `<data>/guests/email-change-limits.json`, wrapped as `EmailChangeLimits` like `SignUpLimits`.
- Credential checks use `LoginThrottle` at `SignInDoor.Guest` with the key `AvatarName.Parse(full).Key` and the request IP. Two-factor asked only when resident two-factor is on (`ResidentTwoFactor.Store.StateOf == On`); staff two-factor never on these pages.
- Password rules: `PasswordRules.Problem`. Written with `IRobustPasswords.SetAsync`. Email written on the database, never the console.
- "Address taken" counts every other account with that address (trimmed, lower-case) at any level **except** principals on `PendingSignUps`.
- Never log or audit a password, code, token or typed address. Audit actions: `password-changed`, `password-change-refused` (reason word), `email-change-asked`, `email-change-refused` (reason word), `email-changed`.
- No inline script, no `on…=` handlers, no `style="…"` (`InlineScriptScanTests`).
- UI copy plain English, short sentences. Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Run tests with `dotnet test Launcher/DreamGrid.Launcher.Tests --filter <Class>`.

## Review Focus

1. **The person changes their password with two tabs of the account page open** → the tab that posted keeps working; the other tab's next request lands on sign-in, not an error page. (Task 4 `Other_tab_signed_out_this_one_kept`.)
2. **The person asks to change their email twice to two different addresses** → only the second link works; opening the first says the link expired, and the address on file never becomes the first one. (Task 3 `Second_ask_replaces_first`.)
3. **The new address differs from another avatar's only in capitals or spaces** → still counted as taken; and the person's own address typed in capitals is "That's already your email address." (Task 3 `Taken_ignores_case_and_spaces`, `Same_address_any_case_refused`.)
4. **The database goes down between asking and opening the link** → busy message, the link still works afterwards. (Task 3 `Write_failure_keeps_link`.)
5. **An account with no email address on file asks to set one** → the link goes to the new address and no notice email is attempted. (Task 3 `No_old_address_no_notice`.)

---

### Task 1: Email write and address lookup (Core)

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/OpenSim/RobustEmails.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Database/StaffDirectory.cs`
- Modify (tests): `FakeGridController.cs` (`FakeRobustAccounts` also implements `IRobustEmails`, with an `EmailsDown` switch; its directory fake gets `ListByEmailAsync`)
- Test: `RobustEmailsTests.cs`, `StaffDirectoryTests.cs` (create if missing)

**Interfaces:**
- Produces:
  - `public sealed class RobustEmailsUnavailableException(string message, Exception inner) : Exception(message, inner);`
  - `public interface IRobustEmails { Task SetAsync(Guid principal, string email, CancellationToken ct); }` — throws `RobustEmailsUnavailableException` when not written.
  - `public sealed class RobustEmails(Func<LauncherSettings> settings) : IRobustEmails` — the `RobustPasswords` pattern (robustuser, 5 s timeout, same caught exceptions). Statement: `public const string Update = "UPDATE robust.UserAccounts SET Email = @email WHERE PrincipalID = @id";` A zero-row update throws `RobustEmailsUnavailableException` (the account is gone).
  - On `IStaffDirectory`: `Task<IReadOnlyList<RobustAccount>> ListByEmailAsync(string normalEmail, CancellationToken ct)` — `ByEmail` without `LIMIT 1`, every match. `FindByEmailAsync` stays as is.
- [ ] **Step 1: Failing tests:** `Update_statement_targets_one_principal` (`RobustEmails.Update` contains `WHERE PrincipalID = @id` and `SET Email = @email`); `Unreachable_database_throws_unavailable` (settings with a closed port → `RobustEmailsUnavailableException`); `List_by_email_has_no_limit` (the query constant has no `LIMIT`); fake: `Fake_list_by_email_returns_every_match`.
- [ ] **Step 2: Run** `--filter "RobustEmailsTests|StaffDirectoryTests"` → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Account: write an avatar's email, list accounts by email`.

### Task 2: Waiting email changes and the two templates

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Guests/PendingEmailChanges.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Email/EmailTemplates.cs`
- Test: `PendingEmailChangesTests.cs`, `EmailTemplatesTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class PendingEmailChanges(string file, TimeProvider time, ILogger<PendingEmailChanges>? log = null)` — the `PendingSignUps` file pattern (`JsonStateFile`, safe from any thread):
    - `void Set(Guid principal, string normalEmail)` — replaces any earlier one, records now.
    - `string? Get(Guid principal, TimeSpan maxAge)` — null when none or older than `maxAge`.
    - `void Remove(Guid principal)`
    - `void Prune(TimeSpan maxAge)` — drops entries older than `maxAge`; called on every `Set` (no background job).
  - In `EmailTemplates`: `ConfirmNewEmail = "confirm-new-email"` (subject "Confirm your new email address"; words `{GridName}`, `{AvatarName}`, `{Link}`, `{ExpiresIn}`; required `{Link}`, `{ExpiresIn}`; body per spec §6 Emails) and `EmailChangeNotice = "email-change-notice"` (subject "Your email address on {GridName}"; words `{GridName}`, `{AvatarName}`, `{Link}`; required `{Link}`; body per spec §6 Emails). Both added to `All`, so the Email wording page lists them.
- [ ] **Step 1: Failing tests:** `Set_replaces_earlier`; `Get_ignores_older_than_max_age` (`FakeTimeProvider` +25 h with max 24 h → null); `Survives_restart`; `Prune_on_set_drops_old`; `New_templates_in_All`; `Notice_template_never_has_new_address_word` (its words list has no address placeholder); `Confirm_new_email_renders_link_and_expiry`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Account: waiting email changes and their templates`.

### Task 3: The AccountChanges service

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Guests/AccountChanges.cs`
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (register `AccountChanges`, `RobustEmails` as `IRobustEmails`, `PendingEmailChanges` at `<data>/guests/email-changes.json`, `EmailChangeLimits` at `email-change-limits.json`), `LauncherFactory.cs` (fakes)
- Test: `AccountChangesTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2; `IRobustClient.FindAccountByIdAsync` / `AuthenticateAsync`, `LoginThrottle`, `ResidentTwoFactor`, `PasswordRules`, `IRobustPasswords`, `StaffSessions`, `GuestStamps`, `EmailSender`, `EmailTokens`, `PendingSignUps`, `AdminRule.RoleOf`, `AuditLog`.
- Produces:
  - `public sealed record EmailChangeLimits(ResetLimits Counts);`
  - `public enum AccountResult { Done, WrongPassword, WrongCode, Locked, Invalid, Unchanged, NoEmail, Busy }`
  - `public sealed record AccountOutcome(AccountResult Result, string? Problem = null);`
  - `public enum EmailLinkResult { Done, Expired, Taken, Busy }`
  - `public sealed class AccountChanges(...)` with `public const string Purpose = "change-email"; public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(24); public const string ExpiresIn = "24 hours"; public const string ConfirmPath = "/guest/confirm-email";`
    - `bool NeedsCode(Guid principal)` — resident two-factor is on.
    - `bool EmailChangeAvailable()` — `EmailSender.IsReady`.
    - `Task<AccountOutcome> ChangePasswordAsync(Guid principal, string? current, string? code, string? password, string? again, IPAddress? ip, CancellationToken ct)` — order: throttle locked → `Locked`; current wrong → `RecordFailure`, `WrongPassword`; code needed and wrong → `RecordFailure`, `WrongCode`; `PasswordRules.Problem` → `Invalid` with it; new equals current → `Unchanged`; `SetAsync` fails → `Busy`; then `RecordSuccess`, spend the code (`VerifyCode`), `GuestStamps.Rotate`, `StaffSessions.EndAll` when `RoleOf != None`, queue `PasswordChanged` to the address on file when there is one and email is ready, audit, `Done`. The code is checked with `CheckCode` and spent only after the write, as `PasswordResets` does. Robust unreachable during the checks → `Busy`, not counted.
    - `Task<AccountOutcome> AskEmailChangeAsync(Guid principal, string? current, string? code, string? newEmail, IPAddress? ip, CancellationToken ct)` — `NoEmail` when email isn't ready; then the same throttle/password/code checks; `SignUpRules.EmailProblem` → `Invalid`; same as on file (`SignUpRules.NormalEmail`) → `Unchanged`; then `Done` in every remaining case, sending only when `AddressAllowed` and `SendAllowed`: `PendingEmailChanges.Set`, `Issue`, queue `ConfirmNewEmail` to the new address and `EmailChangeNotice` to the old one when there is one (`{Link}` = `{site}{PasswordResets.AskPath}`), audit `email-change-asked`. Site URL built as `SignUps` builds it.
    - `Task<(EmailLinkResult Result, string? Email)> ConfirmEmailChangeAsync(string? token, CancellationToken ct)` — `Find`; no waiting address (`Get(principal, LinkLifetime)`) → `Expired`; `ListByEmailAsync` has another principal not on `PendingSignUps` → redeem, `Remove`, audit refused, `Taken`; `IRobustEmails.SetAsync` fails → `Busy` (link kept); then `TryRedeem`, `Remove`, audit `email-changed`, `Done` with the address. One at a time (a `SemaphoreSlim`, as `PasswordResets`).
- [ ] **Step 1: Failing tests:** password — `Wrong_current_counts_in_guest_throttle`; `Locked_checks_nothing`; `Code_asked_only_with_resident_two_factor`; `Wrong_code_counts_and_backup_not_spent`; `Same_password_refused`; `Weak_password_refused_and_nothing_written`; `Write_failure_is_busy_and_sessions_kept`; `Done_rotates_guest_stamp`; `Done_ends_staff_sessions_for_staff_only`; `Done_queues_password_changed`; `Robust_down_is_busy_not_counted`. Email — `No_email_set_up_is_NoEmail`; `Ask_needs_current_password`; `Same_address_any_case_refused`; `Ask_sends_link_to_new_and_notice_to_old`; `Notice_never_contains_new_address`; `No_old_address_no_notice`; `Fourth_ask_in_an_hour_sends_nothing_same_reply`; `Second_ask_replaces_first`; `Link_changes_address_and_is_single_use`; `Expired_link_after_24_hours`; `Taken_by_other_account_refused`; `Taken_ignores_case_and_spaces`; `Pending_sign_up_does_not_block`; `Suspended_account_does_block`; `Write_failure_keeps_link`; `Nothing_secret_audited` (audit text has no password, code, token or either address).
- [ ] **Step 2: Run** `--filter AccountChangesTests` → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Account: the account changes service`.

### Task 4: The account page and the confirm-new-email page

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Guest/Account.cshtml(.cs)` (`@page "/guest/account"`), `Pages/Guest/ConfirmEmail.cshtml(.cs)` (`@page "/guest/confirm-email"`, `?token=`)
- Modify: `Pages/Guest/Index.cshtml(.cs)` (an "Account settings" link), `Program.cs` (open page), `Guests/TermsGate.cs` (`/Guest/ConfirmEmail`)
- Test: `AccountPageTests.cs`

**Interfaces:**
- Consumes: Task 3.
- Account page model: on GET loads the account with `FindAccountByIdAsync` (Robust down → "The grid's sign-in service isn't running right now. Try again later." and no forms). Shows: name; email or "No email address"; created (`RobustAccount.Created`, "d MMMM yyyy", or nothing when null); role `Resident` / `Supervisor` / `Admin` / `Owner` from `AdminRule.RoleOf` (`None` → "Resident"); the two-factor and My regions links under the same tests `IndexModel` uses. Two forms with named handlers `OnPostPasswordAsync` (fields `Current`, `Code`, `Password`, `Again`) and `OnPostEmailAsync` (`Current`, `Code`, `NewEmail`); the code field only when `NeedsCode`. On a `Done` password change the handler calls `GuestSignIn.SignInAsync(HttpContext, account)` before rendering, so this browser gets the new stamp. The email form is replaced by the no-email sentence when `!EmailChangeAvailable()`. Messages are the Global Constraints' words.
- Confirm-new-email page: acts on GET like `Pages/Guest/Confirm.cshtml.cs`; shows the four messages; links to `/guest/account`.
- [ ] **Step 1: Failing tests:** `Account_needs_sign_in` (redirect to `/guest/sign-in`); `Shows_name_email_role_created`; `Staff_shows_their_role`; `No_email_shows_no_email_sentence`; `Email_form_hidden_without_email`; `Code_field_only_with_two_factor`; `Password_change_keeps_this_session` (next GET of `/guest/account` with the response's cookie is 200); `Other_tab_signed_out_this_one_kept` (the cookie from before the change is redirected to sign-in); `Wrong_password_message`; `Email_ask_shows_reply`; `Confirm_link_shows_new_address`; `Confirm_link_anonymous_and_outside_terms_gate`; `Bad_link_shows_expired`; `Pages_absent_on_region_server` (404); `Front_page_links_account`; `No_inline_script`.
- [ ] **Step 2: Run** `--filter AccountPageTests` → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Account: the account page and the confirm-new-email page`.

### Task 5: Worklist and the end-to-end check

**Files:**
- Modify: `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` (2.4: tick change email and change password; tick "see their own details"; leave "choose language" open with "waits for 1.5, plan 10")
- Test: `AccountFlowTests.cs` (through `LauncherFactory`, email captured with `FakeSmtp` as in the sign-up and recovery flow tests)

- [ ] **Step 1: Failing tests:** `Full_password_change` (sign in, change, sign out, the guest door refuses the old password and accepts the new); `Full_email_change` (ask, both emails captured, the notice has no new address, open the link, then `/guest/forgot-password` with the new address sends a reset and with the old one sends nothing); `Email_form_absent_without_email_setup`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** (fixes only; no new behaviour). **Step 4: Run** the whole test project `dotnet test Launcher/DreamGrid.Launcher.Tests` → PASS.
- [ ] **Step 5: Commit** `Account: the end-to-end check and the worklist`.
