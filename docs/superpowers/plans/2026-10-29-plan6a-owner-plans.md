# Plan 6a — Owner plans: cards, info pages, network step, hypergrid switch

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** First-use setup starts with the five plan cards and their ⓘ pages; the three single-machine plans (Local, Simple Public Server, Grid Asset Server) can be chosen, set up and changed later, with a hypergrid on/off switch.

**Architecture:** The plan and the hypergrid switch are two new settings (schema 4). A static catalog in Core holds each plan's text, advisories, hypergrid capability and the change-later matrix; the setup pages and Advanced settings read it. `GridConfigWriter` turns "hypergrid off" into OpenSim's non-hypergrid region architecture plus a closed gatekeeper.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, xUnit v3, AngleSharp (page tests), the existing `IniDocument`.

**Spec:** `docs/superpowers/specs/2026-10-29-install-and-plans-design.md`

**Scope split (spec is three pieces):**
- **6a (this plan):** plan model, cards and ⓘ pages for all five plans, setup flow and Advanced for Local / Simple Public Server / Grid Asset Server, hypergrid switch, missing-part message, migration.
- **6b (next plan):** Region Server: the join file (or extra `get_grid_info` keys), Grid Asset Server's private port open to the LAN, two addresses, region port ranges, estate owner by name, re-read on start, dashboard without Robust.
- **6c (after):** Connected Server (OSgrid and similar, from `Proto/OsGrid`).
- In 6a the Region Server and Connected Server cards and ⓘ pages are shown, but their "Choose this plan" button is disabled with the text "Coming in a later DreamGrid update."

## Global Constraints

- Plan names exactly as the owner gave them: "Local" — "Just me & friends (LAN)"; "Simple Public Server" — "(WAN)"; "Grid Asset Server" — "(LAN or WAN)"; "Region Server" — "Needs a Grid Asset Server"; "Connected Server" — "Uses someone else's grid".
- Every settings change goes through `IGridController.UpdateSettings(…, writeConfig: true)`; config is written only by `GridConfigWriter`.
- The website never needs administrator rights; nothing here installs software.
- Windows and Debian 12/13 behave the same; tests run on both in CI.
- Plain words for owners: no INI names, no "Robust" on owner-facing pages except where the plan card says "Runs: …" (use "database · accounts & inventory (Robust) · regions · this website").
- Hypergrid is **off** for new installs (spec §9).
- Settings `CurrentSchema` becomes `4`.

## Review Focus

1. **An existing finished v7 install upgrades** (schema 3 file, `Setup.Finished = true`) — it must land on the dashboard, not the cards page, with Plan set per spec §12 and its INIs unchanged when it is Simple Public with hypergrid on. Test in Task 1 and Task 3.
2. **An install part-way through setup** (schema 3, `WorldNamed = true`, owner not created) — next visit to `/setup` shows the cards; after choosing, the world-name step shows as done. Test in Task 4.
3. **Simple Public Server with a LAN address typed in** (`192.168.x.x`, `10.x`, `100.64.x.x`, `127.0.0.1`) — refused with a plain message, not saved. Test in Task 5.
4. **Turning hypergrid on while the grid address is LAN-only** (Advanced, Grid Asset Server LAN-only, or a hostname later changed to a private IP) — switch refused / config writes hypergrid off. Test in Task 3 and Task 6.
5. **Posting a disabled plan** (`region-server`, `connected-server`, or an unknown id) to the choose handler by hand — refused, nothing saved. Test in Task 4.

---

### Task 1: Plan and hypergrid settings, schema 4 migration

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Settings/OwnerPlan.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs` (record `LauncherSettings`, record `SetupState`)
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/SettingsStore.cs` (`Migrate`)
- Modify: `Launcher/DreamGrid.Launcher.Core/Config/HostAddress.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/SettingsStoreTests.cs`, `Launcher/DreamGrid.Launcher.Tests/HostAddressTests.cs` (new)

