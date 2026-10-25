# Plan 2b: Website Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The admin website a newcomer uses, served by the launcher on port 8001. A one-time setup link leads through a four-step checklist that ends with them in-world in Firestorm. After that they sign in with their avatar and get a dashboard (status, controls, who's online, connect box, logs) and an Advanced Settings menu that features can extend. It works on a phone screen and needs no internet access.

**Architecture:**
- **Rendering:** ASP.NET Core Razor Pages inside the existing `DreamGrid.Launcher` host (plan 2a), rendered on the server, with a little plain JavaScript (`fetch`) for live status, setup progress and log tailing. There is no JS framework, build step or CDN; one hand-written CSS file is served from `wwwroot`.
- **Sign-in:** cookie authentication. The credentials are checked against Robust (`IRobustClient.GetAccountAsync` + `AuthenticateAsync`), and the account's `UserLevel` ≥ 200 becomes the `admin` role.
- **Setup link:** this is the only way in before an owner exists. Its token is spent when the owner is created.
- **Features:** each `IFeatureModule` contributes a dashboard switch and an Advanced Settings page through two small UI interfaces.

**Tech Stack:**
- .NET 10 ASP.NET Core Razor Pages and Cookie authentication (built in).
- `MySqlConnector` 2.6.2 (already referenced by Core) for the who's-online query.
- Tests: xUnit v3 4.0.2, `Microsoft.AspNetCore.Mvc.Testing` 10.0.13, `AngleSharp` 1.8.6 (MIT) for HTML assertions.

**Spec:** `docs/superpowers/specs/2026-10-24-crossplatform-launcher-v1-design.md`, sections §1 (newcomer), §2 goals 2–4 and 8, §4.3, §4.5, §4.8 (old Wifi stays off), §6.2, §6.4, §6.5, §6.9 (UI half), §7 (no CDN, phone-sized). The chosen UI direction is **B, guided first steps, then a dashboard, with Advanced Settings always available** (brainstorming mockup `home-screen.html`, option B). This is plan 2b of 4; it depends on plan 2a being merged.

**Facts used by this plan:**
- **From plan 2a:**
  - `IGridController`:
    - lifecycle: `StartAsync`, `StopAllAsync`, `CreateOwnerAsync`, `CreateRegionAsync`,
    - state: `GetStatus` → `GridStatus` (with `LoginUri`, `LoginsEnabled`).
  - `IRobustClient`: `GetAccountAsync`, `AuthenticateAsync`.
  - Settings: `SettingsStore`, `LauncherSettings` (`SetupToken`, `Setup`, ports, `Hostname`, `Modules`, `Overrides`).
  - Paths: `LauncherPaths.Logs` holds `robust.log`, `region-<instance>.log` and `mariadb.err`. The launcher's own log is added here as `launcher.log`.
  - Modules: `IFeatureModule` and `LogLevelModule`.
  - The `GET /welcome` placeholder (replaced here).
  - `PortConflictException` is logged at Critical while the host stays up.
- **Robust tables, from `OpenSim/Data/MySQL/Resources/*.migrations`:**
  - `Presence(UserID varchar(255), RegionID char(36), SessionID char(36), LastSeen timestamp)`.
  - `UserAccounts(PrincipalID char(36), FirstName varchar(64), LastName varchar(64), UserLevel int, …)`.
- **Who's online:** `SELECT u.FirstName, u.LastName, p.RegionID FROM Presence p JOIN UserAccounts u ON u.PrincipalID = p.UserID WHERE p.RegionID <> '00000000-0000-0000-0000-000000000000'`.

## Global Constraints

