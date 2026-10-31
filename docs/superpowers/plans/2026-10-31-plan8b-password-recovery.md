# Plan 8b — Password recovery — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Residents, staff and the owner reset a forgotten password themselves from one guest-side "Forgot password" page, by a single-use email link (plus their two-factor code where the owner's rule asks for it), under three owner-chosen rules that Setup fills in from "What kind of grid is this?".

**Architecture:** Pure rules (the three rules and the Setup levels, password rules, request limits, OpenSim's password hash) live in `DreamGrid.Launcher.Core`, unit-tested. The website gets one `PasswordResets` service that owns the ask and the reset (lookup, rule, limits, token, email, audit), two anonymous guest pages on top of it, a second `TwoFactorStore` file for residents with a code step at the guest door, a Setup step and an owner-only Advanced page. The new password is written straight into `robust.auth` as `robustuser`, never through Robust's `setpassword`.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, xUnit v3, AngleSharp (tests), MySqlConnector (existing). No new packages.

**Spec:** `docs/superpowers/specs/2026-10-31-password-recovery-design.md`. The audiences design and the email design (`2026-10-31-email-design.md`) stay binding where the spec doesn't change them.

## Global Constraints

- Windows, Debian 12 and Debian 13; CI `build-and-smoke` stays green.
- With `Recovery` null (every install from before this plan) every rule reads **Off** and every existing page behaves as today, except a "Forgot password" link that leads to the "isn't turned on" message.
- The pages exist only where there is a guest side (`GuestSideOnly`; `AdminRule.HasStaffPages(s)`); on a Region Server or Connected Server `/login` shows no "Forgot password" link.
- `/guest/forgot-password` and `/guest/reset-password` are anonymous (added to the open-page list in `Program.cs`), outside `TermsGate` (add to `TermsGate.Open`), and already exempt from Admin networks (`/guest` is in `AdminNetworkFilter.PublicPaths`; check, don't add).
- The one reply, word for word: "If that name and email match an account, we've sent a link. It works once, for 30 minutes. Check your spam folder."
- The grid-wide message: "Password reset isn't turned on for this grid. Contact the grid owner." (all three rules Off, or `EmailSender.IsReady` false).
- Token: purpose `reset-password`, lifetime **30 minutes**, `EmailTokens` (`<data>/guests/email-tokens.json`); the email is queued with `validFor: 30 minutes`, `{ExpiresIn}` = "30 minutes".
- Limits: **3** emails per account per hour, **3** per typed email address per hour, **10** requests per address (IP) per hour; rolling windows; persisted; keyed by SHA-256 hex, never the typed text.
- Password: at least **8** characters, not on the shipped common-passwords list (case-insensitive), not the avatar's first name, last name or "First Last" (case-insensitive).
- Never log or audit a token, code, password or typed email address. Audit actions: `reset-requested`, `reset-refused` (limit or no match: result `refused`, actor `name.ForLog`), `reset-code-wrong`, `password-reset`.
- No inline script, no `on…=` handlers, no `style="…"` (`InlineScriptScanTests`).
- Never set `AllowSetPassword` in any Robust INI.
- UI copy plain English, short sentences. Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Run tests with `dotnet test Launcher/DreamGrid.Launcher.Tests --filter <Class>`.

## Review Focus

1. **A resident opens the link in two tabs and submits both** → the first changes the password; the second says "This link has expired or was already used. Ask for a new one." and changes nothing. (Task 7 `Second_use_of_a_link_changes_nothing`.)
2. **The database is down when the new password is saved** → "Couldn't change the password right now. Try again in a few minutes."; the link still works afterwards. (Task 7 `Failed_write_keeps_the_link`.)
3. **A staff member whose rule is "Email link + two-factor" never set two-factor up** → the link alone lets them choose a password; no code field is shown. (Task 7 `Rule_with_code_but_no_two_factor_needs_link_only`.)
4. **The owner turns a group's rule to Off after a link was sent** → the link is refused with the expired message. (Task 7 `Link_refused_after_rule_turned_off`.)
5. **The resident types their email with capitals or spaces around it** → it still matches. (Task 6 `Email_match_ignores_case_and_spaces`.)

---

### Task 1: The three rules and the Setup levels (Core + settings)

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Security/Recovery.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs` (`public RecoverySettings? Recovery { get; init; }`, no schema bump: null means "not chosen")
- Test: `Launcher/DreamGrid.Launcher.Tests/RecoveryTests.cs`, `SettingsStoreTests.cs` (round-trip)

**Interfaces:**
- Produces:
  - `public enum RecoveryRule { Off, EmailLink, EmailLinkAndCode }`
  - `public enum GridKind { HomeLab, SchoolOrClub, PublicGrid }`
  - `public sealed record RecoverySettings(RecoveryRule Residents, RecoveryRule Staff, RecoveryRule Owner)`
  - `public static class Recovery { RecoverySettings Rules(LauncherSettings s); RecoveryRule RuleFor(LauncherSettings s, StaffRole role); (RecoverySettings Rules, bool StaffMustUseTwoFactor) Level(GridKind kind); LauncherSettings Apply(LauncherSettings s, GridKind kind); bool AnyOn(LauncherSettings s); string Title(GridKind kind); string Line(GridKind kind); }` — `Rules` of null is all Off; `RuleFor` maps `StaffRole.None` → Residents, `Supervisor`/`Admin` → Staff, `Owner` → Owner; `Apply` sets `Recovery` and `Security.RequireTwoFactorForStaff`.
- [ ] **Step 1: Failing tests:** `Null_means_all_off`; `Levels_match_the_table` (HomeLab → all `EmailLink`, switch false; SchoolOrClub → `EmailLink, EmailLinkAndCode, EmailLinkAndCode`, switch **false**; PublicGrid → all `EmailLinkAndCode`, switch true); `RuleFor_picks_the_group` (four roles); `Apply_sets_rules_and_switch_and_keeps_other_security` (`AdminLevel` unchanged); settings round-trip with `Recovery` set and with it missing (reads null).
- [ ] **Step 2: Run** `--filter "RecoveryTests|SettingsStoreTests"` → FAIL.
- [ ] **Step 3: Implement.** Titles "Home lab", "School or club", "Public grid"; one plain line each (builder's wording).
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Recovery: three rules and the Setup levels`.

### Task 2: Password rules and request limits (Core)

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Security/PasswordRules.cs`, `ResetLimits.cs`, `common-passwords.txt` (embedded resource; the top 10,000 of SecLists `10k-most-common.txt`, MIT, credit in the file's first comment line)
- Test: `PasswordRulesTests.cs`, `ResetLimitsTests.cs`

**Interfaces:**
- Produces:
  - `public static class PasswordRules { const int MinLength = 8; string? Problem(string? password, string? again, string first, string last); }` — null when fine, else one of: "Type the same password twice.", "Use at least 8 characters.", "That password is too common. Choose another.", "Don't use your avatar's name as the password."
  - `public sealed class ResetLimits(TimeProvider time, string? stateFile, ILogger<ResetLimits>? log = null) { const int PerAccount = 3, PerEmail = 3, PerAddress = 10; static readonly TimeSpan Window = 1 hour; bool AddressAllowed(IPAddress? ip); void CountAddress(IPAddress? ip); bool SendAllowed(Guid principal, string email); void CountSend(Guid principal, string email); }` — rolling windows, persisted through `JsonStateFile` like `LoginThrottle`, keys are SHA-256 hex of the principal / lowercased trimmed email / normalised address (IPv4-mapped IPv6 counts as IPv4, as `LoginThrottle`); null address counts nothing.
- [ ] **Step 1: Failing tests:** `Seven_letters_is_too_short`; `Common_password_refused_any_case` (`"Password1"`, `"QWERTY123"`); `Name_as_password_refused` (avatar Alexandra Northwood: `"alexandra"`, `"NORTHWOOD"`, `"Alexandra Northwood"` refused; `"northwood7x"` allowed); `Mismatch_reported_first`; `Fourth_send_in_an_hour_refused_per_account`; `Same_email_with_other_names_is_one_count` (`"Ann@X.com "` and `"ann@x.com"`); `Eleventh_request_from_an_address_refused`; `Window_rolls` (advance 61 minutes → allowed); `Limits_survive_restart` (new instance over the same file); `File_holds_no_typed_text` (file text contains neither the email nor the IP).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Recovery: password rules and request limits`.

### Task 3: Peek at a token, and the "password changed" email

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Guests/EmailTokens.cs`, `Launcher/DreamGrid.Launcher.Core/Email/EmailTemplates.cs`
- Test: `EmailTokensTests.cs`, `EmailTemplatesTests.cs`

**Interfaces:**
- Produces:
  - `public Guid? Find(string purpose, string? token)` on `EmailTokens` — the principal of a live token for that purpose, **without** spending it; null otherwise (expired, other purpose, unknown). The reset page calls `Find` to know whose link it is, then `TryRedeem(purpose, principal, token)` only after the password is written.
  - `EmailTemplates.PasswordChanged = "password-changed"`, in `All`; words `{GridName}`, `{AvatarName}`, none required; default subject "Your password on {GridName} was changed", body along the lines of the spec §5.8.
- [ ] **Step 1: Failing tests:** `Find_does_not_spend` (Find twice → same principal, then `TryRedeem` true); `Find_refuses_other_purpose_and_expired`; `Password_changed_default_has_no_link` and is listed in `All` (so the Email wording page shows it; existing wording-page test still passes).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Recovery: peek at a reset token and the password-changed email`.

### Task 4: Writing the password into Robust's database

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/OpenSim/RobustPasswords.cs`
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (register), `Launcher/DreamGrid.Launcher.Tests/FakeGridController.cs` (`FakeRobustAccounts`: an `email` parameter on `Add`, returned in `RobustAccount.Email`; implements `IRobustPasswords` so `AuthenticateAsync` accepts the new password; `PasswordsDown` makes it throw), `LauncherFactory.cs` (register the fake)
- Test: `RobustPasswordsTests.cs`

**Interfaces:**
- Produces:
  - `public interface IRobustPasswords { Task SetAsync(Guid principal, string password, CancellationToken ct); }` — throws `RobustPasswordsUnavailableException` when the database can't be asked (same exception filter as `StaffDirectory`).
  - `public static class OpenSimPassword { string Hash(string password, string salt); string NewSalt(); }` — `Hash = md5hex(md5hex(password) + ":" + salt)`, lower-case hex as OpenSim's `Util.Md5Hash`; `NewSalt = md5hex(Guid.NewGuid().ToString())`.
  - `public sealed class RobustPasswords(Func<LauncherSettings> settings) : IRobustPasswords` — as `robustuser` (`MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, …)`): `INSERT INTO robust.auth (UUID, passwordHash, passwordSalt, webLoginKey, accountType) VALUES (@id, @hash, @salt, '00000000-0000-0000-0000-000000000000', 'UserAccount') ON DUPLICATE KEY UPDATE passwordHash = @hash, passwordSalt = @salt` (UUID as OpenSim's lower-case `D` form).
- [ ] **Step 1: Failing tests:** `Hash_matches_opensim` (compute the expected value in the test with `MD5` the way `AuthenticationServiceBase.SetPassword` does, for password `"secret-pass"` and salt `"0123456789abcdef0123456789abcdef"`); `New_salt_is_32_hex`; `Fake_accepts_the_new_password` (through `IRobustClient.AuthenticateAsync` after `SetAsync`).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** No test against a real MariaDB here; Task 7's integration test uses the fake. **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Recovery: write a new password into Robust's auth table`.

### Task 5: Two-factor for residents (asleep until the rule asks)

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Guests/ResidentTwoFactor.cs`, `Pages/Guest/TwoFactor.cshtml(.cs)` (`@page "/guest/two-factor"`), `Pages/Guest/SignInCode.cshtml(.cs)` (`@page "/guest/sign-in/code"`), `Guests/GuestTwoFactorPending.cs`
- Modify: `Pages/Guest/SignIn.cshtml.cs`, `Pages/Guest/Index.cshtml` (link), `Pages/Security/Index.cshtml(.cs)` (residents in "People with two-factor sign-in", same Reset), `Program.cs`, `Guests/TermsGate.cs` (`/Guest/SignInCode` open), open-page list (`/Guest/SignInCode`)
- Test: `ResidentTwoFactorTests.cs`, `GuestSignInTests.cs`, `SecurityPageTests.cs`

**Interfaces:**
- Consumes: `Recovery.RuleFor` (Task 1); `TwoFactorStore` as is.
- Produces:
  - `public sealed class ResidentTwoFactor(TwoFactorStore store)` — registered with its own file `<data>/guests/twofactor.json`, a separate `TwoFactorStore` instance (the staff one in `<data>/security/twofactor.json` is untouched). `TwoFactorStore Store { get; }`; `static bool PageShown(LauncherSettings s, TwoFactorState state)` = residents' rule is `EmailLinkAndCode` **or** `state == On`.
  - `GuestTwoFactorPending` — as `TwoFactorPending` but cookie `dg-guest-2fa`, path `/guest`, purpose `DreamGrid.GuestTwoFactorPending.v1`, 5 minutes, carries principal, first, last, level.
  - Guest sign-in: after a right password, if `ResidentTwoFactor.Store.StateOf(principal) == On` → issue the pending cookie and redirect to `/guest/sign-in/code`; otherwise sign in as today. The code page mirrors `Login/CodeModel` (throttle at `SignInDoor.Guest`, backup codes, audit `sign-in`, `backup-code-used`). The staff two-factor file is never read at the guest door.
  - `/guest/two-factor` (GuestPolicy): the staff page's set-up / new backup codes / turn off, against the resident store; 404 unless `PageShown`. A wrong code there counts in the throttle at `SignInDoor.Guest`.
- [ ] **Step 1: Failing tests:** `Resident_page_hidden_until_rule_asks` (rule `EmailLink`, not enrolled → 404; rule `EmailLinkAndCode` → 200); `Enrolled_resident_keeps_page_after_rule_relaxed`; `Guest_door_asks_code_of_enrolled_resident`; `Guest_door_never_asks_staff_two_factor` (staff enrolled in the staff store only → signed in with password alone, as today); `Wrong_guest_code_counts_in_guest_throttle`; `Backup_code_signs_in_once`; `Owner_resets_resident_two_factor_from_security_page`.
- [ ] **Step 2: Run** `--filter "ResidentTwoFactorTests|GuestSignInTests|SecurityPageTests"` → FAIL.
- [ ] **Step 3: Implement.** Share the enrol/turn-off markup with `Profile/TwoFactor` through a partial if it stays simple; the builder's call.
- [ ] **Step 4: Run** → PASS (existing `TwoFactorSignInTests` and `GuestSignInTests` unchanged and green).
- [ ] **Step 5: Commit** `Recovery: two-factor sign-in for residents, asleep until the rule asks`.

### Task 6: The ask page

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Guests/PasswordResets.cs`, `Pages/Guest/ForgotPassword.cshtml(.cs)` (`@page "/guest/forgot-password"`)
- Modify: `Program.cs` (register `ResetLimits` with `<data>/guests/reset-limits.json`, `PasswordResets`; open-page list), `Guests/TermsGate.cs`, `Pages/Login.cshtml` and `Pages/Guest/SignIn.cshtml` ("Forgot password?" link, only where there is a guest side)
- Test: `PasswordResetTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3; `IRobustClient.FindAccountAsync`, `BotGuard.Check(ctx, "forgot-password")`, `EmailSender.Queue/IsReady`, `EmailTokens.Issue`, `AuditLog`, `AdminRule.RoleOf`.
- Produces:
  - `public enum ResetAsk { Sent, GridOff }` and `public sealed class PasswordResets(...) { bool Available(); Task<ResetAsk> AskAsync(string? name, string? email, IPAddress? ip, string resetUrlBase, CancellationToken ct); }`
  - `Available()` = `Recovery.AnyOn(s) && sender.IsReady`. `AskAsync` returns `GridOff` only when `!Available()`; every other outcome is `Sent` (the page shows the one reply). Order: address limit (over → audit `reset-refused`, no lookup); `CountAddress`; parse name; `FindAccountAsync` (always, when the name is complete); match = account found, `UserLevel >= 0`, email equal after `Trim()` and ordinal-ignore-case, `RuleFor(role) != Off`, `SendAllowed`; on match `CountSend`, `Issue("reset-password", principal, 30 min)`, `Queue(ResetPassword, account.Email, "First Last", {Link = resetUrlBase + "?token=" + token, ExpiresIn = "30 minutes"}, 30 min)`, audit `reset-requested`; otherwise audit `reset-refused`. A Robust failure is logged and still returns `Sent`.
  - The page: name, email, the bot fields (`BotGuard` like `SignUp`); a failed bot check shows the same error text `SignUp` shows; `GridOff` shows the grid-wide message before the form (on GET too).
- [ ] **Step 1: Failing tests** (through `LauncherFactory`, with `FakeSmtp` and one email account as `EmailPageTests` set it up): `Match_sends_one_email_with_a_link`; `Same_reply_for_no_match_wrong_email_and_rule_off_for_that_group` (identical page text in all three; no email); `Email_match_ignores_case_and_spaces`; `Fourth_request_for_one_account_sends_nothing`; `Eleventh_request_from_an_address_does_not_look_up` (`Robust.Lookups` unchanged); `All_rules_off_shows_grid_message`; `Email_not_ready_shows_grid_message`; `Suspended_account_gets_no_email`; `Bot_check_failure_shows_sign_up_error`; `Page_404_on_region_server`; `Open_from_outside_admin_networks`; `Login_page_links_to_forgot_password_only_with_guest_side`; `Audit_has_no_email_or_token`.
- [ ] **Step 2: Run** `--filter PasswordResetTests` → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Recovery: the Forgot password page`.

### Task 7: The reset page

**Files:**
- Create: `Pages/Guest/ResetPassword.cshtml(.cs)` (`@page "/guest/reset-password"`)
- Modify: `Guests/PasswordResets.cs`, `Program.cs` (open-page list), `Guests/TermsGate.cs`
- Test: `PasswordResetTests.cs`

**Interfaces:**
- Consumes: Tasks 1–5; `StaffSessions.EndAll`, `GuestStamps.Rotate`, `LoginThrottle` (door from the account's role: `None` → `Guest`, else `Staff`), the staff `TwoFactorStore` for staff and owner, `ResidentTwoFactor.Store` for residents.
- Produces:
  - `public sealed record ResetLink(Guid Principal, RobustAccount Account, bool NeedsCode)`; `Task<ResetLink?> OpenAsync(string? token, CancellationToken ct)` — null when `Find` gives nothing, the account is gone or below level 0, or the account's rule is now `Off` (Review Focus 4). `NeedsCode` = rule `EmailLinkAndCode` and that person's store says `On`.
  - `public enum ResetResult { Done, Expired, WrongCode, Locked, BadPassword, WriteFailed }`; `Task<(ResetResult Result, string? Problem)> ResetAsync(string? token, string? code, string? password, string? again, IPAddress? ip, CancellationToken ct)` — in order: `OpenAsync`; if `NeedsCode`: locked → `Locked`, `VerifyCode` false → `RecordFailure`, audit `reset-code-wrong`, `WrongCode`; `PasswordRules.Problem` → `BadPassword`; `IRobustPasswords.SetAsync` throws → `WriteFailed` (token not spent); `TryRedeem` false → `Expired` (lost a race: nothing more happens); then `EndAll`, `Rotate`, queue `PasswordChanged`, audit `password-reset`, `Done`.
  - One form (my call, to avoid carrying state between steps): hidden token, the code field only when `NeedsCode`, new password twice. Copy: Expired → "This link has expired or was already used. Ask for a new one." (link to the ask page); WrongCode → "That code isn't right. Try again, or use a backup code."; Locked → "Too many attempts. Try again in a few minutes."; WriteFailed → "Couldn't change the password right now. Try again in a few minutes."; Done → "Your password is changed. Sign in with your new password." with a link to `/guest/sign-in` (resident) or `/login` (staff, owner). A code problem keeps the passwords out of the re-rendered form.
- [ ] **Step 1: Failing tests:** `Full_reset_then_sign_in_with_new_password` (ask → read the link from `FakeSmtp.Received` → reset → `/guest/sign-in` works with the new one, the old one fails); `Old_sessions_end_after_reset` (a guest cookie and a staff cookie from before are refused); `Notice_email_sent_after_reset`; `Second_use_of_a_link_changes_nothing`; `Failed_write_keeps_the_link` (`PasswordsDown`, then up, same link works); `Expired_link_after_31_minutes` (advance `Time`); `Rule_with_code_but_no_two_factor_needs_link_only`; `Code_required_when_enrolled` (staff with staff two-factor; resident with resident two-factor); `Wrong_code_counts_in_throttle_and_locks_after_five`; `Backup_code_works_once`; `Link_refused_after_rule_turned_off`; `Weak_password_shows_the_rule`; `Staff_done_page_links_to_login`; `Audit_has_no_secrets`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Recovery: the reset page`.

### Task 8: Setup question and the Advanced page

**Files:**
- Create: `Pages/Setup/Security.cshtml(.cs)` (`@page "/setup/security"`), `Pages/Advanced/PasswordReset.cshtml(.cs)` (`@page "/advanced/password-reset"`)
- Modify: `Launcher/DreamGrid.Launcher/Setup/SetupSteps.cs` (`Single` list only: `("Account security", "/setup/security", s => s.Recovery is not null || s.Setup.Finished)` after "Create your avatar"), `Program.cs` (`/Advanced/PasswordReset` gets OwnerPolicy and `GuestSideOnly` by page convention), `Pages/Advanced/Index.cshtml` (link, owner only, guest side only)
- Test: `SetupFlowTests.cs`, `RecoveryPagesTests.cs`

**Interfaces:**
- Consumes: `Recovery.Apply/Level/Title/Line`, `EmailSender.IsReady`, `IGridController.UpdateSettings(change, writeConfig: false)`.
- Produces:
  - Setup step: "What kind of grid is this?" with three radio answers (title + line), **Home lab** preselected; saving calls `Recovery.Apply`. While `!IsReady` it adds "Set up email first, or password reset won't work." with a link to `/security/email` (opens after setup; the step still saves).
  - Advanced page: three selects (Off / Email link / Email link + two-factor) for Residents, Staff, Owner; the "staff must use two-factor" checkbox (the same `Security.RequireTwoFactorForStaff` the Security page saves); a sentence under the selects: "With "Email link + two-factor", people who haven't set up two-factor get the email link alone."; a second form "Use a level" with the three kinds that calls `Apply`. When `Recovery` is null it shows "Password reset is available. Choose a level to turn it on." When `!IsReady`, the "Set up email first" line.
- [ ] **Step 1: Failing tests:** `Guest_side_plans_ask_the_question_after_the_avatar` (Local; not on Region Server or Connected Server step lists); `Finished_install_skips_the_step` (`Setup.Finished`, `Recovery` null → `FirstUnfinished` unchanged); `Saving_public_grid_sets_rules_and_switch`; `Advanced_page_owner_only` (admin → 403/redirect as other owner pages, owner → 200); `Advanced_page_404_on_region_server`; `Saving_rules_keeps_other_security_settings`; `Use_a_level_applies_the_table`; `Email_not_ready_line_shown`.
- [ ] **Step 2: Run** `--filter "SetupFlowTests|RecoveryPagesTests"` → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS (existing `SetupFlowTests` still green; update any test that walks the Local checklist step by step to answer the new step).
- [ ] **Step 5: Commit** `Recovery: the Setup question and the Advanced page`.

### Task 9: The viewer's "forgot password" link

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Config/GridConfigWriter.cs`, the Robust snapshot(s) under `Launcher/DreamGrid.Launcher.Tests/Snapshots/` if Robust is snapshotted (else only the test)
- Test: `GridConfigWriterTests.cs`

**Interfaces:**
- Produces: on plans with a guest side, `robust.Set("GridInfoService", "password", $"http://{host}:{s.WebPort}/guest/forgot-password")`, next to `welcome`. `register=` untouched. `[AuthenticationService] AllowSetPassword` stays absent/commented.
- [ ] **Step 1: Failing tests:** `Viewer_password_link_points_to_forgot_password`; `Allow_set_password_never_on` (written Robust.HG.ini has no uncommented `AllowSetPassword = true`).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Run the whole suite** `dotnet test Launcher/DreamGrid.Launcher.Tests` → all PASS.
- [ ] **Step 6: Commit** `Recovery: the viewer's forgot-password link`; tick worklist 2.3 in `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` in the same commit.
