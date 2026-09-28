#!/bin/sh
# Usage: check-private-terms.sh --cached | <file-with-message>. The .private-terms denylist over the whole index (the
# pre-commit hook) or over a commit message (the commit-msg hook). .private-terms is gitignored: one extended regex per
# line, case-insensitive, # for comments.
# git grep and grep exit 1 for no match and above 1 when they cannot search (a malformed pattern). That is not a pass:
# it blocks the commit too.
set -eu

terms="$(git rev-parse --show-toplevel)/.private-terms"
if [ ! -f "$terms" ]; then
  echo "private-terms: .private-terms is missing. Copy .private-terms.example and fill it in." >&2
  exit 1
fi

patterns=$(mktemp)
trap 'rm -f "$patterns"' EXIT
grep -vE '^[[:space:]]*(#|$)' "$terms" > "$patterns" || true
[ -s "$patterns" ] || exit 0

rc=0
if [ "$1" = --cached ]; then
  what="staged content"
  # Whole index, not just this commit's diff: a term that slipped in earlier blocks too.
  hits=$(git grep --cached -n -I -i -E -f "$patterns") || rc=$?
else
  what="the commit message"
  # Not the diff `commit -v` adds below the scissors line (a removed term would block its own removal), and not your
  # own sign-off, which repeats the identity every commit carries anyway.
  msg=$(cat "$1")
  me=$(git var GIT_COMMITTER_IDENT | sed 's/ [0-9]* [-+][0-9]*$//')
  hits=$(printf '%s\n' "$msg" | sed '/^# -\{24\} >8 -\{24\}$/,$d' | grep -vxF "Signed-off-by: $me" |
    grep -n -i -E -f "$patterns") || rc=$?
fi

if [ "$rc" -gt 1 ]; then
  echo "private-terms: could not check $what (exit $rc, reason above). Fix .private-terms." >&2
  exit 1
fi
if [ "$rc" -eq 0 ]; then
  echo "private-terms: found in $what:" >&2
  echo "$hits" >&2
  echo "Remove them (CLAUDE.md, Public vs private). Don't bypass with --no-verify." >&2
  exit 1
fi