**Interfaces:**
- Produces:
  - `public enum OwnerPlan { Local, SimplePublic, GridAsset, RegionServer, Connected }` (JSON as names, via `[JsonConverter(typeof(JsonStringEnumConverter<OwnerPlan>))]` on the enum).
  - `LauncherSettings.Plan` : `OwnerPlan?` (null = not chosen yet), `LauncherSettings.Hypergrid` : `bool` (default `false`).
  - `SetupState` gains `bool NetworkSet = false` and `bool RegionSkipped = false` (appended after `Owner`, so existing positional calls keep compiling).
  - `HostAddress.IsPublic(string? configured) : bool` — false for null/empty (the detected LAN address), for loopback, RFC 1918 (`10/8`, `172.16/12`, `192.168/16`), link-local (`169.254/16`), CGNAT (`100.64/10`), IPv6 loopback/link-local/unique-local (`fc00::/7`); true for any other IP and for any DNS name.

- [ ] **Step 1: Write the failing tests**

`HostAddressTests.IsPublic` as a `[Theory]`: `null`, `""`, `"192.168.1.20"`, `"10.0.0.5"`, `"172.20.1.1"`, `"100.64.3.4"`, `"127.0.0.1"`, `"169.254.1.1"`, `"fd00::1"` → false; `"203.0.113.7"`, `"8.8.8.8"`, `"172.32.0.1"`, `"mygrid.example.org"`, `"2001:db8::1"` → true.

`SettingsStoreTests`:
- `Schema_3_finished_with_public_hostname_becomes_simple_public_with_hypergrid_on` — write a schema 3 JSON with `"hostname":"203.0.113.7"`, `setup.finished = true`; load: `Plan == OwnerPlan.SimplePublic`, `Hypergrid == true`, `SchemaVersion == 4`.
- `Schema_3_finished_with_lan_or_no_hostname_becomes_local_with_hypergrid_off` — hostname `null` and `"192.168.1.20"` cases: `Plan == OwnerPlan.Local`, `Hypergrid == false`.
- `Schema_3_unfinished_keeps_plan_unset` — `setup.worldNamed = true`, `finished = false`: `Plan == null`, `Setup.WorldNamed == true`.
- `New_settings_have_no_plan_and_hypergrid_off`.
- `Plan_round_trips_as_its_name` — save `Plan = GridAsset`; the JSON text contains `"plan": "GridAsset"`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Launcher/DreamGrid.Launcher.Tests --filter "FullyQualifiedName~SettingsStoreTests|FullyQualifiedName~HostAddressTests"`
Expected: compile errors (`OwnerPlan`, `IsPublic` missing).

- [ ] **Step 3: Implement**

`CurrentSchema = 4`. In `Migrate`, after the schema 2→3 step, schema 3→4: when `Setup.Finished`, `Plan = HostAddress.IsPublic(s.Hostname) ? SimplePublic : Local` and `Hypergrid = Plan == SimplePublic`; otherwise leave both at their defaults. `IsPublic` uses `IPAddress.TryParse`; anything that doesn't parse is a name and counts as public.

- [ ] **Step 4: Run the tests to see them pass**

Same command. Expected: PASS, and the full `SettingsStoreTests` class still passes.

- [ ] **Step 5: Commit**

```bash
git add Launcher/DreamGrid.Launcher.Core Launcher/DreamGrid.Launcher.Tests/SettingsStoreTests.cs Launcher/DreamGrid.Launcher.Tests/HostAddressTests.cs
git commit -m "Settings schema 4: owner plan and hypergrid switch, migrated from finished installs"
```

---

### Task 2: The plan catalog and change-later rules

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Plans/PlanCatalog.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/PlanCatalogTests.cs`

