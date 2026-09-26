#!/bin/sh
# Usage: check-commits.sh <base> <head>. Runs check-commit-msg.sh on every commit in base..head.
set -eu
dir=$(dirname "$0")
msg=$(mktemp); trap 'rm -f "$msg"' EXIT
for c in $(git rev-list --no-merges "$1..$2"); do
  git log -1 --format=%B "$c" > "$msg"
  "$dir/check-commit-msg.sh" "$msg"
done
