# Install and owner plans — design

Decided in conversation with the owner on 2026-10-29. Marked **(owner)** where the owner decided, **(my call)** where the designer did; everything else is left to the builder. This comes before Phase 1 of `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`, which is on hold until this is built.

## 1. Why

DreamGrid v7 today has one shape: the whole grid (MariaDB, Robust, regions, the launcher website) on one machine, set up by a four-step checklist in the browser. Owners actually want different things: a private world on the LAN, a public grid, a grid spread over several computers, or regions on someone else's grid such as OSgrid. Old DreamGrid could do most of these (grid server, region server, OSgrid and Joomla prototypes in `bin/config-include/Proto/`), but v7 can't.

Later work (HTTPS, resident sign-up, the public WAN website) depends on which of these the owner runs. **(owner)** So first find out the owner's plan, explain it, then set the machine up for it.

## 2. The idea in one paragraph

**(owner)** The installer installs one standard setup and asks nothing about plans. The browser then opens on the setup link. The first page shows the plans as cards; each card has an ⓘ that opens a page about that layout with a diagram. The owner picks a card, the website collects what that plan needs, then turns on and configures the parts that plan uses. The usual setup steps follow, minus those the plan doesn't need. Some things can be changed in the browser afterwards.

## 3. Goals

- Five plans, picked in the browser on first use (§5).
- Each plan explains itself: what it is, a diagram, what it needs, whether hypergrid is possible, what can change later, and advisories (§6).
- **(owner)** Every plan is a target: once built, all five work. No plan is a stepping stone that gets thrown away.
- **(owner)** Changing plan later: if the change is simple, it is allowed (in the browser); if starting from scratch is better, it is not, and the plan's page says so (§8).
- **(owner)** Hypergrid is shown per plan as capable or not, with one on/off switch where capable (§9).
- A Region Server finds out how to join its Grid Asset Server by reading a file the Grid Asset Server publishes, so the owner types only an address (§10).

## 4. Non-goals and anti-goals

**Non-goals (now; see §14, future notes):** HTTPS; the public WAN website; the outside reachability tester; router and firewall automation; security on joining a Grid Asset Server; fine-grained hypergrid controls; Joomla grids.

**Anti-goals:**
- An owner who picks a plan without understanding what it means for their network and their data.
- A plan change that silently loses accounts, inventory or regions.
- Breaking existing v7 installs on upgrade (§12).
- The website needing administrator rights.

## 5. The five plans (owner's names)

| Card | What this computer runs | Who it's for |
|---|---|---|
| 🏠 **Local** — Just me & friends (LAN) | MariaDB, Robust, regions, website | A private world on the home or office network. |
| 🌐 **Simple Public Server** (WAN) | MariaDB, Robust, regions, website | A whole grid on one computer, open to visitors from the internet. |
| 🗄️ **Grid Asset Server** (LAN or WAN) — database, Robust, public website | MariaDB, Robust, website; regions optional, **not advised (owner)** | The heart of a grid spread over several computers. |
| 🏝️ **Region Server** — needs a Grid Asset Server | MariaDB (region data only), regions, website | Adds land to a grid whose Grid Asset Server is on another computer on the LAN. |
| 🔗 **Connected Server** — uses someone else's grid | MariaDB (region data only), regions, website | Regions on another grid (OSgrid and similar). That grid keeps the accounts. |

**(my call)** Region Server and Connected Server still run their own MariaDB: OpenSim keeps each region's own data (objects, terrain, parcels) in the `opensim` database through `[DatabaseService]` in GridCommon.ini, while accounts, inventory and assets come from the grid's Robust. Only Robust stays off. This keeps region data, backups and OAR handling as they are today, and needs no database password to cross the LAN.
  *Checked 2026-10-29:* the region-server prototype `bin/config-include/Proto/Region/Grid.ini` runs `[SimulationDataStore]` and `[EstateDataStore]` locally, and its `GridCommon.ini` gives them a MySQL `[DatabaseService]`; the OpenSim wiki (Configuration, Database Settings) says the same: in grid mode only the data services (accounts, inventory, assets) move to Robust.

- "Public website" on the Grid Asset Server card means, for now, the same local website on the web port. The real public WAN website is future work (§14). The card text must not promise more than the local site.
- **(owner)** The Grid Asset Server can be **LAN-only or public**: its network step offers either address. LAN-only gives a private world spread over several computers; hypergrid is possible only with a public address.
- **(owner)** The Grid Asset Server may also run regions; its page advises against it (busy regions slow logins, inventory and the map for the whole grid). Its "first region" step is optional and starts unticked. **(my call)**
- Local and Simple Public Server run the same parts. The difference is the address the grid gives out (LAN address vs public address) and whether hypergrid is possible.