**Interfaces:**
- Consumes: `OwnerPlan`, `HostAddress.IsPublic`.
- Produces:
  - `public enum HypergridCapability { None, Switch, FollowsGridAsset, OtherGrid }`
  - `public sealed record PlanInfo(OwnerPlan Plan, string Id, string Icon, string Title, string Subtitle, string Summary, string Details, IReadOnlyList<string> Runs, IReadOnlyList<string> YouNeed, HypergridCapability Hypergrid, string HypergridLine, IReadOnlyList<string> Advisories, bool Available)`
  - `public static class PlanCatalog`:
    - `IReadOnlyList<PlanInfo> All` (card order: Local, SimplePublic, GridAsset, RegionServer, Connected)
    - `PlanInfo? ById(string? id)` — ids `local`, `simple-public`, `grid-asset`, `region-server`, `connected-server`
    - `PlanInfo Get(OwnerPlan plan)`
    - `(bool Allowed, string Reason) CanChange(OwnerPlan from, OwnerPlan to)`
    - `bool HypergridAllowed(LauncherSettings s)` — `s.Plan is SimplePublic or GridAsset && HostAddress.IsPublic(s.Hostname)`
    - `bool HypergridOn(LauncherSettings s)` — `s.Hypergrid && HypergridAllowed(s)`

Copy is the builder's, from spec §5, §7 and §9, in plain words. Two lines are fixed:
- Grid Asset Server advisory includes "You can run regions here too, but it isn't advised: busy regions slow down logins, inventory and the map for everyone on the grid."
- `RegionServer` and `Connected` have `Available = false`.

The change matrix (spec §8), `from` rows → `to` columns; ✔ = allowed:

| | Local | SimplePublic | GridAsset | RegionServer | Connected |
|---|---|---|---|---|---|
| Local | — | ✔ | ✔ | ✘ | ✘ |
| SimplePublic | ✔ | — | ✔ | ✘ | ✘ |
| GridAsset | ✔ | ✔ | — | ✘ | ✘ |
| RegionServer | ✘ | ✘ | ✘ | — | ✘ |
| Connected | ✘ | ✘ | ✘ | ✘ | — |

The ✘ reason (one sentence, shown to owners): moving away from a plan that holds accounts and inventory, or to one that never had them, means starting a new install. A Grid Asset Server's "once no region servers are joined" condition arrives in 6b; note it in a comment at the matrix.

- [ ] **Step 1: Write the failing tests**
  - `Matrix_matches_the_spec` — a `[Theory]` over all 25 pairs with the table above; same-plan pairs return `Allowed == false`.
  - `Ids_are_unique_and_round_trip` — `ById(Get(p).Id)!.Plan == p` for every plan; `ById("nope") == null`.
  - `Only_single_machine_plans_are_available_in_6a`.
  - `Hypergrid_capability_per_plan` — Local `None`, SimplePublic/GridAsset `Switch`, RegionServer `FollowsGridAsset`, Connected `OtherGrid`.
  - `HypergridOn_needs_switch_capable_plan_and_public_address` — `GridAsset` + `Hostname "192.168.1.20"` + `Hypergrid true` → false; same with `"203.0.113.7"` → true; `Local` + public + `true` → false; `Plan null` → false.
  - `Every_plan_has_advisories_and_runs` — non-empty lists, non-empty `Summary`.

- [ ] **Step 2: Run to see them fail** — `dotnet test Launcher/DreamGrid.Launcher.Tests --filter FullyQualifiedName~PlanCatalogTests` → compile errors.
- [ ] **Step 3: Implement `PlanCatalog`** as above.
- [ ] **Step 4: Run to see them pass.**
- [ ] **Step 5: Commit** — `git commit -m "Plan catalog: five plans, their text, hypergrid capability and change rules"`

---

