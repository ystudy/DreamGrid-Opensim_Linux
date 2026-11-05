# Home dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Home dashboard. PR 1 is the tiles, regions, counts and `/api/dashboard`. PR 2 is the core services checks.

**Architecture:**
- A Core counter (`IWorldCounter`) runs one read-only COUNT per number against the `robust` and `opensim` databases. Each number is null when its query fails.
- A web-side `DashboardSnapshot` caches the counts for 60 seconds, in the style of `SplashSnapshot`.
- The Home page renders the snapshot. `index.js` refreshes it from `/api/dashboard` every 30 seconds, and from `/api/status` every 5 seconds as today.

**Tech Stack:** ASP.NET Core Razor Pages, MySqlConnector, xUnit + AngleSharp with `LauncherFactory` fakes.

**Spec:** `docs/superpowers/specs/2026-11-05-dashboard-design.md`

## Global Constraints
- **[S]** Queries are fixed SQL text with no request values, run as `robustuser`/`opensimuser` through `MariaDbServer.ConnectionString`, with a 3-second timeout each.
- **[S]** Service checks go only to 127.0.0.1 and the ports in settings, with a 2-second timeout and no redirects, reading at most 64 KB.
- No inline scripts or styles. Numbers use the invariant culture with thousands separators.
- An unknown number shows "—" and never fails the page.

## Review Focus
- **A table that doesn't exist yet** (profiles, groups before first use): that row shows "—" and the others still show. Counter test with a failing query.
- **Region Server and Connected:** no robust counts are asked for, and the people-per-region count is hidden. Page tests.
- **A database that is stopped:** the page renders with "—" everywhere and no error box. Page test with a fake that returns `WorldCounts.Unknown`.
- **The cache:** concurrent first requests share one build; after 60 seconds it rebuilds. Snapshot test with `FakeTimeProvider`.
- **Who's online links:** visitors have no person page and are never linked (`OnlineUsers` already joins `UserAccounts`).

---

### Task 1: Counter (Core)
- **Files:** create `Launcher/DreamGrid.Launcher.Core/Dashboard/WorldCounter.cs`.
- **Produces:**
  - `record WorldCounts(long? Residents, long? JoinedThisMonth, long? NeverSignedIn, long? SignedInToday, long? SignedInThisMonth, long? LandSquareMetres, long? MapRegions, long? Objects, long? LinkedObjects, long? Parcels, long? ParcelsForSale, long? Estates, long? Groups, long? HypergridLinks, long? Profiles, long? Picks, long? Classifieds)` with `static Unknown`.
  - `interface IWorldCounter { Task<WorldCounts> CountAsync(DateTimeOffset monthStart, DateTimeOffset dayStart, CancellationToken ct); }` and its real `WorldCounter(Func<LauncherSettings>, ILogger?)`.
- **Behaviour:**
  - The robust queries run only when `AdminRule.HasStaffPages`.
  - Each query is its own `ScalarAsync` in a try/catch: a MySqlException gives null; cancellation is passed on.
  - The SQL is exactly as in the spec's tables. Residents-only joins use `UserAccounts.PrincipalID`.
- **Tests:** none at unit level (no MariaDB in unit tests). The SQL is checked by hand on the VM (Task 5).

### Task 2: Snapshot, API and who's online IDs (web)
- **Files:**
  - Create `Launcher/DreamGrid.Launcher/Dashboard/DashboardSnapshot.cs` and `Endpoints/DashboardApi.cs`.
  - Modify `Program.cs` (register both), `Core/Grid/OnlineUsers.cs` (add `Guid PersonId = default` to `OnlineAvatar`, select `p.UserID`) and `Endpoints/StatusApi.cs`:
    - add `People` (int?) to `RegionView`, admins only and only where `RunsRobust` and `HasStaffPages`;
    - add `OnlinePeople` (name, region, id) for admins.
  - Test fake: `FakeWorldCounter` in `FakeGridController.cs`, registered in `LauncherFactory`.
- **Produces:** `DashboardSnapshot.CurrentAsync(ct) -> DashboardView(WorldCounts Counts, DateTimeOffset CheckedAt)`, with `Lifetime = 60 s`.
- **Tests (`DashboardSnapshotTests`):**
  - reused within 60 s;
  - rebuilt after;
  - two concurrent calls make one count.
- **Tests (`DashboardApiTests`):**
  - admin gets 200 with `no-store` and the counts;
  - supervisor and anonymous are refused.

### Task 3: Home layout (web)
- **Files:** modify `Pages/Index.cshtml(.cs)`, `wwwroot/js/index.js` and `wwwroot/css/site.css` (`.tiles`, `.tile`, `.pill-*`).
- **Layout:** as the spec, top to bottom. These keep their ids, so the existing tests and script keep working: `#headline`, `#components` (now the database and grid service rows in Core services), `#online`, `#online-note`, `#login-uri`, `#copy-uri`, `#controls`, `#notice`, `#signups-card` and `#features`. New ids:
  - `#tiles` (`.tile[data-tile=online|regions|residents|land|signups]`);
  - `#region-rows`;
  - `#world-counts` (`li[data-count=objects|…]`);
  - `#checked-at`.
- Coming soon goes, so the two `.coming-soon` tests are removed.
- **Tests (`DashboardLayoutTests`):**
  - the tiles and counts show the fake's numbers formatted "12,480";
  - null shows "—";
  - on Region Server: no residents, land or signups tiles, and no people per region;
  - Grid Asset Server shows "on this computer" on objects, parcels and estates;
  - Who's online links to `/people/{id}`.

### Task 4: Core services checks (PR 2)
- **Files:** create `Dashboard/ServiceChecks.cs` (`IServiceChecks.CheckAsync(ct) -> IReadOnlyList<ServiceCheck(string Name, string Detail, ServiceState State, TimeSpan? Took)>`), using an `HttpClient` from `IHttpClientFactory` named "local-checks" with `AllowAutoRedirect = false` and a 2-second timeout. The snapshot caches the checks for 30 seconds.
- **Rows:** as the spec. **Slow** is over 1 second.
  - The Textures request sends `Range: bytes=0-1023`.
  - Profiles is Working when the `Profiles` count isn't null.
- **Tests:**
  - a fake handler: 206 → Working, 404 → Check this, connection refused → Down, 1.5 s → Slow;
  - every request is to 127.0.0.1.

### Task 5: Verify
- The full unit suite. Then the VM install: look at the page as owner, and check the counts against `mysql` by hand.
