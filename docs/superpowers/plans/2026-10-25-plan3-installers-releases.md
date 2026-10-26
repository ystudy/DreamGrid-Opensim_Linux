# Plan 3: Installers, Services and Releases Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A newcomer installs DreamGrid with one command on Debian 12/13 or one `.exe` on Windows 10/11, the launcher runs as a service whose children survive service restarts and upgrades, and every tagged commit produces a tested GitHub Release. Before that, the open follow-ups from plans 2a and 2b are closed, because the installers make them reachable by real users.

**Architecture:**
- **Phase A (Tasks 1–8): follow-ups.** Launcher hardening (command line, single instance, settings durability, PID reuse, Windows data ACL, INI value validation, REST console, setup resume) and the website follow-ups (fallback authorization, expired sign-in, setup start failures and port change, clean shutdown).
- **Phase B (Tasks 9–13): shipping.**
  - The launcher gains a few command-line verbs that the installers call (`--version`, `admin-link`, `stop-grid`, `--web-port`).
  - One packaging script builds the release layout with a **private ASP.NET Core runtime** inside it, so no installer touches Microsoft's package feeds and children never depend on `dotnet` being on a PATH.
  - Debian: `install.sh` and a systemd unit. Windows: an Inno Setup installer that registers a Windows service.
  - One smoke tool drives a real install through setup to a running region on both OSes, then restarts the service and checks that the region was adopted, not restarted.
  - A release workflow runs that smoke on every tag and attaches the artifacts.

**Tech Stack:**
- .NET 10 (launcher, smoke tool), `Microsoft.Extensions.Hosting.Systemd` / `.WindowsServices` 10.0.13 (already referenced).
- Bash for `install.sh` and PowerShell 7 (`pwsh`, preinstalled on both GitHub runner images) for `setup/package.ps1`.
- Inno Setup 6 (build-time tool only; installed on the runner with `choco install innosetup`).
- MariaDB 11.4.9 winx64 zip (the version and SHA256 already pinned in `dreamgrid-ci.yml`); Debian's `mariadb-server`.
- Docker with a systemd-booted `debian:12` / `debian:13` image for the Debian install smoke on `ubuntu-latest`.

**Spec:** `docs/superpowers/specs/2026-10-24-crossplatform-launcher-v1-design.md`: §4.7, §4.12 (service setup), §4.13, §4.14, §4.16, §6.1, §6.6, §7 (`setup/`), §10 (both OSes need a smoke test: install → setup → region up → login port answers). The follow-ups come from `docs/superpowers/plans/2026-10-25-plan2a-followups.md` and `2026-10-25-plan2b-followups.md`. Plan 3 of 4. Branch `feature/installers` from `V7` (plans 1, 2a and 2b are merged).

**Execution order:** 1, 2, 3, 4, 5, 6, 8, 7, 9, 10, 11, 12, 13. Task 7 uses Task 8's `BackgroundWork`, and Task 6 uses Task 2's `PidFile.ReadLive`. Tasks 1–5 are independent of each other.

