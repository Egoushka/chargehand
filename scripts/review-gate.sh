#!/usr/bin/env bash
# Exits 1 when the review's result should fail the pull request's check.
#
# usage: scripts/review-gate.sh <result.json> <none|high>
#   none  never fails
#   high  fails on a completed review with a finding of confidence 0.8 or more. result/v1 has no severity, and the support
#         check halves the confidence of a partly supported finding, so a finding that survives this test is one the
#         reviewer was sure of and its citation backs.
set -euo pipefail

result=${1:?result file}
fail_on=${2:?none or high}
case $fail_on in
  none) exit 0 ;;
  high) ;;
  *)
    echo "review-gate: fail-on must be none or high, not '$fail_on'" >&2
    exit 2
    ;;
esac

count=$(jq '[select(.status == "completed") | .claims[]? | select(.confidence >= 0.8)] | length' "$result")
if [ "$count" -gt 0 ]; then
  echo "review-gate: $count finding(s) with confidence 0.8 or more" >&2
  exit 1
fi