- **No external resources:** no CDN, web font or analytics URL in any page. Every asset is served from `wwwroot` (spec §7: the machine may be offline).
- **Phone width:** pages work at 375 px wide with no horizontal scrolling, and use plain HTML forms that work without JavaScript. JavaScript only adds live updates.
- **Wording:** plain language for a newcomer. "Your world", "your region" and "Firestorm" are fine. "Robust", "INI", "UUID" and "console" appear only inside Advanced Settings and the Logs page.
- **Access before setup:** while `Setup.OwnerCreated` is false, every page except `/setup` (with a valid token cookie), `/welcome` and static files redirects to a "Setup is waiting" page. That page tells the person to use the link the installer showed. The token never appears in a log line after first use.
- **Access after setup:** the setup routes return 404; all pages except `/login`, `/welcome` and static files need the `admin` role.
- **Forms:** every form posts with an antiforgery token (Razor Pages default; do not disable it).
- **Passwords:** never logged, never stored by the launcher (Robust stores the hash), and must not contain spaces (2a constraint: console arguments).
- **Code location:** new code goes under `Launcher/DreamGrid.Launcher/` (pages, `wwwroot`, endpoints), `Launcher/DreamGrid.Launcher.Core/` (services) and `Launcher/DreamGrid.Launcher.Tests/`.
- **Commits** end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Setup interrupted halfway** (browser closed after step 2, launcher restarted). Opening the setup link again resumes at step 3 with the world name and owner kept; nothing is created twice (Task 3 test).
2. **Setup link opened on a second device after the owner exists.** It shows "Setup is already finished — sign in", not the checklist (Task 2 test).
3. **Wrong password or a level-0 avatar at sign-in.** Both give the same generic "Name or password is wrong, or this avatar is not an administrator" message, with no hint which. Five failures for one name in 10 minutes lock that name for 10 minutes, the same rule as the old MCP manual (Task 4 test).
4. **Grid not running while the dashboard is open** (MariaDB down, port conflict). The dashboard still renders, shows the problem in plain words with the log excerpt, and offers Start (Task 5 test).
5. **Advanced Settings saved with a bad value** (port 0, port 70000, two ports equal, hostname with spaces). It is rejected with a field-level message and `settings.json` is untouched (Task 7 test).

---

### Task 1: Razor Pages shell, layout, static assets, and the launcher log

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher/Pages/_ViewImports.cshtml`, `Pages/_ViewStart.cshtml`, `Pages/Shared/_Layout.cshtml`
  - `Pages/Waiting.cshtml(.cs)`, the "Setup is waiting" page
  - `wwwroot/css/site.css`, `wwwroot/js/live.js`
  - `Launcher/DreamGrid.Launcher.Tests/WebTestFactory.cs`, `Launcher/DreamGrid.Launcher.Tests/LayoutTests.cs`
- Modify:
  - `Launcher/DreamGrid.Launcher/Program.cs`: `AddRazorPages`, `UseStaticFiles`, `MapRazorPages`, a file logger writing `paths.Logs/launcher.log`
  - `Launcher/DreamGrid.Launcher.Tests/DreamGrid.Launcher.Tests.csproj`: add `AngleSharp` 1.8.6

**Interfaces:**
- Produces:
  - **The layout:**
    - A header with the grid name and nav links: Home, Advanced, Logs, Sign out (the links show only when signed in).
    - A `<main>`, and a footer showing the launcher version (`AssemblyInformationalVersion`).
    - `<meta name="viewport" content="width=device-width, initial-scale=1">`.
    - Light and dark themes through `prefers-color-scheme` in `site.css`.
  - **`live.js`:** `poll(url, intervalMs, onJson)`, used by later tasks.
  - **`WebTestFactory : WebApplicationFactory<Program>`:**
    - It replaces `IGridController` and `IRobustClient` with in-memory fakes (`FakeGrid`, `FakeRobust`) and points `--data` at a temp dir.
    - `FakeRobust` holds accounts `(first,last,password,level)`.
    - `FakeGrid.Status` can be set by tests.
  - **The launcher log:** `paths.Logs/launcher.log`, a rolling file through `Microsoft.Extensions.Logging` with a minimal hand-written `ILoggerProvider` (no new packages): 10 MB, 5 files.

- [ ] **Step 1: Write failing tests,** using `WebTestFactory` and AngleSharp.
  - `Layout_has_viewport_meta_and_no_external_urls`: GET `/welcome`. The page has the viewport meta, and no `src`/`href` value starts with `http`, except links whose host is this launcher.
  - `Static_css_is_served`: GET `/css/site.css` returns 200 with `text/css`.
  - `Launcher_log_file_is_written`: after a request, `launcher.log` exists in the temp `Logs` dir.
- [ ] **Step 2: Run and confirm they fail.** Run `dotnet test --project Launcher/DreamGrid.Launcher.Tests -c Release --filter-class *LayoutTests`. Expected: FAIL (404 / no page).
- [ ] **Step 3: Implement.** `/welcome` becomes a Razor page: "Welcome to {GridName}" plus one line, "Log in with your avatar name and password." This is what Firestorm shows on its login screen.
- [ ] **Step 4: Run and confirm they pass.**
- [ ] **Step 5: Commit** `git commit -m "Website: Razor Pages shell, layout and launcher log"`.

---

### Task 2: Access rules (setup token, before/after setup)

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher/Security/SetupGate.cs` (middleware)
  - `Launcher/DreamGrid.Launcher/Security/SetupTokenCookie.cs`
  - `Launcher/DreamGrid.Launcher/Pages/SetupDone.cshtml(.cs)`
  - `Launcher/DreamGrid.Launcher.Tests/AccessTests.cs`