**Decisions this plan takes (builder's calls the spec leaves open):**

| Decision | Why |
|---|---|
| Release packages carry a private ASP.NET Core runtime (`dotnet/`), resolved at release time from Microsoft's `releases.json` (latest 10.0 runtime, SHA-512 checked). Debian no longer installs .NET via `dotnet-install` or a package feed. | One tested runtime per release. It fixes "dotnet not on systemd's PATH" and moves the version pin into the release pipeline (2a follow-ups). |
| Debian paths: program `/opt/dreamgrid` (root-owned, read-only), data `/var/lib/dreamgrid` (user `dreamgrid`, 0700), unit `dreamgrid.service`. | Spec §6.1 examples. |
| Windows paths: program `C:\Program Files\DreamGrid`, data `%ProgramData%\DreamGrid`, service `DreamGrid` running as the virtual account `NT SERVICE\DreamGrid`. | Spec §6.1 examples. A virtual account is not LocalSystem and needs no password. |
| On Windows the runtime and MariaDB live in versioned folders (`dotnet-<ver>\`, `mariadb-<ver>\`). An upgrade adds a new folder beside the old one, and old folders are deleted only when nothing has them open. | Children that keep running through an upgrade hold those files open, and Windows cannot replace a file that is in use. |
| Debian upgrade: unpack to `/opt/dreamgrid.new`, stop the service, rename `/opt/dreamgrid` → `/opt/dreamgrid.old` (the previous `.old` is deleted first), rename `.new` into place, start. | Running children keep their open files (Linux inodes), and one previous version stays on disk for a manual rollback. |
| linux-arm64 is not packaged in plan 3. | "arm64 if cheap" (§4.13): it needs its own runner and smoke test. Recorded as a follow-up. |
| Machine shutdown is not intercepted. systemd/Windows end the children at OS shutdown, and MariaDB shuts down cleanly on SIGTERM. | OpenSim saves prims as they change. A graceful-quit hook is a later item. |
| Island plateau width is unchanged. Only the size check is added. | The 2a note said "if wanted"; the owner's starter OAR replaces the island anyway. |
| `--web-port` is applied by `Program` before the host is built, under the instance lock, with `SettingsStore` directly. This is the one documented exception to "settings change only through `UpdateSettings`". | No controller exists yet, and nothing else can write at that moment. |
| The 2b "Deferred minors still open" stay open, except three folded in where a task already touches the code: the double-submitted avatar step (Task 6), the failed log roll and the logger's write after `Dispose` (Task 8). | Scope. |

## Global Constraints

- Platforms: Debian 12 and 13 amd64, Windows 10/11 x64 (spec §3). Ubuntu is not a target.
- Default ports, unchanged: web 8001, Robust public 8002, private 8003, region 8004, MariaDB 3307, consoles 8010/8011.
- Program files and data are always separate. An install or upgrade never writes to, moves or deletes the data directory (§6.1: "Re-running the script on an installed system upgrades in place and never wipes data").
- Children outlive the launcher: the systemd unit uses `KillMode=process` (§4.12), and Windows children are not in a job object that the service's end would kill.
- An upgrade restarts children only when the OpenSim binaries changed. This is the existing `RuntimeStager` stamp; do not add another trigger.
- The Debian one-liner is `curl -fsSL https://github.com/ystudy/DreamGrid-Opensim_Linux/releases/latest/download/install.sh | sudo bash` (§4.13).
- Release assets: `dreamgrid-<version>-linux-x64.tar.gz`, `DreamGridSetup-<version>.exe`, `install.sh`, `SHA256SUMS`. Version tags are `v<major>.<minor>.<patch>`. The version (`<major>.<minor>.<patch>` from the tag; `0.0.0-dev+<short sha>` for untagged builds) is the launcher's `InformationalVersion`, which the site footer already shows (§4.13).
- `DreamGridSetup.exe` is unsigned. README and the release notes carry the SmartScreen note "More info → Run anyway" (§4.16).
- Nothing in an install contacts outworldz.com or any third-party service at run time. Downloads happen only at build time (runtime, MariaDB) and in `install.sh` (GitHub Releases, Debian mirrors) (§4.15).
- New dependencies must be BSD/MIT/Apache-2.0. No ImageSharp (§3).
- No changes under `OpenSim/` (§4.6).
- Wording rules from plan 2b still hold on web pages. Installer output may say "service", "port" and "data directory".
- Every commit ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Re-running the installer on a running install (upgrade).** The data directory is untouched, the region keeps its PID when OpenSim did not change, and the footer shows the new version. Pinned by the second install in the Task 11 and Task 12 smoke runs.
2. **The launcher was killed in the middle of saving settings.** `settings.json` is the old or the new file, never a truncated one, and a leftover `settings.json.tmp` is deleted on the next start without affecting the load. Pinned by a Task 1 test.
3. **Port 8001 already taken at install time.** The installer says which port is taken and how to re-run with `--web-port`, instead of leaving a service that restarts forever with nothing reachable. Pinned by Task 11 (`nc -l 8001`) and Task 12 (installer page check).
4. **A second launcher on the same data directory** (the service is running and someone starts the launcher by hand). The second one exits with code 3 and the message "Another DreamGrid launcher is already running with data directory <path>.", and the first is unaffected. Pinned by Task 1 (lock) and Task 11 (smoke runs it while the service is up).
5. **A Windows upgrade while children run from program files.** No file-in-use failure, no reboot prompt, and no Restart Manager killing `dotnet.exe` or `mariadbd.exe`. The region PID is unchanged. Pinned by Task 12's second install.

---

## Phase A: follow-ups

### Task 1: Command line, single instance, settings durability

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/InstanceLock.cs`
- Modify:
  - `Launcher/DreamGrid.Launcher.Core/LauncherPaths.cs`: split out `ResolveData`; a trailing flag with no value throws
  - `Launcher/DreamGrid.Launcher.Core/Settings/SettingsStore.cs`
  - `Launcher/DreamGrid.Launcher/Program.cs`: take the lock before `LoadOrCreate`
- Test: `Launcher/DreamGrid.Launcher.Tests/LauncherPathsTests.cs`, `SettingsStoreTests.cs`, new `InstanceLockTests.cs`

**Interfaces:**
- Produces:
  - `public static string LauncherPaths.ResolveData(string[] args, IDictionary<string,string?> env, bool isWindows)`, the data-dir half of `Resolve`. `Resolve` calls it.
  - `public sealed class InstanceLock : IDisposable` with `public static InstanceLock? TryAcquire(string dataDir)`. It opens `<data>/launcher.lock` with `FileShare.None` and holds it for the process lifetime. It returns null when the file is held, and never deletes the file.
  - Program exit code 3, with the log line `Another DreamGrid launcher is already running with data directory {Data}.`

- [ ] **Step 1: Write failing tests**
  - `Resolve_trailing_flag_without_value_throws`: for each of `--data`, `--opensim` and `--mariadb-bin` as the last argument, it throws an `ArgumentException` whose message contains the flag name and "needs a value".
  - `Resolve_windows_without_ProgramData_uses_C_ProgramData`: env without `ProgramData` (and with an empty one) gives `C:\ProgramData\DreamGrid`. Pin the behaviour; it may already pass.
  - `Second_lock_on_same_dir_is_refused_until_first_is_disposed`.
  - `Settings_tmp_left_by_a_crash_is_ignored_and_removed`: write a valid `settings.json` and a truncated `settings.json.tmp`. `LoadOrCreate` returns the valid settings, and the `.tmp` is gone afterwards (Review Focus 2).
  - `Save_removes_tmp_when_replace_fails`: make the target path a directory. `Save` throws, and no `.tmp` remains.
  - `Save_does_not_retry_non_sharing_errors`: the same directory-as-target case fails in under 500 ms, so it does not wait out the ~2 s retry loop.
  - `Load_rejects_null_for_required_member`: `"db": null` throws `SettingsCorruptException`.
- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test --project Launcher/DreamGrid.Launcher.Tests -c Release --filter-class *LauncherPathsTests --filter-class *SettingsStoreTests --filter-class *InstanceLockTests`.
- [ ] **Step 3: Implement**
  - Settings JSON options get `RespectNullableAnnotations = true`.
  - The Windows replace retry covers only `IOException` whose `HResult & 0xFFFF` is 32 or 33 (sharing or lock violation) and `UnauthorizedAccessException`. That last one is how an open handle on the target shows up on Windows; keep it and say so in a comment.
  - After a successful rename on Unix, fsync the directory, best effort: libc `open(dir, O_RDONLY)` / `fsync` / `close` through `DllImport("libc")`, with errors ignored.
  - `LoadOrCreate` deletes a stale `.tmp`.
  - `Program` acquires the lock right after `ResolveData`, before `LoadOrCreate`, and keeps it in a `using` for the host's lifetime.
- [ ] **Step 4: Run the tests and confirm they pass.** Then run the full `Launcher.Tests`.
- [ ] **Step 5: Commit** with `git commit -m "Launcher: single-instance lock, strict args, durable settings save"`.

---

### Task 2: Process hardening (PID reuse, zombie check)

**Files:**
- Modify:
  - `Launcher/DreamGrid.Launcher.Core/Processes/PidFile.cs`
  - `ProcessImage.cs`
  - `ManagedProcess.cs` (call sites at lines ~62, 110, 419)
  - `Grid/GridController.cs` (`WillAdopt`, ~495)
  - `Database/MariaDbServer.cs` (doc comment)
- Test: `Launcher/DreamGrid.Launcher.Tests/ManagedProcessTests.cs`, new `PidFileTests.cs`

**Interfaces:**
- Produces: `public static int? PidFile.ReadLive(string path, string? expectedImage)`. It returns the PID only when `Read` gives one, `ProcessImage.Matches` holds, and the process started no later than the PID file's last write time plus 2 s (`Process.StartTime`, compared in UTC). Every adoption and stop path uses it instead of `Read` + `Matches`.

- [ ] **Step 1: Write failing tests**
  - `ReadLive_rejects_process_started_after_file_was_written`: write the current process's PID, set the file's write time to one hour before this process started, pass the current process path as the expected image, and expect null.
  - `ReadLive_accepts_process_started_before_file_was_written`.
  - `ReadLive_missing_file_is_null`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
  - `ProcessImage.IsZombie`: only `FileNotFoundException`/`DirectoryNotFoundException` mean gone. An `UnauthorizedAccessException` means "exists, not a zombie" (2a note).
  - Document on `StartMysql` that a pending upgrade stops leftover Robust and region first.
- [ ] **Step 4: Run them and confirm they pass,** together with the full `Launcher.Tests`.
- [ ] **Step 5: Commit** with `git commit -m "Launcher: reject reused PIDs by start time"`.

---

### Task 3: Windows ACL on the data directory

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/LauncherPaths.cs` (`CreateOwnerOnly`)
- Test: `Launcher/DreamGrid.Launcher.Tests/LauncherPathsTests.cs`

**Interfaces:**
- Produces: on Windows, `CreateOwnerOnly(dir)` leaves `dir` with a protected DACL (no inherited entries) granting `FullControl`, inherited by subfolders and files, only to:
  - the current process user (the service's virtual account in production),
  - `SYSTEM`,
  - `BUILTIN\Administrators`.
- It uses `System.IO.FileSystemAclExtensions` (in the box on Windows) and applies to an existing directory too, like the Unix chmod.

- [ ] **Step 1: Write the failing test** `Windows_data_dir_is_owner_system_admins_only`. It is skipped off Windows with `Assert.SkipUnless(OperatingSystem.IsWindows(), …)`. It asserts:
  - `AreAccessRulesProtected` is true,
  - the set of identities on the directory is exactly {current user SID, `S-1-5-18`, `S-1-5-32-544`},
  - a file created inside afterwards inherits the same set.
- [ ] **Step 2: Run it and confirm it fails** (on Windows).
- [ ] **Step 3: Implement.** Remove the "plan 3's installer sets the ACL" comment.
- [ ] **Step 4: Run it and confirm it passes.**
- [ ] **Step 5: Commit** with `git commit -m "Launcher: owner-only ACL on the Windows data directory"`.

---

### Task 4: Values written to INIs, INI writer edge cases, terrain size, stager tests

**Files:**
- Modify:
  - `Launcher/DreamGrid.Launcher.Core/Ini/IniDocument.cs`
  - `Config/HostAddress.cs`, `Config/GridConfigWriter.cs`
  - `Settings/SettingsValidator.cs`, `Grid/IslandTerrain.cs`
  - `Launcher/DreamGrid.Launcher/Pages/Setup/World.cshtml.cs` (use the grid name check)
- Test: `IniDocumentTests.cs`, `GridConfigWriterTests.cs`, `SettingsValidatorTests.cs`, `IslandTerrainTests.cs`, `RuntimeStagerTests.cs`

**Interfaces:**
- Produces:
  - `public static string? SettingsValidator.GridNameError(string? name)`: 1–64 characters after trimming, with no control characters and no `"`. Message: "Use 1 to 64 letters, digits, spaces or punctuation, without quotes."
  - `public static string HostAddress.Effective(string? configured)`: the trimmed `configured` when it is non-empty and `HostnameError` accepts it, else `Detect()`. `GridController` uses it wherever it does `s.Hostname ?? HostAddress.Detect()` today, and so does `Program`'s setup-link line.
  - `HostAddress.Detect` also catches `InvalidOperationException` and `PlatformNotSupportedException`.

- [ ] **Step 1: Write failing tests**
  - `Set_with_empty_section_throws` (`ArgumentException`).
  - `ReplaceBlock_with_missing_end_marker_throws`: a `FormatException` naming the end marker; the document is unchanged.
  - `Duplicate_sections_get_returns_last_value_and_set_updates_last_occurrence`. This is Nini's "last wins".
  - `Writer_omits_estate_owner_keys_without_owner`: with `Setup.Owner == null`, the region INI has no active `DefaultEstateOwnerName`/`UUID` key (2a note: "clear Estates keys when no owner").
  - `Grid_name_with_quote_or_newline_is_rejected`, and the World page shows that message for `My "World"`.
  - `Effective_whitespace_hostname_falls_back_to_detected` and `Effective_trims_valid_hostname` (`" host.lan "` → `"host.lan"`).
  - `Island_size_must_be_positive_multiple_of_256` (300 and 0 throw `ArgumentOutOfRangeException`).
  - `Stager_skips_excluded_dirs_and_files`: `Regions/`, `addin-db-x/`, `a.log` and a top-level `OpenSim.ini` are not copied, and `config-include/Proto/Robust/OpenSim.ini` is.
  - `Log_config_path_with_spaces_apostrophe_and_backslash_is_valid_xml`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them and confirm they pass,** together with the full `Launcher.Tests`.
- [ ] **Step 5: Commit** with `git commit -m "Launcher: validate INI values, INI edge cases, terrain size"`.

---

### Task 5: REST console reliability

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/OpenSim/RestConsoleClient.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/RestConsoleClientTests.cs` (with the existing `StubHandler`)

**Interfaces:**
- Keeps the public surface unchanged.
- Behaviour:
  - `RunLinesAsync` first drains output already received for earlier calls (logged at Debug, discarded), so a late line is never attributed to the next command.
  - A session that has expired (the server's "unknown session" reply or a 401 on read/send) is reopened once, and the command is retried once.
  - `IsUpAsync` returns true when the session opened, even if the close probe times out.

- [ ] **Step 1: Write failing tests:** `Late_output_from_previous_command_is_not_returned`, `Expired_session_is_reopened_and_command_retried_once`, `Second_expiry_in_same_call_throws`, `IsUp_true_when_close_times_out`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.** Keep the 2a rule that a pending long-poll read is never cancelled.
- [ ] **Step 4: Run them and confirm they pass.**
- [ ] **Step 5: Commit** with `git commit -m "Launcher: REST console drains late output and survives session expiry"`.

---

### Task 6: Resuming an interrupted region setup

**Files:**
- Modify:
  - `Launcher/DreamGrid.Launcher.Core/Grid/GridController.cs` (`StartAsync`, `CreateRegionAsync`, `GetStatus`)
  - `Launcher/DreamGrid.Launcher/Pages/Setup/Avatar.cshtml.cs`, `Setup/SetupRunner.cs`
- Test:
  - `Launcher/DreamGrid.Launcher.Tests/GridControllerTests.cs`, `SetupFlowTests.cs`
  - `Launcher/DreamGrid.Launcher.IntegrationTests/LauncherEndToEndTests.cs`

**Interfaces:**
- Behaviour:
  - In `StartAsync`, when `Setup.RegionCreated` is false but `Region` is set and its PID file names a live, matching process (`PidFile.ReadLive`, Task 2), the region component is adopted and monitored, so status shows it Running, not Stopped.
  - A `CreateRegionAsync` retry that adopts a region that is still booting waits for logins before loading content.
  - When a retry switches between "starter" and "island" for the same name, the content is loaded again for the new choice. The terrain check must not skip the island because a starter OAR loaded earlier.
  - Owner creation runs at most once at a time: a second submit while one is running gets the page that says "Creating your avatar…", not a second `CreateOwnerAsync`.

- [ ] **Step 1: Write failing tests**
  - Unit, with the fake children used by `GridControllerTests`:
    - `Start_adopts_running_region_before_region_created`,
    - `Region_retry_after_adoption_waits_for_logins_before_content`,
    - `Region_retry_switching_to_island_loads_island`.
  - Web: `Avatar_double_submit_creates_owner_once` (`FakeGridController` counts calls; two concurrent posts → count 1).
  - Integration: in `LauncherEndToEndTests`, after the existing adoption check, add `Logins_enabled_after_adoption`. A signed-in admin's `/api/status` has `grid.loginsEnabled == true` within 60 s of the launcher's restart (2a follow-up).
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them and confirm they pass.** Run the unit tests, then `dotnet test --project Launcher/DreamGrid.Launcher.IntegrationTests -c Release` locally (it needs MariaDB).
- [ ] **Step 5: Commit** with `git commit -m "Launcher: resume interrupted region setup; single owner creation"`.

---

### Task 7: Website follow-ups (fallback policy, expired sign-in, setup start failures and ports)

**Files:**
- Modify:
  - `Launcher/DreamGrid.Launcher/Program.cs`, `Endpoints/StatusApi.cs`, `LegacyEndpoints.cs`, `Endpoints/LogsApi.cs`, `Endpoints/SetupApi.cs`
  - `wwwroot/js/live.js`
  - `Pages/Setup/World.cshtml(.cs)`, `Pages/Setup/_Checklist.cshtml`
- Create: `Launcher/DreamGrid.Launcher/Pages/Setup/_PortConflict.cshtml` (partial)
- Test: `AccessTests.cs`, `DashboardTests.cs`, `SetupFlowTests.cs`

**Interfaces:**
- Produces:
  - **M5:** `AddAuthorizationBuilder().SetFallbackPolicy(<the admin policy>)`. Every route that is open today gets an explicit `.AllowAnonymous()` (minimal APIs) or keeps its `AllowAnonymousToPage`/`Folder` convention:
    - the legacy 8001 routes,
    - `/api/status`,
    - `/api/setup/*` (still guarded by `SetupGate`),
    - `/login`, `/logout`, `/welcome`, `/waiting`, `/setup-done`, `/setup*`.
  - **M7:** `StatusView` gains `bool Admin` (true when the caller has the admin role). `live.js`'s dashboard poll reloads to `/login?ReturnUrl=<current path>` when a page rendered for an admin receives `admin: false`.
  - **Setup port conflicts:** when `GridStatus.StartFailure.ConflictPort` is set, every setup step shows `_PortConflict` with:
    - the plain sentence the dashboard already uses,
    - one number field "Use port",
    - a POST to `/setup?handler=Port`.
  - The handler finds which setting holds the conflicting port, validates the change with `SettingsValidator.Network`, saves it through `UpdateSettings`, and starts the grid in the background (Task 8's `BackgroundWork`).
  - **Step 1 (World):** after submit, the World page shows a start failure in the same way the avatar step does.

- [ ] **Step 1: Write failing tests**
  - `Anonymous_endpoints_are_exactly_the_allowlist`. Enumerate `EndpointDataSource` and collect the route patterns whose metadata contains `IAllowAnonymous` (or that are Razor pages under an anonymous convention). Assert that this set equals the list above, so a new endpoint without a decision fails this test.
  - `Status_reports_admin_false_anonymously_and_true_signed_in`.
  - `Setup_shows_port_change_form_on_conflict`. `FakeGridController` has `StartFailure("…", 8002)`, so `/setup/avatar` (with a setup cookie) contains a form posting to `handler=Port`.
  - `Setup_port_change_saves_robust_public_port_and_starts`. The post `port=8102` gives `RobustPublicPort == 8102` and one `StartAsync` call.
  - `Setup_port_change_rejects_duplicate_port`. 8003 shows the field message, and the settings are unchanged.
  - `World_step_shows_start_failure`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them and confirm they pass,** together with the full `Launcher.Tests`.
- [ ] **Step 5: Commit** with `git commit -m "Website: fallback authorization, expired sign-in reload, setup port conflicts"`.

---

### Task 8: Clean shutdown (background work, file logger)

**Files:**
- Create: `Launcher/DreamGrid.Launcher/BackgroundWork.cs`
- Modify:
  - `Setup/SetupRunner.cs` (lines 44, 81), `Endpoints/GridActions.cs` (line 43), the warm-up in `Program.cs`/`SetupRunner`
  - `RollingFileLoggerProvider.cs`
- Test: new `BackgroundWorkTests.cs`, a new `RollingFileLoggerTests.cs`

**Interfaces:**
- Produces: `public sealed class BackgroundWork : IHostedService`.
  - `public void Run(string name, Func<CancellationToken, Task> work)` starts the work, tracks it and logs a failure at Error with `name`.
  - `StopAsync` cancels the shared token and awaits all tracked work until the host's shutdown token fires.
  - Registered as a singleton and as a hosted service. Every `_ = Task.Run(...)` in the host goes through it.
- Logger behaviour:
  - A write after `Dispose` is dropped and never reopens the file.
  - When rolling fails (the oldest file cannot be deleted), the logger keeps appending to the current file.

- [ ] **Step 1: Write failing tests**
  - `Stop_waits_for_running_work`.
  - `Stop_cancels_token_seen_by_work`.
  - `Work_failure_is_logged_not_thrown`.
  - `Write_after_dispose_does_not_recreate_file`: delete the file after `Dispose`, write, and the file is still absent.
  - `Failed_roll_keeps_appending`: make `launcher.log.5` a directory so deleting it fails, write past 10 MB, and the later lines are still in `launcher.log`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them and confirm they pass,** together with the full `Launcher.Tests`.
- [ ] **Step 5: Commit** with `git commit -m "Launcher: await background work at shutdown; logger after dispose and failed roll"`.

---

## Phase B: shipping

### Task 9: Command-line verbs for installers

**Files:**
- Create: `Launcher/DreamGrid.Launcher/CommandLine.cs`
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (dispatch verbs first; apply `--web-port`)
- Test: new `Launcher/DreamGrid.Launcher.Tests/CommandLineTests.cs`

**Interfaces:**
- Consumes: `LauncherPaths.ResolveData`, `InstanceLock` (Task 1), `HostAddress.Effective` (Task 4).
- Produces: `public static class CommandLine` with `public static async Task<int?> RunVerbAsync(string[] args, IDictionary<string,string?> env, TextWriter output, CancellationToken ct)`. It returns null when `args` names no verb, so the host runs. The verbs:
  - `--version`: prints the `InformationalVersion` and returns 0.
  - `admin-link [--data <dir>] [--wait <seconds>] [--open]`:
    - Waits up to `--wait` (default 0) for `settings.json` to appear, reading it without creating it.
    - While the owner does not exist, it prints `http://<Effective host>:<WebPort>/setup?token=<token>`; otherwise `http://<Effective host>:<WebPort>/`.
    - `--open` also opens `http://localhost:<WebPort>/…` (the same path) with `UseShellExecute = true`.
    - Returns 0, or 4 when the settings did not appear in time.
  - `stop-grid --data <dir> --opensim <dir> [--mariadb-bin <dir>]`:
    - Takes the instance lock (returns 3 when it is held, so the service must be stopped first).
    - Builds a `GridController` exactly as `Program` does and calls `StopAllAsync`. Returns 0.
  - `--web-port <n>` (not a verb; the host runs afterwards): before the host is built and under the lock, `Program` validates the new port with `SettingsValidator.Network` against the other ports. It saves through `SettingsStore` only when the value differs. An invalid port exits with code 2 and the validator's message.

- [ ] **Step 1: Write failing tests**
  - `Version_prints_informational_version`.
  - `Admin_link_prints_setup_url_before_owner`.
  - `Admin_link_prints_dashboard_url_after_owner`.
  - `Admin_link_does_not_create_settings`: an empty data dir with `--wait 0` returns 4 and no file was created.
  - `No_verb_returns_null`.
  - `Stop_grid_refuses_while_lock_is_held` (returns 3).
  - `Web_port_flag_is_saved_and_validated`: through `LauncherFactory` with `--web-port 8101`, settings has 8101. With `--web-port 8002`, the exit code is 2.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them and confirm they pass,** together with the full `Launcher.Tests`.
- [ ] **Step 5: Commit** with `git commit -m "Launcher: --version, admin-link, stop-grid, --web-port"`.

---

### Task 10: Release package layout

**Files:**
- Create:
  - `setup/package.ps1`
  - `setup/test/Test-Package.ps1`
  - `setup/README.md` (the layout and how to build a package locally)

**Interfaces:**
- Produces: `pwsh setup/package.ps1 -Rid <linux-x64|win-x64> -Version <x.y.z[-dev+sha]> -Out <dir> [-RuntimeVersion <10.0.n>]`.
  - It builds the solution (`dotnet build -c Release DreamGrid.sln -p:Version=$Version`) and publishes the launcher framework-dependent (`dotnet publish Launcher/DreamGrid.Launcher -c Release -p:Version=$Version -o <pkg>/launcher`).
  - It produces `<Out>/dreamgrid/`:
    - `launcher/`.
    - `bin/`: the repo's built `bin/`, minus `*.pdb`, `*.log`, `*.pid`, `*.db` and the folders `RuntimeStager` excludes. On linux-x64, also minus `lib32/` and Windows-only native DLLs; the implementer lists them in the script with a comment.
    - `content/`: `starter-region.oar` if present, and the README.
    - `dotnet/`: the ASP.NET Core runtime for the RID.
    - `VERSION`: one line.
    - `RUNTIME_VERSION`: one line.
    - linux-x64 only: `setup/debian/` (`install.sh`, `dreamgrid.service`).
  - Linux: `dreamgrid-<Version>-linux-x64.tar.gz` holding `dreamgrid/`, with execute bits kept on `dotnet/dotnet` and `install.sh`. Build it with `tar` on Linux; on Windows the script refuses `-Rid linux-x64`.
  - Windows: the folder only; Task 12's Inno script consumes it.
  - **Runtime resolution:**
    - Download `https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`.
    - Without `-RuntimeVersion`, take `latest-runtime`.
    - Find that release's `aspnetcore-runtime.files` entry for the RID (`.tar.gz` for linux-x64, `.zip` for win-x64).
    - Download it, check its `hash` (SHA-512), and fail on a mismatch.
- `setup/test/Test-Package.ps1 -Package <dir>` asserts:
  - the layout exists,
  - `dotnet/dotnet[.exe] launcher/DreamGrid.Launcher.dll --version` prints exactly the contents of `VERSION`,
  - `bin/OpenSim.dll` and `bin/Robust.dll` exist,
  - there are no `*.pdb` files.

- [ ] **Step 1: Write `Test-Package.ps1` first** and run it against an empty folder. Expected: it fails, listing the missing files.
- [ ] **Step 2: Implement `package.ps1`.**
- [ ] **Step 3: Run** `pwsh setup/package.ps1 -Rid win-x64 -Version 0.0.0-dev -Out $env:TEMP/dgpkg` and then `Test-Package.ps1`. Expected: PASS. Run the linux-x64 variant in WSL or CI.
- [ ] **Step 4: Commit** with `git commit -m "Setup: release package layout with private ASP.NET Core runtime"`.

---

### Task 11: Debian installer, systemd unit, install smoke

**Files:**
- Create:
  - `setup/debian/install.sh`, `setup/debian/dreamgrid.service`
  - `setup/debian/test/Dockerfile`, `setup/debian/test/smoke.sh`
  - `tests/DreamGrid.InstallSmoke/DreamGrid.InstallSmoke.csproj`, `tests/DreamGrid.InstallSmoke/Program.cs`
  - `Launcher/DreamGrid.Launcher.IntegrationTests/WebSession.cs`: move `WebSession`/`WebPage` out of `WebsiteEndToEndTests.cs` unchanged, `internal`
- Modify: `DreamGrid.sln` (add InstallSmoke), `WebsiteEndToEndTests.cs` (use the moved class)

**Interfaces:**
- Consumes: the package layout (Task 10) and the verbs (Task 9).
- Produces:
  - **`dreamgrid.service`:**
    - `Type=notify`, `User=dreamgrid`, `Group=dreamgrid`, `UMask=0077`.
    - `Environment=DOTNET_ROOT=/opt/dreamgrid/dotnet DOTNET_HOST_PATH=/opt/dreamgrid/dotnet/dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1`.
    - `ExecStart=/opt/dreamgrid/dotnet/dotnet /opt/dreamgrid/launcher/DreamGrid.Launcher.dll --data /var/lib/dreamgrid --opensim /opt/dreamgrid/bin --mariadb-bin /usr/sbin` (plus `--web-port N` when the installer was given one).
    - `KillMode=process`, `Restart=on-failure`, `RestartSec=5`, `TimeoutStopSec=60`.
    - `After=`/`Wants=network-online.target`, `WantedBy=multi-user.target`.
  - **`install.sh [--from <tarball>] [--web-port <n>]`**, in this order:
    1. Root check, and Debian 12/13 from `/etc/os-release` (`ID=debian`, `VERSION_ID` 12|13). Anything else → a plain message and exit 1.
    2. `apt-get install -y mariadb-server libgdiplus ca-certificates curl iproute2`, plus `libicu72` on 12 or `libicu76` on 13. The system `mariadb.service` is left as installed (§4.14).
    3. Without `--from`, download `https://github.com/ystudy/DreamGrid-Opensim_Linux/releases/latest/download/dreamgrid-linux-x64.tar.gz` and `SHA256SUMS`, then verify. The release workflow (Task 13) also attaches the tarball under this stable name.
    4. If the service is not active and the web port (8001 or `--web-port`) is listening (`ss -ltnH "sport = :$port"`): print `Port <n> is already used by another program. Re-run with --web-port <free port>, for example --web-port 8101.` and exit 1.
    5. `useradd --system --home-dir /var/lib/dreamgrid --shell /usr/sbin/nologin dreamgrid` (if missing). Create `/var/lib/dreamgrid` with owner `dreamgrid` and mode 0700.
    6. Install or upgrade `/opt/dreamgrid` (Decisions table), owned by root, mode 755.
    7. Write the unit, `systemctl daemon-reload`, `systemctl enable --now dreamgrid` (or `restart` on an upgrade).
    8. `runuser -u dreamgrid -- /opt/dreamgrid/dotnet/dotnet /opt/dreamgrid/launcher/DreamGrid.Launcher.dll admin-link --data /var/lib/dreamgrid --wait 60`, then print "Open this link in your browser to set up your world:" and the link.
    - Re-running it is an upgrade and prints `Upgraded DreamGrid <old> → <new>.`
  - **`DreamGrid.InstallSmoke`:** `--url <base> --setup-link <link> --data <dir> --robust-port <n> --stop "<cmd>" --start "<cmd>"`. It:
    1. Follows the setup link and completes all four steps with `WebSession` (world "Smoke World", owner "Smoke Tester" with a generated password, region "Smoke Island", content "island").
    2. Waits for the admin `/api/status` `grid.loginsEnabled` (10 min).
    3. Checks `GET http://127.0.0.1:<robust-port>/get_grid_info` contains `Smoke World`.
    4. Reads the region PID from `<data>/state/pids/Smoke_Island.pid`.
    5. Runs `--stop` and asserts that PID is still alive.
    6. Runs `--start`, waits for `loginsEnabled`, and asserts the region PID is the same.
    - It prints one line per stage and exits 0 when everything passed, or 1 with the failing stage.
  - **`Dockerfile`:** `ARG DEBIAN=12`, `FROM debian:${DEBIAN}`, install `systemd systemd-sysv netcat-openbsd procps`, `CMD ["/lib/systemd/systemd"]`.
  - **`smoke.sh <debian-version> <tarball> <tarball-v2>`:**
    - Builds and runs the container: `--privileged --cgroupns=host -v /sys/fs/cgroup:/sys/fs/cgroup:rw`.
    - Copies in the tarballs and the published InstallSmoke, and runs every step through `docker exec`.
    - **Busy port:** `nc -l 8001 &`, then `install.sh --from` must exit 1 with the port sentence (Review Focus 3). Kill `nc`.
    - **Install** and capture the link.
    - Run **InstallSmoke** with `--stop "systemctl stop dreamgrid" --start "systemctl start dreamgrid"` (this checks `KillMode=process` and adoption).
    - **Second launcher:** run the launcher as `dreamgrid` by hand and expect exit code 3 (Review Focus 4).
    - **Upgrade:** record the region PID, then `install.sh --from <tarball-v2>`, where v2 is the same build repackaged with another `-Version`. Expect:
      - the region PID unchanged,
      - `/var/lib/dreamgrid/settings.json` byte-identical,
      - the footer of `/login` showing v2's version (Review Focus 1).
    - The launcher runs as `dreamgrid`, so this is also the non-root Linux leg the 2a follow-ups asked for.

- [ ] **Step 1: Write the smoke harness first** (`Dockerfile`, `smoke.sh`, InstallSmoke) and run it with a package but a stub `install.sh` that exits 1. Expected: FAIL at "install".
- [ ] **Step 2: Implement `install.sh` and `dreamgrid.service`.**
- [ ] **Step 3: Run** `bash setup/debian/test/smoke.sh 12 …` and then `13` on a Linux machine with Docker (or in the Task 13 workflow). Expected: every stage prints OK, exit 0.
- [ ] **Step 4: Run** `shellcheck setup/debian/install.sh setup/debian/test/smoke.sh`. Expected: no warnings.
- [ ] **Step 5: Commit** with `git commit -m "Setup: Debian install.sh, systemd unit, install smoke"`.

---

### Task 12: Windows installer and service

**Files:**
- Create: `setup/windows/DreamGrid.iss`, `setup/windows/build.ps1`, `setup/windows/test/smoke.ps1`

**Interfaces:**
- Consumes: the win-x64 package folder (Task 10), the verbs (Task 9), InstallSmoke (Task 11).
- Produces:
  - **`build.ps1 -Package <dir> -MariaDbZip <zip> -Version <v> -Out <dir>`:**
    - Unpacks MariaDB (its `bin`, `lib`, `share`; no `include`, no `*.pdb`) into a staging copy as `mariadb-11.4.9\`.
    - Renames `dotnet\` to `dotnet-<RUNTIME_VERSION>\`.
    - Runs `ISCC.exe /DAppVersion=<v> /DRuntimeVersion=… /DMariaDbVersion=11.4.9 /DSource=<staging> DreamGrid.iss`.
    - Produces `DreamGridSetup-<v>.exe`.
  - **`DreamGrid.iss`:**
    - **Setup options:** `PrivilegesRequired=admin`, `ArchitecturesAllowed=x64compatible`, `ArchitecturesInstallIn64BitMode=x64compatible`, `MinVersion=10.0`, `DefaultDirName={autopf}\DreamGrid`, `CloseApplications=no`, `RestartApplications=no` (Review Focus 5), `UsePreviousAppDir=yes`.
    - **Files:**
      - `launcher\` and `bin\` with `ignoreversion`.
      - `dotnet-<ver>\` and `mariadb-<ver>\` with `onlyifdoesntexist`, since a running child may hold the old ones.
      - `content\`.
    - **Busy web port** (`[Code]`, `InitializeSetup` on a fresh install): if port 8001 is listening (`netstat -ano` via `Exec` + a temp file), show "Port 8001 is already used by another program." and abort. The command-line parameter `/WEBPORT=<n>` overrides the port and is passed to the service as `--web-port <n>`.
    - **Before files** (`PrepareToInstall`): `sc.exe stop DreamGrid`, then wait until it is stopped (≤ 60 s). The children keep running.
    - **After files** (`CurStepChanged(ssPostInstall)`):
      - Service command: `"{app}\dotnet-<ver>\dotnet.exe" "{app}\launcher\DreamGrid.Launcher.dll" --data "{commonappdata}\DreamGrid" --opensim "{app}\bin" --mariadb-bin "{app}\mariadb-<ver>\bin"`.
      - A fresh install runs `sc.exe create DreamGrid start= auto obj= "NT SERVICE\DreamGrid" binPath= <that command>`; an upgrade runs `sc.exe config DreamGrid` with the same values. Then `sc.exe description`, `sc.exe failure DreamGrid reset= 86400 actions= restart/5000/restart/5000/restart/60000`, and `sc.exe start DreamGrid`.
      - Firewall: `netsh advfirewall firewall add rule name="DreamGrid (dotnet <ver>)" dir=in action=allow program="{app}\dotnet-<ver>\dotnet.exe" profile=private,domain` when the rule is missing. Delete any `DreamGrid (dotnet *)` rule whose folder no longer exists.
      - Delete old `dotnet-*`/`mariadb-*` folders that are not the current ones, ignoring failures (they may be in use).
    - **Finish page:**
      - Checkbox (checked) "Open the setup page", which runs `admin-link --data … --wait 120 --open`.
      - Text: "Windows may ask whether DreamGrid can use the network; allow it for private networks."
      - A Start-menu shortcut "DreamGrid admin page" runs `admin-link --open`.
    - **Uninstall:**
      1. `sc.exe stop DreamGrid`.
      2. `stop-grid` with the service's arguments (the world goes down).
      3. `sc.exe delete DreamGrid`.
      4. Remove the `DreamGrid (dotnet *)` firewall rules.
      5. Leave `%ProgramData%\DreamGrid` and tell the user its path.
  - **`smoke.ps1 -Installer <exe> -InstallerV2 <exe>`:**
    1. Occupy 8001 with a `TcpListener` in a background job, run the installer `/VERYSILENT /SUPPRESSMSGBOXES /LOG=<file>`, expect a non-zero exit and the port sentence in the log, then release the port.
    2. Install silently, then get the link with `admin-link --wait 120` (run as admin; Task 3's ACL allows Administrators).
    3. Run InstallSmoke with `--stop "sc.exe stop DreamGrid"` and `--start "sc.exe start DreamGrid"`. This verifies that children survive a service stop, as 2a asked.
    4. Install v2 silently. Expect no reboot request (exit code 0, the log has no "restart"), the region PID unchanged, and the footer version equal to v2.
    5. Uninstall silently. Expect the region PID gone, no `DreamGrid` service, and the data directory still present.
    6. If step 3 shows children dying with the service: replace `Process.Start` for children on Windows with `CreateProcess` + `CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_PROCESS_GROUP` in `ManagedProcess.Launch`, add a test, and record it in the follow-ups file.

- [ ] **Step 1: Write `smoke.ps1`** and run it against a dummy installer that exits 1. Expected: FAIL at "install".
- [ ] **Step 2: Implement `DreamGrid.iss` and `build.ps1`.**
- [ ] **Step 3: Build and run the smoke on Windows** (admin PowerShell, or in CI). Expected: every stage prints OK.
- [ ] **Step 4: Commit** with `git commit -m "Setup: Windows installer and service"`.

---

### Task 13: Release workflow, CI pin, test serialization, docs

**Files:**
- Create: `.github/workflows/release.yml`
- Modify:
  - `.github/workflows/dreamgrid-ci.yml`: the fallback pin; run the install smoke when `setup/**` or `Launcher/**` changes
  - `Launcher/DreamGrid.Launcher.IntegrationTests/*.cs`: one xUnit collection
  - `README.md`: Install section
  - `docs/superpowers/specs/2026-10-24-crossplatform-launcher-v1-design.md`: record the facts
  - `docs/superpowers/plans/2026-10-25-plan3-followups.md` (create at the end of execution, in the format of the 2a/2b files)

**Interfaces:**
- `release.yml`:
  - **Triggers:** `push: tags: ['v*']` and `workflow_dispatch` (dispatch builds and smoke-tests but publishes nothing). The version is `${GITHUB_REF_NAME#v}` on a tag, else `0.0.0-dev+${GITHUB_SHA::7}`.
  - **Jobs:**
    1. `package-linux` (ubuntu-latest) and `package-windows` (windows-latest): `package.ps1` (+ `build.ps1` with the cached, SHA-checked MariaDB zip, the same cache key as CI) twice, as `<version>` and `<version>-upgradetest`, then `Test-Package.ps1`, then upload the artifacts.
    2. `smoke-debian` (a matrix of 12 and 13) and `smoke-windows`: download the artifacts and run Task 11's / Task 12's smoke. Upload the logs on failure.
    3. `publish` (tags only; needs both smokes): `gh release create v<version>` with:
       - the tarball, under both its versioned name and `dreamgrid-linux-x64.tar.gz`,
       - `DreamGridSetup-<version>.exe`,
       - `install.sh`,
       - `SHA256SUMS` covering every asset,
       - notes from a template that includes the SmartScreen paragraph.
- `dreamgrid-ci.yml`: the fallback `--version 10.0.402` becomes the SDK version read from `global.json` (`10.0.100`, which stays on the feed). The comment says that release runtimes are pinned by `package.ps1` instead.
- Integration tests: `[Collection("Grid")]` with `DisableParallelization = true` on `GridBootTests`, `LauncherEndToEndTests` (both partial files), `OnlineUsersTests` and `MariaDbServerTests`. 2b recorded a port-contention flake.
- README, "Install":
  - The Debian one-liner, and what it does.
  - The Windows download, with the SmartScreen note "More info → Run anyway".
  - Where data lives on each OS.
  - How to upgrade (run the same command or installer again).
  - `--web-port` / `/WEBPORT=`.
- Spec: append the 2a/2b "New facts" (`<data>/mariadb/tmp`, `<data>/keys`, log pattern, `GridStatus.Activity`/`StartFailure`, cookie SameSite, `LoginThrottle` sharing) and this plan's decisions table to §6.1/§6.8 as "Recorded during plans 2–3".

- [ ] **Step 1: Run** `workflow_dispatch` on the `feature/installers` branch (`gh workflow run release.yml --ref feature/installers`). Expected: all packaging and smoke jobs green, and `publish` skipped.
- [ ] **Step 2: Push the CI change** and check `build-and-smoke` is green on both launcher jobs.
- [ ] **Step 3: Update the README and spec,** then commit with `git commit -m "Release workflow, CI pin from global.json, docs"`.
- [ ] **Step 4: Manual release checklist (the user does these; record the results in the follow-ups file):**
  - A clean Debian 12 VM and a clean Debian 13 VM (AppArmor enabled): the one-liner from a test tag's release, then Firestorm login from another LAN machine.
  - A clean Windows 11 VM without the Visual C++ runtime: the SmartScreen path, the firewall prompt, setup in the opened browser, then Firestorm login from another machine.
- [ ] **Step 5: Write `2026-10-25-plan3-followups.md`** (decisions, new facts, open follow-ups including linux-arm64 and a graceful quit at OS shutdown) and commit it.
