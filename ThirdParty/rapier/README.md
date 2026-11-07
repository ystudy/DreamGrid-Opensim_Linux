# Rapier

DreamGrid's third physics engine for the BulletS module (next to BulletSim and BulletXNA) is
[Rapier](https://rapier.rs), a Rust physics engine, used through the C bindings in its repository
(`bindings/c`, crate `rapier3d-ffi`). This folder holds what is needed to rebuild the native library and check it.
OpenSim reaches it through `OpenSim/Region/PhysicsModules/BulletS/Rapier/RapierNative.cs`, the only C# file that knows
the layout of `include/rapier.h`.

| File | What it is |
|---|---|
| `source.env` | The Rapier tag and the commit it points to (`v0.36.1`, `3406750a38286a0d3c7d0f4be2f9d53153e81d90`). The scripts fetch the commit. |
| `build-linux.sh OUTDIR [single\|mt]` | Fetches that commit (sparse), runs `cargo build --release` (with `--features parallel` for `mt`), strips the result and writes `OUTDIR/librapier-x86_64.so` or `librapier-arm64.so` (`single`, the default), or `librapier-mt-x86_64.so` or `librapier-mt-arm64.so` (`mt`). |
| `build-windows.ps1 -OutDir DIR [-Parallel]` | The same for 64-bit Windows, with `-C target-feature=+crt-static`; writes `DIR/rapier.dll`, or `DIR/rapier-mt.dll` with `-Parallel`. |
| `include/rapier.h` | The C header from that commit (`bindings/c/include/rapier.h`), unchanged apart from line endings. |
| `exports.txt` | The `r3` functions the header declares for these builds (3D, single precision, no FEM, no robotics): 681 names, the same for `single` and `mt` (a build without `parallel` still exports the thread-pool functions; they answer `R3_UNSUPPORTED`). `gen-exports.sh` writes it from the header. |
| `LICENSE` | Rapier's Apache-2.0 licence. The tag has no NOTICE file. |
| `../../.github/workflows/rapier.yml` | Runs both scripts on GitHub for both builds (Linux x64 and arm64 in Debian 12, Windows x64) and checks exports, glibc and DLL imports. |

## Shipped libraries

Two builds of the same commit, each selected by its own lines in `bin/OpenSim.Region.PhysicsModule.BulletS.dll.config`:

| dllmap name | Build | Files | Loaded when |
|---|---|---|---|
| `rapier` | without `parallel` | `bin/lib64/librapier-x86_64.so`, `librapier-arm64.so`, `rapier.dll` | `RapierNumThreads` is 1 (the default) |
| `rapier_mt` | `--features parallel` (rayon) | `bin/lib64/librapier-mt-x86_64.so`, `librapier-mt-arm64.so`, `rapier-mt.dll` | `RapierNumThreads` is more than 1 |

Why two: a parallel build runs every step inside its rayon pool, even a pool of one thread, and waking that pool's
worker after the gap between two frames costs 35-40 µs per step, more than the whole step of a small scene. Without a
pool (never calling `r3SetNumThreads`, or `r3ClearThreadPool`) its parallel parts would run on rayon's global pool, one
thread per core, so every region would quietly become multi-threaded. The single-threaded build has neither problem; the
parallel one is kept for regions that ask for more threads.

`RapierNative.TryLoad` loads one of them, once per process, chosen by the first Rapier region's `RapierNumThreads`. If
`rapier_mt` cannot be loaded it logs a warning and runs at 1 thread with `rapier`. A later region that asks for a thread
count the loaded build does not fit gets a warning and the loaded build. The region log names the file in use
(`Rapier 0.36.1 native library: ...`, with `+parallel` after the version for `rapier_mt`).

- `librapier-x86_64.so` (7.0 MB), `librapier-mt-x86_64.so` (7.7 MB), `rapier.dll` (8.2 MB) and `rapier-mt.dll`
  (9.1 MB) are built from the pinned commit. The Linux ones were built in a `debian:12` container and need at most
  glibc 2.35 (Debian 12 has 2.36); they need only libgcc_s, libm and libc. The DLLs import only KERNEL32, ntdll and
  api-ms-win-core-synch (no VCRUNTIME, no MSVCP). All four export exactly the 681 names in `exports.txt`.
- `librapier-arm64.so` and `librapier-mt-arm64.so` are not in the repository yet. They are built by `rapier.yml` (the
  `ubuntu-24.04-arm` runner); copy the `librapier-arm64` and `librapier-mt-arm64` artifacts into `bin/lib64` after its
  first successful run. Until then the dllmap lines for arm64 point at files that do not exist, and
  `RapierNative.TryLoad` reports that the library could not be loaded.

## Building

- Linux: `ThirdParty/rapier/build-linux.sh /abs/path/out`, and again with `mt` after the directory. Needs git, a C compiler (the linker) and rustup with the
  toolchain named in `source.env` installed (`rustup toolchain install 1.99.1`). Build on Debian 12.
- Windows: `ThirdParty\rapier\build-windows.ps1 -OutDir out`, and again with `-Parallel`. Needs git, rustup with the `source.env` toolchain and its
  `x86_64-pc-windows-msvc` target installed, and the C++ build tools of Visual Studio 2022 (run it from a "x64 Native
  Tools" prompt if a newer preview Visual Studio is installed that has no C++ libraries).

## What is pinned

A rebuild must produce the same code as the committed binaries, so three things are fixed:

- the Rapier source: `RAPIER_COMMIT` in `source.env` (the commit `v0.36.1` points to);
- every dependency version: `Cargo.lock` in this folder. Rapier's repository has no lockfile, so both scripts copy this one
  into the checkout and build with `cargo build --locked` (the build fails if the lockfile no longer fits);
- the Rust compiler: `RUST_TOOLCHAIN` in `source.env` (1.99.1). The scripts run `cargo +1.99.1`, and `rapier.yml`
  installs exactly that version with rustup.

One dependency is not from crates.io: Rapier's workspace patches `puffin_egui` (0.29.0) to a git repository
(github.com/tedsteen/puffin), and `Cargo.lock` pins it to rev `11771ebe00fd257aedbb545df3339ad597b1cc34`. The build is
therefore not hermetic: it still needs network access to fetch crates.io and that git repository.

The x86_64 Linux libraries rebuilt from these pins on Debian 12 are byte-identical to the committed ones (and the `mt`
one to the parallel `librapier-x86_64.so` shipped before the split). The Windows DLL
differs from build to build in embedded paths, so compare its exports and imports, not its bytes.

To bump Rapier or Rust: change `RAPIER_TAG`/`RAPIER_COMMIT` and/or `RUST_TOOLCHAIN`; fetch the new commit in a scratch
checkout (same sparse set as the scripts), delete `Cargo.lock` there, run `cargo +NEWVERSION generate-lockfile`, and copy
it here; replace `include/rapier.h`, run `sh gen-exports.sh`, and update `RapierNative.cs` for any header change; run
`rapier.yml`, then copy its artifacts (both builds) into `bin/lib64`.


## What the build checks

For each of the two builds:

```sh
nm -D --defined-only librapier-x86_64.so | awk '$3 ~ /^r3/ {print $3}' | LC_ALL=C sort | diff - ThirdParty/rapier/exports.txt
objdump -T librapier-x86_64.so | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1     # at most GLIBC_2.36
dumpbin /dependents rapier.dll                                                    # KERNEL32, ntdll, api-ms-win-core-*, bcrypt only
```

At run time `RapierNative.VerifyAbi()` asks the library (`r3CheckAbi`, `r3PodLayout`) whether its structures have the
sizes `RapierNative.cs` assumes; the engine must not be used if it returns a message.
