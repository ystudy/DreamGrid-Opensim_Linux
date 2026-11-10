# Versioning

GridGate has its own version number, separate from OpenSimulator's. Every release also says which
OpenSimulator code it is built on.

## The GridGate version

GridGate uses [semantic versioning](https://semver.org/): `major.minor.patch`.

- **patch** (`0.1.0` → `0.1.1`): fixes only, nothing for users to change.
- **minor** (`0.1.1` → `0.2.0`): new features, or a new batch of OpenSimulator updates (see below).
- **major**: a change users must act on, such as a config or database step the upgrade cannot do for them.
  While the version is `0.x`, GridGate is not yet declared stable, and a minor release may also contain such a
  change; its release notes say so.

The first release is `0.1.0`. `1.0.0` is the release we call stable.

This number replaces Fred's DreamGrid numbering (`7.x`, e.g. `7.21191`). Our main branch is still named `V7`,
but that name is not a version.

### Where the number comes from

The release tag. Pushing `v0.1.0` runs `.github/workflows/release.yml`, which builds, smoke-tests and publishes
version `0.1.0`. The tag must be `v<major>.<minor>.<patch>`, optionally with a pre-release suffix
(`v0.1.0-rc1`, published as a GitHub pre-release); anything else fails the run. Builds that are not from a tag
are `0.0.0-dev+<short commit>`.

That plain version is used everywhere a machine reads it: the package and installer names
(`dreamgrid-0.1.0-linux-x64.tar.gz`, `DreamGridSetup-0.1.0.exe`), the launcher's `--version`, and the website
footer (`DreamGrid 0.1.0`, which the install smokes read). The product still installs as "DreamGrid".

## The OpenSimulator base

OpenSimulator's own version stays as it is. `OpenSim/Framework/VersionInfo.cs` says `0.9.3.1` `Dev`, and the
simulator reports `OpenSim 0.9.3.1 Nessie Dev`. Viewers and other grids see that string, and OpenSimulator
pads it to a fixed width (`VERSIONINFO_VERSION_LENGTH`), so we do not change it.

`0.9.3.1 Dev` is not enough to say which code we have. OpenSimulator has shown that number on its `master`
branch since November 2024, and it has not released 0.9.3.1. So we name the base by commit:

```
OpenSimulator 0.9.3.1 Dev, 78cb44c0 + 34 picks
```

- `0.9.3.1 Dev` is `VersionNumber` and `VERSION_FLAVOUR` from `VersionInfo.cs`.
- `78cb44c0` is the upstream commit our tree is based on (`BASE` in `MERGING.md`).
- `34 picks` is how many upstream commits after the base we have cherry-picked; those commits carry an
  `Upstream-OpenSim:` trailer. Count them with:

  ```bash
  git log V7 --format='%(trailers:key=Upstream-OpenSim,valueonly)' | grep . | sort -u | wc -l
  ```

  Empty "already have" and "not taking" records count too: they mark upstream commits we have dealt with.
  After the base moves forward, count only the trailers for commits after the new base.

## The full version

The full version puts the two together:

```
GridGate 0.1.0 (OpenSimulator 0.9.3.1 Dev, 78cb44c0 + 34 picks)
```

It goes on the first line of `Version.txt`. The release workflow puts it at the top of each release's notes. Where a single token is
needed, use semantic-versioning build metadata: `0.1.0+os.0.9.3.1-dev.78cb44c0.34`. Never use that form as a
tag; tags stay plain.

## Making a release

1. Pick the version from the rules above.
2. In a PR into `V7`, set `Version.txt` to the full version. Update the OpenSimulator part if an
   update batch has landed since the last release.
3. After that PR is merged, tag the merge commit and push the tag:

   ```bash
   git tag -a v0.1.0 -m "GridGate 0.1.0 (OpenSimulator 0.9.3.1 Dev, 78cb44c0 + 34 picks)"
   git push origin v0.1.0
   ```

The release workflow checks `Version.txt` before it builds anything:

- Every run fails unless the first line has the form
  `GridGate <major>.<minor>.<patch> (OpenSimulator <base>)`.
- A tag push also fails unless the tag matches that GridGate number. A pre-release tag only needs to match
  without its suffix, so `v0.1.0-rc1` can be tagged while `Version.txt` says `0.1.0`.

The published release notes then start with `GridGate <tag version> (OpenSimulator <base>)`.
