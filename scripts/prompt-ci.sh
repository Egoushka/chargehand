#!/bin/sh
# Prompt CI (ADR 0019, ADR 0025): paired evals of a pull request's prompts/ and presets/ against its merge base, then
# the verdict as the commit status "prompt-ci". .github/workflows/prompt-ci.yml runs it on the eval runner; the owner
# can still run it by hand from the main checkout, where the gitignored eval profile and run log live. The runner is
# this checkout's build; the pull request contributes only prompts/ and presets/ (git archive), never code it builds
# or runs. Those files still steer a worker, and a preset can give it tools, so a fork's pull request or a preset
# change waits for --reviewed (in the workflow: an approval in the prompt-ci-review environment).
#
# usage: scripts/prompt-ci.sh <pr-number> [--trusted-build] [--reviewed] [--allow-uncovered] [--no-status]
#   --trusted-build    build the runner (and read evals/cells.json) from the pull request itself; this runs its
#                      code, so only for your own branches
#   --reviewed         you have read the pull request's prompts/ and presets/ diff; needed for a fork's pull request
#                      and for any change under presets/
#   --allow-uncovered  pass although a changed prompt or preset file has no eval cell
#   --no-status        print the verdict without posting the commit status
# env: CHARGEHAND_PROFILE  profile for the eval runs (default profiles/local.eval.json)
#      HEAD                the commit to evaluate (the workflow passes the one its run was approved for; default the
#                          pull request's head now)
set -eu

pr=${1:?pull request number}
shift
trusted='' uncovered='' reviewed='' post=1
for a in "$@"; do
  case $a in
    --trusted-build) trusted=1 ;;
    --allow-uncovered) uncovered=--allow-uncovered ;;
    --reviewed) reviewed=1 ;;
    --no-status) post='' ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done
# Checked before any status is posted: the profile is gitignored, so another checkout or worktree has none.
profile=${CHARGEHAND_PROFILE:-profiles/local.eval.json}
if [ ! -f "$profile" ]; then
  echo "prompt-ci: no eval profile at $profile; run from the main checkout or set CHARGEHAND_PROFILE" >&2
  exit 2
fi

repo=$(gh repo view --json nameWithOwner -q .nameWithOwner)
# Not the head at job start: a push between the approval and this job would otherwise run unreviewed prompts.
head=${HEAD:-$(gh pr view "$pr" --json headRefOid -q .headRefOid)}
git fetch -q origin main "pull/$pr/head"
base=$(git merge-base origin/main "$head")
# Resolved path: macOS's temporary directory sits behind the /var -> /private/var link, and a clean build of the
# runner there fails to resolve its project references.
work=$(cd "$(mktemp -d)" && pwd -P)
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

# A prompt instructs a worker; a preset sets its permissions (a shell allow can run programs, ADR 0006), agent, model
# and budget. Any preset change counts: a grep for permission lines misses quoted YAML keys.
if [ -z "$reviewed" ] && { grep -q '^presets/' "$work/changed" ||
  [ "$(gh pr view "$pr" --json isCrossRepository -q .isCrossRepository)" = true ]; }; then
  status failure "owner review needed: fork or preset change (--reviewed)"
  echo "prompt-ci: failure: a fork's pull request or a preset change; read the prompts/ and presets/ diff, then rerun with --reviewed" >&2
  exit 1
fi

# Each arm's prompts/ and presets/; a symbolic link would read a file outside them.
mkdir -p "$work/base" "$work/change"
git archive "$base" prompts presets | tar -x -C "$work/base"
git archive "$head" prompts presets | tar -x -C "$work/change"
if [ -n "$(find "$work/change" -type l)" ]; then
  status failure "symbolic link in prompts/ or presets/"
  echo "prompt-ci: failure: symbolic link in prompts/ or presets/" >&2
  exit 1
fi

status pending "evals running"
if [ -n "$trusted" ]; then
  git worktree add -q --detach "$work/runner" "$head"
  src="$work/runner"
else
  src=$(git rev-parse --show-toplevel)
fi
dotnet build -v q -c Release "$src/src/Chargehand.Cli" >&2
gh pr view "$pr" --json body -q .body > "$work/body"

set +e
dotnet "$src/src/Chargehand.Cli/bin/Release/net10.0/Chargehand.Cli.dll" --profile "$profile" \
  eval gate "$work/base" "$work/change" --changed-files "$work/changed" --pr-body "$work/body" --cells-file "$src/evals/cells.json" \
  --name "pr-$pr-$(echo "$head" | cut -c1-7)" $uncovered | tee "$work/out"
set -e

verdict=$(grep '^prompt-ci: ' "$work/out" | tail -1)
state=$(echo "$verdict" | cut -d' ' -f2 | tr -d ':')
case $state in success | failure) ;; *) state=error ;; esac
status "$state" "$(echo "$verdict" | cut -d' ' -f3- | cut -c1-140)"
[ "$state" = success ]
