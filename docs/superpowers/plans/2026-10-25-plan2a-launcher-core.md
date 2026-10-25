# Plan 2a: Launcher core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A headless launcher service (C#, .NET 10, same code on Windows and Debian) that owns a private MariaDB, generates every OpenSim/Robust INI from the prototypes, starts and supervises MariaDB → Robust → one region, survives its own restart without taking the region down, provisions the owner avatar and first region without console prompts, and serves the port-8001 contract. Proven by an end-to-end integration test on both OSes.

**Architecture:**
- **Two .NET projects.**
  - `DreamGrid.Launcher.Core` is a class library holding all logic, unit-testable without OpenSim.
  - `DreamGrid.Launcher` is an ASP.NET Core host that wires the Core into a hosted service and maps the port-8001 endpoints. It runs as a systemd unit or a Windows service. Plan 2b adds Razor Pages to this same project.
- **Separate program and data.** OpenSim's `bin/` is never run in place. A **runtime copy** of it is staged under the data directory and refreshed when the program version changes. All user state (database, regions, caches, scripts, logs) lives in data-directory folders *outside* the runtime copy, so an upgrade can replace the runtime copy wholesale.
- **One source of truth for configuration.** A JSON settings file is the only source; every INI is regenerated from it on each start.
- **Control.** The launcher drives OpenSim and Robust through their REST consoles and Robust's HTTP services, never through stdin. Children are adopted after a launcher restart.

**Tech Stack:**
- .NET 10, ASP.NET Core (Kestrel, minimal APIs), `Microsoft.Extensions.Hosting.Systemd` 10.0.13 and `Microsoft.Extensions.Hosting.WindowsServices` 10.0.13.
- `MySqlConnector` 2.6.2 (MIT).
- xUnit v3 4.0.2 on Microsoft Testing Platform (as in plan 1).
- MariaDB: the distro `mariadb-server` on Debian (10.11 on Debian 12, 11.8 on Debian 13); MariaDB 11.4 LTS zip (`mariadb-11.4.9-winx64.zip`) on Windows.

**Spec:** `docs/superpowers/specs/2026-10-24-crossplatform-launcher-v1-design.md`, sections §4.1–4.3, §4.5 (MachineID, setup token), §4.12, §4.14, §4.15, §5, §6.3, §6.6, §6.7, §6.8, §6.9 (core half), and §9. This is plan 2a of 4: plan 1 is done; 2b is the website; 3 is installers and release.

**Facts established while writing this plan** (verified against this repo, branch `design/crossplatform-launcher-v1`):

**Process start and configuration**
- **Region process:** DreamGrid starts one with working directory `bin` and `OpenSim -inidirectory="Regions\<Instance>"` (`go.bat`). `ConfigurationLoader` reads `OpenSimDefaults.ini`, then `OpenSim.ini`, then every `*.ini` in the `-inidirectory`, and `Path.Combine` lets that be an absolute path.
- **Robust:** starts with `Robust -inifile="Robust.HG.ini"` (`runrobust.bat`).
- **Logging:** both accept `-logconfig=<log4net xml>` (`Application.cs:129`, `ServicesServerBase.cs:78`).
  - `bin/OpenSim.exe.config` logs to `${OSIM_LOGPATH}\OpenSim.log`. The backslash breaks on Linux, and an unset variable writes to the drive root (seen in plan 1: `C:\OpenSim.log`).
  - `bin/Robust.exe.config` logs to `Robust.log` in the working directory.

**Remote console** (`OpenSim/Framework/Console/RemoteConsole.cs`)
- Enabled with `-console=rest`. It serves on the process's main HTTP port, or on `[Network] ConsolePort` if non-zero, and authenticates with `[Network] ConsoleUser` / `ConsolePass`.
- **Protocol** (form-encoded POSTs, XML replies):
  - `POST /StartSession/` with `USER`, `PASS` → `<ConsoleSession><SessionID>…</SessionID><Prompt>…</Prompt></ConsoleSession>`.
  - `POST /SessionCommand/` with `ID`, `COMMAND` → `<ConsoleSession><Result>OK</Result></ConsoleSession>`.
  - `POST /ReadResponses/<id>/` → `<ConsoleSession><Line Number=".." Level=".." Prompt="True|False" Command="True|False" Input="True|False">text</Line>…</ConsoleSession>`.
  - `POST /CloseSession/` with `ID`.
- A command that prompts (e.g. `create user` with missing arguments) is answered by sending the next `COMMAND`.
- The listener binds all interfaces; it cannot be made localhost-only without changing OpenSim (see Global Constraints).

**Robust HTTP services and accounts**
- **Accounts:** `POST <PrivURL>:<PrivatePort>/accounts` with `METHOD=getaccount&FirstName=..&LastName=..` returns `result` key/value pairs including `PrincipalID` and `UserLevel`.
- **Authentication:** `POST …/auth` with `METHOD=authenticate&PRINCIPAL=<uuid>&PASSWORD=<md5hex(plain)>` returns a token on success. Robust expects the MD5 hex of the plain password, which is what `LLLoginService.cs:454-456` passes.
- **Readiness:** `GET <public>/get_grid_info` answers once Robust is serving.
- **User commands:** `create user [<first> [<last> [<pass> [<email> [<user id> [<model>]]]]]]` and `set user level [<first> [<last> [<level>]]]` are Robust console commands.

**RegionReady callback** (`RegionReadyModule.cs:264-293`)
- `[RegionReady] enabled=true` plus `alert_uri` POSTs JSON `{"alert":"region_ready","login":"enabled"|"disabled","region_name":..,"region_id":..}`.
- `OpenSimBase.NotifyDreamGridOfPidFireAndForget` POSTs `{"alert":"regionpid","login":"regionpid","region_name":..,"region_id":..,"pid":<int>}` to the same `alert_uri`.

**INI templates**
- **Robust:** `bin/Robust.HG.ini.proto`.
  - `[Const]` keys: `GridName`, `http_listener_port`, `BaseURL`, `PrivURL`, `PublicPort`, `PrivatePort`, `DiagnosticsPort`, `ApachePort`, `MachineID`, `jOpensimURL`.
  - `[DatabaseService] StorageProvider`/`ConnectionString` are at lines 284–285, with `Port=3306` and the `robust`/`robustuser` credentials.
  - `[Network] ConsoleUser`/`ConsolePass`/`ConsolePort` are at lines 215–219.
  - `BaseDirectory=C:\tmp\fsassets/data` and `SpoolDirectory=…/tmp` are at lines 300–301.
- **Region:** `bin/config-include/Proto/Robust/*.ini` (plan 1 established this).
  - `OpenSim.ini` uses `${Const|RegionFolderName}` for `regionload_regionsdir`, `PIDFile`, `ConsoleHistoryFile` and `snapshot_cache_directory`.
  - It includes `Include-Architecture="config-include/GridHypergrid.ini"`, which includes `config-include/GridCommon.ini`.
- **Other config defaults:**
  - `bin/config-include/FlotsamCache.ini` has `CacheDirectory=.\assetcache` (a backslash path, wrong on Linux).
  - YEngine reads `[YEngine] ScriptEnginesPath`.
  - BulletSim needs `[Startup] meshing = Meshmerizer`; ubODE uses `ubODEMeshmerizer` (plan 1).
- **First-boot estate prompts** are avoided by `[Estates] DefaultEstateName`, `DefaultEstateOwnerName`, `DefaultEstateOwnerUUID`, `DefaultEstateOwnerEMail` and `DefaultEstateOwnerPassword` (plan 1, `SmokeRun.cs`).

**Add-on traps** (plan 1 ledger)
- `Gloebit.dll` in `bin/` self-enables and aborts boot without its own MySQL config. Version 1 must not stage it.
- Wifi merges `bin/Wifi.ini`, which switches `[DatabaseService]` to Diva MySQL, unless `[WifiService]` exists. Version 1 sets `[WifiService] Enabled=false`.

## Global Constraints

