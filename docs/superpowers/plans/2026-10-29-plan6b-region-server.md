# Plan 6b — Region Server: join a Grid Asset Server

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The "Region Server — Needs a Grid Asset Server" card works: the owner types the Grid Asset Server's address, setup reads what it publishes, and this computer runs regions (and its own region database) against that grid's Robust, with no Robust of its own.

**Architecture:** The join file is Robust's own `get_grid_info` with extra `dreamgrid_*` keys that `GridConfigWriter` adds to `[GridInfoService]` on every install (spec §10 allows this). A Region Server stores what it read in a new `LauncherSettings.Join` record and re-reads it on every start. One helper, `GridEndpoints`, says where Robust is (local `127.0.0.1` or the joined server) and is used by `RobustClient`, `GridConfigWriter` and the controller. On a Region Server the controller never starts Robust; the status's `Robust` entry instead reports whether the Grid Asset Server answers, so every existing "is Robust running?" check keeps working.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, xUnit v3, AngleSharp (page tests), the existing `IniDocument`, `RobustClient`, `GridController`.

**Spec:** `docs/superpowers/specs/2026-10-29-install-and-plans-design.md` (§5, §7, §8, §9, §10, §11). Builds on plan 6a (`docs/superpowers/plans/2026-10-29-plan6a-owner-plans.md`, PR 12).

**Branch:** `feature/region-server`, cut from `feature/install-plans` (or from `V7` once PR 12 is merged).

**Decisions made here (my call, within what spec §15 leaves to the builder):**
- **Join file = `get_grid_info`** on the Grid Asset Server's Robust public port. The owner types `address` or `address:port` (port defaults to the standard `8002`). No new web endpoint; the file holds no passwords.
- **Join comes before the network step** for a Region Server (spec §11 lists network first), because the network step suggests a first region port clear of the Grid Asset Server's, and only the join file says which ports those are.
- **Only a Grid Asset Server can be joined:** the file carries `dreamgrid_plan`; any other plan is refused with a plain message.
- **Grid Asset Server reachability is watched** every 30 s while the plan is Region Server; if it doesn't answer at start, the database starts, regions don't, and the start fails with a message naming the address. The dashboard's Start retries.
- **Who may sign in** to a Region Server's website: the estate owner avatar (by its account ID), or any account of level ≥ 200 on the joined grid. Passwords are checked by the Grid Asset Server.
- **"No Region Servers joined"** (spec §8, leaving Grid Asset) means: the Grid Asset Server's map lists no region that isn't in its own settings. Regions of a Region Server that is off are not on the map, so this checks the ones running now.
- **No schema bump:** `Join` is a new nullable field, and a missing field reads as null.
- **The Grid Asset Server's private port needs no change** (spec §10 asks the builder to work this out). Robust's HTTP server already listens on every address (`BaseHttpServer` uses `IPAddress.Any`). The Windows installer's firewall rule already allows `dotnet` on private and domain networks (`DreamGrid.iss`). Debian manages no firewall. `PrivURL = 127.0.0.1` in Robust's own INI only affects Robust calling itself. The advisories say the port is open to the network.

## Global Constraints

- Plan names exactly as in 6a: "Region Server" — "Needs a Grid Asset Server"; "Grid Asset Server" — "(LAN or WAN)".
- Every settings change goes through `IGridController.UpdateSettings(…, writeConfig: true)`; config is written only by `GridConfigWriter`.
- The website never needs administrator rights; nothing here installs software or changes a firewall.
- Windows and Debian 12/13 behave the same; tests run on both in CI.
- Plain words for owners: no INI names on owner pages; say "the Grid Asset Server", "the grid service", "region ports".
- Keep it basic (owner): any Region Server on the LAN may join; no join security. Advisories say the private port is open to the network.
- The join file and settings never hold a password from the other computer.
- Local, Simple Public Server and Grid Asset Server installs must write exactly the INIs they write today, apart from the new `dreamgrid_*` grid-info keys.
- The port-8001 contract (RegionReady callbacks to `127.0.0.1:<WebPort>`, `/welcome`) keeps working on a Region Server.

## Review Focus

