# DreamGrid cross-platform launcher — version 1 design

Date: 2026-10-24
Branch: `design/crossplatform-launcher-v1`
Repo: fork of `Outworldz/DreamGrid-Opensim` (branch `V7`), at `ystudy/DreamGrid-Opensim_Linux`

## 1. Intent

DreamGrid today is two halves. This repo holds one of them: the Outworldz fork of OpenSimulator, with DreamGrid's patches (SmartStart and others). The other half is the DreamGrid launcher, a Windows-only VB.NET control panel that installs, configures and supervises OpenSim. That source is not public and we don't have it.

We are building a new launcher that:

- runs on **Linux (Debian) and Windows**,
- is administered entirely through a **web site**, with no desktop GUI, console windows or `.bat` files,
- is written in **C# on .NET 10**, with **Rust** used for standalone helper tools where it earns its place,
- ships with a **Debian setup script** and a **Windows installer `.exe`**.

The OpenSim fork in this repo moves to .NET 10 at the same time.

### Who it is for

**Someone new to OpenSim.** They have never seen an INI file, Robust, a region port or a console prompt. Success for them: run one installer, open a web page, follow a short checklist, then log in with Firestorm and stand in their own green island with trees and an info kiosk.

Existing DreamGrid users moving over and OpenSim experts are *not* the target of version 1. Experts still get an **Advanced Settings** menu, so nothing is hidden.

### Why version 1 is small

The old DreamGrid has about 60 features (inventory: `.ref/txt/*.txt`, the converted DreamGrid manuals; see §11). Version 1 deliberately does **one thing well**: take a newcomer from nothing to a running region they can enter. Everything is built so the other features can be added later as modules, without reworking the foundation. The design must make "there is more to come" visible and cheap.

## 2. Goals, non-goals, anti-goals

### Goals (version 1)

1. **Install:**
   - Debian 12/13: one script installs everything and starts the launcher as a systemd service.
   - Windows: one `.exe` does the same as a Windows service.
2. **Guided first run** in the browser: name the world, create the owner avatar, pick the first region, connect Firestorm.
3. **Dashboard after setup:** running or stopped; start, stop and restart; who's online; Firestorm login address with a copy button; log viewer.
4. **Advanced Settings menu** containing only what version 1 needs: ports, hostname, database.
5. **Supervision:** the launcher starts MariaDB, then Robust, then the region, in that order, and restarts anything that crashes.
6. **INI generation:** the launcher writes all OpenSim/Robust INI files from the prototype templates. The user never edits an INI by hand.
7. **.NET 10:** the whole solution (OpenSim fork, launcher, web site) builds and runs on .NET 10, on Debian and Windows.
8. **Extensible:** adding a feature later means adding a module (a dashboard switch plus an Advanced Settings page plus INI keys plus any helper process), not changing the core.

### Non-goals (version 1). Each comes later as a module.

- Access from outside the home network: DNS names, dynamic DNS, port forwarding, UPnP, firewall rules, HTTPS/SSL, Hypergrid.
- SmartStart (the patched OpenSim code stays, but it is switched off), Endless Land and Sea, freeze/thaw.
- More than one region, region editor, map editor, OAR/IAR load and save from the web.
- Backups and restore.
- User sign-up, password recovery, groups (the old Wifi features), more than one admin.
- Birds, Tides, voice (Janus/Vivox), money (PayPal/Gloebit), Icecast, text-to-speech, bots, mesh uploader, Joomla, WordPress, search, publicity.
- Moving an existing DreamGrid (Windows) install to the new launcher.
- Linux distributions outside the Debian family; macOS.

### Anti-goals: failures even if it technically works

- A newcomer has to open a terminal (beyond running the installer), edit an INI file, or read OpenSim console output to get to their first region.
- A version 1 that can't grow: a feature added later forces a rewrite of the launcher, the web site or the settings store.
- Changes to the `OpenSim/` tree so broad that monthly merges from Fred's fork become painful (see §4.6).
- Behavior that differs between Linux and Windows in ways the user can see, beyond what the OS forces (service names, paths).

## 3. Constraints

