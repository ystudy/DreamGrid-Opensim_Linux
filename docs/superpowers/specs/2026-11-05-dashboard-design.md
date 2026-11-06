# Home dashboard — design

GridGate's Home page (`/`, owner and admins) becomes a status dashboard: number tiles at the top, then cards of rows with a status pill on the right. The look follows a status page the owner showed on 2026-11-05 (tiles, "core services" with response times, counts), using GridGate's own data and its normal background (owner: "with the things we have, not the background stuff"). Mockups: the two dashboard drawings shown in the session on 2026-11-05.

The owner chose the order of work (connection page, then this, then support tickets). They asked for land area, profile picks and classifieds to be included, as "a sensor to how your grid's population is doing" (owner, 2026-11-05). Every other choice not quoted here is marked **(my call)**.

Legend, as in the work list: **[S]** security requirement, **[X]** cross-platform note, **[?]** decision still open.

## Why
Today Home shows the processes (database, grid service, regions), Controls, sign-ups, Who's online, Connect and Features as a column of cards. It doesn't show:
- whether the grid's services actually answer;
- how the population is doing: who comes back, who joins, what they build.

## Goals
- At a glance: is the world working, how many people are using it, and is it growing.
- Each number is cheap to get, read-only, and cached, so opening Home never loads the grid.
- The same on Windows and Debian **[X]**: only MariaDB queries and HTTP to 127.0.0.1.

## Not in this design
- Charts or history over time. These are numbers as of now, not stored. (A history is a later item.)
- Showing other computers' regions in detail. A Grid Asset Server shows the whole grid's land area and residents, but objects and parcels only for its own regions (see "Plans").
- Changing who can open Home, Controls, sign-ups or Features. They keep their current rules.

## The page, top to bottom

### Header
"Your world" and one pill on the right: **Running**, **Starting**, **Stopped** or **Problem**, from the existing `Status.Headline` and `Problem`.

### At a glance (tiles)
| Tile | Big number | Small line under it | Source |
|---|---|---|---|
| In-world now | this grid's residents in-world now | "Signed in today: N · this month: N" | `robust.Presence` through the existing `OnlineUsers`, which already leaves out hypergrid visitors **(my call: residents only, so every number on the page counts the same people)**. `robust.GridUser.Login` (Unix seconds as text) ≥ start of today / start of this month, local time **(my call)**, joined to `UserAccounts` for the same reason |
| Regions | "R of T" running | "N parked" | the launcher's own region states (`GridStatus.Regions`) |
| Residents | `COUNT(*)` of `robust.UserAccounts`, leaving out OpenSim's own "GRID SERVICES" account (`6571e388-6218-4574-87db-f9379718315e`, made by Robust on its first start) here and in every other residents-only number | "Joined this month: N · never signed in: N" | `UserAccounts.Created` ≥ start of month; never signed in = no `GridUser` row or `Login = '0'` |
| Land | km², one decimal | "N regions on the map" | `robust.regions`: `SUM(sizeX*sizeY)` and `COUNT` where `(flags & 512) = 0` (not a hypergrid link) |
| Sign-ups | waiting | "waiting" | existing `SignUpBoard.WaitingCount()`. Shown wherever the New sign-ups card is shown today (everyone who can open Home, on plans with a guest side); the Approve and Deny buttons in the card keep following `ApprovalRules.CanApprove` |

### Core services (left column)
One row per part, each with a name, a small line (the port), the time it took, and a pill: **Working**, **Slow** (over 1 second, **my call**) or **Down**.