- Modify: `Program.cs` (cookie authentication with scheme `"dg"`, an `admin` policy requiring role `admin`, and `SetupGate` placed before the pages)

**Interfaces:**
- Produces:
  - **`SetupGate`, in this order:**
    1. Static files, `/welcome`, port-8001 legacy routes (`/` with `alt`, `type` or `command`; `/dreamgrid/*`) and `/api/status` always pass.
    2. **While the owner does not exist:**
       - `GET /setup?token=X` with `X == SetupToken` (constant-time compare) sets the HttpOnly, SameSite=Strict cookie `dg-setup` = `X`, then redirects to `/setup`, so the token leaves the URL.
       - Any `/setup*` request with a valid `dg-setup` cookie passes.
       - Everything else redirects to `/waiting`.
    3. **Once the owner exists:** `/setup*` → `/setup-done` ("Setup is already finished — sign in"). Other pages follow normal authorization: `[Authorize(Policy="admin")]` on all pages except `/login`, `/welcome`, `/waiting` and `/setup-done`.
  - **Spending the token:** after a successful `CreateOwnerAsync`, set `SetupToken = null` and save. The Task 3 step 2 handler does this; this task's test simulates it.

- [ ] **Step 1: Write failing tests**
  - `Before_setup_root_redirects_to_waiting`.
  - `Setup_with_wrong_token_redirects_to_waiting`.
  - `Setup_with_right_token_sets_cookie_and_strips_token_from_url`: the reply is a 302 to `/setup` (no `token=` in Location) and sets cookie `dg-setup`.
  - `After_owner_exists_setup_link_shows_setup_done` (Review Focus 2).
  - `After_owner_exists_root_requires_sign_in`: anonymous `GET /` → 302 to `/login`.
  - `Legacy_8001_routes_still_open_before_setup`: `GET /?command=RegionStatus&RegionName=x` is not redirected.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and confirm they pass.**
- [ ] **Step 5: Commit** `git commit -m "Website: setup-token gate and access rules"`.

---

### Task 3: Setup checklist (spec §6.2)

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher/Pages/Setup/Index.cshtml(.cs)`, which shows the checklist and the current step
  - `Pages/Setup/World.cshtml(.cs)` (step 1), `Pages/Setup/Avatar.cshtml(.cs)` (step 2), `Pages/Setup/Region.cshtml(.cs)` (step 3), `Pages/Setup/Connect.cshtml(.cs)` (step 4)
  - `Launcher/DreamGrid.Launcher.Core/Setup/SetupProgress.cs`
  - `Launcher/DreamGrid.Launcher/Endpoints/SetupApi.cs` (`GET /api/setup/progress`)
  - `Launcher/DreamGrid.Launcher.Tests/SetupFlowTests.cs`

**Interfaces:**
- Produces:
  - **`public sealed class SetupProgress`** (a singleton) with `string Stage`, `int Percent`, `string? Error`, `string? LogExcerpt` and `void Report(string stage, int percent)`. The plain-language stages are:
    - "Preparing the database",
    - "Starting the grid service",
    - "Creating your region",
    - "Loading the region content",
    - "Waiting for the region to accept logins",
    - "Ready".
  - **The checklist** marks each step done from `Setup.*`. Going to `/setup` lands on the first unfinished step.
  - **Step 1, World:**
    - Field `GridName`: required, 3–40 characters, letters/digits/spaces/`-'`.
    - Saves `GridName` and `Setup.WorldNamed=true`. Calls `grid.StartAsync` in the background if the grid isn't running, so MariaDB and Robust warm up while the person types.
  - **Step 2, Avatar:**
    - Fields: `FirstName`, `LastName` (each required, 2–31 characters, letters/digits only, as the viewer requires), `Password` (≥ 8 characters, no spaces), `Confirm`, optional `Email`.
    - Calls `grid.CreateOwnerAsync`, then spends the setup token (Task 2) and signs the person in as `admin` (sets the auth cookie), so step 3 onward runs signed in.
    - If the owner already exists (resume), the step is skipped.
  - **Step 3, Region:**
    - Fields: `RegionName` (required, 2–32 characters, default "Welcome") and `Content` (radio).
      - "Starter island (trees, greenery and an info kiosk)" is shown only when `StarterContent.StarterOar` is not null.
      - "Empty island" is always available.
    - Posting starts `grid.CreateRegionAsync` as a background task that reports to `SetupProgress`, then redirects to `/setup/region?working=1`.
    - That page shows a progress bar polled from `/api/setup/progress` every 2 s via `live.js`. A no-JavaScript fallback is `<meta http-equiv="refresh" content="3">`.
    - On error it shows the plain message plus a "Show details" `<details>` holding `LogExcerpt`: the last 40 lines of `region-<instance>.log` or `robust.log`.
  - **Step 4, Connect:**
    - Shows `GridStatus.LoginUri` with a copy button (`navigator.clipboard`; without JavaScript the text is selectable).
    - Numbered steps:
      1. "Install Firestorm", a plain link to `https://www.firestormviewer.org/` (shown as text, not loaded by the page).
      2. "Viewer → Preferences → OpenSim → Grid Manager → paste the address → Apply".
      3. "Pick {GridName} on the login screen".
      4. "Log in as {First} {Last}".
    - The "I'm in!" button sets `Setup.Finished=true` and redirects to `/`.