- **Target framework** `net10.0` comes from `Directory.Build.props`; new projects do not set it.
- **New code** goes only under `Launcher/` and `tests/` plus `.github/workflows/dreamgrid-ci.yml`. Do not edit `OpenSim/` or `addon-modules/`, and do not edit any prototype file under `bin/`. The launcher writes generated copies elsewhere.
- **Default ports:**
  - web/legacy 8001,
  - Robust public 8002,
  - Robust private 8003,
  - region 8004 (UDP and TCP),
  - private MariaDB 3307 bound to `127.0.0.1`,
  - Robust REST console 8010 and region REST console 8011 (`[Network] ConsolePort`).
- **Database names and users:** `robust` / `robustuser` and `opensim` / `opensimuser`. Passwords are random, generated per install (spec §4.2). The MariaDB root password is random too, and the user is never asked for it (spec §4.14).
- **Owner avatar level is 200** (spec §6.2).
- **Version 1 contacts no third-party service** (spec §4.15). The generated INIs must contain no active (uncommented) line with `outworldz.com`, `hyperica.com` or `gloebit.com`; the `economymodule` keys are empty; `[SmartStart] Enabled=False`; `[WifiService] Enabled=false`; `Gloebit.dll` is never staged.
- **Remote consoles:** they cannot be bound to localhost without an OpenSim change. Use a dedicated `ConsolePort` with a random 32-character `ConsolePass` per install, and record this in `docs/SECURITY-NOTES.md` (Task 6). Version 1 is LAN-only (spec §3).
- **Children outlive the launcher** (spec §4.12). The launcher never holds the only handle to a child (no stdin control), and never kills children on its own shutdown unless the user asked for a full stop.
- **Data-directory layout.** `<data>` is from `--data`, else the `DREAMGRID_DATA` environment variable, else `%ProgramData%\DreamGrid` on Windows or `/var/lib/dreamgrid` on Linux:
  ```
  <data>/settings.json
  <data>/mariadb/data/            private MariaDB datadir
  <data>/runtime/opensim/         staged copy of the program's OpenSim bin + generated Robust/region INIs
  <data>/state/regions/<Instance>/        region instance ini dir (-inidirectory)
  <data>/state/regions/<Instance>/Region/ region definition files
  <data>/state/assetcache/  <data>/state/fsassets/data/  <data>/state/fsassets/tmp/
  <data>/state/scriptengines/  <data>/state/logs/  <data>/state/pids/
  ```
- **Program directory:** the OpenSim binaries come from `--opensim <dir>`, else `DREAMGRID_OPENSIM`. In development that is the repo's `bin/`. MariaDB binaries come from `--mariadb-bin <dir>`, else `DREAMGRID_MARIADB_BIN`, else `/usr/sbin` + `/usr/bin` on Linux.
- **Commits** end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Launcher restart while the region is running.** No duplicate Robust or region process starts, the dashboard state reads Running, and avatars in-world are not disconnected (Task 11 adoption test).
2. **Data path with spaces and non-ASCII characters** (`/var/lib/dream grid`, `C:\ProgramData\Dréam Grid`). MariaDB init, INI paths and OpenSim start must all work (Task 12 runs from such a path).
3. **Port already in use,** for example another MariaDB on 3307 or anything on 8002. Startup must fail with a message naming the port and the process kind, not hang (Task 7 and Task 10 tests).
4. **A child crashing in a loop** (bad config). The backoff stops after 5 failures in 10 minutes and the state shows Failed with the last log lines, rather than restarting forever (Task 8 test).
5. **Settings file corrupted or half-written** (power loss during save). Writes are atomic, and a corrupt file is reported, not silently replaced with defaults that would regenerate new database passwords and lock out the data (Task 2 tests).

---

### Task 1: Projects, paths and solution wiring

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher.Core/DreamGrid.Launcher.Core.csproj` (classlib; `PackageReference` `MySqlConnector` 2.6.2; `Nullable` enable; `ImplicitUsings` enable)
  - `Launcher/DreamGrid.Launcher/DreamGrid.Launcher.csproj` (`Microsoft.NET.Sdk.Web`; references Core; packages `Microsoft.Extensions.Hosting.Systemd` and `Microsoft.Extensions.Hosting.WindowsServices` 10.0.13)
  - `Launcher/DreamGrid.Launcher.Tests/DreamGrid.Launcher.Tests.csproj` (xUnit v3 exe, like `tests/DreamGrid.Platform.Tests`; references Core and Launcher)
  - `Launcher/DreamGrid.Launcher.Core/LauncherPaths.cs`
  - `Launcher/DreamGrid.Launcher.Tests/LauncherPathsTests.cs`
- Modify: `DreamGrid.sln` (add the three projects)

**Interfaces:**
- Produces:
  - `public sealed record LauncherPaths(string Data, string OpenSimProgram, string? MariaDbBin)`, with:
    - computed properties `SettingsFile`, `MariaDbData`, `RuntimeOpenSim`, `RegionsRoot`, `AssetCache`, `FsAssetsData`, `FsAssetsSpool`, `ScriptEngines`, `Logs`, `Pids`,
    - `string RegionDir(string instance)` and `string RegionDefinitionDir(string instance)`, laid out exactly as in Global Constraints.
  - `public static LauncherPaths LauncherPaths.Resolve(string[] args, IDictionary<string,string?> env, bool isWindows)`, with precedence argument → environment → OS default.
  - `public void LauncherPaths.EnsureCreated()`, which creates every directory.

- [ ] **Step 1: Write failing tests**
  - `Resolve_prefers_args_over_env_over_default`
  - `Default_data_dir_is_ProgramData_on_windows_and_var_lib_on_linux`: `isWindows: true` gives `Path.Combine(env["ProgramData"], "DreamGrid")`; false gives `/var/lib/dreamgrid`.
  - `RegionDir_and_RegionDefinitionDir_follow_the_layout`: `RegionDir("Welcome")` ends with `state/regions/Welcome` (OS separator) and `RegionDefinitionDir` ends with `state/regions/Welcome/Region`.
  - `EnsureCreated_handles_spaces_and_non_ascii`: data dir `"<temp>/Dréam Grid <guid>"`; every computed directory exists afterwards.
- [ ] **Step 2: Run and confirm they fail.** Run `dotnet test --project Launcher/DreamGrid.Launcher.Tests -c Release`. Expected: compile errors, `LauncherPaths` not defined.
- [ ] **Step 3: Implement `LauncherPaths`.**
- [ ] **Step 4: Run and confirm they pass.** Same command. Expected: 4 passed. Also `dotnet build -c Release DreamGrid.sln` gives 0 warnings and 0 errors.
- [ ] **Step 5: Commit** `git commit -m "Launcher: projects and data-directory layout"`.

---

### Task 2: Settings store (single source of truth, atomic, never silently reset)

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs`
  - `Launcher/DreamGrid.Launcher.Core/Settings/SettingsStore.cs`
  - `Launcher/DreamGrid.Launcher.Core/Settings/Secrets.cs`
  - `Launcher/DreamGrid.Launcher.Tests/SettingsStoreTests.cs`

**Interfaces:**
- Produces:
  - **`LauncherSettings`**, a JSON-serialized record with init-only properties:
    - `int SchemaVersion = 1`, `string GridName = "My World"`, `string? Hostname` (null means the detected LAN IP).
    - Ports: `int WebPort = 8001`, `int RobustPublicPort = 8002`, `int RobustPrivatePort = 8003`, `int RegionPort = 8004`, `int MariaDbPort = 3307`, `int RobustConsolePort = 8010`, `int RegionConsolePort = 8011`.
    - Secrets: `DatabaseSecrets Db`, `ConsoleSecrets Console`, `string MachineId`, `string? SetupToken`.
    - State: `SetupState Setup`, `RegionSettings? Region`.
    - Extensions: `Dictionary<string, Dictionary<string,string>> Modules`, `List<IniOverride> Overrides`.
  - Supporting records:
    - `DatabaseSecrets(string RootPassword, string RobustPassword, string OpensimPassword)`.
    - `ConsoleSecrets(string User, string Password)`.
    - `SetupState(bool WorldNamed, bool OwnerCreated, bool RegionCreated, bool Finished, OwnerInfo? Owner)`.
    - `OwnerInfo(string FirstName, string LastName, Guid PrincipalId)`; the password is never stored.
    - `RegionSettings(string Instance, string Name, Guid Uuid, int X = 1000, int Y = 1000, string Physics = "ubODE", string Content = "starter")`.
    - `IniOverride(string Target, string Section, string Key, string Value)`, where `Target` is `"robust"` or `"region"`.
  - **`static class Secrets`:** `string NewPassword(int length = 32)` (cryptographically random, `[A-Za-z0-9]` only, safe in INI and connection strings) and `string NewMachineId()`.
  - **`sealed class SettingsStore(string path)`:**
    - `LauncherSettings LoadOrCreate()` creates the file with fresh secrets and a fresh `SetupToken` only if it does not exist.
    - `void Save(LauncherSettings s)` writes to `path + ".tmp"`, flushes to disk, then does an atomic replace.
    - On Linux the file mode is 0600 (`File.SetUnixFileMode`).
    - A file that exists but does not parse throws `SettingsCorruptException(path, inner)`. It never regenerates.

