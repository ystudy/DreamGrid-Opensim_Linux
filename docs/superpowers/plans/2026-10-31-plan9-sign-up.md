# Plan 9 — Resident sign-up — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new person makes their own avatar from a short guest-side form, confirms it by an email link (or straight away when the grid has no email), and lands on a "How to connect" page that tells them how to reach the grid with a viewer.

**Architecture:** Pure rules (name and email checks, the pending sign-ups list) live in `DreamGrid.Launcher.Core`, unit-tested. `IGridController` gets one call that makes a resident avatar through Robust's console (random console-safe password, then the level) and one that deletes an avatar's rows. The website gets one `SignUps` service that owns the form, the confirm link and the resend (rules, limits, token, email, audit), three anonymous guest pages on top of it, an owner switch, a clean-up background job, and small changes to sign-in, People, Bots and the Robust INI writer.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, xUnit v3, AngleSharp (tests), MySqlConnector (existing). No new packages.

**Spec:** `docs/superpowers/specs/2026-10-31-sign-up-design.md`. The audiences, email and password-recovery designs stay binding where the spec doesn't change them.

## Global Constraints

- Windows, Debian 12 and Debian 13; CI `build-and-smoke` stays green.
- With `SignUp` null (every install from before this plan) sign-up is **closed** and every existing page behaves as today.
- The pages exist only where there is a guest side (`GuestSideOnly`; `AdminRule.HasStaffPages(s)`). `/guest/sign-up`, `/guest/confirm` and `/guest/connect` are anonymous (open-page list in `Program.cs`), outside `TermsGate` (add to `TermsGate.Open`), and already exempt from Admin networks (`/guest` in `AdminNetworkFilter.PublicPaths`; check, don't add).
- Sign-up is open only when `SignUpSettings.Open` **and** `LegalPages.SignUpGateOpen(s)`. Closed message, word for word: "This grid isn't open for sign-ups yet."
- The "check your email" reply, word for word: "Check your email. We sent a link to confirm your account. It works once, for 24 hours. Check your spam folder."
- Busy message, word for word: "Sign-up isn't working right now. Try again in a few minutes."
- Name taken, word for word: "That name is taken. Try another."
- Expired link, word for word: "This link has expired or was already used."
- Sign-in refusal for a pending avatar: "Confirm your email first." with a "Send the link again" button. A suspended (not pending) avatar keeps today's message.
- Token: purpose `confirm-signup`, lifetime **24 hours**, `EmailTokens`; template `EmailTemplates.ConfirmEmail`, `{ExpiresIn}` = "24 hours"; email queued with `validFor: 24 hours`.
- Limits: **3** confirm emails per account per hour, **3** per typed email per hour, **10** posts per address per hour: a second `ResetLimits` instance on its own file `<data>/guests/signup-limits.json`. `BotGuard.Check(ctx, "signup")` runs first.
- Name parts: 1 to **31** characters, `[A-Za-z0-9-]` only, and pass `GridConsoleCommands.ValidateAvatarName`. Password: `PasswordRules.Problem`.
- Pending avatars sit at level **-1** and are on the pending list; confirmed ones are level **0** and off it. The clean-up deletes **only** listed avatars older than 24 hours.
- The typed password never goes on the Robust console; it is written with `IRobustPasswords.SetAsync`.
- Never log or audit a password, token or typed email. Audit actions: `signup-made`, `signup-refused` (result `refused`, reason word), `signup-confirmed`, `signup-expired`, `signup-switch` (`on`/`off`).
- No inline script, no `on…=` handlers, no `style="…"` (`InlineScriptScanTests`).
- UI copy plain English, short sentences. Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Run tests with `dotnet test Launcher/DreamGrid.Launcher.Tests --filter <Class>`.

## Review Focus

1. **Two people submit the same new name at the same moment** → one gets the avatar; the other gets "That name is taken. Try another." and nothing half-made is left. (Task 3 `Second_create_with_same_name_is_taken`.)
2. **Robust stops between making the avatar and writing the password** → busy message, the avatar is deleted, the name is free. (Task 3 `Password_step_failure_deletes_the_avatar`.)
3. **The owner suspends a resident who confirmed weeks ago** → the clean-up never deletes them, and their sign-in shows the suspended message, not "Confirm your email first". (Task 7 `Confirmed_then_suspended_is_kept`, Task 5 `Suspended_not_pending_keeps_old_message`.)
4. **Email stops working after someone signed up but before they clicked** → the link they already have still confirms; "Send the link again" says the busy message rather than pretending. (Task 4 `Resend_without_email_is_busy`.)
5. **The person types their email with capitals or spaces** → the email-taken check and the per-email limit treat it as the same address. (Task 4 `Email_taken_ignores_case_and_spaces`.)

---

### Task 1: Sign-up rules and the switch (Core + settings)

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Guests/SignUpRules.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs` (`public SignUpSettings? SignUp { get; init; }`; null means closed, no schema bump)
- Test: `Launcher/DreamGrid.Launcher.Tests/SignUpRulesTests.cs`, `SettingsStoreTests.cs` (round-trip)

**Interfaces:**
- Produces:
  - `public sealed record SignUpSettings(bool Open = false);`
  - `public static class SignUpRules`:
    - `public const int MaxNamePart = 31;`
    - `public static string? NameProblem(string? first, string? last, LauncherSettings s)` — null when fine; otherwise one plain sentence. Refuses empty, longer than 31, characters outside `[A-Za-z0-9-]`, the owner's name (`s.Setup.Owner`, case-insensitive) and a reserved list (`"Test User"`, `"Grid Owner"`, `"Admin Admin"`, case-insensitive; the builder may add more).
    - `public static string? EmailProblem(string? email)` — trims; refuses empty, spaces inside, not exactly one `@`, nothing before or after it, or a `"`.
    - `public static string NormalEmail(string email)` — trimmed, lower-case invariant.
    - `public static bool IsOpen(LauncherSettings s)` — `s.SignUp?.Open == true && LegalPages.SignUpGateOpen(s)`.
- [ ] **Step 1: Failing tests:** `Null_settings_is_closed`; `Open_needs_the_legal_gate`; `Name_32_chars_refused`; `Name_with_space_or_dot_refused`; `Owner_name_refused_any_case`; `Reserved_name_refused`; `Hyphen_and_digits_allowed` (`"Ann-Marie"`, `"Bot7"`); `Email_two_ats_refused`; `Email_trimmed_and_lowered` (`" Ann@X.com "` → `"ann@x.com"`); settings round-trip with `SignUp` set and missing (reads null).
- [ ] **Step 2: Run** `--filter "SignUpRulesTests|SettingsStoreTests"` → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Sign-up: name and email rules and the switch`.

### Task 2: The pending sign-ups list

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Guests/PendingSignUps.cs`
- Test: `PendingSignUpsTests.cs`

**Interfaces:**
- Produces: `public sealed class PendingSignUps(string file, TimeProvider time, ILogger<PendingSignUps>? log = null)`, the same file pattern as `SuspendedStore` (JSON, owner-only folder, written atomically, safe from any thread):
  - `void Add(Guid principal)` — records now.
  - `bool Contains(Guid principal)`
  - `void Remove(Guid principal)`
  - `IReadOnlyList<Guid> OlderThan(TimeSpan age)`
  - The file holds PrincipalIDs and times only.
- [ ] **Step 1: Failing tests:** `Added_is_contained_until_removed`; `OlderThan_uses_the_clock` (advance 25 h with a `FakeTimeProvider`); `Survives_restart`; `File_holds_no_email` (add, then the file text has only GUIDs and dates).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Sign-up: the list of sign-ups waiting for email`.

### Task 3: Making and deleting a resident avatar (grid controller)

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/IGridController.cs`, `GridController.cs`, `Launcher/DreamGrid.Launcher.Core/Database/StaffDirectory.cs`
- Create: `Launcher/DreamGrid.Launcher.Core/Database/AccountRemover.cs`
- Modify (tests): `FakeGridController.cs` (`FakeRobustAccounts` gets the new calls; a `CreateResidentThrows` and `PasswordsDown` switch), `LauncherFactory.cs`
- Test: `GridControllerResidentTests.cs`, `AccountRemoverTests.cs`

**Interfaces:**
- Produces:
  - `Task<RobustAccount> CreateResidentAsync(string first, string last, string? email, int level, CancellationToken ct)` on `IGridController`. Under `_ops`: throws `NameTakenException` (new, `Core/Grid`) when `robust.GetAccountAsync` finds the name; otherwise `CreateUserAsync` with a random console-safe password (`Convert.ToHexString(RandomNumberGenerator.GetBytes(16))`) and a new PrincipalID, `WaitForAccountAsync`, then `set user level` when `level != 0` and wait for it. Throws `HttpRequestException`/`InvalidOperationException` as `CreateOwnerAsync` does. On a failure after the account appeared, calls `IAccountRemover.DeleteAsync` before rethrowing.
  - `public interface IAccountRemover { Task DeleteAsync(Guid principal, CancellationToken ct); }` and `AccountRemover(Func<LauncherSettings> settings)` — one transaction as `robustuser` deleting the principal's rows from `robust.inventoryitems` (by `avatarID`), `inventoryfolders` (by `agentID`), `Avatars` (by `PrincipalID`), `GridUser` and `Presence` (by `UserID`), `tokens` and `auth` (by `UUID`) and `UserAccounts` (by `PrincipalID`). Throws `StaffDirectoryUnavailableException` when the database can't be asked. (OpenSim has no `delete user` console command.)
  - `Task<RobustAccount?> FindByEmailAsync(string normalEmail, CancellationToken ct)` on `IStaffDirectory` — `WHERE LOWER(TRIM(Email)) = @email LIMIT 1`.
- [ ] **Step 1: Failing tests:** `Creates_at_level_minus_one` (fake console sees `create user` then `set user level … -1`); `Level_zero_skips_set_level`; `Second_create_with_same_name_is_taken`; `Console_password_is_32_hex` (the `create user` line's password argument matches `^[0-9A-F]{32}$`); `Password_step_failure_deletes_the_avatar` (the remover fake records the principal); `Remover_sql_names_every_table` (the statement list built by `AccountRemover` covers the eight tables, tested on the builder method, no live database).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Sign-up: make and delete a resident avatar`.

### Task 4: The SignUps service

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Guests/SignUps.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Email/EmailTemplates.cs` (new `AlreadyHaveAccount = "already-have-account"` in `All`; words `{GridName}`, `{AvatarName}`, `{Link}`; required `{Link}`; subject "Someone tried to sign up on {GridName}"; body per spec §4.7, `{Link}` = the forgot-password page), `Program.cs` (register `SignUps`, `PendingSignUps` at `<data>/guests/pending-signups.json`, the second `ResetLimits` keyed `SignUpLimits` at `signup-limits.json`, `AccountRemover`)
- Test: `SignUpsTests.cs`, `EmailTemplatesTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3; `BotGuard`, `EmailTokens`, `EmailSender`, `IRobustPasswords`, `TermsAcceptances`, `AuditLog`.
- Produces:
  - `public sealed record SignUpForm(string? First, string? Last, string? Email, string? Password, string? Again, bool AcceptTerms);`
  - `public enum SignUpOutcome { Closed, Invalid, NameTaken, CheckEmail, SignedUp, Busy }`
  - `public sealed record SignUpResult(SignUpOutcome Outcome, string? Problem = null, RobustAccount? Account = null);`
  - `public sealed class SignUps(...)` with `public const string Purpose = "confirm-signup"; public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(24); public const string ConfirmPath = "/guest/confirm";`
    - `Task<SignUpResult> SubmitAsync(SignUpForm form, IPAddress? ip, CancellationToken ct)` — order: closed → address limit → name, password, email, terms (first problem as `Invalid`) → with email ready: email taken? (queue `AlreadyHaveAccount` if `SendAllowed`, return `CheckEmail`, make nothing) → `CreateResidentAsync(level: email ready ? -1 : 0)` → `IRobustPasswords.SetAsync` → `TermsAcceptances.Accept` → with email: `PendingSignUps.Add`, `Issue`, queue `ConfirmEmail`, `CheckEmail`; without: `SignedUp` with the account. Bot check is done by the page before calling (it needs `HttpContext`).
    - `Task<RobustAccount?> ConfirmAsync(string? token, CancellationToken ct)` — `Find`, check `PendingSignUps.Contains`, `SetUserLevelAsync(… 0)`, then `TryRedeem`, `Remove`; null for any bad link. Busy errors throw `SignUpBusyException`.
    - `Task<SignUpOutcome> ResendAsync(string? first, string? last, IPAddress? ip, CancellationToken ct)` — `CheckEmail` in every case except email not ready (`Busy`); sends only for a pending avatar within limits, replacing the old link.
- [ ] **Step 1: Failing tests:** `Closed_makes_nothing`; `Problems_in_order` (bad name reported before bad password); `Name_taken_said`; `Email_taken_not_said_and_notice_sent`; `Email_taken_ignores_case_and_spaces`; `With_email_avatar_is_pending`; `Without_email_avatar_is_level_zero_and_not_pending`; `Terms_recorded_at_sign_up`; `Confirm_raises_level_and_spends_link`; `Confirm_failure_keeps_link`; `Confirm_refuses_unlisted_principal`; `Resend_replaces_link_and_counts`; `Resend_without_email_is_busy`; `Fourth_email_in_an_hour_not_sent`; `Nothing_secret_audited` (audit text has no password, token or email); `Already_have_account_template_in_All`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Sign-up: the sign-up service`.

### Task 5: The guest pages and sign-in

**Files:**
- Create: `Pages/Guest/SignUp.cshtml(.cs)` (`@page "/guest/sign-up"`), `Pages/Guest/Confirm.cshtml(.cs)` (`@page "/guest/confirm"`, `?token=`), `Pages/Guest/Connect.cshtml(.cs)` (`@page "/guest/connect"`), `Pages/Shared/_LoginAddress.cshtml` (address box and copy button, the button's script in a `wwwroot/js` file)
- Modify: `Pages/Guest/SignIn.cshtml(.cs)` (pending → "Confirm your email first." and a resend form), `Pages/Guest/Index.cshtml` ("Sign up" when open, "How to connect" always), `Pages/Setup/Connect.cshtml` (use `_LoginAddress`), `Program.cs` (open pages), `Guests/TermsGate.cs` (`/Guest/SignUp`, `/Guest/Confirm`, `/Guest/Connect`)
- Test: `SignUpPageTests.cs`, `GuestSignInTests.cs`, `ConnectPageTests.cs`

**Interfaces:**
- Consumes: Task 4.
- Sign-up page: `_BotFields`; `BotGuard.Check(HttpContext, "signup")` before `SubmitAsync`; `SignedUp` → `GuestSignIn.SignInAsync` then redirect `/guest/connect`; `CheckEmail` → same page showing the reply and a resend form (name fields kept); `Closed` → closed message, no form.
- Confirm page: on success `GuestSignIn.SignInAsync` and redirect `/guest/connect`; on null the expired message with links to `/guest/sign-up` and `/guest/sign-in`; on `SignUpBusyException` the busy message.
- Connect page: grid name, `GridEndpoints.LoginUri(s, host)` in `_LoginAddress`, the four Firestorm steps from spec §6 (Firestorm download link `https://www.firestormviewer.org/downloads/`, "the OpenSim version"), "Log in as {First} {Last}" when signed in, and the one line for other viewers.
- [ ] **Step 1: Failing tests:** `Sign_up_closed_shows_message`; `Sign_up_form_has_bot_fields`; `Without_email_lands_on_connect_signed_in`; `With_email_shows_check_email`; `Failed_bot_check_makes_nothing`; `Confirm_link_lands_on_connect`; `Bad_link_shows_expired`; `Pending_sign_in_says_confirm_first`; `Suspended_not_pending_keeps_old_message`; `Connect_shows_login_uri_and_steps`; `Connect_greets_signed_in_resident`; `Pages_absent_on_region_server` (404); `Front_page_links_sign_up_only_when_open`; `No_inline_script` (scan passes).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Sign-up: the sign-up, confirm and how-to-connect pages`.

### Task 6: The owner's switch, warnings, Bots and People

**Files:**
- Modify: `Pages/Security/Legal.cshtml(.cs)` (a "Let people sign up" switch under the gate sentence; disabled with the gate's missing-items sentence while the gate is shut; owner only; audits `signup-switch`), `Pages/Security/Bots.cshtml(.cs)` (hide the `AdminApproval` switch; on post keep the stored `AdminApproval`), `Pages/People/Index.cshtml(.cs)` (`Person.Pending`; "Waiting for email" badge; no Unsuspend for pending)
- Test: `LegalPageTests.cs`, `BotsPageTests.cs` (update the existing `AdminApproval` cases), `PeoplePageTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2.
- Warnings on the Legal page while open: every bot layer off → "Every protection is off. Bots can create accounts freely."; `EmailSender.IsReady` false → "Email isn't set up, so anyone can sign up without proving an email address."
- [ ] **Step 1: Failing tests:** `Switch_disabled_while_gate_shut`; `Switch_saves_and_audits`; `Non_owner_admin_cannot_switch`; `Warns_when_all_bot_layers_off`; `Warns_when_email_not_ready`; `Bots_page_hides_admin_approval`; `Bots_post_keeps_stored_admin_approval` (stored true stays true); `People_shows_waiting_for_email`; `People_offers_no_unsuspend_for_pending`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Sign-up: the owner's switch, and pending people on the People page`.

### Task 7: Clean-up of unconfirmed sign-ups

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Guests/SignUpCleanup.cs` (`BackgroundService`, every hour, first run 5 minutes after start)
- Modify: `Program.cs` (register after `GridHostedService`, like the other guest workers)
- Test: `SignUpCleanupTests.cs`

**Interfaces:**
- Consumes: `PendingSignUps.OlderThan(SignUps.LinkLifetime)`, `IAccountRemover`, `AuditLog`.
- `public Task RunOnceAsync(CancellationToken ct)` — for each old principal: if the account is gone or its level is not -1, just `Remove` it from the list; otherwise `DeleteAsync`, `Remove`, audit `signup-expired`. A database error leaves the list as it was and logs a warning; the next run tries again.
- [ ] **Step 1: Failing tests:** `Deletes_listed_older_than_24h`; `Keeps_listed_younger`; `Never_touches_unlisted_minus_one` (a suspended account at -1 not on the list stays); `Confirmed_then_suspended_is_kept` (confirmed → off the list → suspended → kept); `Database_down_keeps_the_list`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Sign-up: remove sign-ups nobody confirmed`.

### Task 8: Viewer link, worklist and the end-to-end check

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Config/GridConfigWriter.cs` (inside the `AdminRule.HasStaffPages(s)` block: `robust.Set("GridInfoService", "register", $"http://{host}:{s.WebPort}/guest/sign-up")`), `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` (tick 2.1's form, password rules and `register=` lines; note "first version, plan 9"; leave starter avatar and modes open)
- Test: `GridConfigWriterTests.cs`, `SignUpFlowTests.cs` (through `LauncherFactory`, email captured as in the recovery flow tests)

- [ ] **Step 1: Failing tests:** `Register_points_at_sign_up` and `No_register_on_region_server`; `Full_sign_up_with_email` (open the switch, post the form, sign-in refused, open the captured link, land on Connect signed in, sign-in with the typed password works); `Full_sign_up_without_email`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** the whole test project → PASS.
- [ ] **Step 5: Commit** `Sign-up: the viewer's create-account link and the end-to-end check`.

**Left open (spec §5 [?]):** the viewer's message for a level -1 login is checked by hand on a running grid and noted in the PR; no code depends on it.