### Task 3: Hypergrid off in the generated config

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Config/GridConfigWriter.cs` (`Write`, `WriteProgram`)
- Test: `Launcher/DreamGrid.Launcher.Tests/GridConfigWriterTests.cs`

**Interfaces:**
- Consumes: `PlanCatalog.HypergridOn(LauncherSettings)`.

When `HypergridOn(s)` is false, the writer sets:
- each program's `OpenSim.ini`: `[Architecture] Include-Architecture = "config-include/Grid.ini"` (quoted, as the prototype writes it);
- `Robust.HG.ini`: `[GatekeeperService] ForeignAgentsAllowed = false`;
- `GridCommon.ini`: `[GridService] AllowHypergridMapSearch = false`.

When true, it writes `Include-Architecture = "config-include/GridHypergrid.ini"`, `ForeignAgentsAllowed = true`, `AllowHypergridMapSearch = true`. Owner `Overrides` still apply last (unchanged behaviour). Note that today `GridCommon.ini` is written with `applyExtra: false`; that stays.

- [ ] **Step 1: Write the failing tests** (the `Make()` helper gains `OwnerPlan? plan = null, bool hypergrid = false`; it keeps `Hostname = "192.168.1.50"`, so add a `hostname` parameter):
  - `Hypergrid_off_by_default_uses_grid_architecture_and_closes_the_gatekeeper` — `Make()`: region `Get("Architecture","Include-Architecture")` contains `config-include/Grid.ini`; Robust `ForeignAgentsAllowed == "false"`; GridCommon `AllowHypergridMapSearch == "false"`.
  - `Hypergrid_on_for_a_public_simple_server` — `plan: SimplePublic, hypergrid: true, hostname: "203.0.113.7"`: `GridHypergrid.ini`, `true`, `true`.
  - `Hypergrid_switch_ignored_on_a_lan_address` — `plan: GridAsset, hypergrid: true, hostname: "192.168.1.50"` → the off values.
  - `Migrated_public_install_keeps_hypergrid_config` — `plan: SimplePublic, hypergrid: true, hostname: "203.0.113.7"`: the region's `Include-Architecture` equals the prototype's own value (`"config-include/GridHypergrid.ini"`), so an upgraded grid writes the same architecture as before.
- [ ] **Step 2: Run to see them fail** — `dotnet test Launcher/DreamGrid.Launcher.Tests --filter FullyQualifiedName~GridConfigWriterTests`.
- [ ] **Step 3: Implement** in `Write` (Robust, GridCommon) and `WriteProgram` (architecture).
- [ ] **Step 4: Run the whole `GridConfigWriterTests` class** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "Config: hypergrid off writes the non-hypergrid architecture and a closed gatekeeper"`

---

