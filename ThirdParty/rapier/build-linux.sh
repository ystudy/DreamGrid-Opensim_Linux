#!/bin/sh
# Builds Rapier's C library (3D, single precision) for Linux from the commit in source.env.
#
#   build-linux.sh OUTDIR [single|mt]
#
# OUTDIR  directory for the stripped library. A relative path is taken from the current directory.
# single  (the default) built without Rapier's parallel feature, named librapier-x86_64.so or librapier-arm64.so after
#         the machine the script runs on. OpenSim loads it when RapierNumThreads is 1.
# mt      built with --features parallel (rayon), named librapier-mt-x86_64.so or librapier-mt-arm64.so. OpenSim loads
#         it only when RapierNumThreads is more than 1.
#
# Parry (Rapier's collision library) is downloaded from crates.io, checked against PARRY_CRATE_SHA256, patched with
# patches/parry3d-$PARRY_VERSION-edge-codes.patch (the build stops if it does not apply) and tested with edge-codes-test
# before Rapier is built against it.
#
# Needs git, curl, sha256sum, a C compiler (the linker), and rustup with the toolchain named in source.env (RUST_TOOLCHAIN). Build on the oldest distribution the library has
# to run on (DreamGrid: Debian 12), because the result links against that glibc.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
. "$here/source.env"
out=$1
variant=${2:-single}
case $out in /*) ;; *) out=$(pwd)/$out ;; esac
case $variant in
    single) features= ; name=librapier ;;
    mt) features="--features parallel" ; name=librapier-mt ;;
    *) echo "unknown variant $variant (single or mt)" >&2; exit 1 ;;
esac

case $(uname -m) in
    x86_64) arch=x86_64 ;;
    aarch64|arm64) arch=arm64 ;;
    *) echo "unsupported machine $(uname -m)" >&2; exit 1 ;;
esac

# Everything the build writes goes under one fixed directory, Cargo's downloads included (CARGO_HOME), because Rust
# records the source paths of dependencies in the library (panic messages): with fixed paths the result is the same on
# any machine and for any user. (Mapping a random directory with --remap-path-prefix does not work: the flag's text is
# hashed into the build, so a random path changes the library.) Creating the directory is the lock: a second build of
# the same variant on this machine stops here instead of sharing it.
work=/tmp/rapier-build-$variant
mkdir "$work" 2>/dev/null \
    || { echo "$work exists: another $variant build is running (or one was killed; then remove $work)" >&2; exit 1; }
trap 'rm -rf "$work"' EXIT
CARGO_HOME=$work/cargo-home
export CARGO_HOME
parry="$work/parry3d-$PARRY_VERSION"

git -c init.defaultBranch=main init -q "$work/src"
cd "$work/src"
git config core.autocrlf false
git remote add origin "$RAPIER_REPO"
git fetch -q --depth 1 --filter=blob:none origin "$RAPIER_COMMIT"
# The workspace manifest names every crate in the repository, so all the crate directories must exist.
git sparse-checkout set --cone bindings/c bindings/python crates src examples2d examples3d examples3d-f64
git checkout -q FETCH_HEAD
echo "rapier $RAPIER_TAG $(git rev-parse HEAD) ($variant)"
# Rapier has no Cargo.lock; this folder's one fixes every dependency version.
cp "$here/Cargo.lock" Cargo.lock

# The patched Parry, outside the Rapier checkout (inside a git work tree, git apply would skip the file).
curl -fsSL --proto '=https' -o "$work/parry3d.crate" "https://static.crates.io/crates/parry3d/parry3d-$PARRY_VERSION.crate"
echo "$PARRY_CRATE_SHA256  $work/parry3d.crate" | sha256sum -c -
tar xzf "$work/parry3d.crate" -C "$work"
(cd "$parry" && git apply --verbose "$here/patches/parry3d-$PARRY_VERSION-edge-codes.patch") \
    || { echo "patches/parry3d-$PARRY_VERSION-edge-codes.patch does not apply to parry3d $PARRY_VERSION" >&2; exit 1; }
grep -q 'EDGE-CODES PATCH' "$parry/src/shape/trimesh.rs" \
    || { echo "git apply reported success but $parry/src/shape/trimesh.rs is not patched" >&2; exit 1; }
patch_parry="patch.crates-io.parry3d.path='$parry'"

CARGO_TARGET_DIR="$work/edge-codes-test" cargo "+$RUST_TOOLCHAIN" test --release --locked \
    --manifest-path "$here/edge-codes-test/Cargo.toml" --config "$patch_parry" > "$work/test.log" 2>&1 \
    || { tail -60 "$work/test.log"; exit 1; }
# Every test in the file must have run and passed: a run of no tests also exits 0.
expected=$(grep -c '^#\[test\]' "$here/edge-codes-test/tests/edge_codes.rs")
passed=$(grep -c '^test .* \.\.\. ok$' "$work/test.log" || true)
[ "$expected" -gt 0 ] && [ "$passed" -eq "$expected" ] \
    || { tail -60 "$work/test.log"; echo "edge-codes-test: $passed of $expected tests passed" >&2; exit 1; }
echo "edge-codes-test: $passed of $expected tests passed"

# $features is unquoted on purpose: nothing for single, two words for mt.
# shellcheck disable=SC2086
cargo "+$RUST_TOOLCHAIN" build --release --locked --config "$patch_parry" --package "$RAPIER_FFI_PACKAGE" $features \
    > "$work/cargo.log" 2>&1 \
    || { tail -60 "$work/cargo.log"; exit 1; }

mkdir -p "$out"
strip -o "$out/$name-$arch.so" target/release/librapier3d_ffi.so
echo "built $out/$name-$arch.so: $(nm -D --defined-only "$out/$name-$arch.so" | awk '$3 ~ /^r3/' | wc -l) r3 functions exported"
