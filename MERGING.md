# Taking updates from OpenSimulator

The `OpenSim/` tree here comes from [OpenSimulator](http://opensimulator.org/). We take its changes by
cherry-picking commits from its `master` branch, a batch at a time, each batch in one PR.

This project began as a fork of Fred Beckhusen's Outworldz DreamGrid (`Outworldz/DreamGrid-Opensim`, branch
`V7`), and until November 2026 OpenSimulator's changes reached us through his tree. That repository is no
longer public, so we now take them from OpenSimulator directly.

## Where our tree stands

- **Base:** OpenSimulator `master` at `9d00a5555225b17d3d1063cb0f3496fb1b6217f9` (2026-11-07, "soem extra
  spaces"), version `0.9.3.1` Dev. Every upstream commit up to it has been picked or recorded. On top of it sit
  Fred's patches and our own changes.
- **How we got there:** Fred brought in upstream `78cb44c0` (2026-08-11) as "Update to August core"
  (`a19786cf`). "OpenSim merge 1" (PR #30) picked 34 later commits, and "OpenSim merge 2" picked the other 36.
- **No shared history.** Our history starts with Fred's import of the source in January 2025 ("V5 from Tandy"),
  so `git merge-base` finds no common commit with OpenSimulator. A `git merge` would treat every file that
  differs as a conflict, so we cherry-pick instead.
- **What we have taken since the base** is recorded in commit trailers: every commit picked from
  OpenSimulator carries `Upstream-OpenSim: <full upstream SHA>`.

## One-time setup

```bash
git remote add opensim https://github.com/opensim/opensim.git
```

That is OpenSimulator's official GitHub mirror of its own repository, `git://opensimulator.org/git/opensim`;
both carry the same commits. Use the mirror. It serves HTTPS, while opensimulator.org's HTTPS endpoint
(`https://opensimulator.org/git/opensim`) failed its TLS handshake when this was written.

On Windows, clone with `core.longpaths=true` (see `BUILDING.md`).

## Each update

Work in a branch named after the batch, e.g. `opensim-merge-2`.

### 1. List the commits we have not taken

```bash
git fetch opensim
BASE=9d00a5555225b17d3d1063cb0f3496fb1b6217f9
git log main --format='%(trailers:key=Upstream-OpenSim,valueonly)' | grep . | sort -u > picked.txt
git rev-list --reverse --no-merges $BASE..opensim/master | grep -vFf picked.txt > todo.txt
git log --no-walk=unsorted --date=short --format='%h %ad %an | %s' $(cat todo.txt)
```

Skip merge commits: a pull request's own commits are picked one by one.

Read the list before picking. Upstream messages sometimes say a commit is untested or broken ("wait before
pull"). Take those together with the commit that fixes them, or leave them for a later batch.

### 2. Pick them, oldest first

```bash
for c in $(cat todo.txt); do
  git cherry-pick "$c" &&
  git commit --amend --no-edit --trailer "Upstream-OpenSim: $c" || break
done
```

The loop stops at the first conflict. Resolve it (see below), then `git cherry-pick --continue`, add the trailer
with the `--amend` line above, remove the commits already done from `todo.txt` and start the loop again.

A commit we already have makes an empty pick. A commit we decide not to take gets no pick at all. Record both
so they drop off the list next time:

```bash
git cherry-pick --skip   # only if a pick is in progress
git commit --allow-empty -m "OpenSim: already have <subject>" --trailer "Upstream-OpenSim: <sha>"
git commit --allow-empty -m "OpenSim: not taking <subject>: <reason>" --trailer "Upstream-OpenSim: <sha>"
```

### 3. Fix up what a clean pick leaves broken

- **Project files.** The `.csproj` files and `GridGate.sln` are checked in here, and Prebuild is not run.
  Our projects list every source file (`EnableDefaultItems` is `false`), while OpenSimulator's Prebuild picks
  files up by pattern. So an upstream commit that adds a `.cs` file picks cleanly but fails to build. Add a
  `<Compile Include=...>` to the matching `.csproj` (remove it for a deleted file). When upstream changes its
  `prebuild.xml`, make the same change by hand. To see the batch's project changes, diff upstream's own
  files, using the upstream SHAs of the first and last commits you picked:

  ```bash
  git diff <first-upstream-sha>^ <last-upstream-sha> -- prebuild.xml 'addon-modules/*/prebuild*.xml'
  ```

  Our own `prebuild.xml` files are not used by anything. If a pick conflicts on one, take theirs.

  - **Changed reference:** edit the project's `.csproj`. Reference paths use `$(GgRoot)` (the repo root),
    never an absolute path.
  - **New project:** create a `.csproj` modeled on a sibling project, then
    `dotnet sln GridGate.sln add <path-to-csproj>`.
  - **Removed project:** `dotnet sln GridGate.sln remove <path-to-csproj>` and delete the file.

  The target framework is set once in `Directory.Build.props`; do not add `<TargetFramework>` to a project.
  Put these fixes in a commit of their own after the picks ("OpenSim merge N: build the upstream picks").

- **Config templates.** OpenSimulator documents new settings in `bin/*.ini.example`. GridGate writes its
  configuration from `bin/Robust.HG.ini.proto` and `bin/config-include/Proto/*/OpenSim.ini`, and the launcher
  tests compare the result with `Launcher/GridGate.Launcher.Tests/Snapshots/`. Carry new or changed settings
  over to those files by hand, in a commit of their own.

### Conflicts to expect

- **DreamGrid patches.** Fred patched some OpenSimulator files, for example Smart Start in
  `GridService.cs`, `HypergridLinker.cs`, `LLLoginService.cs` and `UserAgentService.cs`, and
  `OutboundUrlFilter.cs`. These are most of the source conflicts. Keep the GridGate behavior and fit the
  upstream change around it. The patches are usually marked with comments naming GridGate or Smart Start; `grep -rn 'SmartStart\|GridGate'
  OpenSim` finds most of them.
- **Our own changes.** We have changed OpenSim code too (ubODE fixes, the Rapier physics backend, login and
  logging fixes). Look at the blame on our side before you take upstream's side of a hunk.
- **Third-party binaries in `bin/`.** OpenSimulator tracks prebuilt third-party DLLs (libomv, CSJ2K, and
  others), and so do we. When it updates one, the pick conflicts as binary. Take theirs:
  `git checkout --theirs <file> && git add <file>`. Then check that every DLL from the same library was
  updated together.
- **Build outputs in `bin/`.** These are built, not tracked: only third-party binaries and hand-maintained
  files like `*.dll.config` are tracked in `bin/`. OpenSimulator does not track its build outputs either, so a
  pick should not touch them. If one does, `git rm --cached` the file.
- **Upstream CI.** `.github/workflows/msbuildnet.yml` is OpenSimulator's own workflow. It only runs in
  `opensim/opensim`. Skip upstream changes to `.github/`.

### Verify

```bash
dotnet build -c Release GridGate.sln
dotnet test --project tests/GridGate.Platform.Tests -c Release
dotnet run --project tests/GridGate.Smoke -c Release -- --bin bin
```

All three must pass before you open the PR. The PR CI then runs the full set on Debian 12, Debian 13 and
Windows.

### Move the base forward

Once every commit up to some upstream commit has been picked or recorded, update `BASE` above (and the
"Where our tree stands" section) to that commit, so the next list starts there.
