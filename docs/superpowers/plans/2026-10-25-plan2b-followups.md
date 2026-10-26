# Plan 2b — decisions made during execution and open follow-ups

Plan 2b (admin website) was executed task by task with a review after each task and a final whole-branch review (branch `feature/website`, stacked on `feature/launcher-core`, 9f5c845..final fix wave). This file keeps what the execution ledger recorded that later plans need.

## Decisions (rulings) taken during execution

| Decision | Cost if wrong |
|---|---|
| Plan 2b lives on `feature/website`, stacked on `feature/launcher-core` (2a is not merged). | One rebase later. |
| Tests reuse `LauncherFactory` / `FakeGridController` / `FakeRobustAccounts` instead of new fakes. | Test names differ from the plan text. |
| After the owner exists, `/setup*` and `/api/setup/*` pass only for a signed-in admin while `Setup.Finished` is false; once finished they go to `/setup-done`. Final review: an anonymous request before finish is sent to sign in (`/login?ReturnUrl=/setup`; the API answers 401), and the admin's `/` leads to `/setup`. | A signed-in admin can reopen setup steps until it is finished. |
| Every settings change goes through `IGridController.UpdateSettings` (token spending, setup flags, GridName, Advanced Settings, module pages); Advanced saves use `writeConfig: true`. | Lost updates. |
| The region name field validates exactly like `CreateRegionAsync`. | None. |
| Execution order 1, 2, 6, 3, 4, 5, 7, 8 (T3 and T5 need `LogReader`). | None. |
| 2a "Plan 2b" follow-ups not named in plan tasks stay open (except token spending and region-name validation). | Follow-ups remain (below). |
| `OnlineUsers` takes `Func<LauncherSettings>`. | None. |
| Unauthenticated `/api/*` requests needing admin get 401, never a `/login` redirect. | One cookie-event tweak. |
| Bare `GET /` is the website (gated); the old "Test Completed" probe is dropped. | Restore a text probe if an external tool needs it. |
| `LegacyRootPolicy` is symmetric: bare GET/HEAD is the website's, a POST or a legacy key (`alt`, `type`, `command`) the legacy handler's. | None. |
| OpenSim's log4net pattern carries the level (`%date{HH:mm:ss} - [%level] %message%newline`) and the log reader anchors its level filter to that prefix. | Changes the robust/region log format. |
| A missing log file gives an empty tail; the region source exists only when a region does. | None. |
| `IGridController.CreateRegionAsync` gains an optional `IProgress<string>`; stages are reported at the real steps. | One interface parameter. |
| The region setup job outlives the request (`ApplicationStopping`), runs one at a time and can be retried. | None. |
| Sign-in while the account service is down shows a distinct "not running yet" message and is not counted as a failure. | Slightly different message. |
| `GridStatus` gains `StartFailure` (any failed start; `ConflictPort` for a port conflict) and `Activity`; anonymous `/api/status` gets only a plain mapping, the raw message is on the admin page's details box. | Anonymous pollers see less detail. |
| `/api/status` lists who is online only for signed-in admins. | Anonymous pollers lose the list. |
| Dashboard controls are plain POST forms; long grid actions run in the background. | None. |
| Failed-component log lines appear on the page only, never in `/api/status`. | None. |
| A port conflict is shown in plain words built from the port number, never the exception text. | The log keeps the technical text. |
| The dashboard features area comes from `IDashboardSwitch` (Task 7). | None. |
| Database "Show passwords" is a POST behind a JS confirm; passwords are never in the default HTML. | None. |
| Module settings Apply copies the Modules dictionaries (no in-place mutation of cached settings). | None. |
| Task 8 split: implementer runs the e2e locally, controller does the visual check, push waits for the user. | CI on Debian not yet seen. |
| Final fix wave covers I1–I4, M1–M4, M6, M9, SetupDone wording and data-protection keys; M5, M7, M8 stay open (below). | Those stay open. |

## New facts the spec should record
- OpenSim's log4net pattern now carries `[%level]` (robust and region logs).
- `GridStatus` gained `Activity` (`None`, `Starting`, `Stopping`, `Restarting`) and `StartFailure` (`Message`, `ConflictPort`).
- `IGridController.CreateRegionAsync` gained an optional `IProgress<string>`.
- Data-dir layout gained `<data>/keys`: ASP.NET Core data-protection keys (sign-in and antiforgery cookies), application name `DreamGrid`, created owner-only like `<data>`.
- Bare `GET /` is the website; the "Test Completed" probe is gone (`/?type=...` still answers `ACK`).
- The `dg-setup` cookie is `SameSite=Lax` (a setup link opened from webmail or chat arrives cross-site); `dg-auth` stays `Strict`.
- The port-8001 command API and the sign-in page share one `LoginThrottle`, keyed on the normalised "First Last" name.