## 6. The plan pages

### 6.1 The cards page (first page of setup)

- Title: "Welcome to DreamGrid. What will this computer be?" Five cards, each with an icon, the owner's name for the plan, a one-line description and "Runs: …". An ⓘ in the corner opens the plan's page. Picking a card and pressing Continue moves on.
- Mockups (brainstorm, not committed): cards and the Region Server ⓘ page as discussed on 2026-10-29.

### 6.2 The ⓘ page for each plan

Same layout for every plan, different content:
- What the plan is, in two or three plain sentences.
- **A diagram of the layout (owner):** visitors / internet, the router, this computer and any other computers (Grid Asset Server, other region servers, the other grid), with arrows showing who connects to whom. This computer is highlighted. The builder chooses how to draw it (inline SVG suits the site).
- **You'll need:** for example "the Grid Asset Server's address", "router: forward ports 8002 and the region ports".
- **Hypergrid:** one line (§9).
- **Can you change it later?** A list of ✔ / ✘ per other plan, from §8.
- **Before you go on:** advisories for this plan (§7).
- Buttons: "Choose this plan", "Back to all plans".

## 7. Advisories (written into each plan's page)

**(owner)** Warnings and advice belong in the explanation of the plan, not in a separate place. Starting list; the builder words them plainly:

- **Local:** only computers on this network can visit; other grids can't reach you, so no hypergrid.
- **Grid Asset Server, LAN-only:** only computers on this network can visit, and no hypergrid; it can be switched to a public address later.
- **Simple Public Server / Grid Asset Server with a public address:** the router must forward the ports viewers use (Robust public port 8002 and the region ports); the website uses plain HTTP, so passwords cross the internet unencrypted until HTTPS exists; some internet providers share one address between customers (CGNAT) and then nobody outside can reach you.
- **Grid Asset Server:** if it is off, every Region Server's regions stop working; running regions here is not advised.
- **Region Server:** the Grid Asset Server must be set up first and running; both computers should run the same DreamGrid version; the router must forward this computer's region ports.
- **Connected Server:** the other grid's rules apply (their sign-up, their region-placement rules); your regions must be reachable from the internet.

## 8. Changing plan later

**(owner)** A simple change is a yes, done in the browser (Advanced settings). A change where starting over is better is a no, and the plan's page says why.

**(my call)** The matrix, from the plan being run (rows) to the plan wanted (columns):

| From \ To | Local | Simple Public | Grid Asset | Region Server | Connected |
|---|---|---|---|---|---|
| Local | — | ✔ | ✔ | ✘ | ✘ |
| Simple Public | ✔ | — | ✔ | ✘ | ✘ |
| Grid Asset | ✔ (once no region servers are joined) | ✔ (same) | — | ✘ | ✘ |
| Region Server | ✘ | ✘ | ✘ | — (✔ point at a different Grid Asset Server) | ✘ |
| Connected | ✘ | ✘ | ✘ | ✘ | — |

Why the ✘s: accounts and inventory live in the Grid Asset Server's database. A machine that holds them can't hand them to another grid, and a machine that never held them has none to bring. The page says "start a new install" and what is kept (nothing grid-wide) in those cases.

## 9. Hypergrid

**(owner)** The owner doesn't need to understand hypergrid in depth. Each plan says whether it is hypergrid-capable, and where it is, there is one on/off switch.

