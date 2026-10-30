# Plan 6c — Connected Server: regions on someone else's grid

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The "Connected Server — Uses someone else's grid" card works. The owner picks OSgrid, or types another grid's login address and its address for region servers. Setup reads what that grid publishes, and this computer runs regions (and its own region database) on that grid, with no Robust of its own.

**Architecture:** This builds on the Region Server path from 6b, which already starts no Robust, watches the remote grid, looks the estate owner up by name and places regions using the map.
- **Settings:** a new `LauncherSettings.Connected` record holds the other grid's addresses.
- **`GridEndpoints`:** gains a Connected branch and one new call, `ServiceBase`, which says where each service lives. On OSgrid the services are on different hosts; on other grids they are all on one address.
- **INIs:** `GridConfigWriter` writes them two ways:
  - another grid: through the same `[Const]` values a Region Server uses;
  - OSgrid: from the shipped `Proto/OsGrid` files, as spec §10 says.
- **Controller:** treats "uses another computer's grid" (`!RunsRobust`) as one case wherever 6b checked for a Region Server, so both plans share the start, the watch and the refusals.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, xUnit v3, AngleSharp (page tests), the existing `IniDocument`, `RobustClient`, `GridController`.

**Spec:** `docs/superpowers/specs/2026-10-29-install-and-plans-design.md` (§5, §7, §8, §9, §10 last bullet, §11). Builds on plan 6b (`docs/superpowers/plans/2026-10-29-plan6b-region-server.md`, PR 13, merged as 7621bdb).

**Branch:** `feature/connected-server`, cut from `V7` at 7621bdb.

**Decisions made here (my call, within what spec §10 and §15 leave to the builder):**
- **Two ways to name the other grid.**
  - **Why:** `get_grid_info` publishes a grid's name, login address and gatekeeper, but not where its services are. OSgrid's own answer lists `login`, `gridname`, `gatekeeper` and web pages only (checked 2026-10-29). So "asks for the rest" means asking for those services.
  - **OSgrid** is built in. Its service addresses come from `bin/config-include/Proto/OsGrid/` (shipped in every package, since the packager copies all of `bin/`).
  - **Another grid** takes two addresses:
    - its **login address**, the one Firestorm uses (default port 8002);
    - its **address for region servers**, which grids that accept outside regions hand out (default port 8003). Every private service is assumed to be at that one address, as on a DreamGrid or stock Robust grid.
- **Hypergrid on a Connected Server** is on exactly when the other grid's `get_grid_info` publishes a `gatekeeper`. Then the regions include `GridHypergrid.ini`, otherwise `Grid.ini`. There is no switch, because "the other grid decides".
- **Re-read at each start:** like a Region Server, a Connected Server reads `get_grid_info` again at each start. That updates the grid's name and gatekeeper and doubles as the reachability check. The addresses the owner chose are kept.
- **Sign-in to this computer's website** uses the other grid's account and authentication services, as on a Region Server.
  - **Only the estate owner** (by account ID) may sign in. Level ≥ 200 doesn't count on a Connected Server: those are the other grid's staff, not this computer's owner.
  - **Checked:** stock OpenSim always allows the `authenticate` call (`AuthenticationServerPostHandler.cs`; only `setpassword`, `getauthinfo` and `setauthinfo` have switches).
  - **Risk:** a grid could filter that call in front of Robust. Then the owner can't sign in to the website after setup (setup itself signs them in with the setup token). The advisory says sign-in uses that grid. A local website password is the parked fallback, to build only if this shows up.
- **The other grid is a DreamGrid Grid Asset Server:** if its `dreamgrid_plan` is `grid-asset`, setup warns that the Region Server plan joins it more simply, but doesn't refuse. A Local or Simple Public DreamGrid grid is an ordinary "another grid"; the integration test uses one.
- **Placement looks at a window of the map, not the whole map.** OSgrid has many thousands of regions.
  - The first region goes on the first free square in x 1000–1063, y 1000–1063.
  - Adding or moving a region checks the squares around the spot asked for.
  - A Region Server keeps reading the whole map, as in 6b. Moving a region on either plan now also checks the other computers' regions; 6b only checked when adding.
- **No change of grid in Advanced settings:** the spec §8 matrix gives Connected → Connected as "—". A grid that moves its addresses is a parked follow-up.
- **No schema bump:** `Connected` is a new nullable field, as `Join` was.

## Global Constraints

- The plan name is exactly "Connected Server" — "Uses someone else's grid".
- Every settings change goes through `IGridController.UpdateSettings(…, writeConfig: true)`; config is written only by `GridConfigWriter`.
- The website never needs administrator rights; nothing here installs software or changes a firewall.
- Windows and Debian 12/13 behave the same; tests run on both in CI.
- Plain words for owners: no INI names on owner pages. Say "the other grid", "its login address", "its address for region servers", "region ports".
- Settings never hold a password from the other grid. The owner's password is only passed through to that grid's own authentication service at sign-in, and never logged.
- **INIs of the other plans:** Local, Simple Public Server, Grid Asset Server and Region Server installs must write exactly the INIs they write today. The `Snapshots/*` tests and 6b's Region Server writer tests pass unchanged.
- No test contacts OSgrid or any other outside host: unit tests use fake HTTP handlers, and the integration test uses a second launcher as "another grid".
- The port-8001 contract (RegionReady callbacks to `127.0.0.1:<WebPort>`, `/welcome`) keeps working on a Connected Server.

