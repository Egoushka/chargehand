#!/bin/sh
# Prompt CI (ADR 0019), run by the owner on the laptop: paired evals of a pull request's prompts/ and presets/
# against its merge base, then the verdict as the commit status "prompt-ci". The runner is this checkout's build;
# the pull request contributes data only (git archive of prompts/ and presets/), never code. Run it from the main
# checkout, where the gitignored eval profile and run log live.
#
# usage: scripts/prompt-ci.sh <pr-number> [--trusted-build] [--allow-uncovered] [--allow-permissions] [--no-status]
#   --trusted-build      build the runner (and read evals/cells.json) from the pull request itself; this runs its
#                        code, so only for your own branches
#   --allow-uncovered    pass although a changed prompt or preset file has no eval cell
#   --allow-permissions  run although the pull request changes a preset's permission rules (review them first)
#   --no-status          print the verdict without posting the commit status
# env: CHARGEHAND_PROFILE  profile for the eval runs (default profiles/local.eval.json)
set -eu

pr=${1:?pull request number}
shift
trusted='' uncovered='' permissions='' post=1
for a in "$@"; do
  case $a in
    --trusted-build) trusted=1 ;;
    --allow-uncovered) uncovered=--allow-uncovered ;;
    --allow-permissions) permissions=1 ;;
    --no-status) post='' ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done

repo=$(gh repo view --json nameWithOwner -q .nameWithOwner)
head=$(gh pr view "$pr" --json headRefOid -q .headRefOid)
git fetch -q origin main "pull/$pr/head"
base=$(git merge-base origin/main "$head")
work=$(mktemp -d)
trap 'git worktree remove --force "$work/runner" 2>/dev/null || true; rm -rf "$work"' EXIT

status() { # <state> <description>
  if [ -n "$post" ]; then
    gh api -X POST "repos/$repo/statuses/$head" -f state="$1" -f context=prompt-ci -f description="$2" >/dev/null
  fi
}

git diff --name-only "$base" "$head" > "$work/changed"
if ! grep -qE '^(prompts|presets)/' "$work/changed"; then
  status success "no prompt or preset change"
  echo "prompt-ci: success: no prompt or preset change"
  exit 0
fi

# Data only: each arm's prompts/ and presets/.
mkdir -p "$work/base" "$work/change"
git archive "$base" prompts presets | tar -x -C "$work/base"
git archive "$head" prompts presets | tar -x -C "$work/change"

# A preset's permission rules decide what a worker may run on this machine; changing them needs review first.
rules() { cat "$1"/presets/*.yaml 2>/dev/null | grep -E 'action:|resource:|effect:|permissions' | sort; }
if [ -z "$permissions" ] && [ "$(rules "$work/base")" != "$(rules "$work/change")" ]; then
  status failure "preset permission rules changed: owner review needed (--allow-permissions)"
  echo "prompt-ci: failure: preset permission rules changed; review them, then rerun with --allow-permissions" >&2
  exit 1
fi

status pending "evals running on the owner's machine"
if [ -n "$trusted" ]; then
  git worktree add -q --detach "$work/runner" "$head"
  src="$work/runner"
else
  src=$(git rev-parse --show-toplevel)
fi
dotnet build -v q -c Release "$src/src/Chargehand.Cli" >/dev/null
gh pr view "$pr" --json body -q .body > "$work/body"

set +e
dotnet "$src/src/Chargehand.Cli/bin/Release/net10.0/Chargehand.Cli.dll" --profile "${CHARGEHAND_PROFILE:-profiles/local.eval.json}" \
  eval gate "$work/base" "$work/change" --changed-files "$work/changed" --pr-body "$work/body" --cells-file "$src/evals/cells.json" \
  --name "pr-$pr-$(echo "$head" | cut -c1-7)" $uncovered | tee "$work/out"
set -e

verdict=$(grep '^prompt-ci: ' "$work/out" | tail -1)
state=$(echo "$verdict" | cut -d' ' -f2 | tr -d ':')
case $state in success | failure) ;; *) state=error ;; esac
status "$state" "$(echo "$verdict" | cut -d' ' -f3- | cut -c1-140)"
[ "$state" = success ]