- [ ] **Step 1: Write failing tests,** with `WebTestFactory`, `FakeGrid` and `FakeRobust`.
  - `Checklist_lands_on_first_unfinished_step`.
  - `World_step_validates_name_and_saves`: an empty name re-renders with a field error; "Avalon" saves `GridName` and `WorldNamed`.
  - `Avatar_step_rejects_password_with_space_and_mismatch`.
  - `Avatar_step_creates_owner_spends_token_and_signs_in`: after the POST, `SetupToken` is null, `FakeGrid.CreatedOwner` is set, and the auth cookie is present.
  - `Resume_after_restart_skips_completed_steps` (Review Focus 1): settings with `WorldNamed` and `OwnerCreated` true, `RegionCreated` false; a new factory instance; with the auth cookie, `GET /setup` → step 3; `FakeGrid.CreateOwnerCalls == 0`.
  - `Starter_option_hidden_without_starter_oar_shown_with_it`.
  - `Region_step_reports_progress_and_error_excerpt`: `FakeGrid.CreateRegion` throws, then `/api/setup/progress` JSON has a non-empty `error` and `logExcerpt`.
  - `Connect_step_shows_login_uri_and_finishes_setup`: the page contains `http://{host}:8002`; POST sets `Finished`; redirect to `/`.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and confirm they pass.**
- [ ] **Step 5: Commit** `git commit -m "Website: four-step setup checklist"`.

---

### Task 4: Sign-in and sign-out

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher/Pages/Login.cshtml(.cs)`, `Pages/Logout.cshtml(.cs)`
  - `Launcher/DreamGrid.Launcher.Core/Security/LoginThrottle.cs`
  - `Launcher/DreamGrid.Launcher.Tests/LoginTests.cs`, `LoginThrottleTests.cs`

**Interfaces:**
- Produces:
  - **`public sealed class LoginThrottle(TimeProvider time)`** with `bool IsLocked(string name)` and `void RecordFailure(string name)`. Five failures within 10 minutes lock the name for 10 minutes. `RecordSuccess` clears it. Names are compared case-insensitively.
  - **`/login`:**
    - Fields: "Avatar name" (`First Last`, split on the first space) and `Password`.
    - It succeeds iff the name is not locked, `GetAccountAsync` finds the account, `AuthenticateAsync` is true, and `UserLevel >= 200`. Then it sets cookie claims `name`, `principal` and role `admin`, with an 8-hour sliding expiry.
    - Every failure shows the same message: **"Name or password is wrong, or this avatar is not an administrator."**
    - A locked name shows: **"Too many attempts. Try again in a few minutes."**
  - **`/logout`** (POST) signs out and redirects to `/login`.

- [ ] **Step 1: Write failing tests**
  - `Valid_admin_signs_in_and_reaches_dashboard`.
  - `Level_0_avatar_gets_generic_message` and `Wrong_password_gets_identical_message`: assert both pages render the same `.error` text (Review Focus 3).
  - `Fifth_failure_locks_name_for_ten_minutes`: `LoginThrottle` with `FakeTimeProvider`. After 5 failures it is locked; after advancing 10 min it is unlocked.
  - `Logout_requires_post_and_clears_cookie`.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and confirm they pass.**
- [ ] **Step 5: Commit** `git commit -m "Website: sign-in with avatar name and password"`.

---

### Task 5: Dashboard (spec §6.4)

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher/Pages/Index.cshtml(.cs)`
  - `Launcher/DreamGrid.Launcher.Core/Grid/OnlineUsers.cs`
  - `Launcher/DreamGrid.Launcher/Endpoints/StatusApi.cs` (`GET /api/status`, `POST /api/grid/{action}`)
  - `Launcher/DreamGrid.Launcher.Tests/DashboardTests.cs`
  - `Launcher/DreamGrid.Launcher.IntegrationTests/OnlineUsersTests.cs`

