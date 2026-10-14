#!/usr/bin/env bash
# Prints the request/v1 that has the review preset review a change: its goal, its diff and a note on the tests.
# scripts/self-review.sh and .github/workflows/review.yml both build their request here.
#
# usage: scripts/review-request.sh <repository-path> <commit> <goal-file> <diff-file>
#   <repository-path> is where the machine that runs the review keeps a checkout holding <commit>. The diff is cut at
#   60000 characters and the goal at 8000.
# env: REVIEW_PRESET  the preset, default review
set -euo pipefail
export LC_ALL=C.UTF-8 # ${#diff} below counts characters, not bytes

repo=${1:?repository path}
commit=${2:?commit}
goal_file=${3:?goal file}
diff_file=${4:?diff file}

# Parameter expansion, not a pipe into head: under pipefail a closed pipe would fail the script.
diff=$(cat "$diff_file")
if [ "${#diff}" -gt 60000 ]; then
  diff="${diff:0:60000}"$'\n[diff truncated at 60000 characters]'
fi
goal=$(cat "$goal_file")
if [ "${#goal}" -gt 8000 ]; then
  goal="${goal:0:8000}"$'\n[goal truncated at 8000 characters]'
fi

# --rawfile: a 60000-character diff can exceed the length limit of one argument.
jq -n --arg repo "$repo" --arg commit "$commit" --arg preset "${REVIEW_PRESET:-review}" \
  --rawfile diff <(printf '%s' "$diff") --rawfile goal <(printf '%s' "$goal") '{
  contract_version: "request/v1", text: "Review the change against its goal.",
  context: { interactive: false, preset: $preset, repository: { path: $repo, commit: $commit } },
  inputs: [ {id: "goal", kind: "goal", text: $goal}, {id: "diff", kind: "diff", text: $diff},
            {id: "tests", kind: "test-output", text: "CI runs the tests separately."} ] }'
