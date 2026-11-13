# Rename DreamGrid to GridGate — design

Date: 2026-11-12. Owner-approved scope (conversation, 2026-11-12). Branch `chore/rename-to-gridgate`.

## Why

The project began as a fork of Fred Beckhusen's Outworldz DreamGrid. Since then the fundamentals have
been rebuilt: the launcher and website in `Launcher/` were written for this project from 2026-10-25
on (none of Fred's DreamGrid code is in it), and the Diva/Wifi add-ons are gone (PR 74). Fred has also
made his code private, so there is no upstream DreamGrid left to track. The product is GridGate, and
everything a person installs, runs, builds or reads should say so.

We are not hiding where the project came from. The history stays as history (see "What stays").

## Goal

After this change, the word DreamGrid (any spelling: `DreamGrid`, `dreamgrid`, `Dreamgrid`,
`DREAMGRID`, `Dream Grid`) and its short forms (`DG_`, `dg-`, `Dg`) appear nowhere in the tree except
the history list below. A fresh install on Debian or Windows shows GridGate in every folder, service,
user, file name, page and log line.

## Non-goals

- **No data migration.** The owner said a fresh install is fine. The new installers and the launcher know
  nothing about DreamGrid folders, services or users, and do not look for them.
- No behaviour changes. This is a rename; anything that works today works the same afterwards.
- No changes to the public wiki (separate, needs the owner's OK).
- No release or tag.

## Anti-goals

- Rewriting history: dated specs and plans must not be edited to pretend they always said GridGate.
- A half rename that leaves a running install using both names (e.g. a `gridgate` service writing to
  `/var/lib/dreamgrid`).
- Breaking the OpenSim upstream-merge workflow in `MERGING.md`.

## What gets renamed

Rule: `DreamGrid` → `GridGate`, `dreamgrid` → `gridgate`, `Dreamgrid` → `GridGate`,
`DREAMGRID` → `GRIDGATE`, `Dream Grid` → `GridGate`; short forms `DG_` → `GG_`, `dg-` → `gg-`,
`Dg` (identifier prefix, e.g. `DgRoot`, `dgSha`, `dgPow*`) → `Gg`/`gg`. Applies to file and folder
names as well as contents.

1. **Code and build.**
   - `DreamGrid.sln` → `GridGate.sln`.
   - Every project, folder, assembly, namespace and executable: `Launcher/DreamGrid.Launcher*`,
     `tests/DreamGrid.*` (e.g. `DreamGrid.FakeChild`, `DreamGrid.Physics.Rapier.Tests`) and any other
     `DreamGrid.*` project.
   - `Directory.Build.props` (`DgRoot` → `GgRoot`) and every `.csproj` that uses it.
   - `compile.sh`, `compile.bat`, `BUILDING.md`.
   - Test snapshots (`Snapshots/` folder moves with its project; regenerate with
     `DG_CAPTURE_SNAPSHOTS=1`, which itself becomes `GG_CAPTURE_SNAPSHOTS=1`).
2. **Linux install** (`setup/debian/`, `setup/package.ps1`, `install.sh`, systemd unit, smoke tests):
   - `/opt/dreamgrid` → `/opt/gridgate`, `/opt/dreamgrid-runtime` → `/opt/gridgate-runtime`,
     `/var/lib/dreamgrid` → `/var/lib/gridgate` (including the MariaDB socket and data paths under it).
   - `dreamgrid.service` → `gridgate.service`; system user and group `dreamgrid` → `gridgate`.
   - Package `dreamgrid-<version>-linux-x64.tar.gz` → `gridgate-<version>-linux-x64.tar.gz`, top folder
     inside it `dreamgrid/` → `gridgate/`. The stable asset name `dreamgrid-linux-x64.tar.gz` that
     `install.sh` downloads from the latest release → `gridgate-linux-x64.tar.gz`, with every doc that
     quotes it. No release has ever been published (`gh release list` is empty), so nothing published
     needs renaming.
   - Any helper names in the scripts (`dreamgrid-robust`, `dreamgrid-region`, `dreamgrid-mariadb`,
     `dreamgrid-stamp`, `dreamgrid-initialized`, …).
3. **Windows install** (`setup/windows/`):
   - `DreamGrid.iss` → `GridGate.iss`; `AppName=GridGate`; `{autopf}\GridGate`; `C:\ProgramData\GridGate`.
   - `DreamGridSetup-<version>.exe` → `GridGateSetup-<version>.exe`.
   - Service name and display name, firewall rule names, Start-menu entry ("GridGate admin page").
   - **New `AppId` GUID.** GridGate is a new program to Windows, not an upgrade of DreamGrid, so the two
     can never be mixed up. (Old DreamGrid installs are uninstalled by hand first.)
4. **Settings and environment.** `DREAMGRID_*` → `GRIDGATE_*` (e.g. `GRIDGATE_RELEASE`,
   `GRIDGATE_DATA`, `GRIDGATE_OPENSIM`, `GRIDGATE_MARIADB`), `DG_*` → `GG_*` (`GG_CAPTURE_SNAPSHOTS`,
   `GG_SHA`, `GG_TEST_TEMP`, `GG_FRAMELOG`, `GG_BS_*`). Names of files the launcher writes under its data
   folder (e.g. `dreamgrid_hypergrid…`, `dreamgrid_keys`, `dreamgrid_version`) → `gridgate_…`.
5. **Website.** Cookie, form-field, CSS and element names with `dg-` → `gg-` (`gg-lang`, `gg-auth`,
   `gg-captcha`, `gg-guest`, …) in Razor pages, JS, CSS and tests. Every visible "DreamGrid" text,
   including all translations under `Languages/`. Launcher HTTP routes too, e.g. the region-ready
   callback `/dreamgrid/regionready` → `/gridgate/regionready` (both the route and every `alert_uri`
   that points at it).
   - The default grid name `GridName=DreamGrid` (templates and snapshots) → `GridName=GridGate`. Only
     fresh installs use it, so no running grid's hypergrid identity changes.
6. **OpenSim code.**
   - The launcher identity call `admin_get_dreamgrid_identity` → `admin_get_gridgate_identity`
     (`XmlRpcGetDreamGridIdentity` → `XmlRpcGetGridGateIdentity`), renamed at both ends in one commit.
   - `AssemblyLoadContext("DreamGrid.BinaryFormatterCompat")` → `"GridGate.BinaryFormatterCompat"`.
   - Comments say GridGate, including every `DreamGrid:` patch marker, which becomes `GridGate:` (one
     marker for all of this fork's changes to OpenSim). `MERGING.md` says markers are `GridGate:`; its
     history part still says which patches came from Fred. The guard allows no `OpenSim/` exceptions.
7. **Config templates** under `bin/` (`Robust.HG.ini.proto`, `config-include/**`, `Estates/Estates.ini`,
   `data/index.htm`): text that names the product. `bin/data/Dreamgrid Telporter.jpg` is deleted: it is
   Fred's 2025 banner reading "DREAMGRID TELEPORTER", and nothing in the tree uses it.
8. **CI** (`.github/`): workflow names and files (`dreamgrid-ci.yml` → `gridgate-ci.yml`), job names,
   artifact and release asset names, scripts, `.gitleaks.toml` / `.gitleaksignore` paths. The repository
   has no required checks, so no branch-protection settings change.
9. **Docs that describe the product today:** `README.md` (except the origin line), `setup/README.md`,
   `BUILDING.md`, `VERSIONING.md`, `docs/SECURITY-NOTES.md`, `docs/languages.md`, `content/README.md`,
   `ThirdParty/*/README.md` and build scripts, and the current-state parts of `MERGING.md`
   (`GridGate.sln`, `$(GgRoot)`, snapshot paths).

## What stays (history)

- `docs/superpowers/**` (dated specs and plans), `docs/wifi-diva-removal-log.md`, `CONTRIBUTORS.txt`.
- The README origin line ("It started as a fork of Outworldz DreamGrid …").
- The `MERGING.md` text that describes the fork and Fred's patches (e.g. "DreamGrid patches",
  `Outworldz/DreamGrid-Opensim`). These are names of real things in the past.
- Git remote and branch names that name Fred's repository (`fred/*`).
- The licence (OpenSim BSD) is unchanged.

A history mention is allowed only where it is about the past (where the project came from, what a
dated document recorded). Anything that describes how GridGate works today is renamed.

## Guard

Extend `.github/scripts/check-retired-names.sh` (or add a sibling check run by the same `retired-names`
CI job) so that any `dreamgrid` / `dream grid` (case-insensitive) in file contents or paths fails CI,
outside an allow-list: `docs/superpowers/`, `docs/wifi-diva-removal-log.md`, `CONTRIBUTORS.txt`,
`MERGING.md`, `README.md` (origin line only — the check may allow the file but a test or reviewer
confirms only that line remains), the check script itself, and `OpenSim/` lines the builder proves are
history (expected: none). The existing allow-list entries for the Wifi check (`RetiredFiles.cs`,
`RuntimeStagerTests.cs`, …) move with their renamed files; `RetiredFiles.cs` lists only Wifi/Diva names,
so it needs no DreamGrid entry. Short forms (`DG_`, `dg-`) are checked once by the builder during the work,
not by CI, because `dg` appears in unrelated words.

## Installs after the change

- **CI** (`gh workflow run release.yml --ref chore/rename-to-gridgate`, which builds and smoke-tests
  but publishes nothing) builds and smoke-tests the GridGate packages (Debian 12, Debian 13, Windows) including the
  existing upgrade smokes, which upgrade one GridGate build to another and so need no DreamGrid
  knowledge. All must pass.
- **The Debian 12 test VM** (owner's): with the owner's OK, stop and remove the DreamGrid install
  (`dreamgrid.service`, `/opt/dreamgrid`, `/opt/dreamgrid.old`, `/opt/dreamgrid-runtime`,
  `/var/lib/dreamgrid`, the `dreamgrid` user), then install the GridGate package fresh. The owner runs
  setup and the Firestorm check as usual.
- **Windows:** any DreamGrid install is uninstalled by hand before installing GridGate. The Windows
  package is checked by the CI smokes; a hand check on a Windows machine is the owner's choice.
- The VM work and the local-folder moves below are done after the merge, each only with the owner's OK.
  They are steps, not code in the PR.

## Local folders (after merge)

When the PR is merged and no other session is working in it:

- Move `C:\Users\ystud\Desktop\CODE\DreamGrid-Linux` → `C:\Users\ystud\Desktop\CODE\GridGate-OpenSim`,
  then run `git worktree repair` there (and in each linked worktree) so the worktrees under
  `.claude/worktrees/` and `DreamGrid-benchbuild` still find the repository.
- New working folders are named `GridGate-<topic>`. Existing ones (`DreamGrid-benchbuild`, this
  branch's `GridGate-rename`) are renamed or removed once nothing uses them.
- Claude's saved project notes live under a folder keyed by the checkout path; copy them to the new
  path's folder and update notes that name the old paths (e.g. the VM note's `/opt/dreamgrid`).

## Left to the builder

- Order of the work (a mechanical rename commit, then fix-ups, is expected; `git mv` so history follows
  files).
- Handling of names inside binary files, if any (report them).
- Whether the short-form renames (`dg-` → `gg-`) need any care where `dg` is part of another word.

## Done means

- `git grep -I -i -E 'dream ?grid'` and `git ls-files | grep -i dreamgrid` return only allow-listed
  history.
- Full test suite passes; snapshots regenerated; release.yml (packages + all smokes) passes on the
  branch; PR CI passes including the extended guard.
- The VM runs the fresh GridGate install and the owner has checked it.
- The local folders are renamed as above.
