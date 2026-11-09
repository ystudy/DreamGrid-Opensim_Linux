#!/bin/sh
# Builds libubode (OpenSim's ubODE physics library) for Linux from opensim-libs, with this folder's patch applied.
#
#   build-linux.sh OUTPUT_FILE [PATCH]
#
# OUTPUT_FILE  where the stripped library goes, e.g. bin/lib64/libubode-x86_64.so
# PATCH        defaults to 0001-*.patch beside this script; pass check-old-vs-new-preprocess.patch for the test build
#
# Needs git, a C++ compiler, make, autoconf, automake, libtool and pkg-config. Build on the oldest distribution the
# library has to run on (DreamGrid: Debian 12), because the result links against that glibc and libstdc++.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
. "$here/source.env"
out=$1
case $out in /*) ;; *) out=$(pwd)/$out ;; esac
patch=${2:-$(ls "$here"/0001-*.patch)}
case $patch in /*) ;; *) patch=$(cd "$(dirname "$patch")" && pwd)/$(basename "$patch") ;; esac

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

git -c init.defaultBranch=main init -q "$work/src"
cd "$work/src"
git -c core.autocrlf=false remote add origin "$OPENSIM_LIBS_REPO"
git -c core.autocrlf=false fetch -q --depth 1 --filter=blob:none origin "$OPENSIM_LIBS_COMMIT"
git -c core.autocrlf=false sparse-checkout set "$UBODE_PATH"
git -c core.autocrlf=false checkout -q FETCH_HEAD
git -c core.autocrlf=false apply --check "$patch"
git -c core.autocrlf=false apply "$patch"
echo "opensim-libs $OPENSIM_LIBS_COMMIT + $(basename "$patch")"

cd "$UBODE_PATH"
chmod +x bootstrap
./bootstrap > "$work/bootstrap.log" 2>&1 || { tail -40 "$work/bootstrap.log"; exit 1; }
# The flags from ubODE's OPENSIM-README.txt for 64-bit Linux.
./configure --disable-asserts --enable-shared --disable-threading-intf > "$work/configure.log" 2>&1 \
    || { tail -40 "$work/configure.log"; exit 1; }
make -j"$(nproc)" > "$work/make.log" 2>&1 || { tail -60 "$work/make.log"; exit 1; }

mkdir -p "$(dirname "$out")"
strip -o "$out" ode/src/.libs/libubode.so.5.1.0
echo "built $out: $(nm -D --defined-only "$out" | awk '$3 ~ /^d/' | wc -l) ODE C functions exported"