| Row | How it's checked (127.0.0.1 only) |
|---|---|
| Database | the existing database state and uptime/memory line |
| Login | `GET http://127.0.0.1:{RobustPublicPort}/get_grid_info` answers 200 with grid info XML (as `RobustClient.IsReadyAsync`) |
| Hypergrid | `HEAD /helo` on the public port answers 200 (a GET is logged by Robust as obsolete). Shown only when hypergrid is on |
| Textures | `GET /CAPS/GetTexture/?texture_id=89556747-24cb-43ed-920b-47caed15465f` (OpenSim's default "plywood" texture, in every asset set) on the public port, with `Range: bytes=0-1023` so at most 1 KB comes back. 200 or 206 reads **Working**. 404 reads **Check this** (yellow): either the texture service isn't loaded (the bug fixed in PR 35) or the default texture is missing, and both need looking at. A connection failure reads **Down** |
| Assets | `GET /assets/<default texture>/metadata` on the private port answers 200 with `<AssetMetadata`. A 404 would look the same whether the asset or the whole service is missing |
| Accounts | `getaccount` for the zero ID on the private port, which must answer the "no such account" result (`<result>null</result>`): Robust answers 200 even when it fails. Inventory has no equally cheap read, so it has no row of its own **(my call, after review)**. It is sent by a small new local-only client that always uses 127.0.0.1 and the settings' private port, not `RobustClient` (which follows `GridEndpoints` to other hosts on some plans). The builder picks the exact call: one that returns quickly and changes nothing |
| Profiles | Profiles has no cheap HTTP check (JSON-RPC only), so it's checked by reading the profile counts below. Working if that read succeeds **(my call)** |
| Website | "this page" and **Not https** in yellow while the site is plain HTTP (work list 1.1), else **Working** |

**[S]** Every check:
- goes only to 127.0.0.1 and the ports in settings, never to an address from a page, form or database;
- has a 2-second timeout and doesn't follow redirects;
- doesn't read more than 64 KB;
- sends no passwords or tokens.

Hypergrid and Textures run only where this computer runs Robust (`PlanCatalog.RunsRobust`).

There is no Map tiles row **(my call)**: the map service is registered by prefix, so a check can't tell it from a missing one without reading error-page details, and Login already shows the public port answers.

### Connect (left column)
The login address with Copy, as today.

### Regions (right column)
Each region on this computer: its name (links to the region page), how many people are there, and its state pill. On a Region Server and a Connected Server the people count is left out, because the local robust database's `Presence` is empty there. "Runs with" stays as the small line. People per region come from `robust.Presence.RegionID`, which is already read for Who's online.

### What's in your world (right column)
Label and number rows:

| Row | Source |
|---|---|
| Objects | `COUNT(*)` of `opensim.prims` |
| Linked objects | `COUNT(DISTINCT SceneGroupID)` of `opensim.prims` |
| Parcels | `COUNT(*)` of `opensim.land` |
| Parcels for sale | `opensim.land` where `(LandFlags & 16384) <> 0` |
| Estates | `COUNT(*)` of `opensim.estate_settings` |
| Groups | `COUNT(*)` of `robust.os_groups_groups` |
| Hypergrid links | `robust.regions` where `(flags & 512) <> 0` |
| Profiles filled in | `robust.userprofile` rows joined to `UserAccounts` (residents only) with something in them: non-empty `profileAboutText`, or a `profileImage` other than the zero UUID. OpenSim adds an empty row the first time anyone's profile is opened, so a plain count would mean "profiles opened" |
| Profile picks | `COUNT(*)` of `robust.userpicks` |
| Classifieds | `COUNT(*)` of `robust.classifieds` |

A row whose table doesn't exist yet (OpenSim makes some tables only when the module first runs) shows "—", not an error.

### Who's online (right column)
As today, with each name linked to the person's page and the region they're on. For the links, the admin-only part of `/api/status` carries each person's ID with their name (`StatusView.Online` sends only text today).

### Below the two columns
Controls (Power), New sign-ups and Features, unchanged, in that order. Coming soon goes.

### Footer line
- A key: green Working, yellow Check this, red Down.
- "Checked at HH:MM:SS". Counts every 60 seconds, checks every 30 seconds **(my call)**.

## How it updates and caches
- One cached snapshot service, `DashboardSnapshot`. The first caller waits for the first build; after that every caller gets the last view at once, and a stale one is refreshed in the background, so a slow grid never makes Home slow. Nothing is counted while the database isn't running. Otherwise built like `SplashSnapshot`:
  - a lock, so callers at the same moment share one build;
  - an injected `TimeProvider`;
  - counts kept for 60 seconds, service checks kept for 30 seconds.
  - Every query runs with its own short timeout (3 seconds). One failed query shows "—" for that row; it doesn't fail the page.
- The page renders the snapshot server-side, so it works without scripts.
- With scripts, `index.js` keeps polling `/api/status` every 5 seconds for process states. It also polls a new `/api/dashboard` every 30 seconds for the tiles, checks and counts.
- **[S]** `/api/dashboard` needs the admin role, like Home, and sends `Cache-Control: no-store`. It contains no names, only counts and states. Names stay in `/api/status`'s admin-only part, as now.
- **[S]** The queries are read-only `SELECT COUNT`/`SUM`. They use the existing `robustuser` and `opensimuser` connections (`MariaDbServer.ConnectionString`) and contain no values from the request.

## Plans
| Plan | Shown |
|---|---|
| Local, Simple Public | everything |
| Grid Asset Server | everything. Objects, Parcels, Parcels for sale and Estates get the small label "on this computer", because Region Servers keep their own regions' data. Land and Residents are grid-wide |
| Region Server, Connected | no Residents, Sign-ups, Land, Groups, Hypergrid links or profile rows; their accounts live on another grid. Shown: Regions, Database, the rest of What's in your world (Objects, Linked objects, Parcels, Parcels for sale, Estates), Connect and Controls. Core services shows only the database and the website |

## Looks
- The `.panel`, `.panel-head` and `.panel-rows` classes from the connection page (PR 42), plus:
  - `.tiles` and `.tile`;
  - `.pill` with `.pill-ok`, `.pill-warn`, `.pill-down` and `.pill-off`.
- Numbers use the invariant culture's thousands separator ("12,480").
- No inline styles or scripts (CSP).
- Two columns from the md width up; one column on phones.

## Tests
- **Snapshot:** each count comes from a fake database answer; a missing table gives "—"; one failing query leaves the others.
- **Cache:** the snapshot is reused within 60 seconds and rebuilt after, using the `TimeProvider`.
- **Checks:**
  - a 404 from GetTexture reads Check this, and a 206 reads Working;
  - a connection failure reads Down;
  - a 1.5-second answer reads Slow;
  - an address other than 127.0.0.1 is never asked.
- **Page:**
  - the tiles for each plan row above;
  - the Sign-ups tile follows the New sign-ups card;
  - `/api/dashboard` refuses supervisors and anonymous callers.

## Steps (one PR each)
1. **Layout and counts:** the snapshot, tiles, regions, What's in your world, `/api/dashboard`, and the move of Controls, sign-ups and Features below.
2. **Core services checks:** the probes and their rows.

## Left to the builder
- The exact harmless private-port calls for Assets and Inventory.
- Where the small "on this computer" label sits.
- Tile order on narrow screens.

## Decisions still open
- **[?]** "Slow" at 1 second, counts every 60 seconds and checks every 30 seconds are **(my call)**.
