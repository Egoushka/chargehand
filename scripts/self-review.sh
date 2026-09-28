#!/usr/bin/env bash
# Reviews a change of this repository with the review preset and prints the result/v1 JSON; exits like
# `chargehand run` (0 completed, 3 needs_input, 1 otherwise). .github/workflows/self-review.yml runs it on the
# maintainer's self-hosted runner for same-repository pull requests; it builds and runs the checkout's own chargehand.
#
# usage: scripts/self-review.sh <base> <head>   (the diff is base...head: head's changes since the merge base)
# env: CHARGEHAND_PROFILE  profile for the run (required: the presets' placeholder model ids need its models map)
#      CHARGEHAND_RUNTIME  optional, picks the worker runtime when the profile allows more than one
#
# The runner needs: the .NET SDK from global.json, jq, the worker runtime the profile names (a reachable OpenCode
# server, or Claude Code), and its secrets in the profile's secret store. The profile's repository_roots must contain
# the runner's work directory (for example _work/chargehand/chargehand); a profile without repository_roots allows
# worker_root and the directory the script runs in.
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
# Parameter expansion, not a pipe into head: under pipefail a closed pipe would fail the script.
diff=$(git diff "$base...$head")
if [ "${#diff}" -gt 60000 ]; then
  diff="${diff:0:60000}"$'\n[diff truncated at 60000 characters]'
fi
goal=$(git log --format=%B -n1 "$head")

dotnet build -v q -c Release src/Chargehand.Cli >&2
jq -n --arg repo "$PWD" --arg commit "$head" --arg diff "$diff" --arg goal "$goal" '{
  contract_version: "request/v1", text: "Review the change against its goal.",
  context: { interactive: false, preset: "review", repository: { path: $repo, commit: $commit } },
  inputs: [ {id: "goal", kind: "goal", text: $goal}, {id: "diff", kind: "diff", text: $diff},
            {id: "tests", kind: "test-output", text: "CI runs the tests separately."} ] }' |
  dotnet src/Chargehand.Cli/bin/Release/net10.0/Chargehand.Cli.dll run