- **Runtime:** .NET 10 only. The `OpenSim` and `Robust` processes run as `dotnet OpenSim.dll` / `dotnet Robust.dll` (or apphosts) on both OSes.
- **Platforms:** Debian 12 and 13 (amd64 required; arm64 is nice-to-have since the native libs exist in `bin/lib64`), and Windows 10/11 x64. Ubuntu should work as a side effect but is not a supported target.
- **Network:** version 1 is LAN/same-machine only. The login address shown to the user is `http://<LAN IP>:<Robust public port>`.
- **The port-8001 contract is kept** (see §5). Patched OpenSim code calls back to the launcher there, and we change none of that code in version 1.
- **The starter region:** the user (the project owner) will build the starter region in-world and export it as an OAR. The installer ships whatever OAR file is in place. Until it exists, a placeholder is used (see §6.3). Swapping the OAR must not need a code change.
- **License:** OpenSim is BSD. New code in this repo should be license-compatible (BSD/MIT/Apache-2.0 dependencies). Avoid ImageSharp (Six Labors split license).

## 4. Decisions made by the project owner

These were each put to the project owner and agreed.

### 4.1 Architecture: Robust + separate region process, from day one
Not OpenSim "standalone" mode. SmartStart, multiple regions and the Hypergrid all assume Robust, and switching later would mean migrating user data. Version 1 runs **one Robust process and one region process** (one OpenSim instance hosting the one region).

### 4.2 Database: MariaDB, installed and managed by the launcher
Not SQLite.
- **Debian:** the distro's `mariadb-server` package.
- **Windows:** a bundled MariaDB that the installer sets up.

The launcher owns the database lifecycle: create the databases and users, start and stop, health checks. Robust and the region use separate databases and users, the same split as DreamGrid: `robust` / `robustuser` and `opensim` / `opensimuser`. **Passwords are generated per install**, not DreamGrid's well-known defaults.

### 4.3 Web site: ASP.NET Core, hosted inside the launcher process
One process, one port: the launcher *is* the web server and the API. No separate web server (no Apache/PHP) in version 1.

### 4.4 Build: SDK-style `.csproj`, one solution, Prebuild retired
- `prebuild.xml` and the generated project files are replaced by checked-in SDK-style projects.
- A `Directory.Build.props` sets `net10.0` and the common settings.
- One solution holds the OpenSim fork, the launcher and the web site.

### 4.5 Sign-in: Firestorm avatar name and password
- Admin access comes from the avatar's grid **user level** (≥ 200 = admin, matching DreamGrid's levels).
- On first run, before any avatar exists, the installer opens the browser on a **one-time setup link** (a random token). Nobody else on the LAN can reach the setup checklist without it.
- Once the owner avatar exists, the token is spent.

### 4.6 Upstream: keep merging from Fred's `Outworldz/DreamGrid-Opensim`
Follow Fred's fork, not upstream OpenSim directly; his fork already carries the DreamGrid patches and pulls upstream.
- **Don't move, rename or restructure anything under `OpenSim/`.** Keep edits to existing OpenSim files minimal and justified.
- **All new code goes in new top-level folders** (§7).
- **One accepted manual chore:** when Fred changes `prebuild.xml` (new project, new reference), mirror it in our `.csproj` files by hand.
- Merge roughly monthly or per Fred release.

### 4.7 Platforms and installers
- **Debian (12/13) first.** A setup script installs .NET 10, MariaDB and the launcher; creates a systemd service; prints the one-time setup link.
- **Windows:** a single installer `.exe` that installs the launcher as a Windows service, sets up MariaDB, and opens the browser on the setup link.

### 4.8 Old Wifi: kept in the repo, switched off
The Diva Wifi add-on (`addon-modules/21Wifi` and friends) stays in the tree but is disabled in generated configs. The new web site rebuilds its features one at a time (user accounts first, when internet access arrives). Wifi is deleted once nothing useful in it remains.

