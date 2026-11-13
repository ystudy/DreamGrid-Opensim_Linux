#!/bin/sh
# Writes exports.txt: the r3 functions include/rapier.h declares for the builds GridGate ships (3D, single precision, no
# FEM or robotics), one per line, sorted. build-linux.sh's output and rapier.yml are checked against it. The one list
# fits both builds, single and mt: the header does not guard the thread-pool functions (r3SetNumThreads,
# r3ClearThreadPool, r3NumThreads) behind the parallel feature, and a build without it exports them too (they answer
# R3_UNSUPPORTED, or 1 for r3NumThreads). RAPIER_PARALLEL below changes nothing in this header.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
# The preprocessor drops the declarations the header guards behind other builds (2D, double precision, FEM, robotics).
gcc -E -P -DRAPIER_DIM3 -DRAPIER_F32 -DRAPIER_PARALLEL "$here/include/rapier.h" \
    | grep -oE '\br3[A-Za-z0-9_]*\(' | tr -d '(' | LC_ALL=C sort -u > "$here/exports.txt"
wc -l < "$here/exports.txt"
