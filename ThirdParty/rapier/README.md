# Rapier

DreamGrid's third physics engine for the BulletS module (next to BulletSim and BulletXNA) is
[Rapier](https://rapier.rs), a Rust physics engine, used through the C bindings in its repository
(`bindings/c`, crate `rapier3d-ffi`). This folder holds what is needed to rebuild the native library and check it.
OpenSim reaches it through `OpenSim/Region/PhysicsModules/BulletS/Rapier/RapierNative.cs`, the only C# file that knows
the layout of `include/rapier.h`.

| File | What it is |
|---|---|
| `source.env` | The Rapier tag and the commit it points to (`v0.36.1`, `3406750a38286a0d3c7d0f4be2f9d53153e81d90`). The scripts fetch the commit. |
| `build-linux.sh OUTDIR` | Fetches that commit (sparse), runs `cargo build --release --features parallel`, strips the result and writes `OUTDIR/librapier-x86_64.so` or `librapier-arm64.so`. |
| `build-windows.ps1 -OutDir DIR` | The same for 64-bit Windows, with `-C target-feature=+crt-static`; writes `DIR/rapier.dll`. |
| `include/rapier.h` | The C header from that commit (`bindings/c/include/rapier.h`), unchanged apart from line endings. |
| `exports.txt` | The `r3` functions the header declares for this build (3D, single precision, no FEM, no robotics): 681 names. `gen-exports.sh` writes it from the header. |
| `LICENSE` | Rapier's Apache-2.0 licence. The tag has no NOTICE file. |
| `../../.github/workflows/rapier.yml` | Runs both scripts on GitHub (Linux x64 and arm64 in Debian 12, Windows x64) and checks exports, glibc and DLL imports. |

## Shipped libraries

`bin/lib64/librapier-x86_64.so`, `bin/lib64/librapier-arm64.so` and `bin/lib64/rapier.dll`, selected by the `rapier`
lines in `bin/OpenSim.Region.PhysicsModule.BulletS.dll.config`.

- `librapier-x86_64.so` (7.7 MB) and `rapier.dll` (9.1 MB) are built from the pinned commit. The Linux one was built
  on Debian 12 and needs at most glibc 2.35 (Debian 12 has 2.36); it needs only libgcc_s, libm and libc. `rapier.dll`
  imports only KERNEL32, ntdll and api-ms-win-core-synch (no VCRUNTIME, no MSVCP). Both export exactly the 681 names in
  `exports.txt`.
- `librapier-arm64.so` is not in the repository yet. It is built by `rapier.yml` (the `ubuntu-24.04-arm` runner); copy
  the `librapier-arm64` artifact into `bin/lib64` after its first successful run. Until then the dllmap line for arm64
  points at a file that does not exist, and `RapierNative.TryLoad` reports that the library could not be loaded.

## Building

- Linux: `ThirdParty/rapier/build-linux.sh /abs/path/out`. Needs git, a C compiler (the linker) and rustup with the
  toolchain named in `source.env` installed (`rustup toolchain install 1.99.1`). Build on Debian 12.
- Windows: `ThirdParty\rapier\build-windows.ps1 -OutDir out`. Needs git, rustup with the `source.env` toolchain and its
  `x86_64-pc-windows-msvc` target installed, and the C++ build tools of Visual Studio 2022 (run it from a "x64 Native
  Tools" prompt if a newer preview Visual Studio is installed that has no C++ libraries).

## What is pinned

A rebuild must produce the same code as the committed binaries, so three things are fixed:

- the Rapier source: `RAPIER_COMMIT` in `source.env` (the commit `v0.36.1` points to);
- every dependency version: `Cargo.lock` in this folder. Rapier's repository has no lockfile, so both scripts copy this one
  into the checkout and build with `cargo build --locked` (the build fails if the lockfile no longer fits);
- the Rust compiler: `RUST_TOOLCHAIN` in `source.env` (1.99.1). The scripts run `cargo +1.99.1`, and `rapier.yml`
  installs exactly that version with rustup.

The x86_64 Linux library rebuilt from these pins on Debian 12 is byte-identical to the committed one. The Windows DLL
differs from build to build in embedded paths, so compare its exports and imports, not its bytes.

To bump Rapier or Rust: change `RAPIER_TAG`/`RAPIER_COMMIT` and/or `RUST_TOOLCHAIN`; fetch the new commit in a scratch
checkout (same sparse set as the scripts), delete `Cargo.lock` there, run `cargo +NEWVERSION generate-lockfile`, and copy
it here; replace `include/rapier.h`, run `sh gen-exports.sh`, and update `RapierNative.cs` for any header change; run
`rapier.yml`, then copy its artifacts into `bin/lib64`.


## What the build checks

```sh
nm -D --defined-only librapier-x86_64.so | awk '$3 ~ /^r3/ {print $3}' | LC_ALL=C sort | diff - ThirdParty/rapier/exports.txt
objdump -T librapier-x86_64.so | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1     # at most GLIBC_2.36
dumpbin /dependents rapier.dll                                                    # KERNEL32, ntdll, api-ms-win-core-*, bcrypt only
```

At run time `RapierNative.VerifyAbi()` asks the library (`r3CheckAbi`, `r3PodLayout`) whether its structures have the
sizes `RapierNative.cs` assumes; the engine must not be used if it returns a message.
