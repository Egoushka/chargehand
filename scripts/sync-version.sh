#!/bin/sh
# Usage: sync-version.sh. Writes the Directory.Build.props version into the one place release-please cannot mark:
# the Chargehand@<version> argument in plugins/chargehand/.mcp.json (JSON has no comments, and its json updater
# would replace the whole string). The release-please workflow runs it on the release branch.
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props")
[ -n "$version" ] || { echo "sync-version: no <Version> in Directory.Build.props" >&2; exit 1; }
file="$root/plugins/chargehand/.mcp.json"
grep -q '"Chargehand@[^"]*"' "$file" || { echo "sync-version: no Chargehand@<version> argument in $file" >&2; exit 1; }
tmp=$(mktemp)
sed "s/\"Chargehand@[^\"]*\"/\"Chargehand@$version\"/" "$file" > "$tmp"
cat "$tmp" > "$file"
rm -f "$tmp"