1. **Grid Asset Server off when the Region Server starts** (boot, Start button, region power, add region): the database starts, no region starts, the dashboard says which address isn't answering, and the last join values stay in settings. Tests in Task 4.
2. **A typed address the owner gets slightly wrong**: `http://192.168.1.5:8002/`, trailing spaces, `192.168.1.5:`, a port over 65535, or the Grid Asset Server's web port `8001` (answers, but not with grid info): it is accepted when it can be cleaned up, otherwise refused with a plain message, and nothing is saved. Tests in Task 1 and Task 5.
3. **The first region lands on a square another computer already uses** (the Grid Asset Server's own region at 1000,1000, or another Region Server's): it is placed on the first free square instead, and adding a region at a used square is refused like a local overlap. Tests in Task 3 and Task 4.
4. **An estate owner name that doesn't exist on the grid**, or differs only in letter case: refused before any region starts, with the grid's name in the message; a case-only difference saves the account's own spelling. Tests in Task 4 and Task 5.
5. **A Local, Simple Public or Grid Asset install after this change**: its region `OpenSim.ini`, `GridCommon.ini` and Robust INI are the same as before except for the grid-info keys, and its sign-in rule is unchanged (level ≥ 200 only). Tests in Task 2 and Task 6.

---

### Task 1: Publish and read the join information

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Plans/GridJoin.cs`
- Create: `Launcher/DreamGrid.Launcher.Core/LauncherVersion.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs` (add `JoinedGrid`, `LauncherSettings.Join`)
- Modify: `Launcher/DreamGrid.Launcher.Core/Config/GridConfigWriter.cs` (Robust `[GridInfoService]`)
- Modify: `Launcher/DreamGrid.Launcher/CommandLine.cs` (use `LauncherVersion.Current`)
- Test: `Launcher/DreamGrid.Launcher.Tests/GridJoinTests.cs` (new), `Launcher/DreamGrid.Launcher.Tests/GridConfigWriterTests.cs`

**Interfaces:**
- Produces:
  - `public static class LauncherVersion { public static string Current { get; } }`: the entry assembly's `AssemblyInformationalVersion`, else `"dev"`.
  - `public sealed record JoinedGrid(string Host, int PublicPort, int PrivatePort, string GridName, string LoginUri, string Version, bool Hypergrid, int? RegionPortsFrom, int? RegionPortsTo)`.
  - `LauncherSettings.Join : JoinedGrid?` (null until joined).
  - `public static class JoinAddress { public static (string Host, int Port)? Parse(string? typed, out string? error); public const int DefaultPort = 8002; }`.
  - `public static class GridInfoKeys { Version = "dreamgrid_version", PrivatePort = "dreamgrid_private_port", Hypergrid = "dreamgrid_hypergrid", RegionPorts = "dreamgrid_region_ports", Plan = "dreamgrid_plan" }`.
  - `public sealed class GridJoinException(string message) : Exception(message)`: its message is shown to the owner as is.
  - `public static JoinedGrid GridJoin.Parse(string host, int publicPort, string gridInfoXml)`: throws `GridJoinException` (message for owners) when the XML isn't grid info, a `dreamgrid_*` key is missing, or `dreamgrid_plan` isn't `grid-asset`.
  - `public interface IGridJoinClient { Task<JoinedGrid> ReadAsync(string host, int port, CancellationToken ct); }` with `GridJoinClient(HttpClient http)`: GET `http://{host}:{port}/get_grid_info`, 10 s timeout. A connection failure or timeout becomes `GridJoinException("Nothing answered at {host}:{port}. Check the address, and that the Grid Asset Server is running.")`. Registered in `LauncherServices` with the named client `"DreamGrid.Join"`.

- [ ] **Step 1: Write the failing tests**

`GridConfigWriterTests.Robust_grid_info_publishes_the_join_keys`: with `RobustPrivatePort = 8003`, `Plan = GridAsset`, `Hypergrid = true`, a public `Hostname`, and two programs with HTTP ports 8004 and 8006 and regions on 8004, 8005 and 8006: `Robust.HG.ini` `[GridInfoService]` has `dreamgrid_private_port = 8003`, `dreamgrid_hypergrid = true`, `dreamgrid_region_ports = 8004-8006`, `dreamgrid_plan = grid-asset`, `dreamgrid_version = LauncherVersion.Current`. With no programs, `dreamgrid_region_ports` is empty. With `Plan = Local`, `dreamgrid_plan = local`.