## Review Focus

1. **The other grid doesn't answer when the Connected Server starts** (internet down, the grid's maintenance), on boot, the Start button, region power or adding a region:
   - the database starts and no region starts;
   - the dashboard names the grid and its login address;
   - the saved addresses stay as they were.

   Tests in Task 4.
2. **Addresses the owner gets slightly wrong:**
   - accepted and cleaned up: `login.example.org` with no scheme or port, a closing slash, spaces around it;
   - kept as typed: `https://`;
   - refused with a plain message: the grid's web page instead of its login (it answers with HTML), or nothing answering at the address for region servers. Nothing is saved.

   Tests in Task 1 and Task 5.
3. **The squares the first region would use are taken on the other grid:** the first free square in the window is used. A window that is entirely taken gives a plain message. Adding or moving a region onto a used square is refused with that region's name. Tests in Task 3 and Task 4.
4. **An estate owner name that doesn't exist on the other grid,** or differs only in letter case: refused before any region starts, with the grid's name and "sign up" in the message. A case-only difference saves the account's own spelling. Tests in Task 4 and Task 5.
5. **A Local, Simple Public, Grid Asset or Region Server install after this change:**
   - its INIs are the same as before;
   - its sign-in rule is unchanged (level ≥ 200, plus the estate owner on a Region Server);
   - its map calls are unchanged.

   Tests in Task 2, Task 3 and Task 6.

---

### Task 1: The other grid: settings, OSgrid preset, reading its grid info

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Plans/OtherGrid.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs` (add `ConnectedGrid`, `LauncherSettings.Connected`)
- Modify: `Launcher/DreamGrid.Launcher/LauncherServices.cs` (register the client)
- Test: `Launcher/DreamGrid.Launcher.Tests/OtherGridTests.cs` (new)

**Interfaces:**
- Produces:
  - `public sealed record ConnectedGrid(string? Preset, string GridName, string Address, string LoginUri, string ServicesUri, string? GatekeeperUri)`. Each field:
    - `Preset`: `"osgrid"` or null for another grid;
    - `Address`: where this computer reaches the login service, as the owner typed it or as the preset gives it, with a scheme and closing slash, for example `http://login.osgrid.org/`;
    - `LoginUri`: the grid's own published `login`, which Firestorm and the region INIs use;
    - `ServicesUri`: the address for region servers, for example `http://grid.example.org:8003/`; for OSgrid it is the preset's `GridUri`;
    - `GatekeeperUri`: null when the grid publishes no gatekeeper.

    It holds no password.
  - `LauncherSettings.Connected : ConnectedGrid?` (null until connected).
  - `public sealed record GridPreset(string Id, string Title, string Address, string AccountsUri, string AuthenticationUri, string GridUri, string ProtoFolder)` and `public static class GridPresets { IReadOnlyList<GridPreset> All; GridPreset? ById(string? id); }`. `All` has exactly one entry: `new("osgrid", "OSgrid", "http://login.osgrid.org/", "http://users.osgrid.org", "http://login.osgrid.org", "http://grid.osgrid.org", "OsGrid")`. The URIs are copied from `Proto/OsGrid/GridCommon.ini` (`UserAccountServerURI`, `AuthenticationServerURI`, `GridServerURI`).
  - `public static class OtherGridAddress { public static Uri? Parse(string? typed, int defaultPort, out string? error); public const int LoginPort = 8002; public const int ServicesPort = 8003; }`. It applies `JoinAddress`'s rules (host checked by `SettingsValidator.HostnameError`, port 1–65535), but keeps an `https://` scheme. It returns `scheme://host:port/`, with `http` when no scheme was typed. The errors name "the other grid's address", not the Grid Asset Server.
  - `public sealed record OtherGridInfo(string GridName, string LoginUri, string? GatekeeperUri, bool IsGridAssetServer)`.
  - `public static OtherGridInfo OtherGridInfoParser.Parse(Uri address, string gridInfoXml)`. When the XML isn't `<gridinfo>`, it throws `GridJoinException("{address} answered, but it didn't answer like a grid. Check that it is the grid's login address, not its website.")`. Otherwise:
    - `GridName`: `gridname`, else the host;
    - `LoginUri`: `login`, else `address`;
    - `GatekeeperUri`: `gatekeeper`, else null;
    - `IsGridAssetServer`: `dreamgrid_plan == "grid-asset"`.
  - `public interface IOtherGridClient`, implemented by `OtherGridClient(HttpClient http)` with the named client `"DreamGrid.OtherGrid"` and a 10 s timeout:
    - `Task<OtherGridInfo> ReadAsync(Uri address, CancellationToken ct)`: GET `{address}get_grid_info`. A connection failure or timeout throws `GridJoinException("Nothing answered at {address}. Check the address, and this computer's internet connection.", noAnswer: true)`.
    - `Task CheckServicesAsync(Uri services, CancellationToken ct)`: POST `{services}grid` with `METHOD=get_region_range`, `SCOPEID=00000000-0000-0000-0000-000000000000`, `XMIN=YMIN=256000`, `XMAX=YMAX=256256`. Any reply whose root is `<ServerResponse>` passes. No answer throws `GridJoinException("Nothing answered at {services}. Check the address for region servers the grid gave you.", noAnswer: true)`. Anything else throws `GridJoinException("{services} answered, but not like a grid's services. Check the address for region servers the grid gave you.")`.