- [ ] **Step 1: Write failing tests**
  - `LoadOrCreate_creates_file_with_unique_random_secrets`: two stores in two temp dirs give different `Db.RobustPassword`; length 32; matches `^[A-Za-z0-9]{32}$`.
  - `LoadOrCreate_returns_existing_values_unchanged`: create, save with `GridName="Avalon"`, load again; `GridName=="Avalon"` and the passwords are identical.
  - `Corrupt_file_throws_and_is_left_untouched`: write `{"GridName": ` (truncated). Load throws `SettingsCorruptException`, and the file bytes are unchanged afterwards.
  - `Save_is_atomic_leaves_no_tmp_file`: after `Save`, no `settings.json.tmp` exists.
  - `Unix_file_mode_is_owner_only`: `Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only")`; mode is `UserRead|UserWrite`.
- [ ] **Step 2: Run and confirm they fail** (compile errors).
- [ ] **Step 3: Implement.** Use `System.Text.Json` with `JsonSerializerDefaults.Web` and indented output.
- [ ] **Step 4: Run and confirm they pass.**
- [ ] **Step 5: Commit** `git commit -m "Launcher: settings store with generated secrets and atomic writes"`.

---

### Task 3: INI document that preserves the prototypes

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Ini/IniDocument.cs`, `Launcher/DreamGrid.Launcher.Tests/IniDocumentTests.cs`

**Interfaces:**
- Produces `public sealed class IniDocument`:
  - `static IniDocument Parse(string text)` and `static IniDocument Load(string path)`.
  - `void Set(string section, string key, string value)`:
    - It replaces the value of the **last active** (uncommented) `key=` line in the section (Nini's last-wins). Key and section matching is case-insensitive.
    - If the key has no active line, it appends `key=value` after the section's last active line.
    - If the section is missing, it appends `\n[section]\nkey=value` at the end of the file.
  - `string? Get(string section, string key)` returns the active value with surrounding quotes stripped.
  - `void CommentOut(string section, string key)` prefixes every active line for that key with `; `.
  - `void ReplaceBlock(string section, string startMarker, string endMarker, IEnumerable<string> lines)` replaces the lines between two marker comment lines; if the markers are missing, it adds both. This is for PayPal later (spec §6.7).
  - `IEnumerable<(string Section, string Key, string Value)> ActiveEntries()`.
  - `string ToText()` and `void Save(string path)`. Line endings are kept as `\n` on Linux and `\r\n` on Windows (`Environment.NewLine`).
  - Comments (`;`, `#`), blank lines, ordering and the `;# {..}` documentation lines are preserved byte-for-byte except for the lines changed.

- [ ] **Step 1: Write failing tests**
  - `Set_replaces_value_and_keeps_comments`: input `"[Const]\n; note\nBaseURL=http://x\n"` with `Set("Const","BaseURL","http://y")` gives `"[Const]\n; note\nBaseURL=http://y\n"`.
  - `Set_replaces_last_active_occurrence_only`: two `enabled=` lines in `[Chat]`; only the last changes.
  - `Set_ignores_commented_lines`: `"; enabled=true"` stays commented; a new `enabled=false` is appended.
  - `Set_appends_missing_section`: `Set("SmartStart","Enabled","False")` on text without that section ends with `"[SmartStart]\nEnabled=False\n"`.
  - `Section_and_key_match_case_insensitively`.
  - `Get_strips_quotes`: `Include-Architecture="config-include/GridHypergrid.ini"` gives `config-include/GridHypergrid.ini`.
  - `CommentOut_disables_every_active_line`.
  - `ReplaceBlock_inserts_markers_when_missing_and_replaces_between_them`.
  - `Round_trip_of_every_prototype_is_lossless`: for each of `bin/Robust.HG.ini.proto` and `bin/config-include/Proto/Robust/{OpenSim,GridCommon,GridHypergrid,Grid}.ini`, `Parse(text).ToText() == text` after normalizing line endings. Find the repo root by walking up to `DreamGrid.sln`.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement** as a line list with a section index. Do not use Nini; it loses comments.
- [ ] **Step 4: Run and confirm they pass.**
- [ ] **Step 5: Commit** `git commit -m "Launcher: comment-preserving INI document"`.

---

### Task 4: Runtime staging (program bin → `<data>/runtime/opensim`)

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Runtime/RuntimeStager.cs`, `Launcher/DreamGrid.Launcher.Tests/RuntimeStagerTests.cs`

**Interfaces:**
- Consumes `LauncherPaths` (Task 1).
- Produces `public sealed class RuntimeStager(LauncherPaths paths)`:
  - `bool StageIfNeeded()`:
    - **Version stamp:** `"<OpenSim.dll length>-<OpenSim.dll last-write ticks UTC>-<count of top-level files>"`, written to `<runtime>/.dreamgrid-stamp`.
    - **When stamps match:** nothing happens and it returns false.
    - **When they differ:** it deletes `<runtime>/opensim` and recopies, then returns true.
    - **Skipped while copying:**
      - `Gloebit.dll` (spec §4.15),
      - directories named `addin-db-*`, `assetcache`, `Regions`, `ScriptEngines`, `fsassets`, `MeshCache`, `j2kDecodeCache`, `maptiles`, `bakes`,
      - files `*.log`, `*.db`, `*.pid`, `OpenSim.ini`.
  - `void WriteLogConfigs(string? regionInstance, string level = "INFO")`: writes `<runtime>/dreamgrid-robust.log4net.xml` and, if `regionInstance` is set, `<runtime>/dreamgrid-region-<instance>.log4net.xml`. They are log4net `RollingFileAppender` configs with absolute `<file value=…>` paths of `paths.Logs/robust.log` and `paths.Logs/region-<instance>.log`, using forward slashes and XML-escaped. Root level = `level`; max 10 MB, 5 backups.
- Never touches anything under `paths.Data` other than `runtime/`.

- [ ] **Step 1: Write failing tests,** using a fake program dir with `OpenSim.dll` (any bytes), `Gloebit.dll`, `addin-db-002/x`, `config-include/a.ini` and `x.log`:
  - `First_stage_copies_and_skips_excluded_items`: returns true; `config-include/a.ini` copied; `Gloebit.dll`, `addin-db-002`, `x.log` absent.
  - `Second_stage_with_same_program_is_a_no_op`: returns false; a file added to the runtime copy in between survives.
  - `Changed_program_restages`: touch `OpenSim.dll` (new write time); returns true; the stray file is gone.
  - `Log_configs_use_absolute_forward_slash_paths_under_Logs`: parse the XML; `file/@value` equals `Path.Combine(paths.Logs,"robust.log").Replace('\\','/')`.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and confirm they pass.**
- [ ] **Step 5: Commit** `git commit -m "Launcher: stage a runtime copy of OpenSim outside the program dir"`.

---

### Task 5: Grid configuration writer (prototypes → Robust and region INIs)

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher.Core/Config/GridConfigWriter.cs`
  - `Launcher/DreamGrid.Launcher.Core/Config/HostAddress.cs`
  - `Launcher/DreamGrid.Launcher.Tests/GridConfigWriterTests.cs`