**Interfaces:**
- Consumes `IGridController.GetStatus`, `StartAsync`, `StopAllAsync` and `RestartComponentAsync` (plan 2a Task 10).
- Produces:
  - **`public sealed class OnlineUsers(LauncherSettings s)`** with `Task<IReadOnlyList<OnlineAvatar>> ListAsync(CancellationToken)` and `public sealed record OnlineAvatar(string Name, Guid RegionId)`. It runs the who's-online query from Facts against `robust` as `robustuser`. If the database is down it returns an empty list and sets `bool Available=false` instead of throwing.
  - **`GET /api/status`** returns JSON `{ grid: GridStatus, online: [..], onlineAvailable: bool, problem: string|null }`.
    - `problem` is plain language: a port conflict message from `PortConflictException`, or `"The database isn't running."`, `"The grid service isn't running."`, `"Your region isn't running."`, or null when all is well.
    - It needs no sign-in, but returns only names and states (no paths or secrets), because the 8001 legacy contract already exposes region status unauthenticated.
  - **`POST /api/grid/{start|stop|restart|restart-database|restart-robust|restart-region}`** requires `admin` and an antiforgery header.
  - **The page:**
    - A status card: one line per component (state badge, uptime, memory) and an overall "Your world is running" or `problem`.
    - Controls: Start all, Stop all, Restart, and a per-component restart inside a `<details>` "More".
    - A "Who's online" list (or "Nobody is in-world right now").
    - A connect box: `LoginUri` with a copy button.
    - A "Coming soon" card listing future features by plain name: Backups, Visitors from the internet, Hypergrid, More regions, SmartStart, Voice, Birds and Tides (spec §6.4: visibly more to come).
    - A features area rendering every `IDashboardSwitch` (Task 7).
    - Live updates via `poll("/api/status", 5000, …)`.

- [ ] **Step 1: Write failing tests**
  - `Dashboard_shows_running_state_and_login_uri`.
  - `Dashboard_renders_when_grid_is_down_and_shows_problem_and_start` (Review Focus 4): `FakeGrid.Status` with Database Failed and `LastError` set; the page returns 200, contains "The database isn't running." and a Start button.
  - `Status_api_exposes_no_paths_or_secrets`: the JSON contains no value with `"Password"`, `paths.Data` or `settings.MachineId`.
  - `Grid_actions_require_admin_and_antiforgery`: anonymous POST → 401/302; admin POST without the header → 400.
  - Integration `OnlineUsers_lists_presence_rows` (needs MariaDB, as in 2a): create the `robust` tables via the Robust migrations by starting Robust through `GridController` (reuse the 2a fixture), insert one `Presence` row for the owner with a non-zero RegionID; `ListAsync` returns the owner's name.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and confirm they pass.**
- [ ] **Step 5: Commit** `git commit -m "Website: dashboard with status, controls, who's online and connect box"`.

---