### 4.9 `System.Drawing` on Linux: keep the workaround, test it first, SkiaSharp as fallback
OpenSim uses `System.Drawing` in ~52 files (map tiles, terrain import/export, meshing/sculpts, dynamic textures, archives). The repo works around Microsoft's Windows-only restriction by shipping a Linux build (`bin/System.Drawing.Common.dll.linux`) and setting `System.Drawing.EnableUnixSupport` in the runtimeconfig files.
- **First task of the .NET 10 work:** prove whether this still works on .NET 10 on Debian (map tile generation, terrain load from PNG, sculpt meshing).
- **If it fails:** replace `System.Drawing` with **SkiaSharp** (MIT). Not ImageSharp (license), not a Rust library (same C# rewrite plus our own native builds for no gain).

### 4.10 Rust: for standalone helper tools that fill gaps
Rust is not used inside the launcher or OpenSim. It's for separate executables where it's the better tool. Likely future candidates:
- a backup/sync tool (replaces robocopy),
- an ACME/certificate helper (replaces win-acme),
- a TTS tool (replaces SAPI5),
- an installer helper.

Version 1 may need none. The repo layout reserves a place for them (`tools/`).

### 4.11 The starter region: built by the project owner
A simple region with trees, greenery and an info location (a kiosk with notecards: moving and building, changing your avatar, the admin web address). Shipped as an OAR with the installer.

### 4.12 Regions keep running when the launcher restarts
A launcher update, crash or settings reload must not take the world down.
- **Robust and region processes outlive the launcher.** On startup the launcher **adopts** running children (PID files + RegionReady PID reports + HTTP health checks) instead of starting duplicates.
- **Consequence:** the launcher controls OpenSim/Robust through **OpenSim's remote (REST) console over HTTP**, not stdin. That includes `quit`, `load oar`, estate setup and `create user`. Stdin is not used for control.
- **Service setup:**
  - The systemd unit uses `KillMode=process` (or equivalent), so children survive a launcher restart.
  - On Windows, children are started detached from the service process (not in its job object).
  - "Stop all" stops children explicitly before the launcher exits when the user asks for a full shutdown.
- **The remote console** is bound to localhost only, with generated credentials stored in the settings store.

### 4.13 Distribution: prebuilt releases on GitHub Releases
- Nobody compiles on their machine. GitHub Actions builds release artifacts on tag:
  - a Linux tarball (amd64; arm64 if cheap),
  - the Windows `DreamGridSetup.exe`.

  Both attach to a GitHub Release on `ystudy/DreamGrid-Opensim_Linux`.
- **Debian:** a one-line install (`curl -fsSL <url>/install.sh | sudo bash`) downloads the latest release tarball. Re-running it upgrades in place (stop launcher → replace program files → start; data untouched; children are restarted only if the OpenSim binaries changed).
- **Packaging:** the starter OAR in `content/` and the runtime files from `bin/` are packaged into the artifacts.
- **Versioning:** the builder chooses the scheme, but the version must show in the web UI.

### 4.14 The launcher runs its own private MariaDB instance
- **Not** the machine's shared database service. The launcher runs a MariaDB server process with **its own data directory and its own port** (default **3307**), started, stopped and supervised by the launcher like any other child.
- **Binaries:**
  - **Debian:** the distro `mariadb-server` package. The system `mariadb.service` is left as installed; the launcher never stops or reconfigures it. The installer may disable it only if the installer itself just installed it and nothing else uses it (builder's call; default leave it).
  - **Windows:** a bundled MariaDB.
- **Setup:** the launcher initializes the private data directory on first run (`mariadb-install-db` or equivalent), sets a generated root password, and creates the `robust` and `opensim` databases and users itself. No root password is asked of the user.
- **Remote/external databases** are a later Advanced Settings option, not version 1.

### 4.15 Outworldz services and Gloebit: kept in the templates, inactive in version 1
The prototypes carry Outworldz/third-party settings:
- search and region registration (`data_services`, `SearchURL` → outworldz.com),
- the `welcome=` page and `DestinationGuide` (outworldz.com),
- `host_domain_header_from=outworldz.com`,
- `economymodule=Gloebit`,
- `OfflineMessageURL` (a PHP page under `BaseURL`).

**Rules for version 1:**
- **Leave them in the prototype files,** so they can be switched on later as features. Don't delete them from the templates.
- **Generate them inactive:**
  - no search registration or search URL,
  - no external destination guide,
  - economy module empty (no money),
  - the `welcome=` login page pointing at a simple page served by the launcher's own web site,
  - offline IM handled without the PHP page (OpenSim's built-in offline IM service, or disabled; builder's choice, but no broken URL).
- Nothing in a version 1 install contacts outworldz.com or any third-party service.
- Each of these returns later as a feature module with a dashboard switch, Outworldz being one option.

### 4.16 Windows installer is unsigned in version 1
`DreamGridSetup.exe` ships without Authenticode signing. The download page and README include a short note on getting past the SmartScreen warning ("More info → Run anyway"). Code signing is required before any wide public release and is planned as a later item.

## 5. The port-8001 contract (kept for the patched OpenSim code)

The DreamGrid patches in this fork call an HTTP listener the old launcher ran, the "diagnostics port", default 8001. The new launcher serves the same endpoints, **on the same port as the web site** (my call: one Kestrel listener serves both the web UI and these legacy endpoints).

### Called by OpenSim and Robust, so must exist in version 1

| Endpoint | Caller | Behavior in version 1 |
|---|---|---|
| `GET /?alt=<uuid or name>&agentid=<uuid>&password=<MachineID>[&clientversion=…]` | `OpenSim/Framework/SmartStartAltClient.cs` (Scene, GridService, GatekeeperService); `OpenSim/SmartStart/SmartStart.cs` | SmartStart is off in generated INIs (`[SmartStart] Enabled=False`), so this should not be called. If it is, return the requested UUID unchanged ("no redirect"). |
| `GET /?type=<msg>&UUID=<uid>` | `OpenSim/Region/Application/SmartStart.cs` (`SmartStartNotify`) | Accept and log. Note: this code also reads a relative `port.txt` file. The launcher should write it into the instance's working directory, or OpenSim must tolerate its absence. |
| `POST <[RegionReady] alert_uri>`, JSON with `alert` = `regionpid` / region-ready messages | `OpenSimBase.NotifyDreamGridOfPidFireAndForget`, `RegionReadyModule` | **Used:** this is how the launcher learns a region's PID and that it is ready for logins. The generated INI points `alert_uri` at the launcher. |

### Command API (`?command=…`)
Documented in `.ref/txt/API.txt`. Version 1 implements only what it uses, with the same parameter names and `ACK`/`NAK` replies, so later additions stay compatible.

**Permissions (my call):** `API.txt` contradicts itself on levels. In version 1, `RegionStatus` and `regionlist` need no password, and **every other command needs level ≥ 200**. Nothing lets a low-level avatar start or stop processes on a LAN-exposed port.

The version 1 commands are: `RegionStatus`, `StartRegion`, `StopRegion`, `RestartRegion`, `StartRobust`, `StopRobust`, `StartMysql`, `StopMysql`, `regionlist`. The rest (backups, freeze/thaw, Icecast, Apache) arrive with their modules.

**Auth note.** The old API accepted an MD5 "MachineHash" as a master password, sent in the query string. In version 1 the launcher accepts:
- avatar name + password, checked against Robust, with level checks as documented,
- a launcher-generated random `MachineID` for OpenSim's own callbacks only.

The MD5-of-DynDNS-password scheme is not carried over.

### Out of scope for version 1
- The `/mcp` endpoint (MCP server for AI clients). It's a natural later module.
- TTS, teleport signs, mesh upload.

## 6. Version 1 behavior

### 6.1 Installers

**Debian (`setup/debian/install.sh`):**
1. Check it's Debian 12/13 and running as root (or via sudo).
2. Install prerequisites:
   - Microsoft's .NET 10 runtime (via Microsoft's package feed or the `dotnet-install` script; builder's choice),
   - `mariadb-server`,
   - `libgdiplus` if the `System.Drawing` workaround is kept.
3. Create a system user (e.g. `dreamgrid`) and the install layout: program files read-only, data in a separate writable directory (e.g. `/opt/dreamgrid` + `/var/lib/dreamgrid`; builder's choice, but program and data must be separate so upgrades don't touch user data).
4. Install and start a systemd unit for the launcher (restart on failure).
5. Print the one-time setup URL (`http://<LAN IP>:<web port>/setup?token=…`).

Re-running the script on an installed system upgrades in place and never wipes data.

**Windows (`DreamGridSetup.exe`):**
- Same steps: bundled .NET 10 runtime or a framework-dependent install with a prerequisite check; bundled MariaDB; launcher registered as a Windows service; firewall prompt for LAN access if needed.
- Opens the browser on the setup URL.
- Program files and data are separate (e.g. `Program Files\DreamGrid` + `ProgramData\DreamGrid`).

Installer technology is the builder's choice (e.g. Inno Setup or WiX).

#### Recorded during plans 2–3 (installers)
- **Private runtime, pinned per release.** Release packages carry a private ASP.NET Core runtime, resolved by `setup/package.ps1` from Microsoft's .NET 10 `releases.json` (latest 10.0 runtime unless `-RuntimeVersion` pins one, SHA-512 checked). Debian no longer installs .NET via `dotnet-install` or a package feed. CI's `dotnet-install` fallback pins the SDK from `global.json`; release runtimes are pinned by `package.ps1`.
- **The runtime lives in a versioned folder that an upgrade never replaces:** on Debian outside the program folder, `/opt/dreamgrid-runtime/<RUNTIME_VERSION>/`; on Windows inside it, `{app}\dotnet-<ver>\` (and MariaDB in `{app}\mariadb-<ver>\`), beside the `launcher\`, `bin\` and `content\` folders that setup replaces. An upgrade adds the new folder beside the old one; an old folder is deleted only when no running process uses it, so children adopted through an upgrade keep loading from the runtime they started with.
- **Debian paths:** program `/opt/dreamgrid` (root-owned, read-only), data `/var/lib/dreamgrid` (user `dreamgrid`, 0700), unit `dreamgrid.service` with `KillMode=process` and `WorkingDirectory=/opt/dreamgrid/launcher` (the website's static files resolve from there). An upgrade unpacks to `/opt/dreamgrid.new`, stops the service, renames the old program folder to `/opt/dreamgrid.old` (deleting the previous `.old`) and swaps the new one in.
- **Windows paths:** program `C:\Program Files\DreamGrid`, data `%ProgramData%\DreamGrid`, service `DreamGrid` running as the virtual account `NT SERVICE\DreamGrid`. The Start menu entry is a plain `http://localhost:<WebPort>/` shortcut; the installer's finish page opens the setup link as the original (unelevated) user.
- **Adoption across restarts and upgrades** uses a `<pidfile>.start` sidecar holding a clock-independent start identity of the process, so a reused PID is never taken for a child (2a follow-up: PID-reuse hardening).
- **`--web-port` / `/WEBPORT=`** (installer options) must be 1024–65535 and none of DreamGrid's own default ports (3307, 8002, 8003, 8004, 8010, 8011); anything else stops the installer with a plain message before it changes anything. The installer stops the service and saves the port once with the launcher verb `set-web-port <n> --data <dir>` (instance lock, exit 3 while a launcher runs; `SettingsValidator.Network`, exit 2 with its message; creates the settings when there are none), then starts the service. The port is never put into the unit's `ExecStart` or the service's binPath, so a later change in Advanced → Network stays; an upgrade keeps the saved port, and a `--web-port` an older installer pinned there is saved once and dropped. The launcher's own host option `--web-port <n>` (development) is applied at every start, before the host is built, under the instance lock and with `SettingsStore` directly. `set-web-port` and that option are the documented exceptions to "settings change only through `UpdateSettings`".
- **Installers do not report success for a service that does not run:** Debian fails the install with a `journalctl -u dreamgrid` hint when the unit is not active 30 s after the restart; Windows waits for Running and the website to answer and otherwise exits with code 10. A failed or cancelled Windows upgrade starts the service it stopped again (when the launcher's files are still there). `stop-grid` (the uninstaller) waits up to 60 s for the children to exit (exit 5 otherwise).
- **Releases:** a `v*` tag runs `.github/workflows/release.yml`. The tag must match `^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$` or the run fails; a tag with a pre-release suffix (`v0.1.0-rc1`) is published as a GitHub pre-release that never becomes `/releases/latest`, and `install.sh` installs it with `DREAMGRID_RELEASE=<tag>` (`curl … | sudo DREAMGRID_RELEASE=v0.1.0-rc1 bash`). The workflow packages both OSes, runs the Debian 12/13 and Windows install smokes (install, setup, login, upgrade) and publishes `dreamgrid-<version>-linux-x64.tar.gz` (also as `dreamgrid-linux-x64.tar.gz` for the one-liner), `DreamGridSetup-<version>.exe`, `install.sh` and `SHA256SUMS`. Untagged builds are version `0.0.0-dev+<short sha>`.
- **Not done in version 1:** linux-arm64 packages (they need their own runner and smoke) and intercepting machine shutdown (systemd/Windows end the children at OS shutdown; MariaDB shuts down cleanly on SIGTERM; OpenSim saves prims as they change).

### 6.2 First-run checklist (web)
Reached only through the one-time setup link. Steps, each saved as it completes so a refresh or restart resumes in place:

1. **Name your world.** Becomes the grid name in `[Const] GridName` / `[GridInfoService]`.
2. **Create your avatar.** First name, last name, password, optional email.
   - The launcher creates the account in Robust at **user level 200** (my call: matches DreamGrid's "god" level, enough for every version 1 admin action; 500 "WebAdmin" can come later).
   - This avatar also becomes the estate owner.
3. **Choose your first region:**
   - its name,
   - the content: the starter region (§6.3) or an empty island.

   The launcher writes the region INI, boots Robust and the region, creates the estate non-interactively (no console prompts), and loads the OAR.
4. **Connect with Firestorm:**
   - shows the login URI (`http://<LAN IP>:<Robust public port>`) with a copy button,
   - short steps for adding a grid in Firestorm, with a link to Firestorm's download page,
   - "I'm in!" finishes setup.

A progress view shows what the launcher is doing during step 3 (database, Robust, region booting, OAR loading). Failures show a plain-language message plus a "show details" log excerpt.

### 6.3 Starter region content
- Shipped as an OAR at a fixed path in the program files (e.g. `content/starter-region.oar`). Replacing the file changes the content; no code change.
- **Until the owner's OAR exists (my call):** a placeholder of a generated gently rolling island on water, which the launcher can create with OpenSim's built-in terrain commands. The "empty island" choice in step 3 uses the same generator.

### 6.4 Dashboard (after setup)
- **Status:** grid running or stopped; each process (MariaDB, Robust, region) with state, uptime and memory.
- **Controls:** Start all / Stop all / Restart, plus per-process restart.
- **Who's online:** avatar names currently in-world, read from Robust's presence data.
- **Connect box:** login URI with copy button.
- **Logs:** tail of the launcher, Robust and region logs, with level filter.
- **Features area:** visibly shows that more is coming (e.g. a "Coming soon" list of future modules). Exact presentation is the builder's choice.

### 6.5 Advanced Settings (version 1 contents only)
- **Network:**
  - hostname/IP shown to viewers (default: the detected LAN IP),
  - web/launcher port (default 8001),
  - Robust public port (8002) and private port (8003),
  - region port (8004).
- **Database:** host, port; credentials shown masked; "test connection".

Saving regenerates the INIs and offers a restart. Every later module adds its own page here.

### 6.6 Supervision
- **Start order:** MariaDB → Robust (ready when it answers HTTP on its public port) → region (ready on RegionReady alert or a timeout).
- **Stop order:** reverse. Each OpenSim process gets a graceful `quit`, then is killed after a timeout.
- **Crash handling:** restart with backoff. After N rapid failures, stop retrying and show the error on the dashboard.
- **MariaDB** is the launcher's own private instance (§4.14), supervised like the other children.
- **Talking to OpenSim and Robust:** via OpenSim's remote (REST) console over HTTP, bound to localhost (§4.12). Stdout/log files are read for the log viewer.
- **On launcher startup:** adopt already-running children (§4.12) instead of starting duplicates.

### 6.7 INI generation
- **Source (my call, checked against the prototype headers):**
  - The **region process** uses `bin/config-include/Proto/Robust/*`, labeled "ROBUST VERSION": a region on a grid that runs its own Robust.
  - The **Robust process** uses `bin/Robust.HG.ini.proto`.
  - `Proto/Region/` (a region joining someone else's grid), `Proto/OsGrid/` and `Proto/Joomla/` are unused in version 1.
  - `bin/config-addon-opensim/Wifi.ini.proto` is generated with Wifi off.
- **Mechanism (matches what DreamGrid did):**
  1. Copy the prototype into the target location.
  2. Set the `[Const]` values OpenSim expands itself: `BaseHostname`, `BaseURL`, `PublicPort`, `PrivatePort`, `PrivURL`, `http_listener_port`, `RegionFolderName`, `DiagnosticsPort`, `ApachePort`, `GridName`, …
  3. Set specific `[Section] Key=Value` pairs, for example the database connection strings, `[SmartStart] Enabled=False`, `[RegionReady] alert_uri`, Wifi off, and `[Chat] enabled`.
- **The INI writer must:**
  - preserve comments and ordering,
  - add a section if the prototype lacks it,
  - support marker-delimited blocks (used later by PayPal lists).
- **Region files** follow DreamGrid's layout: `Regions/<InstanceName>/Region/<RegionName>.ini`, file name = `[RegionName]` section. Region port starting at 8004.
- **Overrides:** the settings store can hold arbitrary `[Section] Key=Value` overrides that win over generated values. This is DreamGrid's "Settings.ini overrides", useful for experts and for modules. Exposure in the UI is not required in version 1, but the store and the writer must support it.
- **The full DreamGrid key list** (which keys each feature sets) is not documented anywhere we have. Version 1 needs only the keys listed above. Each later module documents its own keys.

### 6.8 Settings store
**My call:** one launcher-owned settings file (JSON) in the data directory, the single source of truth from which all INIs are regenerated. Holds:
- grid name, ports, hostname,
- generated database passwords,
- `MachineID`, setup state,
- region list,
- per-module settings,
- the override list.

Secrets are stored with file permissions limited to the service account. Encryption at rest is not required in version 1.

#### Recorded during plans 2–3 (data directory, settings, status)
- **The whole data directory is secret** (generated INIs hold database passwords, the console password and `MachineID`): 0700 for the `dreamgrid` user on Linux, a protected ACL (the service account, SYSTEM and Administrators only) on `%ProgramData%\DreamGrid` on Windows.
- `<data>/mariadb/tmp` is MariaDB's private tmpdir (a shared `/tmp` let one MariaDB delete another's temp tables).
- `<data>/keys` holds the ASP.NET Core data-protection keys (sign-in and antiforgery cookies), application name `DreamGrid`, created owner-only like `<data>`.
- OpenSim's log4net pattern carries the level: `%date{HH:mm:ss} - [%level] %message%newline` (Robust and region logs); the log viewer's level filter anchors to that prefix.
- `GridStatus` gained `Activity` (`None`, `Starting`, `Stopping`, `Restarting`) and `StartFailure` (`Message`, `ConflictPort`); `IGridController.CreateRegionAsync` gained an optional `IProgress<string>`.
- The `dg-setup` cookie is `SameSite=Lax` (a setup link opened from webmail or chat arrives cross-site); `dg-auth` stays `Strict`.
- The port-8001 command API and the sign-in page share one `LoginThrottle`, keyed on the normalised "First Last" name.
- Bare `GET /` is the website; the old "Test Completed" probe is gone (`/?type=...` still answers `ACK`).
- Region `login status` answers "Login in NAME are enabled|disabled"; enable/disable answer "Logins are enabled|disabled for region NAME".
- Every settings change goes through `IGridController.UpdateSettings`, except the installers' `set-web-port` and the `--web-port` host option at startup (§6.1 above).

#### Recorded during plan 4a (many regions; design: `2026-10-27-regions-design.md`)
- **Settings schema 2:** `Programs` (instance folder, HTTP port, REST console port, physics) and `Regions` (program, name, UUID, map spot, size, content, UDP port, `Parked`, `DeletedAt`) replace the single `Region`. A schema 1 file is migrated on load and saved once: its region becomes one program and one region with the same folder, UUID and ports (`RegionPort` for HTTP and UDP, `RegionConsolePort`), so a running region is adopted with no config change.
- **Ports:** `RegionPort` and `RegionConsolePort` are now the first ports tried for new programs and regions. A new program gets the next free HTTP port from `RegionPort` and console port from `RegionConsolePort`; its first region shares the HTTP port's number for UDP, and a region joining a program gets the next free port from `RegionPort`. Ports held by deleted regions stay reserved. The Windows firewall rule allows the runtime's `dotnet.exe`, so new ports need no installer change.
- **Programs:** one supervised OpenSim process per program (`<pids>/<Instance>.pid`, `region-<Instance>.log`), started in order after Robust; one that fails doesn't keep the others down. Each program's `Region/` folder holds exactly its loadable regions' files: a parked, stopped or deleted region's file is renamed `<Name>.ini.off`.
- **Region states:** Stopped, Starting, Running, Stopping, Failed, Parked. An adopted program is asked `show regions` (IDs, since names pad to 20 characters) and `login status`. A RegionReady alert for a region the launcher doesn't know is ignored.
- **Power:** Stop holds a region until the next world start (in memory); Park is saved. "Start everything" unparks. The dashboard's power menu has Start, Start everything, Restart and Stop all. A region alone stops, starts or restarts only through `RegionPowerAsync`; the component operations accept "database" and "robust" only.
- **Port-8001 API:** `regionlist` lists every live region; `RegionStatus`, `StartRegion`, `StopRegion` and `RestartRegion` need `RegionName` or `RegionUuid` when there is more than one region (`NAK Name the region.`). `StopRegion` is a Stop, never a Park.

### 6.9 Module (feature) extension model
**My call; the exact API is left to the builder.** A feature module is a C# component the launcher discovers at startup. It can contribute:
- settings (with defaults) to the settings store,
- INI keys/sections to the generator,
- a dashboard switch (on/off) and an Advanced Settings page,
- commands for the `?command=` API (and later MCP),
- optional helper processes (e.g. Icecast, a Rust tool) to supervise.

Version 1 ships the core with no optional modules. The plan should prove the model with one trivial internal module (e.g. "Log level") so the seams are real.

## 7. Repository layout

New code lives in new top-level folders. `OpenSim/`, `addon-modules/`, `ThirdParty/` and `bin/` keep their structure for Fred-merge compatibility.

```
Launcher/            C# — the service: supervision, INI generation, settings, 8001 contract, modules
Launcher.Web/        C# — ASP.NET Core UI (may be one project with Launcher; builder's choice)
Launcher.Tests/      tests
tools/               Rust helper tools (cargo workspace), empty or minimal in version 1
setup/debian/        install.sh, systemd unit
setup/windows/       installer script/project
content/             starter-region.oar (owner-supplied) and other shipped content
Directory.Build.props, DreamGrid.sln (replaces the Prebuild-generated OpenSim.sln)
docs/                design and plan documents
```

UI technology inside ASP.NET Core (Razor Pages, Blazor Server, or an API plus a small JS front end) is the builder's choice. It should be simple to extend page by page, work on a phone-sized screen, and need no internet access (no CDN dependencies; LAN-only machines may be offline).

## 8. The .NET 10 move (OpenSim fork)

1. Convert Prebuild projects to SDK-style `.csproj` targeting `net10.0` (one per existing Prebuild project; same assembly names and output to `bin/` so runtime layout and Mono.Addins plugin loading are unchanged).
2. **Early:** the `System.Drawing` check (§4.9).
3. Fix build breaks from .NET 9/10 API changes and obsolete APIs (e.g. `WebRequest`/`HttpWebRequest` still works but warns; leave unless broken, for Fred-merge friendliness).
4. Review the committed DLLs in `bin/` (MySql.Data, Mono.Addins, Nini, BouncyCastle, …) for .NET 10 compatibility. Replace with NuGet `PackageReference` only where a committed DLL breaks or a package is a drop-in.
5. Windows-only code: `OpenSim/Tools/LaunchSLClient` (WinForms) is excluded from the Linux build or dropped. The `kernel32` calls in `OpenSim/Framework/Util.cs` are already OS-guarded.
6. Native libraries in `bin/lib64` (BulletSim, ubODE, openjpeg, e_sqlite3) are already present for Linux x64/arm64. Verify they load under .NET 10 on Debian.
7. CI: a GitHub Actions workflow on *this* repo that builds on Ubuntu (as a Debian proxy) and Windows. The existing workflow only runs for `opensim/opensim`.

## 9. Risks

| Risk | Mitigation |
|---|---|
| `System.Drawing` fails on .NET 10/Linux | Check first; SkiaSharp fallback (§4.9) |
| Committed third-party DLLs incompatible with .NET 10 | Inventory early; swap to NuGet packages case by case |
| Non-interactive estate creation: OpenSim prompts on the console at first region boot | Pre-seed estate settings in the region INI / `[Estates]` config (`DefaultEstateName`, `DefaultEstateOwnerName`, …) so no prompt appears. Stdin is not available after adoption (§4.12). Must be verified early |
| Fred merges conflict with our project files | Keep `OpenSim/` layout untouched; mirror `prebuild.xml` changes by hand |
| Windows MariaDB bundling and service setup | Prototype the installer early; it's the least familiar piece |
| Hidden DreamGrid INI keys we don't know about | Version 1 needs few; the prototypes carry "Set by Dreamgrid" comments; recent commits in this repo name the sections DreamGrid fills |

## 10. Left to the builder

- **Launcher internals:** the UI framework within ASP.NET Core, and the exact module API.
- **Installer technology** on Windows; how .NET 10 is installed on Debian.
- **Exact install paths,** service names and the service account name.
- **Logging library and format** for the launcher.
- **Test strategy details**, but the INI generator, settings store and supervision state machine must have automated tests, and both OSes need a smoke test (install → setup → region up → login port answers).

## 11. Reference material

- **DreamGrid manuals:**
  - Originals: `Outworldz/DreamGridManuals` on GitHub (Word documents).
  - Plain-text copies: `.ref/txt/` in this checkout. This folder is untracked; regenerate with `.ref/docx2txt.py`.
  - Key ones: `SmartStart.txt`, `API.txt`, `MCP.txt`, `Ports.txt`, `Region.txt`, `Startup.txt`, `Restart.txt`, `DreamGrid Service.txt`, `Modifying INI Files.txt`, `Database.txt`.
- **Feature inventory** of the old launcher (summary of the manuals): areas are install/updates, process supervision, service mode, region management, INI prototypes and overrides, database and backups, users and estates, network (DNS/ports/firewall/SSL/loopback), web (Wifi/Apache/Joomla/WordPress), in-world extras, monitoring/logging. Version 1 covers only the core of supervision, INI generation, users (owner only) and a minimal network setting.
- **Prototype templates:** `bin/config-include/Proto/`.
- **DreamGrid patch points in OpenSim:**
  - `OpenSim/Framework/SmartStartAltClient.cs`
  - `OpenSim/Region/Application/SmartStart.cs`
  - `OpenSim/SmartStart/SmartStart.cs`
  - `OpenSimBase.NotifyDreamGridOfPidFireAndForget`
  - `RegionReadyModule`
