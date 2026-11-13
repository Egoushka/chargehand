#!/bin/sh
# Usage: sweep-worktrees.sh [--apply] [repo]. Lists (or with --apply removes) agent worktrees under <repo>/.claude/worktrees
# whose work is already in origin/main: HEAD is an ancestor, or every commit has a patch-id match there (a rebase merge).
# A worktree with uncommitted changes is kept and reported. A squash merge matches neither test: remove those by hand.
set -eu
apply=0
if [ "${1:-}" = "--apply" ]; then apply=1; shift; fi
repo=$(cd "${1:-.}" && git rev-parse --show-toplevel)
root="$repo/.claude/worktrees"
base=$(git -C "$repo" rev-parse --verify -q origin/main) || { echo "sweep-worktrees: no origin/main" >&2; exit 1; }
freed=0
for wt in "$root"/*/; do
  [ -e "${wt}.git" ] || continue
  wt=${wt%/}
  head=$(git -C "$wt" rev-parse HEAD)
  if git -C "$repo" merge-base --is-ancestor "$head" "$base" || ! git -C "$repo" cherry "$base" "$head" | grep -q '^+'; then
    if [ -n "$(git -C "$wt" status --porcelain)" ]; then echo "keep (uncommitted changes): $wt"; continue; fi
    kb=$(du -sk "$wt" | cut -f1)
    branch=$(git -C "$wt" symbolic-ref -q --short HEAD || true)
    if [ "$apply" = 1 ]; then
      git -C "$repo" worktree remove "$wt"
      # -d only deletes a branch git sees as merged, so an unmerged rebase-merge branch stays.
      [ -z "$branch" ] || git -C "$repo" branch -d "$branch" >/dev/null 2>&1 || true
      echo "removed: $wt (${kb} KiB)"
    else
      echo "would remove: $wt (${kb} KiB)"
    fi
    freed=$((freed + kb))
  else
    echo "keep (not in origin/main): $wt"
  fi
done
[ "$apply" = 1 ] && git -C "$repo" worktree prune
echo "freed: $((freed / 1024)) MiB"
