# Plan 2a — decisions made during execution and open follow-ups

Plan 2a (launcher core) was executed task by task with a review after each task and a final whole-branch review (branch `feature/launcher-core`, 5562320..cd2f7c4). This file keeps what the execution ledger recorded that later plans need.

## Decisions (rulings) taken during execution

| Decision | Cost if wrong |
|---|---|
| Plan 2a lives on its own branch `feature/launcher-core`, stacked on the plan-1 branch (PR #1), instead of growing PR #1. | One rebase or merge later. |
| Legacy request parsing (`LegacyRequestParser.From`) is in the host project; Core has no ASP.NET dependency. | One moved method. |
| The port check skips components that will be adopted (PID file names a live process matching the expected image). | A false "port in use" on launcher restart. |
| Restaging the runtime stops adoptable Robust and region first, on both OSes (final review I4), with the Windows IOException retry kept as a backstop. | Children restarted on every OpenSim rebuild — intended. |
| Region display names are used verbatim as `<Name>.ini`, so names are limited to letters, digits, spaces, `-`, `_`, `'` (2–32 chars). `CreateRegionAsync` rejects others; **plan 2b's form must validate the same way.** | A crash or a file outside the region dir for odd names. |
| REST console: follow the real server. Lines with `Input="true"` (echo) or `Command="true"` (command prompt) are dropped; `Prompt="true"` + `Command="false"` lines are interactive prompts. | One filter tweak. |
| REST console reads are never cancelled; a pending long-poll is kept for the next call (aborting a read can lose lines on the server). | One open HTTP request per idle session for up to ~25 s. |
| The controller reads console output only through `RunAsync`, never `SendAsync`. | Misread output during owner creation. |
| Adoption matches full, install-specific images: the staged `OpenSim.dll`/`Robust.dll` path, and `--datadir=<ours>` for MariaDB. Never a bare file name. | Taking over or stopping another install's process. |
| The host binds the web port before the grid starts (the region's one-time RegionReady alert goes there); the controller never checks the web port itself. A busy web port exits with "Port <n> (Web) is already in use by another program." | A missed "logins enabled" alert. |
| **All settings changes go through `IGridController.UpdateSettings`. Plan 2b's Advanced Settings must never call `SettingsStore.Save` directly.** | Lost settings updates. |

## New facts the spec should record
- Data-dir layout gained `<data>/mariadb/tmp` (MariaDB's private tmpdir; a shared /tmp let one MariaDB delete another's temp tables).
- The whole `<data>` directory is secret (generated INIs hold DB passwords, ConsolePass, MachineID). Linux: `<data>` is 0700. Windows: plan 3's ACL must cover all of `%ProgramData%\DreamGrid`.
- Region `login status` answers "Login in NAME are enabled|disabled"; enable/disable answer "Logins are enabled|disabled for region NAME".
- GitHub container jobs run a non-reaping PID 1; a dead adopted process shows as a zombie (`/proc/<pid>/stat` state Z), which the launcher now treats as gone.

## Open follow-ups

### Plan 2b (website)
- Show the last log lines when a component is Failed (only the exit description is carried today).
- Resume an interrupted CreateRegion: adopt a running region whose `RegionCreated` is still false (status shows Stopped today).
- After adopting mid-boot, wait for logins before loading content on a CreateRegion retry; terrain check edge case when a retry switches starter → island.
- End-to-end assertion of the launcher's own `LoginsEnabled` after adoption once status exposes it.
- Add a non-root Linux CI leg (CI containers run as root; the non-root run was local only).
- Input validation for every value written to INIs (CR/LF is now rejected by `IniDocument.Set`); `HostAddress` error handling, whitespace host names, clear Estates keys when no owner.
- Settings: `RespectNullableAnnotations = true`; delete `settings.json.tmp` when the final replace fails; narrow the Windows replace retry to sharing-violation errors.
- REST console: late read output can be dropped or attributed to the next call; a session expiring mid-`RunAsync` throws instead of retrying; `IsUpAsync` returns false when the close probe times out.
- Spend/clear the SetupToken once the owner exists.
- INI: `ReplaceBlock` with a missing end marker; `Set("", key)` creates a bogus `[]` section; duplicated-section tests. Runtime stager exclusion/escaping tests.
- Island terrain: land is only ~40 m across of 256 m (widen the plateau if wanted); validate size as a multiple of 256.
- Small code notes: `ProcessImage.IsZombie` treats UnauthorizedAccessException as gone (narrow it or fix the comment); document that `StartMysql` with a pending upgrade stops leftover Robust/region.

### Plan 3 (installers, services, releases)
- systemd unit: `dotnet` is not on systemd's PATH when installed by dotnet-install — set PATH or `DOTNET_HOST_PATH`; `KillMode=process`.
- Windows service: verify children survive a service stop (job objects / breakaway).
- Windows ACL on the whole data directory.
- Single-instance guard (two launchers creating settings at once); fsync the settings directory after rename.
- Command-line edge cases: trailing flag with no value; `C:\ProgramData` fallback when the env var is unset.
- PID-reuse hardening (compare process start time).
- The CI fallback pin `dotnet-install --version 10.0.402` will go stale; move it into the release pipeline.
- Watch CI for free-port collisions between parallel grid-booting test classes; serialize them if they appear.