### Task 6: Logs page

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher/Pages/Logs.cshtml(.cs)`
  - `Launcher/DreamGrid.Launcher.Core/Logs/LogReader.cs`
  - `Launcher/DreamGrid.Launcher/Endpoints/LogsApi.cs` (`GET /api/logs`)
  - `Launcher/DreamGrid.Launcher.Tests/LogReaderTests.cs`

**Interfaces:**
- Produces:
  - **`public sealed class LogReader(LauncherPaths paths)`** with `IReadOnlyList<string> Sources()` and `LogTail Tail(string source, int lines = 200, string? minLevel = null)`.
    - The sources are `launcher`, `robust`, `region-<instance>` and `database` (`mariadb.err`).
    - `Tail` reads backwards from the end of the file without loading it whole (files up to 10 MB), with the file opened `FileShare.ReadWrite`, since OpenSim keeps writing.
    - `minLevel` is one of `DEBUG|INFO|WARN|ERROR`; it filters on log4net's ` - [LEVEL]` or `[LEVEL]` tokens, and lines without a level are kept.
  - **`public sealed record LogTail(string Source, IReadOnlyList<string> Lines, bool Truncated)`.**
  - **`GET /api/logs?source=&lines=&level=`** requires `admin`; an unknown source → 404. The `source` value is matched against `Sources()`, never used as a path (no path traversal).
  - **The page:** a source picker, a level picker, a `<pre>` of the lines, and auto-refresh every 5 s when a checkbox is on.

- [ ] **Step 1: Write failing tests**
  - `Tail_returns_last_n_lines_of_large_file`: write a 5 MB file, `Tail(lines:50)` returns the last 50, in order.
  - `Tail_reads_while_another_writer_holds_the_file`: keep a `FileStream` open for writing with `FileShare.Read`, then call `Tail`; it does not throw.
  - `Level_filter_keeps_warn_and_error_only`.
  - `Unknown_source_is_404_and_path_traversal_is_impossible`: `source=../settings` → 404.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and confirm they pass.**
- [ ] **Step 5: Commit** `git commit -m "Website: log viewer"`.

---

### Task 7: Advanced Settings and the module UI seam (spec §6.5, §6.9)

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher/Pages/Advanced/Index.cshtml(.cs)` (the menu)
  - `Pages/Advanced/Network.cshtml(.cs)`, `Pages/Advanced/Database.cshtml(.cs)`, `Pages/Advanced/Module.cshtml(.cs)` (generic module page, route `/advanced/module/{id}`)
  - `Launcher/DreamGrid.Launcher.Core/Modules/ModuleUi.cs`
  - `Launcher/DreamGrid.Launcher.Core/Settings/SettingsValidator.cs`
  - `Launcher/DreamGrid.Launcher.Tests/AdvancedSettingsTests.cs`, `SettingsValidatorTests.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Modules/LogLevelModule.cs` (implement the UI interfaces)

**Interfaces:**
- Produces:
  - **`public interface IDashboardSwitch`**, with:
    - `string ModuleId`, `string Label`, `string Description`,
    - `bool IsOn(LauncherSettings s)` and `LauncherSettings Toggle(LauncherSettings s, bool on)`.
  - **`public interface ISettingsPage`**, with:
    - `string ModuleId`, `string Title`,
    - `IReadOnlyList<SettingField> Fields(LauncherSettings s)`,
    - `ValidationResult Validate(IReadOnlyDictionary<string,string> values)`,
    - `LauncherSettings Apply(LauncherSettings s, IReadOnlyDictionary<string,string> values)`.
  - **`public sealed record SettingField(string Key, string Label, string Value, string Kind /* "text"|"number"|"select"|"secret" */, IReadOnlyList<string>? Options = null, string? Help = null)`**.
  - **`public sealed record ValidationResult(IReadOnlyDictionary<string,string> Errors)`** with `bool Ok => Errors.Count == 0`.
  - **`LogLevelModule` implements `ISettingsPage`.** It has one select field, `level`, with options DEBUG/INFO/WARN/ERROR; it has no dashboard switch.
  - **`public static class SettingsValidator`** with `ValidationResult Network(string? hostname, int web, int robustPublic, int robustPrivate, int region, int mariaDb, int robustConsole, int regionConsole)`. Every port must be 1024–65535 and all of them distinct; the hostname is empty (auto) or a DNS name or IPv4 with no spaces.
  - **Network page:** the hostname (placeholder shows the detected LAN IP) and the eight ports.
  - **Database page:** host `127.0.0.1` (read-only in version 1), the port, the `robustuser`/`opensimuser` names, passwords shown as `••••` with a "Show" button behind a confirm, and a "Test connection" button (POST → `MySqlConnection` open as `robustuser` → "Connected" or the error message).
  - **Saving:** validate; if OK, `store.Save`, then `IGridController.RegenerateConfig()` (plan 2a Task 10), then show "Saved. Restart your world to use the new settings." with a Restart button. If not OK, re-render with field errors and **do not save** (Review Focus 5).
  - **Advanced menu:** Network, Database, then one entry per `ISettingsPage` module, then Logs.