- [ ] **Step 1: Write the failing tests** (`OtherGridTests`):
  - `Osgrid_preset_matches_the_shipped_proto`: load `bin/config-include/Proto/OsGrid/GridCommon.ini` with `IniDocument`. Its `UserAccountServerURI`, `AuthenticationServerURI` and `GridServerURI` (quotes trimmed) equal the preset's three URIs.
  - `Parse_reads_osgrid`: the XML `<gridinfo><login>http://login.osgrid.org</login><gridname>OSGrid</gridname><gatekeeper>http://hg.osgrid.org/</gatekeeper></gridinfo>` gives `("OSGrid", "http://login.osgrid.org", "http://hg.osgrid.org/", false)`.
  - `Parse_without_gatekeeper_has_no_hypergrid`: `GatekeeperUri` is null.
  - `Parse_flags_a_grid_asset_server`: XML with `<dreamgrid_plan>grid-asset</dreamgrid_plan>` sets `IsGridAssetServer`.
  - `Parse_refuses_a_web_page`: `<html>…` gives a message containing `"not its website"`.
  - `OtherGridAddress_parses` (`[Theory]`):

    | Typed | Default port | Result |
    |---|---|---|
    | `"grid.example.org"` | 8002 | `http://grid.example.org:8002/` |
    | `" http://grid.example.org:9000/ "` | 8002 | `http://grid.example.org:9000/` |
    | `"https://grid.example.org"` | 8003 | `https://grid.example.org:8003/` |

  - `OtherGridAddress_refuses` (`[Theory]`): `""`, `"grid.example.org:"`, `"grid.example.org:70000"`, `"a b"` and `"ftp://x"` each give null and a non-empty error.
  - `CheckServicesAsync_accepts_a_grid_service_and_refuses_html`: with a fake handler, `<ServerResponse/>` passes; `<html/>` throws with a message containing `"not like a grid's services"`.
- [ ] **Step 2: Run them; they fail** (`dotnet test --project Launcher/DreamGrid.Launcher.Tests -- --filter-class "*OtherGridTests"`): the types are missing.
- [ ] **Step 3: Implement** the interfaces above. `OtherGridAddress` shares `JoinAddress`'s host-and-port parsing. Factor out a private helper rather than copying it, and keep `JoinAddress.Parse`'s behaviour and messages unchanged (`GridJoinTests` pass as they are).
- [ ] **Step 4: Run the whole unit suite; it passes** (`dotnet test --project Launcher/DreamGrid.Launcher.Tests`).
- [ ] **Step 5: Commit** — `Connected Server: the other grid's settings, the OSgrid preset, reading grid info`.

---

### Task 2: Where the other grid's services are, and the Connected Server's INIs

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Plans/GridEndpoints.cs`, `Plans/PlanCatalog.cs` (`HypergridOn`)
- Modify: `Launcher/DreamGrid.Launcher.Core/OpenSim/RobustClient.cs` (per-service bases)
- Modify: `Launcher/DreamGrid.Launcher.Core/Config/GridConfigWriter.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/GridConfigWriterTests.cs`, `RobustClientTests.cs`, `PlanCatalogTests.cs`

**Interfaces:**
- Consumes: `ConnectedGrid`, `GridPresets` (Task 1).
- Produces:
  - `public enum GridServiceKind { Accounts, Authentication, Grid }`.
  - `public static Uri GridEndpoints.ServiceBase(LauncherSettings s, GridServiceKind kind)`:
    - Connected with an OSgrid preset: the preset's `AccountsUri`, `AuthenticationUri` or `GridUri`;
    - Connected with another grid: `ServicesUri`;
    - every other install: `PrivateBase(s)`.

    `RobustClient` posts `/accounts`, `/auth/plain` and `/grid` against it.
  - The other `GridEndpoints` calls gain a Connected branch. When `Plan == Connected` and `Connected` is set:

    | Call | Returns |
    |---|---|
    | `PublicBase` | `Address` without its closing slash; `IsReadyAsync` reads `get_grid_info` there |
    | `PrivateBase` | `ServicesUri` without its closing slash |
    | `PrivatePort` | `ServicesUri`'s port |
    | `Advertised` | parsed from `LoginUri` exactly as for a Region Server, so OSgrid gives `("http://login.osgrid.org", 80)` |
    | `LoginUri` | `Connected.LoginUri` |

    Connected settings with no `Connected` record yet fall back to the local values, as an unjoined Region Server does.
  - `public static string GridEndpoints.PrivateUrl(LauncherSettings s)`: the scheme and host written to `[Const] PrivURL`:
    - `http://127.0.0.1` on single-machine plans;
    - `http://{Join.Host}` on a Region Server;
    - `ServicesUri`'s scheme and host on a Connected Server.

    It replaces `PrivateHost` at its one call site in the writer, so an `https` services address keeps its scheme. Remove `PrivateHost` if nothing else uses it.
  - `PlanCatalog.HypergridOn(s)` for `Connected` returns `s.Connected?.GatekeeperUri is not null`.