| Plan | Hypergrid |
|---|---|
| Local | ✘ not possible (other grids can't reach a LAN-only grid) |
| Simple Public Server | ✔ capable, switch |
| Grid Asset Server | ✔ capable with a public address, one switch for the whole grid; ✘ when LAN-only |
| Region Server | follows its Grid Asset Server |
| Connected Server | the other grid decides |

- The switch has one sentence of explanation ("lets avatars travel between your grid and other grids, in both directions") and a "Before you turn it on" box (router must forward port 8002 and the region ports; visitors from anywhere can arrive).
- **(my call)** It is off by default on new installs, shown in the setup network step and in Advanced settings, and changeable at any time.
- *Checked:* hypergrid needs `HomeURI` and `GatekeeperURI` set to the grid's external IP or name (wiki, Installing and Running Hypergrid), so a LAN-only grid can't use it, which confirms the ✘ for Local and LAN-only Grid Asset Servers.
- *Checked, a trap for "off":* `[UserAgentService] ForeignTripsAllowed_Level_<n>` applies only to the exact user levels listed; any level without a key may still travel (`UserAgentService.cs`). `[GatekeeperService] ForeignAgentsAllowed = false` blocks visitors and still lets local residents leave (wiki, Hypergrid Security). **(my call)** So "off" should not rely on the per-level keys alone. Suggested: regions include `config-include/Grid.ini` instead of `GridHypergrid.ini` (both are in every `Proto` folder), plus `ForeignAgentsAllowed = false` and `AllowHypergridMapSearch = false` on Robust. The builder confirms local logins and teleports still work.
- What "off" writes is otherwise left to the builder. OpenSim's settings include Robust `[GatekeeperService] ForeignAgentsAllowed`, `[UserAgentService] ForeignTripsAllowed_Level_<n>`, and on regions `[HGInventoryAccessModule] OutboundPermission`. The builder confirms which combination fully blocks travel both ways and still lets local logins work. Today v7 writes the hypergrid-ready Robust (`Robust.HG.ini`) with no switch.

## 10. Joining a Grid Asset Server (Region Server)

**(owner)** The Region Server's region settings point at the Grid Asset Server for its data. The Grid Asset Server publishes a file that a Region Server's setup can fetch to learn how to connect, so the owner types only the address.

- The Grid Asset Server's website serves a small, anonymous, read-only file (path and format the builder's choice, for example JSON). It holds what a region needs to connect that OpenSim's own `get_grid_info` (Robust public port) doesn't: grid name, Robust public and private ports, the DreamGrid version, whether hypergrid is on, and anything else the region INIs need. **It holds no passwords.**
- Region Server setup:
  1. The owner types the Grid Asset Server's address (IP or name, with the web port if not the default).
  2. Setup fetches the file and shows what it found: grid name, version, hypergrid on/off. A version mismatch is a warning, not a refusal.
  3. The owner confirms. Setup writes the region INIs to use that server (the region prototypes in `bin/config-include/Proto/Region/` already point services at `BaseURL:PrivatePort`), then checks the Grid Asset Server answers on its private port.
- **(my call)** The Region Server reads the file again each time it starts, so a later change on the Grid Asset Server (hypergrid switch, grid name, ports) reaches it without rejoining. If the Grid Asset Server can't be reached, it keeps the last values and the dashboard says the Grid Asset Server is unreachable; its regions wait rather than start against nothing. *Checked:* the launcher already rewrites every INI before each start (`GridController.PrepareAsync` → `WriteConfig`), so re-reading the file fits the existing flow.
- *Found while checking:* OpenSim's `get_grid_info` publishes every key in Robust's `[GridInfoService]` section, extra ones included (wiki GridInfo; `GridInfoHandlers.cs`). The builder may use that as the join file (adding DreamGrid keys such as the private port, version and hypergrid state) instead of a separate file on the website. Either way it holds no passwords.
- **(owner)** Keep it basic: any Region Server on the LAN may join. Security on the private port is future work (§14). The Region Server and Grid Asset Server advisories say the private port is open to the LAN.
- The Grid Asset Server's Robust must accept private-port connections from the LAN (today v7 points everything at `127.0.0.1`). The builder works out which Robust settings and listen addresses change for the Grid Asset Server plan only.
- **(my call)** The Connected Server plan reads the other grid's `get_grid_info` for what it can (name, login address) and asks for the rest. OSgrid publishes its own settings; the builder uses the existing `Proto/OsGrid` files as the starting point.

## 11. Setup flow

**(owner)** The installer installs everything; the website switches on and configures what the plan needs. Both installers already place MariaDB, Robust, OpenSim and the launcher on every machine (`setup/windows/DreamGrid.iss`, `setup/debian/install.sh`).