**Interfaces:**
- Consumes `LauncherSettings` (Task 2), `IniDocument` (Task 3) and `LauncherPaths` (Task 1).
- Produces:
  - `public static class HostAddress { string Detect(); }`: the LAN IPv4 of the default route. Open a UDP socket "connected" to `8.8.8.8:53` (no packet is sent) and read the local endpoint; fall back to `127.0.0.1`.
  - `public sealed class GridConfigWriter(LauncherPaths paths)`, with `void Write(LauncherSettings s, IReadOnlyList<IIniContributor> contributors)`. It writes:
    1. `<runtime>/Robust.HG.ini` from `<runtime>/Robust.HG.ini.proto`.
    2. `<runtime>/config-include/GridCommon.ini` from `<runtime>/config-include/Proto/Robust/GridCommon.ini`.
    3. `<runtime>/config-include/FlotsamCache.ini` from `FlotsamCache.ini.example`.
    4. `paths.RegionDir(instance)/OpenSim.ini` from `<runtime>/config-include/Proto/Robust/OpenSim.ini`.
    5. `paths.RegionDefinitionDir(instance)/<Name>.ini`, only if `s.Region` is set.
    6. `s.Overrides` and contributor entries are applied last.
  - `public interface IIniContributor { IEnumerable<IniOverride> Contribute(LauncherSettings s); }`, the module seam from spec §6.9.

**Keys the writer sets.** `host` = `s.Hostname ?? HostAddress.Detect()`; `dbConn(db,user,pw)` = `Data Source=127.0.0.1;Database={db};Port={MariaDbPort};User ID={user};Password={pw};Old Guids=true;Allow Zero Datetime=true;Connect Timeout=60;`. Values are exact:

| File | Section | Key = value |
|---|---|---|
| Robust.HG.ini | Const | `GridName={GridName}`, `http_listener_port={RobustPublicPort}`, `BaseURL=http://{host}`, `PrivURL=http://127.0.0.1`, `PublicPort={RobustPublicPort}`, `PrivatePort={RobustPrivatePort}`, `DiagnosticsPort={WebPort}`, `MachineID={MachineId}` |
| Robust.HG.ini | DatabaseService | `StorageProvider="OpenSim.Data.MySQL.dll"`, `ConnectionString="{dbConn(robust,robustuser,Db.RobustPassword)}"` |
| Robust.HG.ini | Network | `ConsoleUser={Console.User}`, `ConsolePass={Console.Password}`, `ConsolePort={RobustConsolePort}` |
| Robust.HG.ini | (the section holding `BaseDirectory`/`SpoolDirectory`, found by key) | `BaseDirectory={FsAssetsData}`, `SpoolDirectory={FsAssetsSpool}` (forward slashes) |
| Robust.HG.ini | SmartStart | `Enabled=False`, `URL=http://127.0.0.1:{WebPort}`, `MachineID={MachineId}` |
| Robust.HG.ini | WifiService | `Enabled=false` |
| Robust.HG.ini | GridInfoService | `welcome=http://{host}:{WebPort}/welcome`, `gridname={GridName}` |
| GridCommon.ini | DatabaseService | `StorageProvider="OpenSim.Data.MySQL.dll"`, `ConnectionString="{dbConn(opensim,opensimuser,Db.OpensimPassword)}"` |
| FlotsamCache.ini | AssetCache | `CacheDirectory={AssetCache}` (forward slashes) |
| region OpenSim.ini | Const | `BaseHostname={host}`, `BaseURL=http://{host}`, `PublicPort={RobustPublicPort}`, `PrivatePort={RobustPrivatePort}`, `PrivURL=http://127.0.0.1`, `http_listener_port={RegionPort}`, `RegionFolderName={Instance}`, `DiagnosticsPort={WebPort}` |
| region OpenSim.ini | Startup | `regionload_regionsdir={RegionDefinitionDir}/`, `PIDFile={Pids}/{Instance}.pid`, `ConsoleHistoryFile={RegionDir}/ConsoleHistory.txt`, `physics={Physics}`, `meshing=` `Meshmerizer` if Physics is `BulletSim`, else `ubODEMeshmerizer` |
| region OpenSim.ini | Network | `ConsoleUser`, `ConsolePass` as above; `ConsolePort={RegionConsolePort}` |
| region OpenSim.ini | Estates | `DefaultEstateName={GridName} Estate`, `DefaultEstateOwnerName={Owner.FirstName} {Owner.LastName}`, `DefaultEstateOwnerUUID={Owner.PrincipalId}` (only once the owner exists) |
| region OpenSim.ini | RegionReady | `enabled=true`, `alert_uri=http://127.0.0.1:{WebPort}/dreamgrid/regionready` |
| region OpenSim.ini | SmartStart | `Enabled=False`, `URL=http://127.0.0.1:{WebPort}`, `MachineID={MachineId}` |
| region OpenSim.ini | WifiService | `Enabled=false` |
| region OpenSim.ini | YEngine | `ScriptEnginesPath={ScriptEngines}` |
| region `<Name>.ini` | `<Name>` | `RegionUUID={Uuid}`, `Location={X},{Y}`, `InternalAddress=0.0.0.0`, `InternalPort={RegionPort}`, `ExternalHostName={host}`, `SizeX=256`, `SizeY=256` |

**Third parties inactive (spec §4.15).** In all generated files, every active line whose value contains `outworldz.com`, `hyperica.com` or `gloebit.com` is commented out with `CommentOut`. Every active `economymodule` key is set to empty. The prototypes on disk are untouched; only the generated copies change.

- [ ] **Step 1: Write failing tests.** Run against a temp runtime that holds copies of the real prototype files from the repo.
  - `Robust_ini_has_private_db_port_and_generated_password`: `Get("DatabaseService","ConnectionString")` contains `Port=3307` and `Db.RobustPassword`, and not `robustpassword`.
  - `Region_ini_points_state_dirs_outside_runtime`: `regionload_regionsdir`, `PIDFile`, `ScriptEnginesPath` and `CacheDirectory` all start with `paths.Data` and none contains `runtime`.
  - `BulletSim_gets_Meshmerizer_and_ubODE_gets_ubODEMeshmerizer`.
  - `No_active_third_party_urls_in_any_generated_file`: across all generated files, `ActiveEntries()` has no value containing `outworldz.com`, `hyperica.com` or `gloebit.com`, and every `economymodule` value is empty.
  - `SmartStart_disabled_and_Wifi_disabled_in_both`.
  - `Region_definition_file_named_after_region_with_matching_section`: `<Name>.ini` has section `[<Name>]` (a spec §6.7 rule).
  - `Estates_keys_present_only_after_owner_created`.
  - `Overrides_and_contributors_win_over_generated_values`: `IniOverride("region","Startup","physics","BulletSim")` gives `physics=BulletSim`.
  - `Paths_with_spaces_and_non_ascii_survive`: data dir `"Dréam Grid"`; the generated `CacheDirectory` round-trips through `Get`.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and confirm they pass.**
- [ ] **Step 5: Commit** `git commit -m "Launcher: generate Robust and region INIs from the prototypes"`.

---

