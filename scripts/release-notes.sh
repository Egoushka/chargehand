#!/bin/sh
# Usage: release-notes.sh <version>. Prints the CHANGELOG.md section of <version> without its heading, the notes of
# its GitHub Release. Exits 1 when the section is missing or empty, so a tag without notes never publishes.
set -eu
[ $# -eq 1 ] || { echo "usage: $0 <version>" >&2; exit 2; }
notes=$(awk -v v="$1" '
  index($0, "## [" v "]") == 1 { on = 1; next }
  on && /^## \[/ { exit }
  on { print }
' "$(dirname "$0")/../CHANGELOG.md" | sed -e '/./,$!d')
[ -n "$notes" ] || { echo "release-notes: CHANGELOG.md has no section for $1" >&2; exit 1; }
printf '%s\n' "$notes"
