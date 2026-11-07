#!/bin/sh
# Builds Rapier's C library (3D, single precision, parallel) for Linux from the commit in source.env.
#
#   build-linux.sh OUTDIR
#
# OUTDIR  directory for the stripped library; it is named librapier-x86_64.so or librapier-arm64.so after the machine
#         the script runs on. A relative path is taken from the current directory.
#
# Needs git, a C compiler (the linker), and rustup with the toolchain named in source.env (RUST_TOOLCHAIN). Build on the oldest distribution the library has
# to run on (DreamGrid: Debian 12), because the result links against that glibc.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
. "$here/source.env"
out=$1
case $out in /*) ;; *) out=$(pwd)/$out ;; esac

case $(uname -m) in
    x86_64) arch=x86_64 ;;
    aarch64|arm64) arch=arm64 ;;
    *) echo "unsupported machine $(uname -m)" >&2; exit 1 ;;
esac

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

git -c init.defaultBranch=main init -q "$work/src"
cd "$work/src"
git config core.autocrlf false
git remote add origin "$RAPIER_REPO"
git fetch -q --depth 1 --filter=blob:none origin "$RAPIER_COMMIT"
# The workspace manifest names every crate in the repository, so all the crate directories must exist.
git sparse-checkout set --cone bindings/c bindings/python crates src examples2d examples3d examples3d-f64
git checkout -q FETCH_HEAD
echo "rapier $RAPIER_TAG $(git rev-parse HEAD)"
# Rapier has no Cargo.lock; this folder's one fixes every dependency version.
cp "$here/Cargo.lock" Cargo.lock

cargo "+$RUST_TOOLCHAIN" build --release --locked --package "$RAPIER_FFI_PACKAGE" --features parallel > "$work/cargo.log" 2>&1 \
    || { tail -60 "$work/cargo.log"; exit 1; }

mkdir -p "$out"
strip -o "$out/librapier-$arch.so" target/release/librapier3d_ffi.so
echo "built $out/librapier-$arch.so: $(nm -D --defined-only "$out/librapier-$arch.so" | awk '$3 ~ /^r3/' | wc -l) r3 functions exported"
