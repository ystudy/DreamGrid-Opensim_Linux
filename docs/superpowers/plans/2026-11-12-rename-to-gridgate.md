# Rename DreamGrid to GridGate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every DreamGrid name in the tree (paths, code, installers, CI, docs, short forms) becomes GridGate, except the listed history.

**Architecture:** A guard script that fails on any non-history "DreamGrid" comes first and is the test for every task. Then the tree is renamed area by area with `git mv` plus a mechanical text replacement, each area built and tested before the next: code, short forms, installers and CI, docs. A last task proves the whole thing in CI and on the release build.

**Tech Stack:** .NET 10 (C#, Razor, xUnit v3 on Microsoft.Testing.Platform), bash, PowerShell, Inno Setup, systemd, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-12-rename-to-gridgate-design.md`

## Global Constraints

- Work only in the worktree `C:\Users\ystud\Desktop\CODE\GridGate-rename` on branch `chore/rename-to-gridgate`. Never touch `C:\Users\ystud\Desktop\CODE\DreamGrid-Linux`.
- Rename rule: `DreamGrid` → `GridGate`, `dreamgrid` → `gridgate`, `Dreamgrid` → `GridGate`, `DREAMGRID` → `GRIDGATE`, `Dream Grid` → `GridGate`; `DG_` → `GG_`, `dg-` → `gg-`, `Dg`/`dg` identifier prefix → `Gg`/`gg`. Applies to file and folder names and to contents.
- Use `git mv` for every path rename, so history follows the files.
- History that never changes: `docs/superpowers/**`, `docs/wifi-diva-removal-log.md`, `CONTRIBUTORS.txt`, the `fred/*` remote/branch names, and the single history lines listed in Task 1's allow-list file. A line is history only if it is about the past (where the project came from, what Fred did); anything describing how GridGate works today is renamed.
- No behaviour changes and no data migration: nothing may look for or read a DreamGrid folder, service, user or variable.
- Keep each file's existing line endings (most are CRLF).
- Test commands: build `dotnet build GridGate.sln -c Release` (before Task 2: `DreamGrid.sln`); unit tests `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release` (about 15 minutes — run it in the background); one class `… -- --filter-class <Class>`. Baseline on main: 3,956 passed, 0 failed, 10 skipped.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Do not push until Task 6.

## Review Focus

1. **Launcher defaults and installers disagree** (launcher looks for `/var/lib/dreamgrid` while `install.sh` creates `/var/lib/gridgate`, or a `GRIDGATE_*` variable the installer sets is read as `DREAMGRID_*`) → a fresh install would not start. Pinned by `LauncherPathsTests` (Task 2) and the install smokes (Task 6).
2. **The region-ready route and `alert_uri` disagree** → regions never report ready, so the grid never says it's running. Pinned by `GridConfigWriterTests` and `LegacyEndpointsTests` (Task 2).
3. **A short form renamed on one side only** (a cookie, form field or element id written as `gg-lang` by the server but read as `dg-lang` by JS) → the language picker, captcha or sign-in silently stops working. Pinned by Task 3's zero-left check and the full test suite.
4. **The mechanical replacement rewrites a history line or a link to Fred's repository** (`Outworldz/DreamGrid-Opensim`, outworldz.com URLs) into something false. Pinned by Task 1's allow-list (exact lines that must still exist) and Task 5's check.
5. **Awkward or doubled text after replacement** ("GridGate's GridGate website", "the GridGate GridGate service") → visible on pages and in docs. Pinned by the doubled-name check in Tasks 2, 4 and 5.

---

### Task 1: Guard script for the old name

**Files:**
- Create: `.github/scripts/check-old-name.sh`
- Create: `.github/scripts/old-name-history.txt`
- Modify: `.github/workflows/dreamgrid-ci.yml` (the `retired-names` job only: add a step that runs the new script)

**Interfaces:**
- Produces: `bash .github/scripts/check-old-name.sh [pathspec...]` — exit 0 when clean, 1 when a non-history match is found (prints each), any other code is git's own error. With no pathspec it checks the whole tree. Later tasks run it scoped to their area as their test.

- [ ] **Step 1: Write the allow-list** `.github/scripts/old-name-history.txt`. One entry per line, `path|exact substring`, `#` comments allowed. Start with exactly these four (Task 5 may add others, each only for a line about the past):

```
README.md|It started as a fork of Outworldz DreamGrid
MERGING.md|This project began as a fork of Fred Beckhusen's Outworldz DreamGrid
MERGING.md|**DreamGrid patches.** Fred patched
VERSIONING.md|This number replaces Fred's DreamGrid numbering
```

- [ ] **Step 2: Write the script** in the style of `.github/scripts/check-retired-names.sh` (`set -euo pipefail`, `cd` to the repo root, git grep exit-code handling). Behaviour:
  - Content pass: `git grep -n -I -i -E 'dream ?grid'` over the given pathspecs (default `.`), excluding `docs/superpowers/`, `docs/wifi-diva-removal-log.md`, `CONTRIBUTORS.txt` and the script and allow-list themselves. Drop any match whose path equals an allow-list path and whose line contains that entry's substring (fixed-string match).
  - Path pass: `git ls-files -- <pathspecs>` filtered by `grep -i -E 'dream ?grid'`, minus the same excluded paths.
  - Allow-list rot: every allow-list entry must still match a line in its file; an entry that matches nothing fails with "stale history entry: …" (this catches a history line that the replacement rewrote).
  - Print each failure group with a one-line heading naming the spec, exit 1 if any failed.

- [ ] **Step 3: Run it and see it fail**

Run: `bash .github/scripts/check-old-name.sh; echo rc=$?`
Expected: hundreds of content and path matches, `rc=1`, and no "stale history entry" line (all four history lines exist today).

Run: `bash .github/scripts/check-old-name.sh CONTRIBUTORS.txt docs/superpowers; echo rc=$?`
Expected: `rc=0`.

- [ ] **Step 4: Add the CI step.** In the `retired-names` job, after the existing step, add a step named `Old name` that runs `bash .github/scripts/check-old-name.sh`. (The job stays red until Task 5; that is expected on this branch.)

- [ ] **Step 5: Commit**

```bash
git add .github/scripts/check-old-name.sh .github/scripts/old-name-history.txt .github/workflows/dreamgrid-ci.yml
git commit -m "ci: guard against the old DreamGrid name"
```

---

### Task 2: Code, build and templates

Areas: `Launcher/`, `tests/`, `OpenSim/`, `bin/`, `ThirdParty/`, `content/`, `Directory.Build.props`, `DreamGrid.sln`, `compile.sh`, `compile.bat`, `.gitleaks.toml`, `.gitleaksignore`, `.github/scripts/check-retired-names.sh` (its allow-list paths only).

**Files:**
- Rename (`git mv`): `DreamGrid.sln` → `GridGate.sln`; `Launcher/DreamGrid.Launcher{,.Core,.Tests,.IntegrationTests}/` → `Launcher/GridGate.Launcher{…}/` with each `.csproj` inside; `tests/DreamGrid.{FakeChild,InstallSmoke,Physics.Rapier.Tests,Platform.Tests,Smoke}/` → `tests/GridGate.{…}/` with each `.csproj`.
- Delete: `bin/data/Dreamgrid Telporter.jpg` (spec: Fred's unused banner).
- Modify: every text file in the areas above that contains a DreamGrid spelling, including the visible text of every page and every translation under `Launcher/GridGate.Launcher/Languages/`.
- Test: `Launcher/GridGate.Launcher.Tests/LauncherPathsTests.cs`, `GridConfigWriterTests.cs`, `LegacyEndpointsTests.cs`, `AccessTests.cs`, snapshots.

**Interfaces:**
- Consumes: `check-old-name.sh` (Task 1).
- Produces: solution `GridGate.sln`; projects and namespaces `GridGate.Launcher`, `GridGate.Launcher.Core`, `GridGate.Launcher.Tests`, `GridGate.Launcher.IntegrationTests`, `GridGate.FakeChild`, `GridGate.InstallSmoke`, `GridGate.Physics.Rapier.Tests`, `GridGate.Platform.Tests`, `GridGate.Smoke`; launcher program `GridGate.Launcher.dll`; data defaults `/var/lib/gridgate` and `<ProgramData>\GridGate`; environment variables `GRIDGATE_DATA`, `GRIDGATE_OPENSIM`, `GRIDGATE_MARIADB_BIN` (and every other `DREAMGRID_*` the launcher reads, same rule); route `/gridgate/regionready`; OpenSim RemoteAdmin method `admin_get_gridgate_identity` (handler `XmlRpcGetGridGateIdentity`; nothing in the launcher calls it today); `InternalsVisibleTo("GridGate.Physics.Rapier.Tests")`; default `GridName=GridGate`. Task 4 must use exactly these names.

- [ ] **Step 1: Update the expectations first.** In `LauncherPathsTests.cs` change the expected default to `Path.GetFullPath("/var/lib/gridgate")`, the Windows default folder name to `GridGate`, and the env keys to `GRIDGATE_*`. In `LegacyEndpointsTests.cs` and `AccessTests.cs` post to `/gridgate/regionready`. In `GridConfigWriterTests.cs` add (or extend) an assertion that `RegionReady` `alert_uri` is `http://127.0.0.1:{WebPort}/gridgate/regionready` and that `GridName` is `GridGate`.

- [ ] **Step 2: Run those classes and see them fail**

Run: `dotnet test --project Launcher/DreamGrid.Launcher.Tests -c Release -- --filter-class DreamGrid.Launcher.Tests.LauncherPathsTests` (and the same for the other three classes)
Expected: FAIL on the new values.

- [ ] **Step 3: Rename the paths** with `git mv` (list under Files). Then delete the picture with `git rm`.

- [ ] **Step 4: Replace the text** in every tracked text file under the areas above, applying the rename rule's five long spellings (not the short forms; Task 3 does those). Skip binary files. Use a replacement that keeps CRLF. Check before committing that `git diff --stat` shows no file outside these areas.

- [ ] **Step 5: Comments in `OpenSim/`.** The replacement turns every `DreamGrid:` patch marker into `GridGate:`. Read each changed OpenSim comment once: it must still be true (e.g. "report our PID to GridGate" is right; a comment crediting Fred's original patch should say "Fred's DreamGrid patch" only if it is about the past, and then it needs an allow-list entry — expected: none).

- [ ] **Step 6: Doubled names.** Run `git grep -n -E 'GridGate.{0,15}GridGate' -- Launcher tests OpenSim bin ThirdParty content` and fix prose that now reads twice (e.g. "GridGate's GridGate website" → "the GridGate website"). Leave code identifiers alone.

- [ ] **Step 7: Build and run the tests**

Run: `dotnet build GridGate.sln -c Release`
Expected: 0 errors.

Run: the four classes from Step 2 with the new project path and namespace.
Expected: PASS.

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release` (background, ~15 min) and `dotnet test --project tests/GridGate.Platform.Tests -c Release`.
Expected: same counts as the baseline, 0 failed. If a snapshot test fails only because the snapshot text changed, regenerate with `DG_CAPTURE_SNAPSHOTS=1` (the variable is renamed in Task 3) and check the diff is only the rename.

- [ ] **Step 8: Guard, scoped**

Run: `bash .github/scripts/check-old-name.sh Launcher tests OpenSim bin ThirdParty content Directory.Build.props GridGate.sln compile.sh compile.bat .gitleaks.toml .gitleaksignore; echo rc=$?`
Expected: `rc=0`.

- [ ] **Step 9: Update `check-retired-names.sh`** allow-list paths to the renamed files (`Launcher/GridGate.Launcher.Core/Runtime/RetiredFiles.cs`, `Launcher/GridGate.Launcher.Tests/RuntimeStagerTests.cs`) in both passes, then run `bash .github/scripts/check-retired-names.sh; echo rc=$?` — expected `rc=0`.

- [ ] **Step 10: Commit** (one commit, or a `git mv` commit followed by a content commit — both fine)

```bash
git add -A
git commit -m "refactor: rename DreamGrid to GridGate in code, tests, OpenSim and templates"
```

---

### Task 3: Short forms

**Files:** every tracked text file outside the history paths containing `DG_`, `dg-`, `Dg`/`dg` prefixes. Known today: `Launcher/GridGate.Launcher/**` (Razor, JS such as `wwwroot/js/pow.js`, C#), `Launcher/GridGate.Launcher.Tests/**`, `Launcher/GridGate.Launcher.IntegrationTests/**`, `Directory.Build.props` and every `.csproj` (`DgRoot`), `MERGING.md` (`$(DgRoot)`), `docs/languages.md`, `docs/SECURITY-NOTES.md`, `.github/workflows/*.yml`, and wherever `_DgDrawingSource` and `dgSha256` live.

**Interfaces:**
- Consumes: Task 2 names.
- Produces: `GgRoot` (MSBuild), `GG_CAPTURE_SNAPSHOTS`, `GG_SHA`, `GG_TEST_TEMP`, `GG_FRAMELOG`, `GG_BS_*`; cookies and names `gg-lang`, `gg-auth`, `gg-captcha`, `gg-guest`, `gg-setup`, … (every `dg-` token → `gg-`); `ggPowStart`, `ggPowSolve`, `ggPowDigest`, `ggSha256`, `_GgDrawingSource`.

- [ ] **Step 1: List the tokens** with `git grep -I -h -o -E '\bDG_[A-Z_]+|\bdg-[a-z0-9-]+|\b_?(dg|Dg)[A-Z][A-Za-z0-9]*' -- . ':!docs/superpowers' ':!docs/wifi-diva-removal-log.md' | sort -u` and confirm each one is ours (not part of another word or a third-party API; e.g. ODE's `dGeom…` has a capital G and does not match).

- [ ] **Step 2: Replace them** (`DG_`→`GG_`, `dg-`→`gg-`, `Dg`→`Gg`, `dg`+Capital → `gg`+Capital) in those files.

- [ ] **Step 3: Zero left**

Run: the Step 1 command again.
Expected: empty output.

- [ ] **Step 4: Build and test**

Run: `dotnet build GridGate.sln -c Release`, then the full unit suite (background) and `tests/GridGate.Platform.Tests`.
Expected: 0 errors; baseline counts, 0 failed. Snapshots now regenerate with `GG_CAPTURE_SNAPSHOTS=1`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor: rename the DG/dg short forms to GG/gg"
```

---

### Task 4: Installers, packages and CI

**Files:**
- Rename (`git mv`): `setup/debian/dreamgrid.service` → `setup/debian/gridgate.service`; `setup/windows/DreamGrid.iss` → `setup/windows/GridGate.iss`; `.github/workflows/dreamgrid-ci.yml` → `.github/workflows/gridgate-ci.yml`.
- Modify: everything under `setup/` (`debian/install.sh`, `debian/test/smoke.sh`, `package.ps1`, `windows/build.ps1`, `windows/test/smoke.ps1`, `test/Test-Package.ps1`, `README.md`, …) and `.github/workflows/*.yml`.

**Interfaces:**
- Consumes: Task 2 names (project paths, `GridGate.Launcher.dll`, `GRIDGATE_*`, data defaults) and Task 3 names (`GG_*`).
- Produces: Linux `/opt/gridgate`, `/opt/gridgate-runtime`, `/var/lib/gridgate`, `gridgate.service`, user and group `gridgate`, package `gridgate-<version>-linux-x64.tar.gz` with top folder `gridgate/`, stable asset `gridgate-linux-x64.tar.gz`, `GRIDGATE_RELEASE`; Windows `{autopf}\GridGate`, `C:\ProgramData\GridGate`, service `GridGate`, `GridGateSetup-<version>.exe`, Start-menu "GridGate admin page", firewall rule names with GridGate; CI workflow `gridgate-ci.yml`.

- [ ] **Step 1: Rename the paths** with `git mv` (list under Files).

- [ ] **Step 2: Replace the text** under `setup/` and `.github/` with the rename rule's five long spellings (the short forms there were done in Task 3). `.github/scripts/old-name-history.txt` and `check-old-name.sh` are excluded.

- [ ] **Step 3: New Windows AppId.** Generate a fresh GUID (`[guid]::NewGuid()`) and set `AppId={{<NEW-GUID>}` in `GridGate.iss`, replacing `DC03D674-08A7-42DF-AC65-F9045BF5707F`. Nothing else may refer to the old GUID (`git grep -i DC03D674` → no hits outside `docs/superpowers`).

- [ ] **Step 4: Cross-check names between the launcher and the installers.** Every path and variable the installers set or create must be one the launcher reads (Review Focus 1): `git grep -n -E 'GRIDGATE_[A-Z_]+' -- setup Launcher/GridGate.Launcher.Core | sort` — each variable set in `setup/` appears in the launcher, and `/var/lib/gridgate`, `/opt/gridgate`, `GridGate.Launcher.dll` match `LauncherPaths.cs` and the systemd unit's `ExecStart`.

- [ ] **Step 5: Doubled names** — the Task 2 Step 6 check over `setup .github`.

- [ ] **Step 6: Syntax checks**

Run: `bash -n setup/debian/install.sh && bash -n setup/debian/test/smoke.sh && echo ok`
Expected: `ok`.

Run: `pwsh -NoProfile -Command "foreach ($f in 'setup/package.ps1','setup/windows/build.ps1','setup/windows/test/smoke.ps1','setup/test/Test-Package.ps1') { $null = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $f), [ref]$null, [ref]$e); if ($e) { $e; exit 1 } }; 'ok'"`
Expected: `ok`.

Run: `pwsh -NoProfile -File setup/test/Test-Package.ps1` if it runs locally without a built package; otherwise note that Task 6's release run covers it.

- [ ] **Step 7: Guard, scoped**

Run: `bash .github/scripts/check-old-name.sh setup .github; echo rc=$?`
Expected: `rc=0`.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "build: GridGate names for the Debian and Windows installers, packages and CI"
```

---

### Task 5: Docs and the whole-tree guard

**Files:** `README.md`, `MERGING.md`, `VERSIONING.md`, `BUILDING.md`, `docs/*.md` other than `docs/wifi-diva-removal-log.md` (e.g. `SECURITY-NOTES.md`, `languages.md`, `jopensim-notes.md`, `opensim-search-notes.md`), any other root-level text file still matching; `.github/scripts/old-name-history.txt` only if a new history line is found.

- [ ] **Step 1: Replace the text** in those files with the five long spellings, then put back the four allow-listed history lines exactly as they were (`git diff` must show them unchanged).

- [ ] **Step 2: Hand-check the history parts.** In `MERGING.md`, the fork/Fred sections stay about the past (e.g. "Fred patched some OpenSimulator files"), while current instructions use the new names: `GridGate.sln`, `dotnet sln GridGate.sln …`, `$(GgRoot)`, `Launcher/GridGate.Launcher.Tests/Snapshots/`, and the marker search becomes `grep -rn 'SmartStart\|GridGate:'` with a sentence saying our patches are marked `GridGate:`. Any further line that is truly about the past gets an allow-list entry; nothing describing today does. `README.md`'s install table shows `/var/lib/gridgate`, `/opt/gridgate`, `C:\ProgramData\GridGate`, `C:\Program Files\GridGate`, `GridGateSetup-<version>.exe`, `GRIDGATE_RELEASE`.

- [ ] **Step 3: `bin/data/index.htm`** (if Task 2 left it reading "GridGate is a free Opensimulator server powered by Outworldz.com"): that claim is false for GridGate. Reword the sentence to "GridGate is a free OpenSimulator server." with no Outworldz links.

- [ ] **Step 4: Doubled names** — the Task 2 Step 6 check over the doc files.

- [ ] **Step 5: Whole-tree guard**

Run: `bash .github/scripts/check-old-name.sh; echo rc=$?` and `bash .github/scripts/check-retired-names.sh; echo rc=$?`
Expected: both `rc=0`.

Run: `git ls-files | grep -i dreamgrid | grep -v '^docs/superpowers/'`
Expected: empty.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "docs: GridGate names in the docs; DreamGrid stays only as history"
```

---

### Task 6: Prove it and open the PR

The controller does this task (it pushes and opens a PR).

- [ ] **Step 1: Full local run.** `dotnet build GridGate.sln -c Release`; full unit suite; `tests/GridGate.Platform.Tests`. Expected: baseline counts, 0 failed.
- [ ] **Step 2: Push the branch** `chore/rename-to-gridgate` (a new branch; no force-push).
- [ ] **Step 3: Release build.** `gh workflow run release.yml --ref chore/rename-to-gridgate`, then watch it. Expected: `package-linux`, `package-windows`, `smoke-debian (12/13, upgradetest, upgradetest-rt)`, `smoke-windows (upgradetest, upgradetest-rt)` all succeed; `publish` skipped. Download `package-linux` and confirm the tarball is `gridgate-<version>-linux-x64.tar.gz` with top folder `gridgate/`.
- [ ] **Step 4: Open the PR** with a body that lists what was renamed, the history that stays, the new Windows AppId, and the after-merge steps below. Wait for every check with `gh pr checks --watch` (not `--auto`). Merge only on the owner's word.

## After the merge (owner's OK for each; not subagent work)

1. **VM:** stop and remove the DreamGrid install on `root@192.168.86.37` (`dreamgrid.service`, `/opt/dreamgrid`, `/opt/dreamgrid.old`, `/opt/dreamgrid-runtime`, `/var/lib/dreamgrid`, user `dreamgrid`), then install the Task 6 package fresh. The owner runs setup and the Firestorm check.
2. **Folders:** when no session is working in it, move `C:\Users\ystud\Desktop\CODE\DreamGrid-Linux` → `C:\Users\ystud\Desktop\CODE\GridGate-OpenSim`, run `git worktree repair` there, then remove or rename `GridGate-rename` and `DreamGrid-benchbuild` once unused.
3. **Notes:** copy Claude's project notes to the folder for the new path and update the ones that name old paths.
