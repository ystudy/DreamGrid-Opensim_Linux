#!/usr/bin/env bash
# Downloads the larger glTF test models into Models/downloaded/ (git-ignored). Tests that need one skip when it is missing.
# Source: https://github.com/KhronosGroup/glTF-Sample-Assets (check each model's LICENSE.md there before using it elsewhere).
set -euo pipefail
cd "$(dirname "$0")"
BASE=https://raw.githubusercontent.com/KhronosGroup/glTF-Sample-Assets/main/Models
OUT=Models/downloaded
mkdir -p "$OUT"

# A binary glTF: one file, downloaded/<Name>.glb
fetch_glb() {
    local name=$1
    [ -s "$OUT/$name.glb" ] || curl -fsSL -o "$OUT/$name.glb" "$BASE/$name/glTF-Binary/$name.glb"
}

# A text glTF with the files it points at, downloaded/<Out>/<Out>.gltf (the files keep their names).
# fetch_gltf <Name> <variant folder> <Out>
fetch_gltf() {
    local name=$1 variant=$2 out=$3
    local dir="$OUT/$out"
    [ -s "$dir/$out.gltf" ] && return 0
    mkdir -p "$dir"
    curl -fsSL -o "$dir/$out.gltf" "$BASE/$name/$variant/$name.gltf"
    grep -o '"uri"[[:space:]]*:[[:space:]]*"[^"]*"' "$dir/$out.gltf" | sed 's/.*:[[:space:]]*"\(.*\)"/\1/' | { grep -v '^data:' || true; } | sort -u |
    while read -r uri; do
        # The uris come from a downloaded file: nothing that climbs out of the folder, is absolute, or names a scheme.
        case "$uri" in
            *..*|/*|\\*|*%2[eE]*|[A-Za-z]*:*) echo "Refusing unsafe uri in $out.gltf: $uri" >&2; rm -f "$dir/$out.gltf"; exit 1 ;;
        esac
        mkdir -p "$dir/$(dirname "$uri")"
        curl -fsSL -o "$dir/$uri" "$BASE/$name/$variant/$uri"
    done
}

for m in DamagedHelmet ABeautifulGame MaterialsVariantsShoe CesiumMan MultiUVTest NegativeScaleTest NodePerformanceTest; do
    fetch_glb "$m"
done
fetch_gltf FlightHelmet glTF FlightHelmet
fetch_gltf Sponza glTF Sponza
fetch_gltf Lantern glTF-Draco Lantern-draco
fetch_gltf MeshoptCubeTest glTF-Meshopt MeshoptCubeTest
fetch_gltf MeshPrimitiveModes glTF-Embedded MeshPrimitiveModes
echo "Done: $OUT"
