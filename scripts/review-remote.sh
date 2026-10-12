#!/usr/bin/env bash
# Runs a request/v1 on a chargehand server over HTTP and prints the run's result/v1. It posts to /v1/runs, then polls
# /v1/runs/{id} until the run finishes or the deadline passes (docs/guide/server.md). .github/workflows/review.yml runs it.
# When there is no result from the server (it is unreachable, refuses the key, loses the run or takes too long), it prints a
# failed result/v1 of its own with the error code that fits, so the caller always has a result to render.
#
# usage: scripts/review-remote.sh <request.json>   exits like `chargehand run`: 0 completed, 3 needs_input, 1 otherwise
# env: CHARGEHAND_URL            the server's base URL
#      CHARGEHAND_API_KEY        its bearer key
#      REVIEW_TIMEOUT_SECONDS    how long to wait for the result, default 1200
#      REVIEW_WAIT_SECONDS       how long the first call waits for a quick run to finish, default 50 (the server allows 60)
#      REVIEW_POLL_SECONDS       between polls, default 10
set -euo pipefail

request=${1:?request file}
url=${CHARGEHAND_URL:?set CHARGEHAND_URL to the server URL}
url=${url%/}
: "${CHARGEHAND_API_KEY:?set CHARGEHAND_API_KEY to the server key}"
timeout=${REVIEW_TIMEOUT_SECONDS:-1200}
poll=${REVIEW_POLL_SECONDS:-10}
first_wait=${REVIEW_WAIT_SECONDS:-50}

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
# The key reaches curl in a file: an argument would show in the process list.
(umask 077 && printf 'Authorization: Bearer %s\n' "$CHARGEHAND_API_KEY" > "$work/auth")

# A failed result/v1 that did not come from the server. The message is public text (a pull request comment), so it never
# carries the server's address or what curl said; curl's own message goes to stderr.
fail() { # <code> <retryable> <message> <action> [<run id>]
  jq -n --arg code "$1" --argjson retryable "$2" --arg message "$3" --arg action "$4" --arg id "${5:-none}" '{
    contract_version: "result/v1", task_id: $id, node_id: "client", trace_id: "00000000000000000000000000000000",
    prompt_chain: {blocks: [], as_sent: {opencode_version: "", agent: "", model: "", date: "1970-01-01"}},
    status: "failed", summary: $message, claims: [], evidence: [], artifacts: [], open_questions: [], confidence: 0,
    usage: {input: 0, output: 0, cache_read: 0, cache_write: 0, usd: null},
    error: {code: $code, message: $message, retryable: $retryable, action: $action} }'
  exit 1
}

# Prints the HTTP status (000: no answer); the body is in $work/body.
call() {
  local code
  code=$(curl -sS -o "$work/body" -w '%{http_code}' -H "@$work/auth" "$@" 2> "$work/curl.err") || {
    cat "$work/curl.err" >&2
    code=000
  }
  echo "$code"
}

emit() { # the body is a result/v1
  jq -e '.contract_version == "result/v1"' "$work/body" > /dev/null ||
    fail internal false "The server answered with a body that is not a result/v1." "Check the server and its version." "${run_id:-none}"
  cat "$work/body"
  case $(jq -r .status "$work/body") in completed) exit 0 ;; needs_input) exit 3 ;; *) exit 1 ;; esac
}

unreachable() { fail runtime_unavailable true "The review server could not be reached." "Check the server-url input and that the runner can reach the server." "${run_id:-none}"; }

# Prefer: wait lets a quick run answer in this call.
status=$(call --max-time $((first_wait + 40)) -X POST -H 'Content-Type: application/json' -H "Prefer: wait=$first_wait" --data-binary "@$request" "$url/v1/runs")
case $status in
  200) emit ;;
  202) run_id=$(jq -r '.run_id // empty' "$work/body") ;;
  000) unreachable ;;
  401) fail internal false "The review server refused the key (401)." "Check the chargehand-api-key secret." ;;
  400) fail invalid_request false "The review server refused the request: $(jq -r '(.errors // [.]) | map(tostring) | join("; ") | .[0:300]' "$work/body" 2> /dev/null || true)" "Check the preset and repository-path inputs." ;;
  429) fail rate_limited true "The review server has too many unfinished runs." "Retry later." ;;
  *) fail internal false "The review server answered $status." "Check the server." ;;
esac
[ -n "$run_id" ] || fail internal false "The review server accepted the request without a run id." "Check the server and its version."

deadline=$(($(date +%s) + timeout))
errors=0
while :; do
  [ "$(date +%s)" -lt "$deadline" ] || fail deadline_exceeded true "The review did not finish within ${timeout} seconds; the server may still be running it (run $run_id)." "Raise timeout-minutes, or retry." "$run_id"
  sleep "$poll"
  status=$(call --max-time 30 "$url/v1/runs/$run_id")
  case $status in
    200) emit ;;
    202) errors=0 ;;
    410) fail internal true "The review server restarted before the run finished." "Retry." "$run_id" ;;
    404) fail internal false "The review server does not know run $run_id." "Retry." "$run_id" ;;
    401) fail internal false "The review server refused the key (401)." "Check the chargehand-api-key secret." "$run_id" ;;
    *)
      errors=$((errors + 1))
      [ "$errors" -lt 5 ] || unreachable
      ;;
  esac
done