- [ ] **Step 1: Write the failing tests.** Put the test runtime's `Proto/OsGrid` beside `Proto/Robust`: the fixture copies only `Proto/Robust` today, so extend that copy.

  `GridConfigWriterTests.Connected_to_another_grid_uses_its_addresses`. **Settings:**
  - `Plan = Connected`, `Hostname = "203.0.113.7"`;
  - `Connected = new(null, "Elsewhere", "http://10.0.0.9:8002/", "http://grid.example.org:8002/", "http://grid.example.org:8003/", "http://grid.example.org:8002/")`;
  - an owner with `OwnerCreated = true`, and one program and region.

  **Assert in the region's `OpenSim.ini`:**
  - `BaseHostname = 203.0.113.7`;
  - `BaseURL = http://grid.example.org` and `PublicPort = 8002`;
  - `PrivURL = http://grid.example.org` and `PrivatePort = 8003`;
  - `Include-Architecture = "config-include/GridHypergrid.ini"`;
  - `DefaultEstateOwnerName` is set and `DefaultEstateOwnerUUID` is not active.

  Also assert:
  - `GridCommon.ini [AssetService] AssetServerURI` still reads `"${Const|PrivURL}:${Const|PrivatePort}"`;
  - the region file's `ExternalHostName = 203.0.113.7`.

  `…_without_gatekeeper_uses_grid_ini`: `GatekeeperUri = null` gives `"config-include/Grid.ini"` and `AllowHypergridMapSearch = false`.

  `Connected_to_osgrid_uses_the_osgrid_proto`. **Settings:** `Connected = new("osgrid", "OSGrid", "http://login.osgrid.org/", "http://login.osgrid.org", "http://grid.osgrid.org/", "http://hg.osgrid.org/")`.

  **Assert in `GridCommon.ini`:**
  - `[AssetService] AssetServerURI = "http://assets.osgrid.org"`;
  - `[UserAccountService] UserAccountServerURI = "http://users.osgrid.org"`;
  - `[Hypergrid] GatekeeperURI = "http://hg.osgrid.org:80"`;
  - `[DatabaseService] ConnectionString` names `Database=opensim` and this install's MariaDB port.

  **Assert in the region's `OpenSim.ini`:**
  - `BaseURL = http://login.osgrid.org` and `PublicPort = 80`;
  - `[Groups] GroupsServerURI = "http://groups.osgrid.org/xmlrpc.php"` and `ServicesConnectorModule = XmlRpcGroupsServicesConnector`;
  - `[Groups] LocalService` not active;
  - `[UserProfiles] ProfileServiceURL = http://services.osgrid.org`;
  - `[XBakes] URL = http://xbakes.osgrid.org`;
  - `[Messaging] OfflineMessageURL = http://services.osgrid.org`;
  - `[SimulatorFeatures] MapTileURL = "http://maptiles.osgrid.org:80"`;
  - `[Search] SearchURL = http://search.osgrid.org/v2/query.php`.

  The existing `Single_machine_plans_write_what_they_did_before` and 6b's `Region_server_*` writer tests pass unchanged.

  `RobustClientTests.Connected_asks_each_service_where_it_lives`, with a fake handler recording request URLs:

  | Settings | Call | Request URL |
  |---|---|---|
  | OSgrid | `GetAccountAsync` | `http://users.osgrid.org/accounts` |
  | OSgrid | `AuthenticateAsync` | `http://login.osgrid.org/auth/plain` |
  | OSgrid | `GetMapRegionsAsync` | `http://grid.osgrid.org/grid` |
  | OSgrid | `IsReadyAsync` | `http://login.osgrid.org/get_grid_info` |
  | Another grid | all three posts | `http://grid.example.org:8003/` + path |

  `PlanCatalogTests.HypergridOn_connected_follows_the_gatekeeper`.
