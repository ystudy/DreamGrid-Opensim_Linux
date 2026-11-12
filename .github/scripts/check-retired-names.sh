#!/usr/bin/env bash
# Fails when a name of the retired add-ons shows up in a tracked text file outside the allow-list below.
# Exit codes: 0 clean, 1 matches found, anything else is git grep's own error code.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

rc=0
matches=$(git grep -n -I -i -E 'wifi|diva|jopensim' -- . \
  ':!CONTRIBUTORS.txt' ':!OpenSim/' ':!docs/superpowers/' ':!docs/jopensim-notes.md' ':!docs/opensim-search-notes.md' \
  ':!docs/wifi-diva-removal-log.md' ':!Launcher/DreamGrid.Launcher.Core/Runtime/RetiredFiles.cs' \
  ':!Launcher/DreamGrid.Launcher.Tests/RuntimeStagerTests.cs' ':!setup/package.ps1' ':!.github/scripts/check-retired-names.sh') || rc=$?

if [ "$rc" -eq 0 ]; then
  echo "These names were removed on 2026-11-12 (see docs/wifi-diva-removal-log.md) and must not come back:"
  echo "$matches"
  exit 1
fi
if [ "$rc" -eq 1 ]; then
  exit 0
fi
exit "$rc"
