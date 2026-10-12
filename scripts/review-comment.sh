#!/usr/bin/env bash
# Prints the pull request comment for a review's result/v1: each finding with its support verdict and its citations,
# linked to the cited lines at the reviewed commit, then the run id and its cost. The first line is a hidden marker, which
# scripts/review-post.sh uses to find the comment again and edit it. .github/workflows/review.yml and self-review.yml use it.
#
# usage: scripts/review-comment.sh <result.json> <owner/repo> <head-sha>
# env: REVIEW_MAX_FINDINGS  findings listed, highest confidence first, default 20
#      GITHUB_SERVER_URL    default https://github.com
#
# Everything in the comment comes from the result file and these arguments, never from shell interpolation. Model text is
# neutralised first: an @ gets a zero-width space so a written @name pings no one, < becomes &lt; so it cannot open HTML
# or a comment, and line breaks become spaces so text cannot start a new block.
set -euo pipefail

result=${1:?result file}
repo=${2:?owner/repo}
head=${3:?head sha}
max=${REVIEW_MAX_FINDINGS:-20}
case $max in '' | *[!0-9]*) echo "review-comment: REVIEW_MAX_FINDINGS must be a whole number, not '$max'" >&2; exit 2 ;; esac

full=$(mktemp)
trap 'rm -f "$full"' EXIT
jq -r --arg repo "$repo" --arg head "$head" --arg server "${GITHUB_SERVER_URL:-https://github.com}" --argjson max "$max" '
  def q: gsub("@"; "@​") | gsub("<"; "&lt;") | gsub("\r?\n|\r"; " ");
  def cut($n): if length > $n then .[0:$n] + "..." else . end;
  def code: gsub("`"; "'"'"'") | gsub("\r?\n|\r"; " ") | cut(120) | "`\(.)`";
  def cite($e; $blob):
    ([$e.locator | capture("^(?<p>[A-Za-z0-9._/+-]+)(?::(?<a>[0-9]+)(?:-(?<b>[0-9]+))?)?$")] | .[0]) as $m
    | if ($e.kind == "file" or $e.kind == "diff") and $m != null
         and ($m.p | startswith("/") | not) and ($m.p | test("(^|/)\\.\\.(/|$)") | not)
      then "[\($e.locator | code)](\($blob)/\($m.p)\(if $m.a then "#L\($m.a)\(if $m.b then "-L\($m.b)" else "" end)" else "" end))"
      else "\($e.kind) \($e.locator)" | code end;
  "\($server)/\($repo)/blob/\($head)" as $blob
  | (.evidence | map({(.id): .}) | add // {}) as $ev
  | (.claims | length) as $n
  | (.claims | sort_by(-.confidence)) as $claims
  | "<!-- chargehand-review -->\n### chargehand review\n\n"
  + (if .status == "failed" then
       "The review failed (`\(.error.code // "unknown")`): \((.error.message // .summary) | q | cut(1000))"
       + (if .error.action then "\n\nAction: \(.error.action | q | cut(1000))" else "" end)
     elif .status == "needs_input" or .status == "denied" then
       "The review stopped with status `\(.status)`: \(.summary | q | cut(1000))"
     elif $n == 0 then
       (.summary | q | cut(1000)) + "\n\nNo findings."
     else
       (.summary | q | cut(1000)) + "\n\n"
       + (if $n > $max then "Showing \($max) of \($n) findings, highest confidence first.\n\n" else "" end)
       + ([$claims[0:$max][] | "- " + (.text | q | cut(1500)) + "\n  Support: `\(.support // "not checked")`, confidence \(.confidence).\n  Evidence: "
           + ([.evidence[] | cite($ev[.] // {kind: "unresolved", locator: .}; $blob)] | join(", "))]
          | join("\n"))
     end)
  + (if (.open_questions | length) > 0 and .status == "completed" then
       "\n\nOpen questions:\n" + ([.open_questions[0:5][] | "- " + (q | cut(500))] | join("\n"))
     else "" end)
  + "\n\nRun `\(.task_id)`"
  + (if .usage.usd != null then ", $\(.usage.usd * 10000 | round / 10000)" else "" end)
  + (if .status == "completed" then "; citations checked against `\($head[0:12])`." else "." end)' "$result" > "$full"
# GitHub caps a comment at 65536 characters; a file, not a pipe, since the default shell sets pipefail.
head -c 60000 "$full"