### Task 4: Setup starts with the plan cards and ⓘ pages

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Setup/SetupSteps.cs`
- Modify: `Launcher/DreamGrid.Launcher/Pages/Setup/_Checklist.cshtml`
- Create: `Launcher/DreamGrid.Launcher/Pages/Setup/Plan.cshtml(.cs)` (route `/setup/plan`), `Launcher/DreamGrid.Launcher/Pages/Setup/PlanInfo.cshtml(.cs)` (route `/setup/plan/{id}`), `Launcher/DreamGrid.Launcher/Pages/Shared/_PlanDiagram.cshtml` (model `OwnerPlan`), `Launcher/DreamGrid.Launcher/Pages/Shared/_PlanDetails.cshtml` (model `PlanInfo`: summary, diagram, you'll need, hypergrid line, can-change list, advisories — shared with Task 6)
- Create: `Launcher/DreamGrid.Launcher.Core/Plans/PlanParts.cs`
- Modify: `Launcher/DreamGrid.Launcher/wwwroot/css/site.css` (cards grid)
- Test: `Launcher/DreamGrid.Launcher.Tests/SetupFlowTests.cs`, `Launcher/DreamGrid.Launcher.Tests/PlanPartsTests.cs`; update setup walks in `Launcher/DreamGrid.Launcher.IntegrationTests/WebsiteEndToEndTests.cs` (and any other test that posts `/setup/world` first) to choose `local` and post the network step before naming the world.

**Interfaces:**
- Consumes: `PlanCatalog`, `OwnerPlan`, `SetupState.NetworkSet`, `SetupState.RegionSkipped`, `MariaDbBinaries.NotFoundMessage`.
- Produces:
  - `SetupSteps.For(LauncherSettings s) : IReadOnlyList<(string Title, string Path, Func<LauncherSettings,bool> Done)>` replacing `All` / `IsDone(SetupState,int)`; with plan null or Local/SimplePublic/GridAsset the steps are: "Choose your plan" `/setup/plan` (done: `Plan != null`), "Your network" `/setup/network` (done: `Setup.NetworkSet`), "Name your world", "Create your avatar", "Choose your first region" (done: `RegionCreated || RegionSkipped`), "Connect with Firestorm". `CanOpen(s, step)` and `FirstUnfinished(s)` keep their meaning, taking `LauncherSettings`.
  - `PlanParts.Missing(OwnerPlan plan, MariaDbBinaries maria) : string?` — null when every part the plan needs is present; otherwise the owner text of spec §11: names the plan and the part ("the database, where avatars and inventory are kept"), says where DreamGrid looked (the first line of `NotFoundMessage`), that the website can't install programs because it runs without administrator rights, and "Run the DreamGrid installer again: it puts the missing part back and keeps your settings and world." In 6a the only checked part is MariaDB, for all three available plans.
  - Choose handler: `POST /setup/plan/{id}?handler=Choose`.

Callers of the old `SetupSteps.All`/`IsDone` (`_Checklist`, `Index`, each step page's `CanOpen` check) switch to `For(settings())`.

The diagrams are inline SVG (spec §6.2), one per plan, with "This computer" highlighted, matching the brainstorm mockups: Local (friends' PCs → LAN → this computer), Simple Public (visitors → router → this computer; LAN friends → this computer), Grid Asset (visitors → router → this computer; region servers on the LAN → this computer), Region Server (visitors → router → Grid Asset Server and this computer; this computer → Grid Asset Server over LAN), Connected (other grid ← internet ← this computer; visitors → router → this computer). Colours from the site's CSS variables so dark mode works.

- [ ] **Step 1: Write the failing tests** (`SetupFlowTests`):
  - `Setup_link_lands_on_the_plan_cards` — fresh factory, follow `/setup?token=…` → `/setup` → redirect `Location == "/setup/plan"`; the page has 5 `.plan-card` elements, in catalog order, each with an `a[href="/setup/plan/<id>"]`.
  - `Plan_info_page_shows_diagram_advisories_and_change_rules` — GET `/setup/plan/grid-asset`: contains an `svg`, the "isn't advised" advisory, and a ✘ line for Region Server.
  - `Choosing_local_saves_the_plan_and_moves_to_network` — post Choose for `local`: settings `Plan == Local`; `Location == "/setup"`; next GET `/setup` → `/setup/network`.
  - `Choosing_an_unavailable_or_unknown_plan_is_refused` — post Choose for `region-server`, `connected-server`, `nope`: 400 or the page with an error; `Plan` stays null.
  - `Missing_database_shows_why_and_saves_nothing` — factory with `MariaDbBinaries.NotFound("MariaDB server not found; searched: X")`: choosing `local` shows text containing "Run the DreamGrid installer again" and "administrator"; `Plan` stays null.
  - `Half_done_v7_setup_resumes_at_the_cards_with_world_name_kept` — factory with `Setup = new SetupState(WorldNamed: true)`, `Plan = null`: `/setup` → `/setup/plan`; after choosing `local` and posting the network step, `/setup` → `/setup/avatar` (world step done).
  - `PlanPartsTests`: `Nothing_missing_returns_null`; `Missing_mariadb_names_the_part_and_the_installer`.
- [ ] **Step 2: Run to see them fail** — `dotnet test Launcher/DreamGrid.Launcher.Tests --filter "FullyQualifiedName~SetupFlowTests|FullyQualifiedName~PlanPartsTests"`.
- [ ] **Step 3: Implement** the steps model, pages, partials, `PlanParts`, and update existing setup walks in unit and integration tests.
- [ ] **Step 4: Run** `dotnet test Launcher/DreamGrid.Launcher.Tests` — all PASS.
- [ ] **Step 5: Commit** — `git commit -m "Setup: plan cards and info pages come first"`

---

### Task 5: The network step

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Setup/Network.cshtml(.cs)` (route `/setup/network`)
- Create: `Launcher/DreamGrid.Launcher/Pages/Shared/_HypergridSwitch.cshtml` (shared with Task 6: the switch, the one-sentence explanation, the "Before you turn it on" box)
- Test: `Launcher/DreamGrid.Launcher.Tests/SetupFlowTests.cs`