`GridJoinTests`:
- `Parse_reads_a_grid_asset_server`: XML `<gridinfo><gridname>Avalon</gridname><login>http://192.168.1.5:8002/</login><dreamgrid_version>7.1.0</dreamgrid_version><dreamgrid_private_port>8003</dreamgrid_private_port><dreamgrid_hypergrid>false</dreamgrid_hypergrid><dreamgrid_region_ports>8004-8006</dreamgrid_region_ports><dreamgrid_plan>grid-asset</dreamgrid_plan></gridinfo>` → `new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", "7.1.0", false, 8004, 8006)`.
- `Parse_refuses_another_plan`: same XML with `dreamgrid_plan = simple-public` → `GridJoinException` whose message contains `"Simple Public Server"` and `"Grid Asset Server"`.
- `Parse_refuses_a_grid_without_dreamgrid_keys` (OSgrid-like `<gridinfo><gridname>OSgrid</gridname></gridinfo>`) → message contains `"isn't a DreamGrid Grid Asset Server"`.
- `Parse_refuses_what_is_not_grid_info` (`<html>…`) → message contains `"didn't answer like a grid"`.
- `JoinAddress_parses` as a `[Theory]`: `"192.168.1.5"` → `("192.168.1.5", 8002)`; `" 192.168.1.5:9002 "` → `(…, 9002)`; `"http://grid.example.org:8002/"` → `("grid.example.org", 8002)`; `"grid.example.org"` → `(…, 8002)`.
- `JoinAddress_refuses` as a `[Theory]`: `""`, `"192.168.1.5:"`, `"192.168.1.5:70000"`, `"a b"`, `"ftp://x"` → null with a non-empty error.

- [ ] **Step 2: Run them; they fail** (`dotnet test --project Launcher/DreamGrid.Launcher.Tests -- --filter-class "*GridJoinTests"`, then `*GridConfigWriterTests`): types missing.

- [ ] **Step 3: Implement** the interfaces above. Errors use the plan's title from `PlanCatalog.Get`. `dreamgrid_region_ports` is the lowest and highest of every program's `HttpPort` and every live region's `UdpPort`, as `"from-to"`. `JoinAddress` reuses `SettingsValidator.HostnameError` for the host.

- [ ] **Step 4: Run the whole unit suite; it passes** (`dotnet test --project Launcher/DreamGrid.Launcher.Tests`).

- [ ] **Step 5: Commit** — `Region Server: publish and read the join information`.

---

### Task 2: Where Robust is, and the Region Server's INIs

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Plans/GridEndpoints.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Plans/PlanCatalog.cs` (`RunsRobust`, `HypergridOn`)
- Modify: `Launcher/DreamGrid.Launcher.Core/OpenSim/RobustClient.cs` (bases from `GridEndpoints`)
- Modify: `Launcher/DreamGrid.Launcher.Core/Config/GridConfigWriter.cs` (`WriteProgram`, GridCommon)
- Test: `Launcher/DreamGrid.Launcher.Tests/GridConfigWriterTests.cs`, `Launcher/DreamGrid.Launcher.Tests/PlanCatalogTests.cs`, `Launcher/DreamGrid.Launcher.Tests/RobustClientTests.cs` (existing or new)

**Interfaces:**
- Consumes: `LauncherSettings.Join`, `JoinedGrid` (Task 1).
- Produces:
  - `public static bool PlanCatalog.RunsRobust(LauncherSettings s)`: false for `RegionServer` and `Connected`, true otherwise (null plan included).
  - `PlanCatalog.HypergridOn(s)`: for `RegionServer` returns `s.Join?.Hypergrid ?? false`; unchanged otherwise.
  - `public static class GridEndpoints`, where a Region Server with `Join` set uses `Join.Host`/ports and every other install uses `127.0.0.1` and its own ports:
    - `Uri PrivateBase(LauncherSettings s)`
    - `Uri PublicBase(LauncherSettings s)`
    - `string ServiceHost(LauncherSettings s)`: the host written into `BaseURL`/`PrivURL`. It is `Join.Host` on a Region Server; otherwise `HostAddress.Effective(s.Hostname)` for `BaseURL` and `127.0.0.1` for `PrivURL`, as today.
    - `string LoginUri(LauncherSettings s, string host)`: `Join.LoginUri` on a Region Server, otherwise `http://{host}:{RobustPublicPort}`.

- [ ] **Step 1: Write the failing tests**