## Open follow-ups

### Plan 3 / later
- **M5 — no authorization fallback policy.** `SetupGate` and the Razor folder convention cover today's pages, but every new endpoint must call `RequireAuthorization` itself. Consider a `FallbackPolicy` with explicit `AllowAnonymous` on the open routes.
- **M7 — dashboard after the sign-in cookie expires.** `/api/status` answers anonymously, so an open dashboard keeps polling and shows "who's online unavailable" instead of asking to sign in. Add an `admin` flag to the response, or reload to `/login`.
- **M8 — shutdown.** Fire-and-forget tasks (warm-up, region setup, dashboard actions) are not awaited at shutdown; the file logger reopens its file when written after `Dispose` (add a disposed flag).
- **Ports during setup.** Before an owner exists the Network page is unreachable, so a port conflict during setup can only be solved by closing the other program (the setup pages say so). Offer a port change on the setup pages or in the installer.
- Step 1 (name your world) starts the grid in the background but does not show a start failure; the avatar step does.

### Deferred minors still open
- **Access and sign-in:** `AccessDeniedPath` unset (a non-admin hitting a page gets a 404 at `/Account/AccessDenied`); `dg-setup` and `dg-auth` are not `Secure` (plain HTTP on the LAN); the throttle is in memory (resets on restart) and anyone can lock a named admin out for 10 minutes (plan rule); timing differs between unknown and known names; no server-side name length cap; O(n) prune under the global lock; a broad catch shows "service not running" for any `InvalidOperationException`; no tests for missing antiforgery tokens or long names; AccessTests gaps (case and trailing slash, `/setupX`, garbage cookie, HEAD/POST with token, stale cookie); `/__test/signin` vs `/__test/challenge` matching differ; `AddAntiforgery` header name line is redundant.
- **Setup:** the avatar step blocks its request while the grid starts and a double click can run owner creation twice; no test that a password inside the controller's error is masked; the region page keeps polling silently if the response stops being JSON; the launcher-log excerpt fallback (and `launcher.log` history) can show the spent "Setup link" token line; avatar "Show details" can contain "Robust"/"Console" (inside details, accepted).
- **Dashboard and grid actions:** "restart" is stop+start, not atomic, and Stop during Start waits silently; restart-region with no region returns 202 with the error only in the log; `StartFailure` is not cleared by a successful single-component start, and the success-path clear has no unit test; the headline lags the "Stopping your world." notice by one poll, the notice stays after the action ends, and "Start all" is the primary button while running; the switch notice always says a restart is needed; the `OnlineUsers` integration test needs MariaDB (runs in CI only).
- **Advanced Settings:** no `Cache-Control: no-store` on the Database "Show" response; module secret "unchanged" resolved from a pre-save snapshot (latent); Network restart notes compare against a snapshot read outside `UpdateSettings`; a hand-edited log level ALL/FATAL/OFF is replaced by DEBUG on save; no CR/LF hostname test; no token-rejection tests for switch/test-connection; inline `onsubmit` confirm breaks under a future strict CSP.
- **Logs:** stack-trace continuation lines are kept after their line is filtered out; Show reloads and loses auto-refresh, lines fixed at 200; `Sources()` computed twice per request; a level-filtered tail on a large file re-parses its window several times; test gaps (UTF-8 multibyte at the window boundary, last line without newline, lines clamp 2000, invalid level 400).
- **Launcher log file:** a failed roll (oldest file locked) drops every later line instead of appending; `Launcher_log_file_is_written` passes on startup lines alone.
- **Tests and CI:** the external-URL layout test misses `//host`, form actions and CSS `url()`; `PortCheckTests` flaked once under full-suite port contention; e2e helper gaps (`SubmitAsync` ignores select/textarea defaults, `RegionReadyAsync` fails on any transient error, a POST response is not disposed on assert failure, starter content not e2e-tested); `Legacy_8001_routes_still_open` asserts only "not redirect" for `POST /dreamgrid/regionready`.
