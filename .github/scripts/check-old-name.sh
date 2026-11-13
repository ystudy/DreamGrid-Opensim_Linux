#!/usr/bin/env bash
# Fails when the old name (DreamGrid) shows up in a tracked file or file name, except in the history paths
# and on the history lines listed in old-name-history.txt.
# Usage: check-old-name.sh [pathspec...]   (default: the whole tree)
# Exit codes: 0 clean, 1 matches found, anything else is git's own error code.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

history_file=.github/scripts/old-name-history.txt
pattern='dream ?grid'
if [ "$#" -eq 0 ]; then
  set -- .
fi

excludes=(':!docs/superpowers/' ':!docs/wifi-diva-removal-log.md' ':!CONTRIBUTORS.txt'
  ':!.github/scripts/check-old-name.sh' ':!.github/scripts/old-name-history.txt')

# Allow-list entries, comments and blank lines dropped.
entries=()
while IFS= read -r line || [ -n "$line" ]; do
  case "$line" in ''|'#'*) continue ;; esac
  entries+=("$line")
done < "$history_file"

failed=0

# Content pass. The grep's "no match" exit 1 must not abort the script.
rc=0
matches=$(git grep -n -I -i -E "$pattern" -- "$@" "${excludes[@]}") || rc=$?
if [ "$rc" -ne 0 ] && [ "$rc" -ne 1 ]; then
  exit "$rc"
fi

bad=()
if [ "$rc" -eq 0 ]; then
  while IFS= read -r match; do
    allowed=0
    for entry in "${entries[@]}"; do
      epath=${entry%%|*}
      esub=${entry#*|}
      case "$match" in
        "$epath":*) if [[ "$match" == *"$esub"* ]]; then allowed=1; break; fi ;;
      esac
    done
    if [ "$allowed" -eq 0 ]; then bad+=("$match"); fi
  done <<< "$matches"
fi
if [ "${#bad[@]}" -gt 0 ]; then
  echo "Content pass: the old name DreamGrid must not appear here (history lines go in $history_file):"
  printf '%s\n' "${bad[@]}"
  failed=1
fi

# Path pass: tracked file paths.
paths=$(git ls-files -- "$@" "${excludes[@]}" | grep -i -E "$pattern" || true)
if [ -n "$paths" ]; then
  echo "Path pass: these tracked file names contain the old name DreamGrid:"
  echo "$paths"
  failed=1
fi

# Allow-list rot: every entry must still match a line in its file.
for entry in "${entries[@]}"; do
  epath=${entry%%|*}
  esub=${entry#*|}
  if ! git grep -q -F -e "$esub" -- "$epath"; then
    echo "stale history entry: $entry"
    failed=1
  fi
done

exit "$failed"
