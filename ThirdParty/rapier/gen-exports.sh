#!/bin/sh
# Writes exports.txt: the r3 functions include/rapier.h declares for the build DreamGrid ships (3D, single precision, no
# FEM or robotics), one per line, sorted. build-linux.sh's output and rapier.yml are checked against it.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
# The preprocessor drops the declarations the header guards behind other builds (2D, double precision, FEM, robotics).
gcc -E -P -DRAPIER_DIM3 -DRAPIER_F32 -DRAPIER_PARALLEL "$here/include/rapier.h" \
    | grep -oE '\br3[A-Za-z0-9_]*\(' | tr -d '(' | LC_ALL=C sort -u > "$here/exports.txt"
wc -l < "$here/exports.txt"