### Task 6: OpenSim REST console client and Robust HTTP client

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher.Core/OpenSim/RestConsoleClient.cs`
  - `Launcher/DreamGrid.Launcher.Core/OpenSim/RobustClient.cs`
  - `Launcher/DreamGrid.Launcher.Core/OpenSim/RegionAlert.cs`
  - `Launcher/DreamGrid.Launcher.Tests/RestConsoleClientTests.cs`, `RobustClientTests.cs`, `RegionAlertTests.cs`
  - `docs/SECURITY-NOTES.md`

**Interfaces:**
- Produces:
  - **`public sealed class RestConsoleClient(HttpClient http, Uri baseUri, string user, string password)`:**
    - `Task<bool> IsUpAsync(CancellationToken)` is true when `/StartSession/` answers with a SessionID.
    - `Task<IReadOnlyList<string>> RunAsync(string command, TimeSpan settle, CancellationToken)`: opens a session (cached and reused), sends the command, then polls `/ReadResponses/<id>/` every 250 ms until no new lines have arrived for `settle`. It returns the non-command `Line` texts.
    - `Task SendAsync(string command, CancellationToken)` sends without reading.
    - `Task CloseAsync()`.
    - A 401/403 or an empty SessionID throws `ConsoleAuthException`.
  - **`public interface IRobustClient`** with the three methods below, so later tasks can use a test double.
  - **`public sealed class RobustClient(HttpClient http, Uri privateBase, Uri publicBase) : IRobustClient`:**
    - `Task<bool> IsReadyAsync(CancellationToken)`: `GET {public}/get_grid_info` returns 200 with a body containing `<gridinfo>` or `gridname`.
    - `Task<RobustAccount?> GetAccountAsync(string first, string last, CancellationToken)`: `POST {private}/accounts` with `METHOD=getaccount&FirstName=&LastName=`; parse the `result` key/value XML (`ServerUtils` reply format).
    - `Task<bool> AuthenticateAsync(Guid principal, string plainPassword, CancellationToken)`: `POST {private}/auth`, `METHOD=authenticate&PRINCIPAL=&PASSWORD=<lowercase md5 hex of plainPassword>`. True iff the reply has a non-empty token.
  - **`public sealed record RobustAccount(Guid PrincipalId, string FirstName, string LastName, int UserLevel, string Email)`.**
  - **`public sealed record RegionAlert(string Alert, string Login, string RegionName, Guid RegionId, int? Pid)`** with `static RegionAlert? Parse(string json)`. It accepts both payload shapes listed in Facts.

- [ ] **Step 1: Write failing tests.** Use a `HttpMessageHandler` stub that records requests and returns canned bodies copied from the formats in Facts.
  - `StartSession_posts_USER_and_PASS_form_and_reads_SessionID`.
  - `RunAsync_returns_line_texts_and_skips_echoed_command_lines`: canned `ReadResponses` with one `Command="True"` line and two output lines gives the two output texts.
  - `Bad_credentials_throw_ConsoleAuthException`.
  - `Authenticate_sends_md5_hex_of_password`: for `"secret"` the posted `PASSWORD` is `5ebe2294ecd0e0f08eab7690d2a6ee69`.
  - `GetAccount_parses_PrincipalID_and_UserLevel`, using a canned reply:
    ```xml
    <ServerResponse><result type="List"><PrincipalID>…</PrincipalID><UserLevel>200</UserLevel>…</result></ServerResponse>
    ```
  - `GetAccount_returns_null_on_result_null`.
  - `RegionAlert_parses_region_ready_and_regionpid_payloads`.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.** Write `docs/SECURITY-NOTES.md` with one section, "Remote consoles". It explains that the Robust and region REST consoles listen on ports 8010 and 8011 on all interfaces with a random 32-character password from `settings.json`. Version 1 is LAN-only, and the firewall must not forward those ports.
- [ ] **Step 4: Run and confirm they pass.**
- [ ] **Step 5: Commit** `git commit -m "Launcher: REST console and Robust HTTP clients"`.

---

### Task 7: Private MariaDB instance

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher.Core/Database/MariaDbServer.cs`
  - `Launcher/DreamGrid.Launcher.Core/Database/DatabaseProvisioner.cs`
  - `Launcher/DreamGrid.Launcher.Core/Database/MariaDbBinaries.cs`
  - `Launcher/DreamGrid.Launcher.Tests/MariaDbBinariesTests.cs`
  - `Launcher/DreamGrid.Launcher.IntegrationTests/DreamGrid.Launcher.IntegrationTests.csproj` (xUnit v3 exe, references Core and Launcher)
  - `Launcher/DreamGrid.Launcher.IntegrationTests/MariaDbServerTests.cs`
  - `Launcher/DreamGrid.Launcher.IntegrationTests/IntegrationEnvironment.cs`
- Modify: `DreamGrid.sln`

**Interfaces:**
- Consumes `LauncherPaths` and `LauncherSettings`.
- Produces:
  - **`public sealed record MariaDbBinaries(string Server, string InstallDb, string? Admin)`** with `static MariaDbBinaries Locate(string? binDir, bool isWindows)`:
    - It looks for `mariadbd[.exe]` (falling back to `mysqld[.exe]`) and `mariadb-install-db[.exe]` (falling back to `mysql_install_db[.exe]`).
    - On Linux, with `binDir` null, it searches `/usr/sbin` and `/usr/bin`.
    - If something is missing it throws `MariaDbNotFoundException` naming each path searched.
  - **`public sealed class MariaDbServer(MariaDbBinaries bin, LauncherPaths paths, LauncherSettings s)`:**
    - `bool IsInitialized` is true iff `paths.MariaDbData/mysql` exists.
    - `Task InitializeAsync(CancellationToken)`:
      - **Windows:** run `mariadb-install-db.exe --datadir=<dir> --password=<RootPassword>`. Root has a password from the start.
      - **Linux:**
        1. Run `mariadb-install-db --no-defaults --datadir=<dir> --auth-root-authentication-method=normal --skip-test-db`. If the launcher runs as root, add `--user=root`; the service account case is plan 3's.
        2. Start the server once (as in `StartInfo()`).
        3. Connect over TCP to 127.0.0.1 as root with an empty password, and run `ALTER USER 'root'@'localhost' IDENTIFIED BY '<RootPassword>'`.
        4. Shut the server down.
      - Afterwards root always needs `RootPassword`.
    - `ProcessStartInfo StartInfo()`:
      - Arguments: `--no-defaults --datadir=<dir> --port=<MariaDbPort> --bind-address=127.0.0.1 --skip-name-resolve --character-set-server=utf8mb4 --collation-server=utf8mb4_unicode_ci --pid-file=<Pids>/mariadb.pid --log-error=<Logs>/mariadb.err`.
      - On Linux add `--socket=<data>/mariadb/mariadb.sock`.
      - Supervision is Task 8's job; this only builds the start info.
    - `Task<bool> PingAsync(CancellationToken)`: `SELECT 1` as root over TCP to 127.0.0.1:port.
    - `Task ShutdownAsync(CancellationToken)`: the `SHUTDOWN` statement as root.
  - **`public sealed class DatabaseProvisioner(LauncherSettings s)`** with `Task EnsureAsync(CancellationToken)`:
    - It is idempotent: `CREATE DATABASE IF NOT EXISTS robust`, then `opensim`, both `CHARACTER SET utf8mb4`.
    - `CREATE USER IF NOT EXISTS 'robustuser'@'localhost' IDENTIFIED BY …` and `ALTER USER … IDENTIFIED BY …` (so a changed password is applied), the same for `'@'127.0.0.1'`, and `GRANT ALL ON robust.* TO …`. The same again for `opensim`/`opensimuser`.
  - **`IntegrationEnvironment`:** `static string? MariaDbBin => Environment.GetEnvironmentVariable("DREAMGRID_MARIADB_BIN")`. On Linux, `"/usr/sbin"` counts as present when `/usr/sbin/mariadbd` exists. `static string OpenSimBin` is the repo `bin/`, which must contain `OpenSim.dll`. Tests that need MariaDB call `Assert.Skip(...)` when it is absent.