- [ ] **Step 1: Write failing tests**
  - `Validator_rejects_out_of_range_duplicate_ports_and_bad_hostname`: port 0, 70000, web == region, and `"my host"` each give an error on the right field.
  - `Network_save_with_bad_value_leaves_settings_file_untouched`: capture the `settings.json` bytes, POST a bad form, compare bytes.
  - `Network_save_with_good_values_saves_and_regenerates`: `FakeGrid.RegenerateCalls == 1`, and the page shows "Restart your world".
  - `Database_page_masks_passwords_by_default`: the HTML does not contain `Db.RobustPassword`.
  - `Module_page_renders_LogLevel_and_saves_choice`: POST `level=DEBUG` gives `settings.Modules["loglevel"]["level"] == "DEBUG"`.
  - `Advanced_menu_lists_module_pages`.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and confirm they pass.**
- [ ] **Step 5: Commit** `git commit -m "Website: Advanced Settings and module settings pages"`.

---

### Task 8: End-to-end through the browser, and CI

**Files:**
- Create: `Launcher/DreamGrid.Launcher.IntegrationTests/WebsiteEndToEndTests.cs`
- Modify: `.github/workflows/dreamgrid-ci.yml` (the plan 2a `launcher` job also runs the new tests; no new job)

**Interfaces:**
- Consumes the out-of-process launcher harness from plan 2a Task 12.

- [ ] **Step 1: Write the HTTP-level end-to-end test.** `Newcomer_path_from_setup_link_to_dashboard` uses a real launcher, MariaDB and OpenSim with an `HttpClient` that keeps cookies; antiforgery tokens are parsed from each form with AngleSharp.
  1. Read the setup link from `launcher.log` (`Setup link: …`).
  2. GET it, then follow the redirect to `/setup`.
  3. Step 1: post "Avalon".
  4. Step 2: post owner `Test Owner` / `pass-word1`.
  5. Step 3: post region "Welcome", content "island". Poll `/api/setup/progress` until `Stage=="Ready"` (timeout 300 s).
  6. Step 4: the page contains the login URI; post "I'm in!".
  7. `GET /` → the dashboard shows "Your world is running".
  8. `POST /logout`, then `GET /` → `/login`; sign in again → the dashboard.
  9. Opening the original setup link → `/setup-done`.
- [ ] **Step 2: Run on Windows and in `debian:12`/`debian:13`, and confirm it passes.**
- [ ] **Step 3: Visual check in a real browser.** Open the running launcher's pages in the in-app browser at desktop width and at 375 px (mobile preset). Take a screenshot of each of the setup steps, the dashboard, Advanced and Logs. Confirm there is no horizontal scroll, both themes are readable, and the copy button works. Record the result in the commit message. This is a manual check; fix layout bugs before committing.
- [ ] **Step 4: Push and confirm CI is green** (with the user's OK, as in 2a).
- [ ] **Step 5: Commit** `git commit -m "Website: newcomer end-to-end test and visual check"`.

---

## Self-review notes

**Spec coverage:**
- §6.2 checklist (four steps, resumable, progress view, plain errors with details) → Task 3.
- §4.5 one-time setup link, Firestorm-credential sign-in, level ≥ 200 → Tasks 2 and 4.
- §6.4 dashboard (status, controls, who's online, connect box, logs, "more to come") → Tasks 5 and 6.
- §6.5 Advanced Settings (network, database, test connection, regenerate + restart prompt) → Task 7.
- §6.9 the UI half of modules (dashboard switch, settings page) → Task 7, proven with `LogLevelModule`.
- §7 phone-sized, no CDN → Global Constraints, Task 1 test, Task 8 visual check.
- §4.8 old Wifi off → unchanged from 2a; nothing here enables it.

**Not in this plan:** user sign-up, password recovery, more than one admin, internet-facing features. Installers and release packaging are plan 3.
