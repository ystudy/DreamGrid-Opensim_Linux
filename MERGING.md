# Merging from Outworldz/DreamGrid-Opensim

This repository follows Fred's fork, [Outworldz/DreamGrid-Opensim](https://github.com/Outworldz/DreamGrid-Opensim)
(branch `V7`), which carries the DreamGrid patches and pulls upstream OpenSimulator.
Merge roughly monthly, or when Fred ships a release.

Two things differ from Fred's tree, and both show up during a merge:

- **Project files.** Fred generates `.csproj` files with Prebuild from `prebuild.xml`. Here they are checked in
  and Prebuild is not run. `prebuild.xml` is kept only so you can see what Fred changed.
- **Build outputs.** Fred commits the DLLs and EXEs the build produces into `bin/`. Here they are built, not
  tracked. Only third-party binaries (and hand-maintained files like `*.dll.config`) are tracked in `bin/`.

## One-time setup

```bash
git remote add fred https://github.com/Outworldz/DreamGrid-Opensim.git
```

## Each merge

```bash
git fetch fred
git merge fred/V7
```

### Conflicts on build outputs in `bin/`

Fred's commits modify DLLs we no longer track, so git reports modify/delete conflicts on files like
`bin/OpenSim.Region.CoreModules.dll`. Resolve each by keeping them untracked:

```bash
git status --short | grep '^DU bin/' | cut -c4- | xargs git rm --cached --quiet
```

A conflict on a third-party DLL or a `*.dll.config` file is real: take Fred's version
(`git checkout --theirs <file> && git add <file>`).

### Mirror project changes

```bash
git diff ORIG_HEAD..fred/V7 -- prebuild.xml 'addon-modules/*/prebuild*.xml'
```

For each project, reference or file pattern added, removed or changed there, make the same change by hand to
the matching `.csproj`:

- **Changed reference or file list:** edit the project's `.csproj`. Reference paths use `$(DgRoot)` (the repo
  root), never an absolute path.
- **New project:** create a `.csproj` modeled on a sibling project, then
  `dotnet sln DreamGrid.sln add <path-to-csproj>`.
- **Removed project:** `dotnet sln DreamGrid.sln remove <path-to-csproj>` and delete the file.

The target framework is set once in `Directory.Build.props`; do not add `<TargetFramework>` to a project.

### Verify

```bash
dotnet build -c Release DreamGrid.sln
dotnet test --project tests/DreamGrid.Platform.Tests -c Release
dotnet run --project tests/DreamGrid.Smoke -c Release -- --bin bin
```

All three must pass before the merge is committed.
