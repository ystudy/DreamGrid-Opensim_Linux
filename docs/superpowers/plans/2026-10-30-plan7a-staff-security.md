# Plan 7a — Staff side and security baseline — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Several staff (owner, admins, supervisors) on one grid, with throttled sign-in by name and IP, staff sessions with "sign out everywhere", optional/required two-factor codes, an audit log, an admin network allow-list, browser security headers, and the port-8001 command API brought under the same rules.

**Architecture:** Pure rules (roles, staff changes, TOTP, throttle, audit lines) live in `DreamGrid.Launcher.Core/Security` and `Core/Logs`, unit-tested. The website (`DreamGrid.Launcher`) keeps the existing `dg` cookie scheme for staff and adds session/stamp validation, role claims and three policies; small JSON stores in a new owner-only `<data>/security` folder hold everything sign-in needs, so the staff side works while the grid is stopped. Plan 7b (guest side) builds on this.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, xUnit v3, AngleSharp (tests), MySqlConnector (existing), QRCoder (new, MIT, QR as SVG; no outside service).

**Spec:** `docs/superpowers/specs/2026-10-30-audiences-security-design.md` (sections 2, 3, 6, 7, 8 for the staff side). The worklist `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` 1.2/1.3 stays binding where the spec doesn't change it.

## Global Constraints

- Windows, Debian 12 and Debian 13; CI `build-and-smoke` must stay green (launcher-windows, launcher-debian 12/13, debian, windows, secret-scan, install-smoke).
- No paid or registered outside service, no CDN, nothing loaded from another origin.
- Never write a password, two-factor code, backup code, secret, MachineId or token to any log, including the audit log. Sign-in log lines name the cleaned first name only (`AvatarName.ForLog`), as today.
- Default levels: supervisor **100**, admin **200**; compared with `>=`. The owner is `Setup.Owner` whatever their level.
- Throttle: name **5** failures in **10 minutes** → **10-minute** lock (unchanged); IP **20** failures in **10 minutes** → **10-minute** lock.
- TOTP: RFC 6238, HMAC-SHA1, **6** digits, **30-second** steps, **±1** step accepted, a step already used for that account is refused. **10** backup codes, single use, only SHA-256 hashes stored.
- Level recheck while signed in: at most every **5 minutes**, only when Robust answers; a Robust failure never counts as a demotion.
- Everything sign-in needs is in `<data>/security` (owner-only like `<data>`), never in MariaDB. Writes are atomic (temp file + rename), as `SettingsStore.Save` does.
- The owner can never be demoted, suspended, or locked out by a setting: the two-factor switch doesn't apply to the owner; loopback is always allowed by the allow-list.
- Region Server and Connected Server: no supervisors, no Staff or People pages (404); the Security page, sessions, two-factor, throttle, audit, allow-list and headers apply.
- UI copy is plain English, short sentences, matching existing pages. Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Run tests with: `dotnet test Launcher/DreamGrid.Launcher.Tests --filter <Class>` (Windows locally; Debian-only integration tests run in CI).

## Review Focus

1. **The owner saves an allow-list from a remote browser that doesn't cover their own address** → the save is refused with "This list would lock out the address you're using now (<ip>)." (Task 7 test `Save_refuses_list_that_excludes_the_caller`.)
2. **Robust is down during the 5-minute recheck** → the staff member stays signed in; nothing is ended or logged as a demotion. (Task 4 test `Recheck_keeps_session_when_robust_is_down`.)
3. **A `security/*.json` file is damaged** (half-written, hand-edited) → throttle starts empty with a warning; sessions treat it as no sessions (everyone signs in again); two-factor **fails closed**: staff with unknown two-factor state can't sign in, the log says to run `reset-two-factor`, and that verb moves the damaged file aside. (Tests in Tasks 2, 4, 5.)
4. **A supervisor posts a crafted form to suspend an admin or promote themselves** → refused server-side by `StaffChanges`, audited as refused. (Task 8 test `Supervisor_cannot_suspend_admin_by_post`.)
5. **The same two-factor code is submitted twice within its 30 seconds** (double click, replay) → second use refused. (Task 5 test `Verify_refuses_reused_step`.)

---

### Task 1: Security settings and staff roles

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs` (add `SecuritySettings`, `Security` property; `CurrentSchema = 5`)
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/SettingsStore.cs` (migration 4 → 5: version only)
- Modify: `Launcher/DreamGrid.Launcher.Core/Security/AdminRule.cs`
- Create: `Launcher/DreamGrid.Launcher.Core/Security/StaffRole.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/StaffRoleTests.cs`, `SettingsStoreTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record SecuritySettings(int SupervisorLevel = 100, int AdminLevel = 200, bool RequireTwoFactorForStaff = false, IReadOnlyList<string>? AdminNetworks = null)`; `LauncherSettings.Security { get; init; } = new();`
  - `public enum StaffRole { None, Supervisor, Admin, Owner }`
  - `AdminRule.RoleOf(LauncherSettings s, RobustAccount a) -> StaffRole`; `AdminRule.MayAdminister` = `RoleOf(...) >= StaffRole.Admin`; `AdminRule.EffectiveLevel` unchanged in meaning but uses `s.Security.AdminLevel`. `AdminRule.AdminLevel` const is removed; callers read `s.Security.AdminLevel`.
  - `AdminRule.HasStaffPages(LauncherSettings s) -> bool` (false on RegionServer and Connected).