`GridConfigWriterTests.Region_server_points_regions_at_the_grid_asset_server`: settings with `Plan = RegionServer`, `Hostname = "192.168.1.20"` (this computer), `Join = new("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", "7.1.0", Hypergrid: false, 8004, 8006)`, `Setup.Owner = new("Ann", "Lee", id)`, `OwnerCreated = true`, and one program and region. Assert in the region's `OpenSim.ini`:
- `[Const] BaseHostname = 192.168.1.20` (LSL URLs point at this computer).
- `BaseURL = http://192.168.1.5` and `PrivURL = http://192.168.1.5`.
- `PublicPort = 8002` and `PrivatePort = 8003`.
- `[Architecture] Include-Architecture = "config-include/Grid.ini"`.
- `[Estates] DefaultEstateOwnerName = Ann Lee`, with `DefaultEstateOwnerUUID` not active.

Also assert `GridCommon.ini [GridService] AllowHypergridMapSearch = false` and region file `ExternalHostName = 192.168.1.20`.

`…_with_hypergrid_on_follows_the_grid_asset_server`: same with `Join.Hypergrid = true` → `GridHypergrid.ini`, `AllowHypergridMapSearch = true`, even though `s.Hypergrid` is false.

`Single_machine_plans_write_what_they_did_before`: for `Local`, `SimplePublic` and `GridAsset`, the region `OpenSim.ini` and `GridCommon.ini` written now equal a snapshot taken by running the same settings through the writer at the start of this task. Capture the snapshot as test data in Step 1, before changing the writer.

`PlanCatalogTests.RunsRobust` (`[Theory]` over all five plans and null) and `HypergridOn_region_server_follows_join`.

`RobustClientTests.Region_server_asks_the_grid_asset_server`: with a fake `HttpMessageHandler`, `GetAccountAsync` on Region Server settings posts to `http://192.168.1.5:8003/accounts`, and on Local settings to `http://127.0.0.1:<RobustPrivatePort>/accounts`.

- [ ] **Step 2: Run them; they fail.**