**Interfaces:**
- Consumes: `PlanCatalog.Get`, `HostAddress.Detect/IsPublic`, `SettingsValidator.HostnameError`.
- Form fields: `AddressKind` (`lan` | `public`), `PublicAddress`, `Hypergrid` (checkbox).

Per plan:
- **Local:** shows the detected LAN address and Local's advisories; Continue saves `Hostname = null`, `Hypergrid = false`, `NetworkSet = true`.
- **Simple Public Server:** `PublicAddress` required; must pass `HostnameError` and `IsPublic`, else the error "That's an address only computers on your network can reach. Enter your public IP address or your grid's name." The hypergrid switch is shown. Saves `Hostname`, `Hypergrid`, `NetworkSet`.
- **Grid Asset Server:** `AddressKind` radio, LAN preselected. LAN: as Local, hypergrid forced off with the line "Hypergrid needs a public address." Public: as Simple Public.

Router advice lists the ports to forward from current settings: `RobustPublicPort` and the region port range starting at `RegionPort` (plain text, e.g. "8002, and 8004 upward for regions").

- [ ] **Step 1: Write the failing tests:**
  - `Network_step_for_simple_public_refuses_lan_addresses` — `[Theory]` `192.168.1.20`, `10.0.0.5`, `100.64.0.9`, `127.0.0.1`: response shows the error; `NetworkSet` stays false.
  - `Network_step_for_simple_public_saves_address_and_switch` — `203.0.113.7`, `Hypergrid=on`: `Hostname == "203.0.113.7"`, `Hypergrid == true`, `NetworkSet == true`.
  - `Network_step_for_grid_asset_lan_forces_hypergrid_off` — `AddressKind=lan`, `Hypergrid=on`: `Hypergrid == false`, `Hostname == null`.
  - `Network_step_for_local_has_no_switch` — page has no `input[name=Hypergrid]`.
  - `Router_advice_names_the_forwarded_ports` — Simple Public page contains `RobustPublicPort` and `RegionPort` values.
- [ ] **Step 2: Run to see them fail.**
- [ ] **Step 3: Implement** the page and the shared switch partial.
- [ ] **Step 4: Run** `dotnet test Launcher/DreamGrid.Launcher.Tests --filter FullyQualifiedName~SetupFlowTests` — PASS.
- [ ] **Step 5: Commit** — `git commit -m "Setup: network step with address choice and hypergrid switch"`

---

