#!/usr/bin/env bash
# Reviews a change of this repository with the review preset and prints the result/v1 JSON; exits like
# `chargehand run` (0 completed, 3 needs_input, 1 otherwise). .github/workflows/self-review.yml runs it on the
# maintainer's self-hosted runner for same-repository pull requests; it builds and runs the checkout's own chargehand.
# The workflow is off until the maintainer sets the repository variable SELF_REVIEW=true, once a run of this script
# on the runner completes.
#
# usage: scripts/self-review.sh <base> <head>   (the diff is base...head: head's changes since the merge base; the request
#        is built by scripts/review-request.sh, which .github/workflows/review.yml uses too)
# env: CHARGEHAND_PROFILE  profile for the run (this script requires one; its models map is optional, as an unmapped
#                          placeholder model runs on the runtime's default model)
#      CHARGEHAND_RUNTIME  optional, names the worker runtime. Order (ADR 0026, ADR 0032): the profile's runtime field,
#                          then this variable, then the profile's only runtime block, then a PATH probe
#
# The runner needs: the .NET SDK from global.json, jq, the worker runtime that order picks (a reachable OpenCode
# server, or Claude Code), and its secrets, which the profile's ordered secrets list resolves (each source in turn,
# first success wins). The profile's repository_roots must contain the runner's work directory (for example
# _work/chargehand/chargehand); a profile without repository_roots allows worker_root and the directory the script
# runs in.
set -euo pipefail

base=${1:?base commit}
head=${2:?head commit}
profile=${CHARGEHAND_PROFILE:-}
if [ -z "$profile" ] || [ ! -f "$profile" ]; then
  echo "self-review: set CHARGEHAND_PROFILE to a profile file (got '${profile}')" >&2
  exit 2
fi

cd "$(git rev-parse --show-toplevel)"
head=$(git rev-parse --verify "$head^{commit}")
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
git diff "$base...$head" > "$work/diff.txt"
git log --format=%B -n1 "$head" > "$work/goal.txt"

dotnet build -v q -c Release src/Chargehand.Cli >&2
scripts/review-request.sh "$PWD" "$head" "$work/goal.txt" "$work/diff.txt" |
  dotnet src/Chargehand.Cli/bin/Release/net10.0/Chargehand.Cli.dll run