- [ ] **Step 2: Run them; they fail.**
- [ ] **Step 3: Implement.**
  - **Another grid:** this needs only the `GridEndpoints` values, since `Proto/Robust/GridCommon.ini` already builds every service URL from `[Const]`. When `GatekeeperUri` is set and differs from `BaseURL:PublicPort`, also set these to it, without its closing slash:
    - `[Hypergrid] HomeURI` and `GatekeeperURI`;
    - `[GridService] Gatekeeper` and `[Messaging] Gatekeeper`;
    - `[HGInventoryAccessModule] HomeURI` and `Gatekeeper`;
    - `[HGAssetService] HomeURI`;
    - `[UserAgentService] UserAgentServerURI`.
  - **A preset:** load `GridCommon.ini` from `Proto/{ProtoFolder}/` instead of `Proto/Robust/`, then set `[DatabaseService]` and `AllowHypergridMapSearch` as today. In the region's `OpenSim.ini`, copy from `Proto/{ProtoFolder}/OpenSim.ini`:
    - **Whole sections:** `[Groups]`, `[UserProfiles]`, `[XBakes]` and `[Search]`. Every active key of the preset's section is set, and every active key of the Robust proto's section that the preset lacks is commented out.
    - **Single keys:** `[Messaging] OfflineMessageURL` and `MuteListURL`, and `[SimulatorFeatures] MapTileURL`.

    Keep those section names and key pairs as two small lists next to the writer, not in `GridPreset`, since there is one preset. `[Const] BaseHostname` stays this computer (the OSgrid proto's `hg.osgrid.org` there is not copied).
  - **Estates:** `[Estates]` comments out `DefaultEstateOwnerUUID` whenever `!PlanCatalog.RunsRobust(s)` (it was Region Server only).
  - **Single-machine and Region Server paths** stay byte-for-byte the same.
- [ ] **Step 4: Run the whole unit suite; it passes.**
- [ ] **Step 5: Commit** — `Connected Server: regions use the other grid's services`.

---

### Task 3: Map windows and placing a region on a big grid

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/OpenSim/RobustClient.cs`, `Grid/RegionPlacement.cs`
- Modify: every `IRobustClient` fake in `Launcher/DreamGrid.Launcher.Tests`
- Test: `Launcher/DreamGrid.Launcher.Tests/RegionPlacementTests.cs`, `RobustClientTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record MapArea(int XMin, int YMin, int XMax, int YMax)` (squares, inclusive). It has two members:
    - `static MapArea Start => new(1000, 1000, 1063, 1063)`;
    - `static MapArea Around(int x, int y, int size) => new(x - 1, y - 1, x + size, y + size)`, clamped to 0 … `RegionPlacement.MaxCoordinate`.
  - `IRobustClient.GetMapRegionsAsync(MapArea area, CancellationToken ct)`: posts `get_region_range` with `XMIN = XMin * 256`, `YMIN = YMin * 256`, `XMAX = XMax * 256 + 255` and `YMAX = YMax * 256 + 255`. The existing parameterless `GetMapRegionsAsync(CancellationToken ct)` stays and keeps posting exactly what it does today (`0`…`16776960`), so the Region Server's calls don't change.
  - `RegionPlacement.FirstFreeIn(LauncherSettings s, IReadOnlyList<MapRegion> elsewhere, MapArea area, int size = 1) : (int X, int Y)?`: rows `y = area.YMin … area.YMax - size + 1`, and within each row `x = area.XMin … area.XMax - size + 1`, the first spot where `Check` is OK. It returns null when there is none. `FirstFree` is unchanged.

- [ ] **Step 1: Write the failing tests:**
  - `RobustClientTests.GetMapRegionsAsync_in_an_area_asks_for_that_area`: `MapArea(1000, 1000, 1001, 1001)` posts `XMIN=256000`, `YMIN=256000`, `XMAX=256511`, `YMAX=256511`. The parameterless call still posts `XMAX=16776960`.
  - `RegionPlacementTests.FirstFreeIn_skips_used_squares`: with Hub at 1000,1000 size 2, `FirstFreeIn(…, MapArea.Start)` = `(1002, 1000)`.
  - `FirstFreeIn_returns_null_when_the_area_is_full`: a 2×2 area with one size-2 region in it gives null.
  - `MapArea_around_clamps_at_the_edge`: `Around(0, 0, 1)` = `(0, 0, 1, 1)`.
- [ ] **Step 2: Run them; they fail.**
- [ ] **Step 3: Implement.** Update the fakes: they ignore the area, or filter by it where a test needs that.
- [ ] **Step 4: Run the whole unit suite; it passes.**
- [ ] **Step 5: Commit** — `Region placement can ask the grid's map for one area`.

---

### Task 4: The controller on a Connected Server

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/GridController.RegionServer.cs` (rename it to `GridController.OtherGrid.cs`; the partial now covers both plans), `GridController.cs`, `IGridController.cs`, `GridStatus.cs`
- Modify: every catch of `GridAssetUnreachableException` in `Launcher/DreamGrid.Launcher` (Setup Avatar, Advanced Join, `SetupRunner`), which now catches the base class
- Test: `Launcher/DreamGrid.Launcher.Tests/ConnectedServerControllerTests.cs` (new, on `RegionServerControllerTests`' fixtures, with a fake `IOtherGridClient`)

**Interfaces:**
- Consumes: `IOtherGridClient` (Task 1), `GridEndpoints` (Task 2), `MapArea`, `FirstFreeIn`, `GetMapRegionsAsync(MapArea, …)` (Task 3).
- Produces:
  - `GridController` takes `IOtherGridClient otherGrid` as a new constructor parameter after `join`. Update every construction site and the test helper `NewGrid`.
  - **Exceptions:**
    - `public abstract class GridUnreachableException(string message, Exception? inner) : InvalidOperationException(message, inner)`.
    - `GridAssetUnreachableException` now derives from it, with the same message.
    - New: `public sealed class OtherGridUnreachableException(string gridName, string address, Exception? inner = null) : GridUnreachableException`, whose `static string MessageFor(string gridName, string address)` = `"{gridName} isn't answering at {address}. Your regions will start once it does: check this computer's internet connection, then press Start."`.
  - `GridStatus.GridAssetHost` is renamed `OtherGridAddress`. It holds `"{Join.Host}:{Join.PublicPort}"` on a Region Server and `Connected.Address` on a Connected Server.

**Behaviour on a Connected Server** (`Plan == Connected`; everything 6b does for `!RunsRobust` applies as is unless listed here):
- **Start:** after the database and the leftover-Robust stop (both already in `StartGridServiceAsync`), it calls `otherGrid.ReadAsync(Connected.Address)`.
  - **On success:** save `GridName` and `Connected` with the new `GridName`, `LoginUri` and `GatekeeperUri`. Keep `Preset`, `Address` and `ServicesUri`. As in 6b, save only while `Connected.Address` is still the one this start read.
  - **On a `GridJoinException` with `NoAnswer`:** throw `OtherGridUnreachableException`.
  - **On one without `NoAnswer`:** rethrow it, as 6b does.

  Then the watch starts.
- **Watch, refusals, `GridServiceUp` and `DescribeGridAsset`:** shared with the Region Server. The failed message comes from a private `Unreachable(LauncherSettings s, Exception? inner) : GridUnreachableException`, which picks the right subclass for the plan. `AskGridServiceAsync` and `GridServiceDown` use it too.
- **The estate owner not found** (`SetEstateOwnerAsync`): on a Connected Server the message is `"There's no avatar called {first} {last} on {GridName}. Check the spelling, or sign up with {GridName} first."`. The Region Server's message is unchanged.
- **Placement:**
  - **`CreateRegionAsync`:** `FirstFreeIn(s, map(MapArea.Start), MapArea.Start)`. When that is null, throw `ArgumentException("The squares near 1000,1000 on {GridName}'s map are all taken. Add your region from the Regions page at a spot you choose.")`.
  - **`AddRegionAsync`:** checks against `map(MapArea.Around(x, y, size))`.
  - **`MoveRegionAsync` on both plans that use another computer's grid:** checks against the other computers' regions too (Region Server: the whole map; Connected: `Around`), and refuses an overlap with the same message `AddRegionAsync` gives.
  - **Single-machine plans** make no map call, as today.
- **`JoinedRegionCountAsync`:** returns 0 on a Connected Server (it already does for `!RunsRobust`).

- [ ] **Step 1: Write the failing tests** (`ConnectedServerControllerTests`; settings: `Plan = Connected` with another grid, `Connected.Address = "http://10.0.0.9:8002/"`, `GridName = "Elsewhere"`):
  - `Connected_start_never_starts_robust`: after `StartAsync`, `Robust.State == Running`, `RunsRobust == false`, and there is no `robust.pid`.
  - `Connected_start_rereads_grid_info`: the fake returns `GridName = "Elsewhere 2"` and no gatekeeper. Afterwards the settings have both changes, and the region `OpenSim.ini` includes `Grid.ini`.
  - `Connected_start_waits_when_the_other_grid_is_off`: the fake throws a `NoAnswer` `GridJoinException`. Then:
    - `StartAsync` throws `OtherGridUnreachableException`;
    - `StartFailure.Message` contains `"Elsewhere isn't answering at http://10.0.0.9:8002/"`;
    - the database runs and no program started;
    - `Connected` is unchanged.
  - `Connected_refuses_robust_operations`: start, stop and restart `"robust"` throw `"doesn't run a grid service"`.
  - `Estate_owner_on_the_other_grid`: found by name with its own spelling. A name that isn't found gives a message containing `"sign up with Elsewhere"`.
  - `First_region_takes_the_first_free_square_in_the_window`: the map has 1000,1000 taken, so the region lands at 1001,1000. The fake map is asked only for `MapArea.Start`.
  - `First_region_with_a_full_window_is_refused`: the message contains `"all taken"`.
  - `Adding_or_moving_onto_a_used_square_is_refused`: both give an `ArgumentException` naming the other region. Add the move case to `RegionServerControllerTests` too.
  - The existing `RegionServerControllerTests` pass apart from the constructor argument and the renamed status field.
- [ ] **Step 2: Run them; they fail.**
- [ ] **Step 3: Implement.** Where 6b compares `s.Plan == OwnerPlan.RegionServer` in the controller, use `!PlanCatalog.RunsRobust(s)` unless the line is about `Join` itself.
- [ ] **Step 4: Run the whole unit suite; it passes.**
- [ ] **Step 5: Commit** — `Connected Server: start without Robust, re-read the other grid, watch it`.

---

### Task 5: Setup flow for a Connected Server

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Setup/SetupSteps.cs`, `Setup/SetupRunner.cs`
- Create: `Launcher/DreamGrid.Launcher/Pages/Setup/OtherGrid.cshtml` and `OtherGrid.cshtml.cs` (`/setup/other-grid`)
- Modify: `Launcher/DreamGrid.Launcher/Pages/Setup/Network.cshtml(.cs)`, `Avatar.cshtml(.cs)`
- Modify: `Launcher/DreamGrid.Launcher.Core/Plans/PlanCatalog.cs` (`Available = true`, text)
- Test: `Launcher/DreamGrid.Launcher.Tests/SetupFlowTests.cs` (or a new `ConnectedServerPagesTests.cs` beside `RegionServerPagesTests.cs`), `PlanCatalogTests.cs`

**Interfaces:**
- Consumes: `IOtherGridClient`, `OtherGridAddress`, `GridPresets`, `IGridController.SetEstateOwnerAsync`, `LauncherSettings.Connected`.
- Produces:
  - **Steps:** `SetupSteps.For(s)` for `Plan == Connected`:
    1. ("Choose your plan", `/setup/plan`)
    2. ("Connect to the other grid", `/setup/other-grid`, done when `Connected is not null`)
    3. ("Your network", `/setup/network`, `NetworkSet`)
    4. ("Your avatar", `/setup/avatar`, `OwnerCreated`)
    5. ("Choose your first region", `/setup/region`, `RegionCreated`)
    6. ("Connect with Firestorm", `/setup/connect`, `Finished`)

    Remove the "Connected Server gets its own list" comment.
  - **The other-grid page** (`/setup/other-grid`). Fields: `Grid` (`"osgrid"` or `"other"`, default `"osgrid"`), `LoginAddress` and `ServicesAddress`. The last two are shown only for `"other"`, with help text naming them "its login address (the one you'd give Firestorm)" and "its address for region servers (the grid gives this to people who host regions)".
    - **First post** reads the grid info:
      - **For OSgrid:** at the preset's `Address`, then `CheckServicesAsync(preset.GridUri)`.
      - **For another grid:** at the typed login address, then `CheckServicesAsync` at the typed services address.

      It shows what it found: the grid's name and "Hypergrid: on/off (that grid decides)". When `IsGridAssetServer`, it adds a warning: "This is a DreamGrid Grid Asset Server. The Region Server plan joins it more simply; go back to the plans to choose it." Buttons: "Connect to this grid" and "Change".
    - **`?handler=Confirm`** reads both again (it never trusts form fields for values the grid publishes), then saves:
      - `Connected = new(preset?.Id, info.GridName, address, info.LoginUri, services, info.GatekeeperUri)`, with `address` and `services` as `Uri.ToString()`;
      - `GridName = info.GridName`;
      - `Setup.WorldNamed = true`.

      Then it redirects to `/setup`.
    - **Errors:** an `OtherGridAddress` error or a `GridJoinException` message is shown, and nothing is saved.
  - **The network page on a Connected Server:** the Region Server variant, with these differences:
    - the address choice defaults to "From the internet";
    - the suggested first region port is `RegionPort`;
    - the hypergrid line reads "Hypergrid is on/off on {GridName}; that grid decides.";
    - the advisories are the Connected Server's.
  - **The avatar page on a Connected Server:** the by-name variant, with the help text "Type the name of your avatar on {GridName}. It becomes the owner of the regions on this computer, and it is the avatar you sign in to this website with. No password is needed here." Errors are shown as they are, with `GridUnreachableException` in the catch.
  - **`PlanCatalog` for Connected:**
    - `Available = true`;
    - `YouNeed` = `["An avatar on the other grid", "The grid's login address, and its address for region servers (OSgrid's are built in)", "A public address for your regions"]`;
    - the advisories add: "Signing in to this computer's website uses your avatar on the other grid: that grid checks the password."
- [ ] **Step 1: Write the failing tests** (page tests with a fake `IOtherGridClient` registered in the test host):
  - `Connected_server_card_can_be_chosen`: posting `/setup/plan/connected-server` saves the plan, and `/setup` goes to `/setup/other-grid`. Update the existing tests that expect the card to be unavailable: `Choosing_an_unavailable_or_unknown_plan_is_refused` keeps only `nope`, and the "Coming in a later DreamGrid update." assertion goes.
  - `Osgrid_shows_what_it_found_then_saves_on_confirm`:
    - the first post shows "OSGrid" and "Hypergrid: on", and saves nothing;
    - Confirm saves `Connected.Preset == "osgrid"`, `GridName == "OSGrid"` and `WorldNamed`;
    - the next step is `/setup/network`.
  - `Another_grid_needs_both_addresses`: with the services address left empty, the page shows an error and saves nothing. With `grid.example.org` and `grid.example.org`, it saves `Address = "http://grid.example.org:8002/"` and `ServicesUri = "http://grid.example.org:8003/"`.
  - `Another_grid_that_is_a_grid_asset_server_is_warned_not_refused`: the warning text is shown, and Confirm still saves.
  - `Other_grid_errors_are_shown`: the fake throws a `GridJoinException` ("Nothing answered at …") for the grid info, then for the services. Each message is shown, and `Connected` stays null.
  - `Connected_network_defaults_to_the_internet_and_has_no_switch`: there is no `Hypergrid` input, the text includes "that grid decides", and the public choice is checked.
  - `Connected_avatar_is_typed_by_name`: there is no password field. "ann lee" is saved as "Ann Lee", and the owner is signed in.
  - `Connected_skips_name_your_world`: `/setup/world` is not in the steps.
  - `PlanCatalogTests`: `Connected.Available == true`, and every plan is now available. Update `Every_plan_but_connected_server_is_available`.
- [ ] **Step 2: Run them; they fail.**
- [ ] **Step 3: Implement.**
  - Share what the Region Server's join page and this page have in common (the "found" panel) only where it stays readable. Two short pages are fine.
  - All saves go through `grid.UpdateSettings(…, writeConfig: true)`.
  - `SetupRunner` uses `GridUnreachableException` where it named `GridAssetUnreachableException`.
- [ ] **Step 4: Run the whole unit suite; it passes.**
- [ ] **Step 5: Commit** — `Connected Server: other-grid, network and avatar steps`.

---

### Task 6: Sign-in, dashboard and Advanced settings

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Security/AdminRule.cs`
- Modify: `Launcher/DreamGrid.Launcher/Endpoints/StatusApi.cs`, `Pages/Index.cshtml(.cs)`
- Modify: `Launcher/DreamGrid.Launcher/Pages/Advanced/Network.cshtml.cs`, `Pages/Advanced/Plan.cshtml(.cs)`
- Test: the `AdminRule` tests, `AdvancedSettingsTests.cs`, the dashboard and status tests (6b added theirs to `RegionServerPagesTests.cs`)

**Interfaces:**
- Produces:
  - **`AdminRule.MayAdminister`:**
    - on a Connected Server, only `a.PrincipalId == s.Setup.Owner?.PrincipalId`;
    - on a Region Server, unchanged (level ≥ 200 or the owner);
    - on other plans, unchanged (level ≥ 200).

    Update its summary.
  - **Status and dashboard on a Connected Server:**
    - the grid-service row is labelled with `Connected.GridName` (for example "OSGrid"), with the detail `Connected.Address`;
    - its failed text is `"{GridName} isn't answering."`;
    - there is no "Restart the grid service" button (already true when `!RunsRobust`).

    Put the label and the address in one helper that both the Region Server and the Connected Server use, instead of a second `if`.
  - **Advanced Network on a Connected Server:** the hypergrid line from Task 5 replaces the switch.
  - **Advanced Plan on a Connected Server:**
    - the current plan's card shows the grid's name and login address (as the Region Server's shows its Grid Asset Server);
    - every other plan is ✘ with the matrix reason;
    - there is no change button and no "point at a different grid" link.
  - The dashboard's "Coming soon" list no longer names the Connected Server. If the list is then empty, remove it.

- [ ] **Step 1: Write the failing tests:**
  - **`AdminRule`:**
    - a level-0 owner on Connected → true;
    - a level-250 non-owner on Connected → false;
    - the Region Server and Local cases from 6b stay true and false as before.
  - **Sign-in on a Connected Server** (fake `IRobustClient`): the level-0 estate owner signs in, and a level-250 account that isn't the owner gets the generic refusal.
  - **Status JSON** on a Connected Server labels the row `"OSGrid"`. The dashboard HTML has no `restart-robust` button and no "Connected Server" under "Coming soon".
  - **Advanced Plan** on a Connected Server: no change form, and the text contains the grid's name.
  - **Advanced Network** on a Connected Server: no `Hypergrid` input.
- [ ] **Step 2: Run them; they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the whole unit suite; it passes.**
- [ ] **Step 5: Commit** — `Connected Server: sign-in, dashboard and Advanced settings`.

---

### Task 7: Two launchers end to end, and owner docs

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.IntegrationTests/WebsiteEndToEndTests.cs` (reuse 6b's two-launcher helpers)
- Modify: `content/README.md` (Connected Server line), `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` (6c done)

- [ ] **Step 1: Write the integration test** `Connected_server_joins_another_grid_and_boots_a_region`. It needs MariaDB like the others: skipped locally without it, required in CI.
  1. **Launcher A**, Local, as "another grid": walk setup with world "Elsewhere", owner "Test Owner" and region "Hub". Wait for Ready.
  2. **Launcher B**, with its own data folder and free ports: walk the Connected Server setup.
     - Choose "Another grid", with login `127.0.0.1:{A.RobustPublicPort}` and services `127.0.0.1:{A.RobustPrivatePort}`, and confirm.
     - Network: "On my network", with a free first region port.
     - Avatar: "Test Owner".
     - Region: "Isle".
     - Wait for Ready.
  3. **Assert:**
     - B's settings have `Connected.GridName == "Elsewhere"` and `Connected.Preset == null`;
     - "Isle" is at `(1001, 1000)`;
     - B has no `robust.pid`;
     - A's map (`GetMapRegionsAsync` against A) lists "Hub" and "Isle";
     - signing in to B's website as "Test Owner" with A's password succeeds.
  4. **Stop A's world, then stop and start B's.** B's `/api/status` start failure contains `"isn't answering at"`, and B's `Connected` is unchanged.
- [ ] **Step 2: Run it in CI** (push the branch; the Debian launcher jobs run the integration tests). Locally, check that it builds and skips cleanly without MariaDB.
- [ ] **Step 3: Update the docs.**
  - The README's Connected Server line says what it needs: an avatar on that grid, the grid's two addresses or OSgrid, and a public address. It also says that website sign-in uses that avatar.
  - The worklist marks 6c done, and says Phase 1 (HTTPS) is no longer on hold for the plans work.
- [ ] **Step 4: Commit** — `Connected Server: end-to-end test with two launchers, owner docs`.

---

**Parked (not in this plan):**
- A local website password, in case a grid filters `authenticate`.
- Changing a Connected Server's addresses in Advanced settings.
- More presets beyond OSgrid.
- OSgrid's DataSnapshot and search registration.