Steps per plan (today's four steps stay, plus the plan and plan-specific steps):

| Step | Local | Simple Public | Grid Asset | Region Server | Connected |
|---|---|---|---|---|---|
| Your plan (cards) | ✔ | ✔ | ✔ | ✔ | ✔ |
| Network (address, router advice, hypergrid switch) | LAN address | public address + HG | LAN or public address (+ HG if public) | this computer's address + first region port | this computer's address + first region port |
| Connect to the Grid Asset Server / other grid | — | — | — | ✔ (§10) | ✔ |
| Name your world | ✔ | ✔ | ✔ | — (from the file) | — |
| Create your avatar | ✔ | ✔ | ✔ | "Your avatar": type its name, setup looks it up | "Your avatar": type its name on that grid |
| Choose your first region | ✔ | ✔ | optional, unticked | ✔ | ✔ |
| Connect with Firestorm | ✔ | ✔ | ✔ | ✔ (Grid Asset Server's login address) | ✔ (other grid's login address) |

- Parts a plan doesn't use are not started (Region Server and Connected Server don't start Robust). The dashboard shows only the parts the plan uses.
- **(my call) Two addresses on Region and Connected Servers.** Today one address is both Robust's (`BaseURL`) and each region's `ExternalHostName`. On these plans they differ: services use the Grid Asset Server's (or other grid's) address; regions give out this computer's own address, asked in the network step (default: its LAN address; the owner enters the public address when visitors come from the internet). *Checked:* the OpenSim wiki (Configuring Regions) defines `ExternalHostName` as the router's external IP or a name, the same for every region of one simulator.
- **(my call) Region ports behind one router.** Every install starts region ports at 8004, so a Grid Asset Server and its Region Servers behind one router would clash. The Region Server network step asks for the first region port, suggesting one clear of the Grid Asset Server's (the join file says which ports it uses); its advisory says each computer needs its own forwarded range. *Checked:* OpenSim requires a different `InternalPort` per region on one machine (wiki, Configuring Regions); across machines the limit is the router, which forwards each port to one computer only. Each program's HTTP port (`http_listener_port`) needs its own forward too.
- **(my call) The estate owner on Region and Connected Servers.** No avatar is created. The owner types the name of their avatar on that grid; OpenSim's `DefaultEstateOwnerName` is set to it and the region looks up the account through the grid's user service on first start. No password is asked. If no account by that name exists, setup says so before the first region starts.
  *Checked in OpenSim's code* (`OpenSimBase.SetUpEstateOwner`): with `DefaultEstateOwnerName` set, the region looks the name up through the grid's user account service; `DefaultEstateOwnerUUID` is used only in standalone mode. If the name is not found it logs an error and leaves the estate without an owner, so setup must check first. If the name is missing entirely OpenSim stops at a console prompt, so the key must always be written on these plans.
- Setup resumes where it stopped: the setup link keeps working until setup is finished (today's behaviour via `SetupToken` and `SetupSteps.FirstUnfinished`).
- **(owner) A missing part says why.** If a part the plan needs is missing (for example the MariaDB program), the page names the plan, the part and what it is for, where DreamGrid looked, and that the website can't install programs because it runs without administrator rights. It then says: run the DreamGrid installer again; it puts the part back and keeps your settings and world.
- Headless Debian: the owner opens the setup link from another computer on the LAN (the Debian installer already prints it). The pages must work that way.

## 12. Existing v7 installs

**(my call)** An install whose setup already finished becomes **Simple Public Server** if its grid address (`Hostname`) is a public address, otherwise **Local**. A Simple Public Server keeps hypergrid **on** (what it effectively has today), so nothing changes for its visitors. A Local install gets hypergrid off, as §9 says; nothing changes in practice, since a grid giving out a LAN address can't be reached by other grids anyway. Hypergrid is stored as a setting; a plan without the capability ignores it. Its owner sees the plan and the switch in Advanced settings. An install part-way through setup starts at the cards page.

## 13. Constraints

- Windows (service account, data under `%ProgramData%\DreamGrid`) and Debian 12/13 (systemd); same behaviour on both.
- The website runs without administrator rights and needs no internet access except for the plan steps that contact another computer or grid.
- All settings changes go through `IGridController.UpdateSettings` (plan 2a ruling); config is written by `GridConfigWriter`.
- The port-8001 contract (RegionReady callbacks, SmartStart, legacy API, `/welcome`) keeps working on every plan that runs regions.
- Settings schema version bumped for the plan and hypergrid fields.

## 14. Future build notes

- **HTTPS** for the website: draft in `2026-10-29-https-design.md`, kept as notes. **(owner)** HTTPS belongs to the WAN public website ("advanced grid"), with Let's Encrypt as an advanced option on ports 80/443.
- **The public WAN website:** one public site for a multi-computer grid (admin, monitoring, sign-up, forum, status), reading an API on each local machine.
- **The outside tester** that confirms the grid is reachable from the internet, and **router/firewall help** (UPnP with step-by-step fallback; Debian firewall).
- **Joining security:** who may join a Grid Asset Server (for example an Allow / Refuse prompt on its admin page) and locking its private port to known machines.
- **Hypergrid controls:** visitors in and residents out set separately, allow/deny lists of grids, per-level travel, taking inventory abroad.
- **Domain names**, including dynamic DNS.
- **Joomla grids** (`Proto/Joomla`).

## 15. Left to the builder

The diagram drawing method; the join file's path and format; exact advisory wording; how Advanced settings presents plan changes; which Robust/OpenSim settings implement "hypergrid off" and the Grid Asset Server's LAN-facing private port; how the dashboard hides unused parts; test coverage (unit tests for the plan rules and change matrix, integration tests for at least Local, Simple Public and a Region Server joining a Grid Asset Server on two launchers).