- [ ] **Step 1: Failing tests** in `StaffRoleTests`:
  - `RoleOf_by_level_on_local`: levels 0, 99 → None; 100, 199 → Supervisor; 200, 250 → Admin; the owner's principal at level 0 → Owner.
  - `RoleOf_uses_level_settings`: with `Security = new(SupervisorLevel: 50, AdminLevel: 150)`, level 50 → Supervisor, 150 → Admin.
  - `RoleOf_region_server_has_no_supervisors`: plan RegionServer, level 100 → None; level 200 → Admin; owner → Owner.
  - `RoleOf_connected_only_owner`: plan Connected, level 250 → None; owner → Owner.
  - `HasStaffPages`: true for Local, SimplePublic, GridAsset, null; false for RegionServer, Connected.
  - In `SettingsStoreTests`: `Schema4_file_loads_with_default_security`: a schema-4 JSON without `security` loads with `SupervisorLevel == 100`, `AdminLevel == 200`, `RequireTwoFactorForStaff == false`, `AdminNetworks` null, and `SchemaVersion == 5`.
- [ ] **Step 2: Run** `dotnet test Launcher/DreamGrid.Launcher.Tests --filter "StaffRoleTests|SettingsStoreTests"` → FAIL (types missing).
- [ ] **Step 3: Implement** the records, enum and `RoleOf`; replace every `AdminRule.AdminLevel` / `LoginModel.AdminLevel` use (Login page, `LegacyCommandApi`, `GridController.CreateOwnerAsync` keeps setting level **200** literally — the owner is created at the default admin level and `Setup.Owner` makes them owner anyway). `SettingsValidator`: `SupervisorLevel` ≥ 1, `AdminLevel` > `SupervisorLevel`, `AdminLevel` ≤ 255 (OpenSim's god level range).
- [ ] **Step 4: Run** the same filter, then the whole test project → PASS.
- [ ] **Step 5: Commit** `Security: staff roles from levels set in settings`.

### Task 2: Security folder and throttle by name and IP, kept across restarts

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/LauncherPaths.cs` (`Security => Path.Combine(Data, "security")`; created owner-only in `Program.cs` with `CreateOwnerOnly`, like `DataProtectionKeys`)
- Create: `Launcher/DreamGrid.Launcher.Core/AtomicFile.cs` (moved out of `SettingsStore`: temp file, owner-only mode on Unix, flush, `ReplaceWithRetry`, directory sync; `SettingsStore.Save` uses it)
- Modify: `Launcher/DreamGrid.Launcher.Core/Security/LoginThrottle.cs`
- Modify: `Launcher/DreamGrid.Launcher/Pages/Login.cshtml.cs`, `Launcher/DreamGrid.Launcher.Core/Api/LegacyCommandApi.cs`, `Launcher/DreamGrid.Launcher/LegacyEndpoints.cs`, `Program.cs` (registration)
- Test: `LoginThrottleTests.cs`, `LoginTests.cs`, `LegacyCommandApiTests.cs`

**Interfaces:**
- Produces:
  - `public static class AtomicFile { public static void WriteAllText(string path, string text); }`
  - `public sealed class LoginThrottle(TimeProvider time, string? stateFile, ILogger<LoginThrottle>? log = null)` with `NameMaxFailures = 5`, `IpMaxFailures = 20`, `Window = 10 min`; `bool IsLocked(string name, IPAddress? ip)`, `void RecordFailure(string name, IPAddress? ip)`, `void RecordSuccess(string name)` (clears the name only; IP failures stay). State saved to `stateFile` (`<data>/security/throttle.json`) after each change; loaded at construction; IPv4-mapped IPv6 normalised to IPv4.
  - `LegacyCommandApi.HandleAsync(LegacyRequest r, IPAddress? caller, CancellationToken ct)`.

- [ ] **Step 1: Failing tests** in `LoginThrottleTests` (existing name tests updated to the new signature):
  - `Ip_locks_after_20_failures_across_names`: 20 failures from 10.0.0.5 with 20 different names → `IsLocked("Someone Else", 10.0.0.5)` true; from 10.0.0.6 false.
  - `Success_clears_name_not_ip`.
  - `Mapped_ipv6_counts_as_ipv4`: failures from `::ffff:10.0.0.5` lock `10.0.0.5`.
  - `Lock_survives_new_instance`: lock a name, construct a new `LoginThrottle` on the same file and same `FakeTimeProvider` → still locked; after 10 minutes → not locked.
  - `Damaged_state_file_starts_empty`: file containing `{not json` → constructs, nothing locked, one Warning logged.
  - In `LoginTests`: `Ip_lock_refuses_correct_password`: 20 wrong passwords for assorted names from one fake remote address (`LauncherFactory.Before` sets `RemoteIpAddress`), then the owner's correct password from the same address shows the locked message.
  - In `LegacyCommandApiTests`: `Command_api_counts_failures_by_caller_ip`.
- [ ] **Step 2: Run** `--filter "LoginThrottleTests|LoginTests|LegacyCommandApiTests"` → FAIL.
- [ ] **Step 3: Implement.** Login page passes `HttpContext.Connection.RemoteIpAddress`; `LegacyEndpoints.Root` passes the caller address.
- [ ] **Step 4: Run** filter, then full project → PASS.
- [ ] **Step 5: Commit** `Security: throttle sign-ins by name and address, kept across restarts`.

### Task 3: Audit log

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Logs/AuditLog.cs`
- Create: `Launcher/DreamGrid.Launcher/Security/AuditMiddleware.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Logs/LogReader.cs` (source "audit" → `<logs>/audit.log`), `Pages/Logs.cshtml(.cs)` (lists it), `Pages/Login.cshtml.cs`, `LegacyCommandApi.cs`, `Program.cs`
- Test: `AuditLogTests.cs`, `LoginTests.cs`, `LogReaderTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record AuditEntry(string Actor, Guid? ActorId, string Role, string? Ip, string Action, string? Target, string Result)`
  - `public sealed class AuditLog(string path, TimeProvider time, long maxBytes = 10 MiB, int files = 5) { public void Write(AuditEntry e); }` — one JSON object per line: `{"time":"<UTC ISO-8601>","actor":…,"actorId":…,"role":…,"ip":…,"action":…,"target":…,"result":…}`; rotation as `RollingFileLoggerProvider` (reuse its rotation code by extracting it, or call the same helper). Thread-safe.
  - Action names used across the plan (exact strings): `sign-in`, `sign-in-refused`, `sign-out-everywhere`, `session-ended`, `two-factor-on`, `two-factor-off`, `two-factor-reset`, `backup-code-used`, `level-changed`, `suspended`, `unsuspended`, `security-settings`, `command` (port-8001), `request` (generic).
  - `AuditMiddleware`: for every request that isn't GET/HEAD/OPTIONS made by a signed-in staff principal, after the response, writes `Action = "request"`, `Target = "<METHOD> <path>"` (path only, never the query string), `Result = status code`. Registered after authentication.
- [ ] **Step 1: Failing tests:**
  - `AuditLogTests.Line_is_json_with_utc_time`: parse the line; `time` ends with `Z`; fields as given.
  - `AuditLogTests.Rotates_at_max_bytes`.
  - `LoginTests.Sign_in_is_audited_without_password`: successful sign-in → `audit.log` has `sign-in` with actor "Test Owner"; a failed one → `sign-in-refused` with actor equal to the cleaned first name only; the file contains neither password string.
  - `LoginTests.Staff_post_is_audited_without_query`: signed in, POST `/dashboard?handler=Start&password=x` (any staff POST) → a `request` entry whose target is `POST /dashboard` and no `password=x` anywhere in the file.
  - `LogReaderTests.Lists_audit_source`.
- [ ] **Step 2: Run** `--filter "AuditLogTests|LoginTests|LogReaderTests"` → FAIL.
- [ ] **Step 3: Implement**; register `AuditLog` as a singleton on `Path.Combine(paths.Logs, "audit.log")`; `LegacyCommandApi` writes `command` entries for commands with `MinLevel > 0` that ran (actor: avatar name, or `MachineId` — the literal word, never its value).
- [ ] **Step 4: Run** filter, full project → PASS.
- [ ] **Step 5: Commit** `Security: audit log for staff actions`.

### Task 4: Staff sessions, role claims, policies and the 5-minute recheck

**Files:**
- Rename: `Launcher/DreamGrid.Launcher/Security/AdminSignIn.cs` → `StaffSignIn.cs`
- Create: `Launcher/DreamGrid.Launcher/Security/StaffSessions.cs`, `Security/StaffPrincipalValidator.cs`
- Modify: `Security/AuthSchemes.cs`, `Program.cs`, `Pages/Login.cshtml.cs`, `Pages/Logout.cshtml.cs`, `Pages/Setup/Avatar.cshtml.cs` (and any other `AdminSignIn` caller), `Pages/Shared/_Layout.cshtml` (link "Sessions")
- Create: `Pages/Profile/Sessions.cshtml(.cs)`
- Modify: `Launcher/DreamGrid.Launcher.Tests/TestAuth.cs`
- Test: `StaffSessionsTests.cs`, `StaffAccessTests.cs`

**Interfaces:**
- Consumes: `AdminRule.RoleOf` (Task 1), `AuditLog` (Task 3), `AtomicFile` (Task 2).
- Produces:
  - `AuthSchemes`: keep `Admin = "dg"`, `AdminPolicy = "admin"`; add `OwnerPolicy = "owner"`, `StaffPolicy = "staff"`; role claim values `"owner"`, `"admin"`, `"staff"`. An owner carries all three, an admin `admin`+`staff`, a supervisor `staff`. Fallback policy unchanged (admin).
  - `public sealed record StaffSession(string Id, DateTimeOffset Started, DateTimeOffset LastSeen, string? Ip, string? Browser)`
  - `public sealed class StaffSessions(string file, TimeProvider time)`: `StaffSession Start(Guid principal, string? ip, string? browser)`, `string StampOf(Guid principal)`, `bool IsValid(Guid principal, string sessionId, string stamp)`, `void Touch(Guid principal, string sessionId)` (writes at most once per minute per session), `IReadOnlyList<StaffSession> List(Guid principal)`, `void End(Guid principal, string sessionId)`, `void EndAll(Guid principal)` (new stamp, no sessions). File `<data>/security/sessions.json`; a damaged file reads as empty (warning logged). Sessions older than the cookie's 8-hour sliding lifetime since `LastSeen` are dropped on write.
  - `StaffSignIn.SignInAsync(HttpContext ctx, RobustAccount account, StaffRole role, bool mustEnrolTwoFactor = false)`: claims Name, `principal`, roles, `sid`, `stamp`, `checked` (unix seconds), and `enrol` when `mustEnrolTwoFactor`.
  - `StaffPrincipalValidator.ValidateAsync(CookieValidatePrincipalContext c)` (wired to `OnValidatePrincipal`): rejects (and signs out) when `sid`/`stamp` are missing or `!IsValid`; when `checked` is 5 minutes old, re-reads the account from `IRobustClient` and `RoleOf`: lower role → `EndAll`, reject, audit `session-ended` with target "role changed"; same role → renew `checked`; Robust error → keep as is.
  - Page `/profile/sessions` (StaffPolicy): table of own sessions, "End" per row, "Sign out everywhere" button (audited).
  - `TestAuth`: `/__test/signin?role=owner|admin|supervisor` (default `admin`) signs in through `StaffSignIn` with a real session; principal Guids fixed per role and exposed as `TestAuth.OwnerId/AdminId/SupervisorId`.
- [ ] **Step 1: Failing tests:**
  - `StaffSessionsTests`: `Start_then_valid`; `EndAll_invalidates_all_and_rotates_stamp`; `End_one_keeps_others`; `Damaged_file_reads_as_empty`; `Touch_writes_at_most_once_a_minute` (file timestamp/contents unchanged after two touches 10 s apart).
  - `StaffAccessTests` (integration):
    - `Cookie_without_session_is_refused`: a cookie minted with only Name+role (old-style) → `/dashboard` redirects to `/login`.
    - `Sign_out_everywhere_ends_other_browser`: two clients signed in as the same admin; client A posts "Sign out everywhere"; client B's next `/dashboard` → redirect to `/login`.
    - `Supervisor_refused_on_admin_page`: supervisor GET `/regions` → 403 page; GET `/api/grid/status` (or any admin API) → 403.
    - `Recheck_demotes_after_level_drop`: admin signed in; fake Robust level set to 0; advance the factory's `FakeTimeProvider` 5 minutes → next request redirects to `/login` and `audit.log` has `session-ended`.
    - `Recheck_keeps_session_when_robust_is_down` (Review Focus 2): fake Robust throws; advance 5 minutes → still signed in; no `session-ended` entry.
- [ ] **Step 2: Run** `--filter "StaffSessionsTests|StaffAccessTests"` → FAIL.
- [ ] **Step 3: Implement.** `LauncherFactory` gets a `FakeTimeProvider Time` registered as `TimeProvider` if it isn't already. After sign-in, a supervisor lands on `/people` (Task 8; until then `/profile/sessions`), others on the return URL or `/`.
- [ ] **Step 4: Run** filter, then full project (every test using `TestAuth` must still pass) → PASS.
- [ ] **Step 5: Commit** `Security: staff sessions, roles and sign out everywhere`.

### Task 5: Two-factor core — TOTP, backup codes, store

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Security/Totp.cs`, `Core/Security/BackupCodes.cs`, `Core/Security/ITwoFactorStatus.cs`
- Create: `Launcher/DreamGrid.Launcher/Security/TwoFactorStore.cs`
- Test: `TotpTests.cs`, `BackupCodesTests.cs`, `TwoFactorStoreTests.cs`

**Interfaces:**
- Produces:
  - `public static class Totp`: `byte[] NewSecret()` (20 random bytes); `string Base32(byte[] secret)` (RFC 4648, no padding); `string Code(byte[] secret, long step)`; `long StepAt(DateTimeOffset t)` (`unix seconds / 30`); `bool Verify(byte[] secret, string code, DateTimeOffset now, long? lastUsedStep, out long matchedStep)` — accepts steps now−1..now+1, refuses any step ≤ `lastUsedStep`, ignores spaces in `code`, constant-time compare; `string OtpAuthUri(string issuer, string account, byte[] secret)` → `otpauth://totp/{issuer}:{account}?secret=…&issuer=…&digits=6&period=30` with URI-escaping.
  - `public static class BackupCodes`: `IReadOnlyList<string> New()` → 10 codes `xxxxx-xxxxx` from alphabet `abcdefghjkmnpqrstuvwxyz23456789`; `string Hash(string code)` (lower-case, dash and spaces removed, SHA-256 hex).
  - `public interface ITwoFactorStatus { TwoFactorState StateOf(Guid principal); }` with `public enum TwoFactorState { Off, On, Unknown }` (`Unknown` = store damaged).
  - `public sealed class TwoFactorStore(string file, IDataProtectionProvider dp, TimeProvider time) : ITwoFactorStatus` — reads the file on every call (no cache, so the CLI verb's changes are seen): `TwoFactorState StateOf(Guid)`, `(byte[] Secret, string Uri) BeginEnrol(Guid principal, string accountName, string issuer)` (pending secret kept protected until confirmed), `IReadOnlyList<string>? ConfirmEnrol(Guid principal, string code)` (returns the 10 backup codes, or null when the code is wrong), `bool VerifyCode(Guid principal, string codeOrBackup, out bool usedBackup)`, `void Disable(Guid principal)`, `IReadOnlyList<(Guid Principal, string Name)> Enrolled()`, `static void MoveAsideIfDamaged(string file)` (renames to `twofactor.json.damaged-<utc yyyyMMddHHmmss>`). Data-protection purpose `"DreamGrid.TwoFactor.v1"`. File `<data>/security/twofactor.json`.
- [ ] **Step 1: Failing tests:**
  - `TotpTests.Rfc6238_sha1_vectors`: secret ASCII `12345678901234567890`, times 59 → `287082`, 1111111109 → `081804`, 1111111111 → `050471`, 1234567890 → `005924`, 2000000000 → `279037` (6-digit truncations of the RFC's 8-digit vectors).
  - `TotpTests.Accepts_one_step_either_side_only`: codes for step−1 and step+1 verify; step−2 and step+2 don't.
  - `TotpTests.Verify_refuses_reused_step` (Review Focus 5).
  - `TotpTests.Base32_rfc4648_vectors`: "foobar" → `MZXW6YTBOI`.
  - `BackupCodesTests`: 10 distinct codes, format `^[a-hjkmnp-z2-9]{5}-[a-hjkmnp-z2-9]{5}$`; `Hash("ABCDE-fghij") == Hash("abcdefghij")`.
  - `TwoFactorStoreTests`: `Enrol_confirm_then_verify`; `Wrong_confirm_code_keeps_off`; `Backup_code_works_once`; `Secret_not_in_file_in_clear` (file text doesn't contain the Base32 secret); `Damaged_file_is_unknown_state` (Review Focus 3); `MoveAsideIfDamaged_then_off`.
- [ ] **Step 2: Run** `--filter "TotpTests|BackupCodesTests|TwoFactorStoreTests"` → FAIL.
- [ ] **Step 3: Implement.** No package for TOTP (HMACSHA1 from the BCL).
- [ ] **Step 4: Run** filter → PASS.
- [ ] **Step 5: Commit** `Security: two-factor codes and backup codes`.

### Task 6: Two-factor in sign-in, enrolment page, required switch

**Files:**
- Create: `Pages/Login/Code.cshtml(.cs)` (route `/login/code`), `Pages/Profile/TwoFactor.cshtml(.cs)` (route `/profile/two-factor`)
- Create: `Launcher/DreamGrid.Launcher/Security/TwoFactorGate.cs` (middleware)
- Modify: `Pages/Login.cshtml.cs`, `Program.cs`, `DreamGrid.Launcher.csproj` (QRCoder), `_Layout.cshtml` (link "Two-factor")
- Test: `TwoFactorSignInTests.cs`

**Interfaces:**
- Consumes: `TwoFactorStore`, `Totp`, `StaffSignIn` (with `mustEnrolTwoFactor`), `LoginThrottle`, `AuditLog`.
- Produces:
  - Sign-in rule: password OK and role ≠ None, then:
    - two-factor **On** → a pending cookie `dg-2fa` (data-protected: principal, role, expires 5 minutes) and redirect to `/login/code`; a correct code or backup code signs in (audit `sign-in`, plus `backup-code-used` when a backup code was used); a wrong one counts as a failure in the throttle for that name and IP.
    - **Unknown** → refuse: "Two-factor sign-in can't be checked on this computer. The grid owner can run `reset-two-factor` on the grid computer." Logged as a Warning.
    - **Off**, and `RequireTwoFactorForStaff` is on, and role is Admin or Supervisor → sign in with `mustEnrolTwoFactor: true`.
  - `TwoFactorGate`: a principal carrying `enrol` may reach only `/profile/two-factor`, `/logout`, static files; everything else redirects there (API calls get 403).
  - `/profile/two-factor` (StaffPolicy): when Off — QR code (QRCoder `SvgQRCode`, rendered inline as SVG markup, no `data:` URL needed) plus the Base32 secret as text, a code field, "Turn on"; on success shows the 10 backup codes once and re-issues the cookie without `enrol` (audit `two-factor-on`). When On — "Turn off" (asks for a current code; audit `two-factor-off`), "New backup codes" (asks for a current code). An admin or supervisor can't turn it off while the switch is on ("The grid owner requires two-factor sign-in.").
  - Issuer in the QR: the grid name; account: "First Last".
- [ ] **Step 1: Failing tests** (`TwoFactorSignInTests`, integration, fake time):
  - `Enrolled_admin_needs_code`: password → redirect `/login/code`; right code → signed in; `/dashboard` 200.
  - `Wrong_code_counts_toward_lock`: 5 wrong codes → locked message even with the right code.
  - `Backup_code_signs_in_once`.
  - `Required_switch_sends_admin_to_enrol`: switch on, admin without two-factor → after sign-in `/dashboard` redirects to `/profile/two-factor`; after enrolling, `/dashboard` 200.
  - `Required_switch_does_not_apply_to_owner`: switch on, owner without two-factor → `/dashboard` 200.
  - `Unknown_state_refuses_staff_sign_in`: damaged `twofactor.json` → sign-in refused with the reset message.
  - `Admin_cannot_turn_off_while_required`.
- [ ] **Step 2: Run** `--filter TwoFactorSignInTests` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filter, full project → PASS.
- [ ] **Step 5: Commit** `Security: two-factor sign-in for staff`.

### Task 7: Security page, admin network allow-list, reset-two-factor verb

**Files:**
- Create: `Pages/Security/Index.cshtml(.cs)` (route `/security`, OwnerPolicy)
- Create: `Launcher/DreamGrid.Launcher.Core/Security/NetworkList.cs`, `Launcher/DreamGrid.Launcher/Security/AdminNetworkFilter.cs` (middleware)
- Modify: `CommandLine.cs` (verb `reset-two-factor`), `Program.cs`, `_Layout.cshtml` (link "Security", owner only)
- Test: `NetworkListTests.cs`, `SecurityPageTests.cs`, `CommandLineTests.cs`

**Interfaces:**
- Produces:
  - `public static class NetworkList`: `string? Validate(IReadOnlyList<string> entries)` (each a CIDR or single address; returns the first bad entry's message); `bool Allows(IReadOnlyList<string>? entries, IPAddress caller)` — true when entries null/empty, when caller is loopback, or when any entry contains it (IPv4-mapped normalised).
  - `AdminNetworkFilter`: when `Security.AdminNetworks` is non-empty and the caller isn't allowed → 404 for every request except static files and paths `AdminNetworkFilter.Exempt(PathString)` returns true for. In 7a `Exempt` returns true only for the setup pages; plan 7b adds the guest and public paths. The port-8001 command API is **not** exempt.
  - `/security` page: "Require two-factor sign-in for admins and supervisors" switch; allow-list textarea (one entry per line); list of staff with two-factor on, each with "Reset" (audit `two-factor-reset`; also `EndAll` their sessions). Saving goes through `IGridController.UpdateSettings(..., writeConfig: false)` and audits `security-settings` with target describing the change (e.g. `require-two-factor=off`).
  - Save refusal (Review Focus 1): a list that wouldn't allow the request's own address → "This list would lock out the address you're using now (<ip>)."
  - Verb `reset-two-factor`: with no argument, turns two-factor off for `Setup.Owner` (by editing `<data>/security/twofactor.json` through a store built without the web host) and prints `Two-factor sign-in is off for <First Last>.`; with `--all-damaged`, runs `TwoFactorStore.MoveAsideIfDamaged` and prints what it did. Exit code 0 on success, 2 with a one-line reason otherwise (no owner yet, data folder missing). Works while the launcher runs.
- [ ] **Step 1: Failing tests:**
  - `NetworkListTests`: `Empty_allows_all`; `Loopback_always_allowed` (`127.0.0.1`, `::1` with list `10.0.0.0/8`); `Cidr_contains`; `Single_address_entry`; `Mapped_ipv6`; `Validate_reports_bad_entry` (`"10.0.0.0/33"`).
  - `SecurityPageTests`: `Supervisor_and_admin_get_403`; `Save_refuses_list_that_excludes_the_caller`; `Outside_caller_gets_404_on_login_and_api_and_command_api`; `Inside_caller_reaches_login`; `Reset_turns_off_and_ends_sessions`; `Switch_change_is_audited`.
  - `CommandLineTests`: `Reset_two_factor_turns_off_owner`; `Reset_two_factor_moves_damaged_file_aside`; `Reset_two_factor_without_owner_exits_2`.
- [ ] **Step 2: Run** `--filter "NetworkListTests|SecurityPageTests|CommandLineTests"` → FAIL.
- [ ] **Step 3: Implement.** `TwoFactorStore` needs data protection outside the host for the verb only to *disable* (removing an entry needs no decryption), so the verb constructs it with an ephemeral provider and only calls `Disable`/`MoveAsideIfDamaged`.
- [ ] **Step 4: Run** filter, full project → PASS.
- [ ] **Step 5: Commit** `Security: owner's security page, admin network list, reset-two-factor`.

### Task 8: Staff and People pages

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/IGridController.cs`, `GridController.cs` (new `SetUserLevelAsync`), `Launcher/DreamGrid.Launcher.Tests/FakeGridController.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/OpenSim/RobustClient.cs` (`RobustAccount` gains `DateTimeOffset? Created = null`, parsed from the account reply's `Created` unix time)
- Create: `Launcher/DreamGrid.Launcher.Core/Security/StaffChanges.cs`, `Core/Database/StaffDirectory.cs`, `Launcher/DreamGrid.Launcher/Security/SuspendedStore.cs`
- Create: `Pages/Staff/Index.cshtml(.cs)` (`/staff`, OwnerPolicy), `Pages/People/Index.cshtml(.cs)` (`/people`, StaffPolicy)
- Modify: `Program.cs` (policies for the two folders; both 404 when `!AdminRule.HasStaffPages`), `_Layout.cshtml` (links by role), `Pages/Login.cshtml.cs` (supervisor lands on `/people`)
- Test: `StaffChangesTests.cs`, `StaffPagesTests.cs`, `GridControllerTests.cs`, `RobustClientTests.cs`

**Interfaces:**
- Produces:
  - `Task<RobustAccount> IGridController.SetUserLevelAsync(string first, string last, int level, CancellationToken ct)` — Robust console `set user level {first} {last} {level}`, then waits for the account to show that level (as `CreateOwnerAsync` does); throws `InvalidOperationException` with the console transcript when it doesn't.
  - `public enum StaffChange { MakeSupervisor, MakeAdmin, MakeGuest, Suspend, Unsuspend }`
  - `public static class StaffChanges { public static string? Refusal(StaffRole actor, StaffRole target, bool targetIsOwner, StaffChange change); }` — null when allowed, else the message shown. Rules: owner may do anything to anyone but the owner; admin may Suspend/Unsuspend supervisors and guests; supervisor may Suspend/Unsuspend guests only; MakeSupervisor/MakeAdmin/MakeGuest are owner-only; nobody changes the owner ("The grid owner can't be changed here.").
  - `public sealed class StaffDirectory(Func<LauncherSettings> settings)`: `Task<IReadOnlyList<RobustAccount>> ListAtOrAboveAsync(int level, CancellationToken ct)` — `SELECT PrincipalID, FirstName, LastName, UserLevel, Email, Created FROM robust.UserAccounts WHERE UserLevel >= @level ORDER BY LastName, FirstName` as `robustuser` on the private MariaDB port.
  - `public sealed class SuspendedStore(string file)` (`<data>/security/suspended.json`): `void Remember(Guid principal, int previousLevel)`, `int? PreviousLevel(Guid principal)`, `void Forget(Guid principal)`.
  - Levels used: MakeSupervisor → `Security.SupervisorLevel`; MakeAdmin → `Security.AdminLevel`; MakeGuest → 0; Suspend → −1 (remember the previous level); Unsuspend → remembered level, or 0. Demote/suspend also `StaffSessions.EndAll` the target. Every change audited (`level-changed`, `suspended`, `unsuspended`; refused ones with `Result = "refused"`).
  - `/staff`: table of accounts at or above the supervisor level (role, level), buttons Make admin / Make supervisor / Make guest; "Add" by avatar name. When the database isn't running: "Start your world to see and change staff."
  - `/people`: find by "First Last" → name, level, role, email, created; Suspend / Unsuspend when `StaffChanges.Refusal` allows. Shows "Suspended" for level < 0. Ruling: the spec's "last login" needs Robust's GridUser service, which the launcher doesn't call yet; it arrives with the 2.5 user admin pages. Region rights arrive with plan 7b.
- [ ] **Step 1: Failing tests:**
  - `StaffChangesTests`: a table-driven test over every (actor, target, change) combination with the expected allow/refuse from the rules above; `Nobody_changes_owner`.
  - `StaffPagesTests`: `Owner_promotes_guest_to_supervisor` (fake controller records `SetUserLevelAsync(…, 100)`; audit `level-changed`); `Admin_gets_403_on_staff_page`; `Supervisor_suspends_guest_and_unsuspend_restores_level` (guest at 50 → −1 → 50); `Supervisor_cannot_suspend_admin_by_post` (Review Focus 4; audit `refused`); `Suspend_ends_target_sessions`; `Pages_404_on_region_server_and_connected`.
  - `GridControllerTests.SetUserLevel_runs_console_command_and_waits`.
  - `RobustClientTests.Account_created_time_parsed`.
- [ ] **Step 2: Run** `--filter "StaffChangesTests|StaffPagesTests|GridControllerTests|RobustClientTests"` → FAIL.
- [ ] **Step 3: Implement.** `StaffDirectory` is integration-tested only on Debian CI against the real MariaDB if an existing database test fixture exists; otherwise behind an interface `IStaffDirectory` faked in page tests.
- [ ] **Step 4: Run** filter, full project → PASS.
- [ ] **Step 5: Commit** `Staff: owner's staff page, people page with suspend`.

### Task 9: Port-8001 command API under the staff rules

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Api/LegacyCommandApi.cs`, `Program.cs` (register `ITwoFactorStatus`)
- Test: `LegacyCommandApiTests.cs`, `LegacyEndpointsTests.cs`

**Interfaces:**
- Consumes: `ITwoFactorStatus`, `SecuritySettings.RequireTwoFactorForStaff`, `AdminRule.RoleOf`, `AuditLog`, `LoginThrottle(name, ip)`.
- Produces: in `LevelAsync`, after a correct avatar password: level 0 (and an Information log naming the first name only) when `StateOf(principal)` is On or Unknown, or when the switch is on and the role is Admin or Supervisor. The MachineId path is unchanged.
- [ ] **Step 1: Failing tests:**
  - `LegacyCommandApiTests`: `Avatar_with_two_factor_refused_by_password`; `Required_switch_refuses_admin_password`; `Owner_without_two_factor_still_allowed_when_switch_on`; `MachineId_unaffected`; `Command_is_audited_with_actor_name` (and the MachineId case audits actor `MachineId` without its value).
  - `LegacyEndpointsTests`: confirm the existing non-loopback refusal test for `/dreamgrid/regionready` exists; if it doesn't, add `Regionready_refuses_non_loopback`. Add `Command_api_obeys_allow_list` (outside caller with a list set → 404).
- [ ] **Step 2: Run** `--filter "LegacyCommandApiTests|LegacyEndpointsTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filter, full project → PASS.
- [ ] **Step 5: Commit** `Security: port-8001 commands follow the two-factor and audit rules`.

### Task 10: Security headers and no inline script

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Security/SecurityHeaders.cs` (middleware, first in the pipeline)
- Create: `wwwroot/js/*.js` for each page that has inline script today: `Shared/_Layout.cshtml`, `Index.cshtml`, `Logs.cshtml`, `Regions/Add.cshtml`, `Regions/Job.cshtml`, `Regions/_RunsChoices.cshtml`, `Setup/Connect.cshtml`, `Setup/Network.cshtml`, `Setup/OtherGrid.cshtml`, `Setup/Region.cshtml` (data the scripts need moves to `data-*` attributes)
- Modify: those pages; the two `style="…"` attributes become classes in `wwwroot/css/site.css`
- Test: `SecurityHeadersTests.cs`, `InlineScriptScanTests.cs`

**Interfaces:**
- Produces: on every response:
  - `Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`
  - `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin`, `X-Frame-Options: DENY`.
- [ ] **Step 1: Failing tests:**
  - `SecurityHeadersTests`: `Headers_on_page`, `Headers_on_api`, `Headers_on_static_file`, `Headers_on_legacy_text_reply` — each asserts the four headers with the exact values above.
  - `InlineScriptScanTests.No_inline_script_or_handlers`: scans every `.cshtml` under `Launcher/DreamGrid.Launcher/Pages`; fails on a `<script` tag without `src=`, on any ` on[a-z]+="` attribute, and on `style="`; the failure message lists file and line.
- [ ] **Step 2: Run** `--filter "SecurityHeadersTests|InlineScriptScanTests"` → FAIL.
- [ ] **Step 3: Implement**, page by page; behaviour of each page must not change (the existing page tests are the check). The theme script in `_Layout` that runs before paint becomes a tiny `wwwroot/js/theme.js` loaded in `<head>` without `defer`.
- [ ] **Step 4: Run** filter, full project → PASS. Then start the launcher locally (`dotnet run --project Launcher/DreamGrid.Launcher -- --web-port 18001` with a scratch `DREAMGRID_DATA`) and open the dashboard, Logs, Add region and the setup pages in a browser with the console open: no CSP violation messages.
- [ ] **Step 5: Commit** `Security: browser security headers, scripts moved to files`.

### Task 11: Owner docs and worklist

**Files:**
- Modify: `docs/SECURITY-NOTES.md` (sections: Staff levels; Two-factor and `reset-two-factor`; Admin network list; Audit log; Port-8001 command API and two-factor)
- Modify: the owner manual page that covers signing in (find it with `grep -ril "sign in" docs/manual` or the nearest equivalent), adding Staff, People, Security, Sessions
- Modify: `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`: tick the 1.2/1.3 items 7a delivers (staff levels as settings, more than one admin, throttling, headers, audit log, TOTP, sessions page, port-8001 check), and add under a "Later" heading: "Advanced install: let the owner choose the database password (owner, 2026-10-30)" and "HTTPS for the website (owner: later, 2026-10-30)".
- [ ] **Step 1:** Write the docs; every command and page name matches the code (`grep` each one).
- [ ] **Step 2: Run** the full test project → PASS.
- [ ] **Step 3: Commit** `Docs: staff, two-factor and security notes`.