- [ ] **Step 3: Implement.** In `WriteProgram`, the region server sets `BaseHostname` to this computer's effective host, but `BaseURL`/`PrivURL`/ports from `GridEndpoints`/`Join`. On a Region Server the `[Estates]` branch writes the owner name and comments out `DefaultEstateOwnerUUID` (spec §11: the UUID is standalone-only). Robust's INIs are still written on every plan (harmless; Robust doesn't start).

- [ ] **Step 4: Run the whole unit suite; it passes.**

- [ ] **Step 5: Commit** — `Region Server: regions use the Grid Asset Server's services`.

---

### Task 3: The grid's map, and placing a region where nobody is

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/OpenSim/RobustClient.cs` (`GetMapRegionsAsync`)
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/RegionPlacement.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/RegionPlacementTests.cs`, `Launcher/DreamGrid.Launcher.Tests/RobustClientTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record MapRegion(Guid Uuid, string Name, int X, int Y, int Size)` (squares, not metres; `Size` = `max(sizeX, sizeY) / 256`).
  - `IRobustClient.GetMapRegionsAsync(CancellationToken ct) : Task<IReadOnlyList<MapRegion>>`: POST `{PrivateBase}/grid` with `METHOD=get_region_range`, `SCOPEID=00000000-0000-0000-0000-000000000000`, `XMIN=0`, `YMIN=0`, `XMAX=YMAX=16776960`. The reply is `<ServerResponse><region0 type="List">…<uuid>…<locX>(metres)…<sizeX>…<regionName>…</region0>…`; parse `regionN` elements. An empty or `null` reply is an empty list.
  - `RegionPlacement.Check(LauncherSettings s, int x, int y, int size, Guid? ignore = null, IReadOnlyList<MapRegion>? elsewhere = null)`: `elsewhere` regions whose UUID is in `s.Regions` are skipped (they are ours); the others count like local regions for overlap and neighbours.
  - `RegionPlacement.FirstFree(LauncherSettings s, IReadOnlyList<MapRegion> elsewhere, int size = 1) : (int X, int Y)`: the first spot where `Check` is OK, trying y = 1000, 1001, … and, within each row, x = 1000 … 1063.

- [ ] **Step 1: Write the failing tests**

`RegionPlacementTests`:
- `Check_counts_regions_on_other_computers`: `elsewhere = [new(Guid.NewGuid(), "Hub", 1000, 1000, 2)]`, an empty `s` → `Check(s, 1001, 1001, 1, elsewhere: …)` is not OK and `OverlapsWith == "Hub"`; `Check(s, 1002, 1000, 1, …)` is OK with neighbour `("Hub", "east")`.
- `Check_ignores_our_own_regions_on_the_map`: `s` holds region `u` at 1000,1000 and `elsewhere` lists `u` too → `Check(s, 1001, 1000, 1, …)` OK with exactly one neighbour.
- `FirstFree_skips_used_squares`: `elsewhere` Hub at 1000,1000 size 2 → `FirstFree` = `(1002, 1000)`; with nothing anywhere → `(1000, 1000)`.

`RobustClientTests.GetMapRegionsAsync_reads_get_region_range`: the canned reply with `locX = 256000`, `locY = 256256`, `sizeX = sizeY = 512` → `MapRegion(uuid, "Hub", 1000, 1001, 2)`; reply `<ServerResponse>null</ServerResponse>` → empty.

- [ ] **Step 2: Run them; they fail.**
- [ ] **Step 3: Implement.** Check the reply shape against `OpenSim/Server/Handlers/Grid/GridServerPostHandler.cs` (`GetRegionRange`) and `GridRegion.ToKeyValuePairs` in `OpenSim/Services/Interfaces/IGridService.cs` before writing the parser.
- [ ] **Step 4: Run the whole unit suite; it passes.**
- [ ] **Step 5: Commit** — `Region placement counts regions on the grid's other computers`.

---

### Task 4: The controller on a Region Server

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/GridController.cs`, `IGridController.cs`, `GridStatus.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/GridControllerWiringTests.cs` (or a new `RegionServerControllerTests.cs` using the same fixtures; a fake `IRobustClient` and fake `IGridJoinClient`)

**Interfaces:**
- Consumes: `PlanCatalog.RunsRobust`, `GridEndpoints`, `IGridJoinClient`, `IRobustClient.GetMapRegionsAsync`, `RegionPlacement.FirstFree`/`Check(…, elsewhere)`.
- Produces:
  - `GridController` takes `IGridJoinClient join` as a new constructor parameter, after `robust`. Update every construction site and the test helper `NewGrid`.
  - `GridStatus` gains `bool RunsRobust = true` and `string? GridAssetHost = null`. On a Region Server, `Robust` is a `ComponentStatus` describing the Grid Asset Server: `Running` when the last check answered, `Failed` when it didn't, `Stopped` before the first check. `LoginUri` = `GridEndpoints.LoginUri`. `RobustOutdated` is false.
  - `Task<RobustAccount> IGridController.SetEstateOwnerAsync(string first, string last, CancellationToken ct)`. It looks the name up via `IRobustClient.GetAccountAsync`. When there is none, it throws `ArgumentException($"There's no avatar called {first} {last} on {GridName}. Check the spelling, or create the avatar on the Grid Asset Server first.")`. Otherwise it saves `Setup.OwnerCreated = true` and `Owner = new OwnerInfo(account.FirstName, account.LastName, account.PrincipalId)`, using the account's own spelling.
  - `Task<int> IGridController.JoinedRegionCountAsync(CancellationToken ct)`: on a plan that runs Robust, the number of map regions not in `s.Regions`. A Robust that doesn't answer throws `InvalidOperationException`.
  - `public sealed class GridAssetUnreachableException(string host, int port) : InvalidOperationException`. Its message is `"The Grid Asset Server at {host}:{port} isn't answering. Your regions will start once it does: check that computer is on and running DreamGrid, then press Start."`.

Behaviour on a Region Server (`!PlanCatalog.RunsRobust(s)`):
- **Start (`StartCoreAsync`):** start the database. Then `join.ReadAsync(Join.Host, Join.PublicPort)`. On success, save the new values through `UpdateSettings` (keeping `Host`/`PublicPort`, which the owner chose). On failure keep the old values, mark the Grid Asset Server unreachable and throw `GridAssetUnreachableException`, which becomes `_startFailure` like a port conflict. Robust is never started; then start the programs as today.
- **Watch:** while the plan is Region Server, a monitor loop on `_monitorCts` calls `robust.IsReadyAsync` (now the Grid Asset Server's public port) every 30 s and updates the cached state. `GetStatus` reads only the cache.
- **Checks:** every `_robust!.Process.State != Running` guard becomes a private `GridServiceUp()`: `_robust` running, or the cached Grid Asset Server state.
- **Refusals:** `StartComponentAsync`, `StopComponentAsync` and `RestartComponentAsync("robust")` throw `InvalidOperationException("This computer doesn't run a grid service; it uses the Grid Asset Server's.")`, and `CreateOwnerAsync` throws too.
- **Placement (Region Server only; single-machine plans keep 1000,1000 and local-only checks, with no map call):** `CreateRegionAsync` places the first region at `RegionPlacement.FirstFree(s, await robust.GetMapRegionsAsync(ct))`. `AddRegionAsync`'s `RegionRequestError` passes the map as `elsewhere`.

- [ ] **Step 1: Write the failing tests**, one per behaviour:
  - `Region_server_start_never_starts_robust`: Region Server settings with a fake join that answers. After `StartAsync`, `GetStatus().Robust.State == Running` (Grid Asset Server answered), `RunsRobust == false`, and no `robust.pid` is written.
  - `Region_server_start_rereads_the_join_file`: the fake join returns `GridName = "Avalon 2"` and `Hypergrid = true`. Afterwards settings have both, and the region `OpenSim.ini` includes `GridHypergrid.ini`.
  - `Region_server_start_waits_when_the_grid_asset_server_is_off`: the fake join throws `GridJoinException`. `StartAsync` throws `GridAssetUnreachableException`, `GetStatus().StartFailure!.Message` contains `"192.168.1.5:8002"`, `Database.State == Running`, no program started, and `s.Join` is unchanged.
  - `Region_server_refuses_robust_operations`: start, stop and restart `"robust"` throw with `"doesn't run a grid service"`.
  - `Estate_owner_is_found_by_name` (fake account `"ann" "lee"` → stored as `"Ann" "Lee"` with its ID) and `Estate_owner_not_on_the_grid_is_refused` (message contains `"Avalon"`, settings unchanged).
  - `First_region_avoids_squares_used_elsewhere`: the fake map has Hub at 1000,1000 size 1, so the created region's settings say 1001,1000.
  - `Adding_a_region_on_a_used_square_is_refused_on_a_region_server`: `ArgumentException` naming `"Hub"`.
  - `Joined_region_count_ignores_our_own`: map = our region plus one other → 1.

- [ ] **Step 2: Run them; they fail.**
- [ ] **Step 3: Implement.** Keep the single-machine paths byte-for-byte the same where `RunsRobust` is true; the existing `GridControllerWiringTests` must pass unchanged apart from the new constructor argument.
- [ ] **Step 4: Run the whole unit suite; it passes** (this class takes ~7 min).
- [ ] **Step 5: Commit** — `Region Server: start without Robust, re-read the join file, watch the Grid Asset Server`.

---

### Task 5: Setup flow for a Region Server

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Setup/SetupSteps.cs`
- Create: `Launcher/DreamGrid.Launcher/Pages/Setup/Join.cshtml` and `Join.cshtml.cs` (`/setup/join`)
- Modify: `Launcher/DreamGrid.Launcher/Pages/Setup/Network.cshtml(.cs)` (Region Server variant)
- Modify: `Launcher/DreamGrid.Launcher/Pages/Setup/Avatar.cshtml(.cs)` (by-name variant)
- Modify: `Launcher/DreamGrid.Launcher/Setup/SetupRunner.cs` (owner step on a Region Server)
- Modify: `Launcher/DreamGrid.Launcher.Core/Plans/PlanCatalog.cs` (Region Server `Available = true`; advisories)
- Test: `Launcher/DreamGrid.Launcher.Tests/SetupFlowTests.cs`, `PlanCatalogTests.cs`

**Interfaces:**
- Consumes: `IGridJoinClient`, `JoinAddress`, `IGridController.SetEstateOwnerAsync`, `LauncherSettings.Join`.
- Produces:
  - `SetupSteps.For(s)` for `Plan == RegionServer` returns the steps below. `CanOpen`/`FirstUnfinished` work unchanged.
    1. ("Choose your plan", `/setup/plan`)
    2. ("Join the Grid Asset Server", `/setup/join`, done when `Join is not null`)
    3. ("Your network", `/setup/network`, `NetworkSet`)
    4. ("Your avatar", `/setup/avatar`, `OwnerCreated`)
    5. ("Choose your first region", `/setup/region`, `RegionCreated`)
    6. ("Connect with Firestorm", `/setup/connect`, `Finished`)
  - **Join page** (`/setup/join`), field `Address`.
    - **First post** reads the file and shows what it found: grid name, the Grid Asset Server's DreamGrid version, hypergrid on/off.
    - **Version warning:** when that version differs from `LauncherVersion.Current`, a warning names both versions. It doesn't refuse.
    - **Buttons:** "Join this grid" and "Change the address".
    - **`?handler=Confirm`** re-reads the file (never trusts form fields for its values), then saves. It sets `Join`, `GridName = Join.GridName` and `Setup.WorldNamed = true`, and redirects to `/setup`.
    - **Errors:** a `JoinAddress` error or a `GridJoinException` shows its message, and nothing is saved.
  - **Network page on a Region Server:**
    - **This computer's address:** "On my network" (the detected address) or "From the internet" (a typed public address, checked by `PlanCatalog.AddressError(OwnerPlan.SimplePublic, …)`). It is saved as `Hostname`.
    - **First region port:** `FirstRegionPort` (int, 1024–65535, none of this computer's `WebPort`, `MariaDbPort`, `RegionConsolePort`, `RobustPublicPort`, `RobustPrivatePort`, `RobustConsolePort`), saved as `RegionPort`.
    - **Suggested port:** `Join.RegionPortsTo + 1` when the Grid Asset Server reports ports, else `RegionPort`.
    - **Hypergrid:** no switch. One line instead: "Hypergrid is on/off for this grid; the Grid Asset Server decides."
    - **Advisories:** the Region Server's.
  - **Avatar page on a Region Server:**
    - **Title:** "Your avatar".
    - **Fields:** first and last name only.
    - **Help text:** "Type the name of your avatar on {GridName}. It becomes the owner of the regions on this computer. No password is needed here."
    - **On success:** calls `SetupRunner.TrySetEstateOwnerAsync` (start the database if needed, then `SetEstateOwnerAsync`). Then it clears the setup token and signs the owner in, exactly as the create path does today.
    - **Errors:** the `ArgumentException` or `GridAssetUnreachableException` message is shown as is.
  - Region Server advisories gain: "Every computer on your network can reach the Grid Asset Server's private port; anyone on your network can add regions to the grid." The same sentence goes in the Grid Asset Server's list too.

- [ ] **Step 1: Write the failing tests** (page tests with a fake `IGridJoinClient` registered in the test host):
  - `Region_server_card_can_be_chosen`: posting `/setup/plan/region-server` saves `Plan = RegionServer` and redirects to `/setup`, which goes to `/setup/join`.
  - `Join_shows_what_it_found_then_saves_on_confirm`: the first post shows "Avalon" and "Hypergrid: off" and saves nothing. Confirm saves `Join`, `GridName == "Avalon"` and `WorldNamed`, and the next step is `/setup/network`.
  - `Join_warns_on_a_different_version`: the page contains both version strings, and Confirm still works.
  - `Join_refuses_a_bad_address` (`"192.168.1.5:70000"`) and `Join_shows_why_it_could_not_read` (the fake throws `GridJoinException("Nothing answered at …")`): the message is shown and `Join` stays null.
  - `Region_server_network_suggests_a_port_after_the_grid_asset_servers`: with `RegionPortsTo = 8006`, the field shows `8007`. Posting `8007` with "On my network" saves `RegionPort = 8007` and `Hostname = null`. Posting `8001` (the web port) is refused.
  - `Region_server_network_has_no_hypergrid_switch`: no `Hypergrid` input, and the text "the Grid Asset Server decides".
  - `Region_server_avatar_is_typed_by_name`: no password field. Posting "ann lee" (the fake finds "Ann Lee") saves the owner and signs in.
  - `Region_server_avatar_not_found_is_shown`: the message contains `"There's no avatar called"`.
  - `Region_server_skips_name_your_world`: `/setup/world` is not in the steps, and opening it redirects to `/setup`.
  - `PlanCatalogTests`: `RegionServer.Available == true`, and both advisory lists contain `"private port"`.
- [ ] **Step 2: Run them; they fail.**
- [ ] **Step 3: Implement.** The Join page reuses the setup layout and checklist. All saves go through `grid.UpdateSettings(…, writeConfig: true)`.
- [ ] **Step 4: Run the whole unit suite; it passes.**
- [ ] **Step 5: Commit** — `Region Server: join, network and avatar steps`.

---

### Task 6: Sign-in, dashboard and Advanced settings

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Security/AdminRule.cs`
- Modify: `Launcher/DreamGrid.Launcher/Pages/Login.cshtml.cs`, `Launcher/DreamGrid.Launcher.Core/Api/LegacyCommandApi.cs`
- Modify: `Launcher/DreamGrid.Launcher/Endpoints/StatusApi.cs`, `Pages/Index.cshtml(.cs)`
- Modify: `Launcher/DreamGrid.Launcher/Pages/Advanced/Plan.cshtml(.cs)`; create `Pages/Advanced/Join.cshtml(.cs)` (`/advanced/join`)
- Test: `Launcher/DreamGrid.Launcher.Tests/LoginTests.cs` (or wherever sign-in is tested), `AdvancedSettingsTests.cs`, the dashboard/status tests

**Interfaces:**
- Produces:
  - `public static class AdminRule { public const int AdminLevel = 200; public static bool MayAdminister(LauncherSettings s, RobustAccount a); }`: `a.UserLevel >= 200`, or, on a Region Server, `a.PrincipalId == s.Setup.Owner?.PrincipalId`. `Login.AdminLevel` points at `AdminRule.AdminLevel`.
  - Status and dashboard on a Region Server:
    - the `Robust` row is labelled `"Grid Asset Server"`, with the detail `"{Join.Host}:{Join.PublicPort}"`;
    - the "Restart the grid service" button is not shown;
    - the problem text for a failed row reads `"The Grid Asset Server isn't answering."`.
  - `/advanced/join` is the Join page's form for a finished Region Server. It does the same read, show and confirm, and saving rewrites the INIs. The spec §8 "point at a different Grid Asset Server" ✔ links here from `/advanced/plan`.
  - `/advanced/plan`, from Grid Asset Server to Local or Simple Public: before saving, `JoinedRegionCountAsync`. When it is above 0, it refuses with `"{n} region(s) from other computers are joined to this grid. Move them to another grid or shut those computers down first."`. When Robust doesn't answer, it refuses with `"Start your world first, so DreamGrid can check whether other computers have joined."`.
  - The "Coming soon" list on the dashboard no longer names the Region Server.

- [ ] **Step 1: Write the failing tests:**
  - `AdminRule`:
    - level 200 on any plan → true;
    - level 0 owner on a Region Server → true;
    - level 0 owner on Local → false;
    - level 0 non-owner on a Region Server → false.
  - Sign-in page on a Region Server with a fake `IRobustClient`: the level-0 estate owner signs in.
  - Status JSON on a Region Server: the label is `"Grid Asset Server"`. The dashboard HTML has no `restart-robust` button.
  - Advanced plan, from Grid Asset to Local: with a fake count of 2, it is refused with `"2 region(s)"` and the plan is unchanged; with 0, it is saved.
  - `/advanced/join` on a finished Region Server: confirm saves the new `Join`. On another plan the page redirects to `/advanced`.
- [ ] **Step 2: Run them; they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the whole unit suite; it passes.**
- [ ] **Step 5: Commit** — `Region Server: sign-in, dashboard and Advanced settings`.

---

### Task 7: Two launchers end to end, and owner docs

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.IntegrationTests/WebsiteEndToEndTests.cs` (helpers may move to a shared file if they grow)
- Modify: `content/README.md` (Region Server line), `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` (6b done)

- [ ] **Step 1: Write the integration test** `Region_server_joins_a_grid_asset_server_and_boots_a_region` (needs MariaDB like the others; skipped locally without it, required in CI):
  1. **Launcher A**, Grid Asset Server, LAN, hypergrid off: walk setup with world "Avalon", owner "Test Owner", and a region "Hub" (not Skip, so the map has a region at 1000,1000). Wait for Ready.
  2. **Launcher B**, a second data folder with its own free ports: walk the Region Server setup.
     - Join `127.0.0.1:{A.RobustPublicPort}` and confirm.
     - Network "On my network", first region port = a free port.
     - Avatar "Test Owner".
     - Region "Isle".
     - Wait for Ready.
  3. **Assert:**
     - B's settings have `Join.GridName == "Avalon"`;
     - B's "Isle" is at `(1001, 1000)`;
     - B has no `robust.pid`;
     - A's `get_region_range` (`IRobustClient.GetMapRegionsAsync` against A) lists both "Hub" and "Isle";
     - B's `/api/status` reports logins enabled;
     - signing in to B's website as "Test Owner" with A's password succeeds.
  4. **Stop launcher A's world** (dashboard form post `gridAction=stop`). Stop B's world, then start it again (`gridAction=stop`, then `gridAction=start`). B's `/api/status` start failure contains `"isn't answering"`, and B's `Join` is unchanged.
- [ ] **Step 2: Run it in CI** (push the branch; the Debian launcher jobs run integration tests). Locally, check that it builds and skips cleanly without MariaDB.
- [ ] **Step 3: Update the docs:** the README line for Region Server says what it needs (Grid Asset Server running, same version, its address), and the worklist marks 6b done with 6c next.
- [ ] **Step 4: Commit** — `Region Server: end-to-end test with two launchers, owner docs`.
