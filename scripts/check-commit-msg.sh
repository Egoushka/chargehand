#!/bin/sh
# Usage: check-commit-msg.sh <file-with-message>. Checks the first line only.
set -eu
first=$(head -n1 "$1")
case "$first" in Merge\ *|Revert\ *|fixup!\ *|squash!\ *) exit 0 ;; esac
if ! printf '%s\n' "$first" | grep -qE '^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([a-z0-9._/-]+\))?!?: .{1,100}$'; then
  echo "commit-msg: not a Conventional Commit: '$first'" >&2
  echo "Expected: type(scope)?: subject, type one of feat fix docs style refactor perf test build ci chore revert." >&2
  exit 1
fi