- [ ] **Step 1: Write failing tests**
  - Unit test `Locate_reports_every_searched_path_when_missing`.
  - Integration test `Initialize_start_provision_and_connect_as_robustuser`:
    - temp data dir `"<temp>/Dréam Grid <guid>"`, a random free port, then initialize, start the process (a plain `Process` here), poll `PingAsync` for up to 60 s, `EnsureAsync`;
    - open a `MySqlConnection` as `robustuser` with the generated password to database `robust`; `SELECT DATABASE()` returns `robust`;
    - `ShutdownAsync`; the process exits within 30 s.
  - Integration test `Provisioning_twice_is_idempotent`.
  - Integration test `Port_in_use_fails_fast_with_named_port`: hold a `TcpListener` on the chosen port, start the server, then wait up to 20 s for exit. Expect the process to exit with a non-zero code, and `mariadb.err` to contain `Address already in use` or `Can't start server` (MariaDB's wording). The launcher-level message is Task 10's.
- [ ] **Step 2: Run and confirm they fail.** Unit: `dotnet test --project Launcher/DreamGrid.Launcher.Tests -c Release`. Integration: `dotnet test --project Launcher/DreamGrid.Launcher.IntegrationTests -c Release` with `DREAMGRID_MARIADB_BIN` set.
  - **Windows dev:** download `https://archive.mariadb.org/mariadb-11.4.9/winx64-packages/mariadb-11.4.9-winx64.zip` and extract it to a folder outside the repo; the variable points at its `bin`.
  - **Debian:** `apt-get install -y mariadb-server`, then `systemctl disable --now mariadb` if systemd is present, so the distro service is not running (spec §4.14).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and confirm they pass,** on Windows and in a `debian:12` container (plan 1's Docker approach: `apt-get install mariadb-server`, no service started).
- [ ] **Step 5: Commit** `git commit -m "Launcher: private MariaDB instance and idempotent provisioning"`.

---

### Task 8: Supervised child processes with adoption

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher.Core/Processes/ManagedProcess.cs`
  - `Launcher/DreamGrid.Launcher.Core/Processes/ProcessState.cs`
  - `Launcher/DreamGrid.Launcher.Core/Processes/PidFile.cs`
  - `Launcher/DreamGrid.Launcher.Core/Processes/RestartPolicy.cs`
  - `Launcher/DreamGrid.Launcher.Tests/ManagedProcessTests.cs`
  - `tests/DreamGrid.FakeChild/DreamGrid.FakeChild.csproj` + `Program.cs`
- Modify: `DreamGrid.sln`

**Interfaces:**
- Produces:
  - `public enum ProcessState { Stopped, Starting, Running, Stopping, Crashed, Failed }`.
  - `public sealed record ProcessSpec(string Name, Func<ProcessStartInfo> StartInfo, string PidFile, Func<CancellationToken, Task<bool>> IsHealthy, Func<CancellationToken, Task> GracefulStop, TimeSpan StartTimeout, TimeSpan StopTimeout, string? ExpectedImage)`. `ExpectedImage` is a substring the adopted process's main module path or command line must contain, for example `OpenSim.dll`.
  - `public sealed record RestartPolicy(int MaxFailures = 5, TimeSpan Window = default /* 10 min */, TimeSpan InitialDelay = default /* 2 s */, TimeSpan MaxDelay = default /* 60 s */)`, with exponential backoff.
  - **`public sealed class ManagedProcess(ProcessSpec spec, RestartPolicy policy, TimeProvider time)`:**
    - Members: `ProcessState State`, `int? Pid`, `string? LastError`, `event Action<ProcessState>? StateChanged`.
    - `Task StartAsync(CancellationToken)`:
      - **Adopt or start:** if `PidFile` names a live process whose image matches `ExpectedImage`, adopt it (state Running once `IsHealthy`). Otherwise start a new process and write `PidFile`.
      - **Detached start:** `UseShellExecute=false`, no redirected stdin, stdout/stderr redirected and discarded. The process is not tied to the launcher's lifetime. On Linux, start through `/usr/bin/setsid` when present so it leaves the launcher's process group.
      - **Health wait:** poll `IsHealthy` until `StartTimeout`.
    - `Task StopAsync(CancellationToken)`: `GracefulStop`, then wait `StopTimeout`, then kill the process tree, then delete `PidFile`.
    - `Task MonitorAsync(CancellationToken)`: a loop that detects exit through `Process.WaitForExitAsync` for started processes, or by polling the PID every 2 s for adopted ones. An unexpected exit goes to Crashed, then restart per policy. After `MaxFailures` within `Window` the state is Failed with `LastError` set. Monitoring stops when the token is cancelled, and **the child is not stopped**.
  - **`PidFile`:** `static int? Read(string path)` and `static void Write(string path, int pid)`.
  - **`tests/DreamGrid.FakeChild`:** a console app. With `--crash` it exits code 3 after 200 ms. With `--serve <port>` it answers HTTP 200 on `/health`, exits on `/quit`, and runs until killed.

- [ ] **Step 1: Write failing tests,** using `FakeChild` and `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`, NuGet, MIT.
  - `Start_writes_pid_and_becomes_Running_when_healthy`.
  - `Stop_uses_graceful_stop_then_deletes_pid_file`.
  - `Adopts_live_process_from_pid_file_instead_of_starting_another`: start FakeChild, then create a *second* `ManagedProcess` with the same `PidFile`, then `StartAsync`. Assert that only one FakeChild process exists (the same PID), and the state is Running.
  - `Stale_pid_of_unrelated_process_is_not_adopted`: write the PID of the test runner itself; the image doesn't match `ExpectedImage`, so a new process starts.
  - `Crash_loop_stops_at_MaxFailures_and_reports_Failed` (Review Focus 4): `--crash` with policy 3 failures per 10 min; advance the fake time; the state is Failed after the 3rd crash and no 4th start happens.
  - `Cancelling_monitor_leaves_child_running` (spec §4.12): cancel the token; the FakeChild PID is still alive; then kill it in cleanup.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and confirm they pass** on Windows and in `debian:12`.
- [ ] **Step 5: Commit** `git commit -m "Launcher: supervised child processes that survive and are re-adopted"`.

---

### Task 9: Island terrain and region provisioning helpers

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher.Core/Grid/IslandTerrain.cs`
  - `Launcher/DreamGrid.Launcher.Core/Grid/StarterContent.cs`
  - `Launcher/DreamGrid.Launcher.Tests/IslandTerrainTests.cs`
  - `content/README.md`

**Interfaces:**
- Produces:
  - **`public static class IslandTerrain`** with `void WriteR32(string path, int size = 256, int seed = 1)`. It writes `size*size` little-endian `float32` heights, row-major (OpenSim's `.r32` format), as a gently rolling island:
    - Height is 0–25 m in the middle, falling to 15 m below water (water level 20 m) near the edges.
    - Center height is 20 + 5 m; corner height is below 10 m.
    - A seeded value-noise ripple of ±1.5 m is added.
    - It is deterministic for a given seed.
  - **`public static class StarterContent`** with `string? StarterOar(string programRoot)`. It returns `<programRoot>/content/starter-region.oar` if that file exists, else null (spec §6.3: the owner supplies it later, and swapping needs no code change).

- [ ] **Step 1: Write failing tests**
  - `R32_has_expected_size`: the file length is 256×256×4.
  - `Center_is_land_and_corners_are_water`: read back; `h[128,128] > 20`; all four corners `< 20`.
  - `Same_seed_same_bytes`.
  - `StarterOar_null_when_absent_path_when_present`.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.** `content/README.md` says: "Put the starter region at `content/starter-region.oar`. The launcher loads it into the first region when present; otherwise it generates an island."
- [ ] **Step 4: Run and confirm they pass.**
- [ ] **Step 5: Commit** `git commit -m "Launcher: generated island terrain and starter OAR lookup"`.

---

### Task 10: Grid controller (start order, provisioning, status)

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher.Core/Grid/GridController.cs`
  - `Launcher/DreamGrid.Launcher.Core/Grid/GridStatus.cs`
  - `Launcher/DreamGrid.Launcher.Core/Grid/PortCheck.cs`
  - `Launcher/DreamGrid.Launcher.Core/Modules/IFeatureModule.cs`
  - `Launcher/DreamGrid.Launcher.Core/Modules/LogLevelModule.cs`
  - `Launcher/DreamGrid.Launcher.Tests/PortCheckTests.cs`, `LogLevelModuleTests.cs`
  - `Launcher/DreamGrid.Launcher.IntegrationTests/GridBootTests.cs`

**Interfaces:**
- Consumes everything from Tasks 1–9.
- Produces:
  - **Status records:**
    - `public sealed record ComponentStatus(string Name, ProcessState State, int? Pid, TimeSpan? Uptime, long? MemoryBytes, string? LastError)`.
    - `public sealed record GridStatus(ComponentStatus Database, ComponentStatus Robust, ComponentStatus? Region, bool LoginsEnabled, string LoginUri)`, where `LoginUri` is `http://{host}:{RobustPublicPort}`.
  - **`public static class PortCheck`** with `string? FindConflict(LauncherSettings s)`. For each of MariaDb, RobustPublic, RobustPrivate, Region (TCP and UDP), RobustConsole, RegionConsole and Web, it tries to bind on `0.0.0.0` (`127.0.0.1` for MariaDB). The return is null if all are free, else `"Port 8002 (Robust public) is already in use by another program."`. Ports held by the launcher's own adopted children are excluded by the caller.
  - **Command records** (in `Launcher/DreamGrid.Launcher.Core/Api/LegacyCommand.cs`, created in this task; Task 11 implements the API that runs them):
    - `public sealed record LegacyCommand(string Name, int MinLevel, Func<LegacyRequest, CancellationToken, Task<string>> Run)`.
    - `public sealed record LegacyRequest(string Command, string? Avatar, string? Password, string? RegionName, Guid? RegionUuid, IReadOnlyDictionary<string,string> Raw)`.
  - **`public interface IFeatureModule : IIniContributor`**, with `string Id`, `string Title`, `IReadOnlyDictionary<string,string> Defaults`, and `IEnumerable<LegacyCommand> Commands => []`.
  - **`public sealed class LogLevelModule : IFeatureModule`** (spec §6.9 proving module):
    - `Id="loglevel"` and `Defaults={"level":"INFO"}`.
    - The controller passes `settings.Modules["loglevel"]["level"]` (default `INFO`) to `RuntimeStager.WriteLogConfigs(instance, level)`.
    - It contributes no INI keys.
  - **`public interface IGridController`** with the public members of `GridController` below, so Task 11 can use a test double.
  - **`public sealed class GridController(LauncherPaths paths, SettingsStore store, MariaDbBinaries mariaBin, IEnumerable<IFeatureModule> modules, IRobustClient robust, IHttpClientFactory http, TimeProvider time) : IGridController`:**
    - **`Task StartAsync(CancellationToken)`.** Each step must finish before the next:
      1. Load settings.
      2. `RuntimeStager.StageIfNeeded()` + `WriteLogConfigs`.
      3. `GridConfigWriter.Write`.
      4. Port conflicts: if any (excluding adoptable children) → throw `PortConflictException(message)`.
      5. MariaDB: initialize if needed; start and adopt (`ManagedProcess`, health = `PingAsync`); `DatabaseProvisioner.EnsureAsync`.
      6. Robust: start and adopt. Working directory = runtime; `dotnet Robust.dll -inifile=Robust.HG.ini -console=rest -logconfig=dreamgrid-robust.log4net.xml`; health = `RobustClient.IsReadyAsync`; graceful stop = console `quit`; start timeout 120 s.
      7. Region, only if `Setup.RegionCreated`: start and adopt. Working directory = runtime; `dotnet OpenSim.dll -inidirectory=<RegionDir> -console=rest -logconfig=dreamgrid-region-<instance>.log4net.xml`; health = region console `IsUpAsync`; graceful stop = console `quit`; start timeout 300 s.
    - **`Task StopAllAsync(CancellationToken)`:** reverse order. Region, Robust, then MariaDB `ShutdownAsync`.
    - **`Task DetachAsync()`:** stop monitoring and leave the children running; used on launcher shutdown.
    - **`Task<RobustAccount> CreateOwnerAsync(string first, string last, string password, string? email, CancellationToken)`:**
      - Robust console `create user {first} {last} {password} {email ?? ""} {newGuid}`. If the next read shows a prompt line (a `Prompt="True"` line), send an empty line for each remaining prompt.
      - Then `set user level {first} {last} 200`, then `GetAccountAsync` must return `UserLevel == 200`.
      - Save `Setup.OwnerCreated` and `Owner`, then regenerate the INIs so the `[Estates]` keys appear.
      - Passwords containing spaces are rejected *before* sending (`ArgumentException`), because the console splits on spaces.
    - **`Task CreateRegionAsync(string name, string content /* "starter" | "island" */, CancellationToken)`:**
      - Settings: `Region = new RegionSettings(Instance: SafeFolderName(name), Name: name, Uuid: Guid.NewGuid())`, then regenerate the INIs and start the region.
      - Content: if `content=="starter"` and `StarterContent.StarterOar(...)` is not null, `load oar --force-terrain --force-parcels <path>` on the region console. Otherwise `IslandTerrain.WriteR32(<RegionDir>/island.r32)` + `terrain load <path>`.
      - Wait for `LoginsEnabled` (Task 11's alert endpoint sets it) up to 300 s, then save `Setup.RegionCreated`.
    - **`GridStatus GetStatus()`.**
    - **`void OnRegionAlert(RegionAlert a)`** sets `LoginsEnabled` on `login=="enabled"`, clears it on `"disabled"`, and records the PID on `regionpid`.

- [ ] **Step 1: Write failing tests**
  - Unit `PortCheck_reports_first_busy_port_by_name`: hold a listener on RobustPublicPort; the message equals `"Port <n> (Robust public) is already in use by another program."` exactly.
  - Unit `LogLevelModule_sets_level_in_generated_log_config`.
  - Integration `GridBootTests.Fresh_grid_boots_owner_and_region` (needs MariaDB and a built `bin/`; skip otherwise):
    - temp data dir `"<temp>/Dréam Grid <guid>"`, ports from a free range;
    - `StartAsync` (no region yet): Database and Robust are Running;
    - `CreateOwnerAsync("Test","Owner","pass-word1",null)`: `UserLevel==200`, and `AuthenticateAsync(principal,"pass-word1")` is true while `AuthenticateAsync(principal,"wrong")` is false;
    - `CreateRegionAsync("Welcome","island")`: `LoginsEnabled` becomes true within 300 s;
    - `GridStatus.LoginUri == $"http://{host}:{RobustPublicPort}"`;
    - `StopAllAsync`: no `OpenSim.dll` or `Robust.dll` processes started by the test remain, and MariaDB exits.
  - Integration `Port_conflict_on_Robust_public_port_is_reported_before_anything_starts`: hold the port; `StartAsync` throws `PortConflictException` whose message names the port; no MariaDB process was started.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.** Integration failures are where the Facts may be wrong. If Robust does not boot with the generated INI, read `<Logs>/robust.log`, fix the **writer** (Task 5 table), and add the key to the Task 5 tests. Do not hand-edit generated files. Record every key added this way in the commit message.
- [ ] **Step 4: Run and confirm they pass** on Windows and in `debian:12` and `debian:13` (plan 1's Docker approach plus `apt-get install mariadb-server`).
- [ ] **Step 5: Commit** `git commit -m "Launcher: grid controller boots MariaDB, Robust and the first region"`.

---

### Task 11: Launcher host: port-8001 contract, hosted service, OS service integration

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher/Program.cs`
  - `Launcher/DreamGrid.Launcher/GridHostedService.cs`
  - `Launcher/DreamGrid.Launcher/LegacyEndpoints.cs`
  - `Launcher/DreamGrid.Launcher.Core/Api/LegacyCommandApi.cs`
  - `Launcher/DreamGrid.Launcher.Tests/LegacyCommandApiTests.cs`
  - `Launcher/DreamGrid.Launcher.Tests/LegacyEndpointsTests.cs` (uses `Microsoft.AspNetCore.Mvc.Testing` 10.0.13 `WebApplicationFactory<Program>`)

**Interfaces:**
- Consumes `GridController` (Task 10), `RobustClient` (Task 6) and `RegionAlert` (Task 6).
- Produces:
  - **`static LegacyRequest LegacyRequest.From(IQueryCollection query, IFormCollection? form)`**, using the records from Task 10. Parameter names are read case-insensitively (spec §5, `API.txt`).
  - **`public sealed class LegacyCommandApi(IGridController grid, IRobustClient robust, LauncherSettings s, IEnumerable<IFeatureModule> modules)`** with `Task<string> HandleAsync(LegacyRequest r, CancellationToken)`.
    - **Commands in version 1:**
      - `RegionStatus` and `regionlist`, level 0, no password.
      - `StartRegion`, `StopRegion`, `RestartRegion`, `StartRobust`, `StopRobust`, `StartMysql` and `StopMysql`, each level ≥ 200.
      - Module commands are appended.
    - **Auth:**
      - `password == s.MachineId` gives level 1000.
      - Otherwise, avatar name plus password: `GetAccountAsync` + `AuthenticateAsync`, then the account's `UserLevel`.
      - Otherwise 0.
      - Insufficient level returns `"NAK You do not have permission to <Command>"`.
    - **Replies:** success `"ACK"` or data; an unknown command `"NAK Unknown command <Command>"`.
    - **`regionlist`** returns JSON `{"regions":[{"name":..,"uuid":..,"status":..}]}`.
  - **`LegacyEndpoints.Map(WebApplication app)`:**
    - `GET /` with query `alt`: **SmartStart** callback. Returns the requested `alt` value unchanged as `text/plain` (spec §5, SmartStart off).
    - `GET /` with query `type`: `SmartStartNotify`. Logs and returns `"ACK"`.
    - `GET|POST /` with `command`: `LegacyCommandApi`.
    - `GET /` with none of these: `"Test Completed"`, the old DreamGrid port probe (manual Ports.txt).
    - `POST /dreamgrid/regionready`: `RegionAlert.Parse` → `grid.OnRegionAlert`; returns 200.
    - `GET /welcome`: a minimal static HTML page with the grid name and "Welcome to {GridName}". This is the `GridInfoService welcome` target; plan 2b replaces it.
  - **`Program`:**
    - `WebApplication.CreateBuilder` + `UseSystemd()` + `UseWindowsService(o => o.ServiceName = "DreamGrid")`.
    - Kestrel listens on `0.0.0.0:{settings.WebPort}`.
    - `LauncherPaths.Resolve(args, env, OperatingSystem.IsWindows())`.
    - The DI singletons are `SettingsStore`, `GridController`, `RobustClient`, `LegacyCommandApi`, and `IFeatureModule` = `LogLevelModule`.
    - When `SetupToken` is not null and `Setup.OwnerCreated` is false, it logs one line: `Setup link: http://{host}:{WebPort}/setup?token={SetupToken}`. Plan 3's installers print and open it.
    - `public partial class Program {}` so `WebApplicationFactory` works.
  - **`GridHostedService`:**
    - On start: `grid.StartAsync`. A `PortConflictException` is logged at Critical, and the host keeps running so the website can show it (plan 2b).
    - On stop: `grid.DetachAsync()` and no child is killed (spec §4.12).

- [ ] **Step 1: Write failing tests**
  - `RegionStatus_needs_no_password`.
  - `StartRegion_without_credentials_is_NAK_permission`: the reply starts with `"NAK You do not have permission to StartRegion"`.
  - `MachineId_grants_level_1000`.
  - `Avatar_level_100_cannot_StopRobust_but_level_200_can`, with a fake `IRobustClient` (Task 6) and a fake `IGridController` (Task 10).
  - `Parameter_names_are_case_insensitive`: `Command=RegionStatus&RegionName=Welcome` and `command=regionstatus&regionname=Welcome` give the same reply.
  - Endpoint `Alt_query_echoes_requested_region`: `GET /?alt=6f1f…&agentid=…&password=x` returns body `6f1f…`.
  - Endpoint `Bare_root_returns_Test_Completed`.
  - Endpoint `RegionReady_post_sets_logins_enabled`: POST the canned `region_ready`/`enabled` JSON; then `GetStatus().LoginsEnabled` is true. Register a fake `IGridController` in the `WebApplicationFactory`.
- [ ] **Step 2: Run and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and confirm they pass,** plus `dotnet build -c Release DreamGrid.sln` with 0 warnings.
- [ ] **Step 5: Commit** `git commit -m "Launcher: host, port-8001 contract and hosted grid service"`.

---

### Task 12: End-to-end launcher test and CI

**Files:**
- Create: `Launcher/DreamGrid.Launcher.IntegrationTests/LauncherEndToEndTests.cs`
- Modify: `.github/workflows/dreamgrid-ci.yml`

**Interfaces:**
- Consumes the built launcher (`Launcher/DreamGrid.Launcher` output) and repo `bin/`.
- Produces a CI job `launcher` on Windows (MariaDB zip downloaded and cached) and on Debian 12 and 13 (`apt-get install mariadb-server`).

- [ ] **Step 1: Write the end-to-end tests.** They start the real launcher as a child process: `dotnet <launcher dll> --data "<temp>/Dréam Grid <guid>" --opensim <repo bin> --mariadb-bin <bin>`, with ports from a free range written into a pre-seeded `settings.json`.
  - `Launcher_boots_grid_and_answers_8001`: wait for `GET /` == `"Test Completed"`; `RegionStatus` works without a password.
  - `Launcher_adopts_a_running_grid_twice` (Review Focus 1). No test-only command goes into the product:
    1. **In-process provisioning.** In the test process, build a `GridController` on the same data dir. Run `StartAsync`, `CreateOwnerAsync`, `CreateRegionAsync("Welcome","island")` and then `DetachAsync`. The children keep running.
    2. Record the MariaDB, Robust and region PIDs (from `<Pids>`).
    3. **First adoption.** Start the launcher out-of-process. Wait for `"Test Completed"`, then for `RegionStatus` == Running. Assert the three PIDs are unchanged.
    4. **Second adoption.** Kill the launcher process only, start it again, and repeat the assertions. Also assert that exactly one process runs `OpenSim.dll` with this data dir on its command line.
  - Cleanup: `?command=StopMysql`/`StopRobust` with the MachineId, or `GridController.StopAllAsync`. Leave no process behind (assert via PID files).
- [ ] **Step 2: Run and confirm they fail,** before the restart-adoption bits are wired; it is fine if this goes RED on one assertion. Then make them pass on Windows and in `debian:12`/`debian:13` containers.
- [ ] **Step 3: Add the CI job.** Matrix: `windows-latest` and `debian:12`/`debian:13` containers, as in plan 1.
  - **Windows step:** `Invoke-WebRequest` the MariaDB zip (cached with `actions/cache`, keyed on the version), extract it, and set `DREAMGRID_MARIADB_BIN`.
  - **Debian step:** `apt-get install -y mariadb-server` and set `DREAMGRID_MARIADB_BIN=/usr/sbin`.
  - **Then:** `dotnet build`, `dotnet test --project Launcher/DreamGrid.Launcher.Tests`, `dotnet test --project Launcher/DreamGrid.Launcher.IntegrationTests`.
  - Add `timeout-minutes: 30` to the new jobs.
  - Upload `<data>/state/logs/*` on failure: the tests write their data dirs under `$RUNNER_TEMP` (Windows `TMP`/`TEMP` set as in plan 1).
- [ ] **Step 4: Push and watch CI.** This needs the user's OK if it is not already given for this branch's PR. Expected: all jobs green.
- [ ] **Step 5: Commit** `git commit -m "Launcher: end-to-end tests and CI on Windows and Debian 12/13"`.

---

## Self-review notes

**Spec coverage:**
- §4.1 Robust plus one region → Task 10.
- §4.2 MariaDB, generated passwords, separate databases → Tasks 2 and 7.
- §4.3 one process and one port → Task 11. The Razor UI arrives in 2b.
- §4.5 MachineID and setup token → Tasks 2 and 11. Sign-in pages are 2b.
- §4.12 children outlive the launcher, REST console control, adoption → Tasks 6, 8 and 12.
- §4.14 private MariaDB on 3307 → Task 7.
- §4.15 third parties inactive → Tasks 4 (Gloebit) and 5.
- §5 port-8001 contract → Task 11. `/mcp` is out of scope for version 1.
- §6.3 starter OAR or island → Task 9.
- §6.6 start order, graceful stop, crash backoff, adoption → Tasks 8 and 10.
- §6.7 INI mechanism, region file layout, overrides, marker blocks → Tasks 3 and 5.
- §6.8 settings store, secrets, file permissions → Task 2.
- §6.9 module seam (core half) and the proving module → Task 10.
- §9 non-interactive estate creation → Tasks 5 and 10.

**Not in this plan:**
- The website: setup checklist, sign-in, dashboard, Advanced Settings, logs view and the module UI. That is plan 2b.
- Installers, systemd unit, Windows service registration, release packaging and the bundled MariaDB. That is plan 3.