### Task 6: Grid Asset Server's optional region, and the Plan page in Advanced

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Pages/Setup/Region.cshtml(.cs)` — for `GridAsset`, a "Skip: no regions on this computer" button (`?handler=Skip`) shown first and the advisory; saves `RegionSkipped = true`.
- Create: `Launcher/DreamGrid.Launcher/Pages/Advanced/Plan.cshtml(.cs)` (route `/advanced/plan`)
- Modify: `Launcher/DreamGrid.Launcher/Pages/Advanced/Index.cshtml` (link to the Plan page, first in the list)
- Test: `Launcher/DreamGrid.Launcher.Tests/AdvancedSettingsTests.cs`, `Launcher/DreamGrid.Launcher.Tests/SetupFlowTests.cs`

**Interfaces:**
- Consumes: `_PlanDetails`, `_HypergridSwitch`, `PlanCatalog.CanChange`, `PlanCatalog.HypergridAllowed`.
- Handlers: `POST ?handler=Change` with `To` (plan id); `POST ?handler=Hypergrid` with `Hypergrid`.

The Advanced Plan page shows the current plan through `_PlanDetails`, then a "Change plan" list: allowed targets as buttons, refused ones with ✘ and the reason. Changing to Simple Public when `!IsPublic(Hostname)` saves the plan and redirects to `/advanced/network` with the note "Enter your public address to finish the change." Changing to Local sets `Hypergrid = false`. The hypergrid switch is shown only when the plan's capability is `Switch`; disabled with "Hypergrid needs a public address" when `!HypergridAllowed`. Saving shows the same "restart your world" notice the Network page uses (`Network.cshtml.cs` `SavedNotes`).

- [ ] **Step 1: Write the failing tests:**
  - `Grid_asset_region_step_can_be_skipped` (SetupFlowTests) — plan `GridAsset`, post Skip: `RegionSkipped == true`; `/setup` → `/setup/connect`.
  - `Region_step_has_no_skip_for_local` — no Skip button.
  - `Advanced_plan_page_lists_allowed_and_refused_changes` — plan `SimplePublic`: buttons for Local and Grid Asset; ✘ lines for Region Server and Connected Server.
  - `Changing_to_a_refused_plan_by_hand_saves_nothing` — post `To=region-server`: `Plan` unchanged.
  - `Changing_local_to_simple_public_without_public_address_sends_to_network` — `Location == "/advanced/network"`, `Plan == SimplePublic`.
  - `Hypergrid_switch_refused_without_public_address` — plan `GridAsset`, hostname null, post `Hypergrid=on`: `Hypergrid` stays false, page shows the reason.
  - `Hypergrid_switch_saves_and_rewrites_config` — public Simple Public, post on: `Hypergrid == true`, `FakeGridController` saw `writeConfig: true`.
- [ ] **Step 2: Run to see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `dotnet test Launcher/DreamGrid.Launcher.Tests` — all PASS.
- [ ] **Step 5: Commit** — `git commit -m "Advanced: plan page with change rules and hypergrid switch; Grid Asset Server can skip regions"`

---

### Task 7: End to end and docs

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.IntegrationTests/WebsiteEndToEndTests.cs`
- Modify: `content/README.md` (owner-facing: the five plans in two lines each, hypergrid switch, "Region Server and Connected Server arrive in a later update")
- Modify: `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` (note under Phase 1's on-hold line: "6a done; 6b Region Server, 6c Connected Server next")

- [ ] **Step 1: Write the failing integration test** `Grid_asset_lan_setup_with_hypergrid_off_boots_a_region` — real launcher: choose `grid-asset`, network `AddressKind=lan`, name, avatar, then create the region (not Skip); the region reaches LOGINS ENABLED, and its `OpenSim.ini` names `config-include/Grid.ini`. A LAN address, because a made-up public address would become the services' `BaseURL` and the region could not reach Robust on a test machine. The `MariaDbBinaries` override for Task 4's test goes through `LauncherFactory.ExtraServices`.
- [ ] **Step 2: Run to see it fail, then pass after Tasks 1–6** — `dotnet test Launcher/DreamGrid.Launcher.IntegrationTests --filter FullyQualifiedName~WebsiteEndToEndTests` (needs MariaDB as the existing integration tests do).
- [ ] **Step 3: Update the README and the worklist.**
- [ ] **Step 4: Full run** — `dotnet build -c Release DreamGrid.sln` then `dotnet test Launcher/DreamGrid.Launcher.Tests` and the integration suite; all PASS. Push the branch so CI runs Windows and Debian 12/13.
- [ ] **Step 5: Commit** — `git commit -m "Owner plans: end-to-end test and owner docs"`
