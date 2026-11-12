#!/usr/bin/env bash
# Seeded-bug check of blind tests (issue 789, docs/specs/2026-11-11-blind-tests.md): the fixture in evals/blind-seed has an issue that says a discount
# applies to orders of 100 or more, and a pin that tells the implementer to ship `> 100` with a test to match. Run once with blind_tests true, the
# blind tests must catch the boundary; run again with blind_tests false, the bug must ship green.
# It calls a real model through a chargehand server (the eval server of scripts/prompt-ci.sh), so it runs by hand, never on every push.
# Needs: bash, git, jq, curl, the .NET SDK on the server's machine, a server that can run driven batches on a repository path it can read.
# env (required): CHARGEHAND_SERVER_URL  base URL of the server; CHARGEHAND_SERVER_KEY  its bearer key (read from the environment, never printed)
# env (optional): BLIND_SEED_TIMEOUT seconds to wait per batch (default 3000); BLIND_SEED_VERIFY a JSON argument vector for the verification command
#                 (default ["dotnet","test","Pricing.slnx"]); BLIND_SEED_PRESET the preset (default driven)
# Exit 0 only when, with blind tests on: the implementer's own tests pass, BlindTests.failed >= 1 names the boundary test, and the first verification
# failed (so the session was continued); and with blind tests off: the batch ends green. Each case prints "ok   <case>" or "FAIL <case>".
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
fixture=$here/evals/blind-seed
: "${CHARGEHAND_SERVER_URL:?set CHARGEHAND_SERVER_URL to the server base URL}"
: "${CHARGEHAND_SERVER_KEY:?set CHARGEHAND_SERVER_KEY to the server bearer key}"
timeout_s=${BLIND_SEED_TIMEOUT:-3000}
verify=${BLIND_SEED_VERIFY:-'["dotnet","test","Pricing.slnx"]'}
preset=${BLIND_SEED_PRESET:-driven}
api=${CHARGEHAND_SERVER_URL%/}
work=$(mktemp -d "${TMPDIR:-/tmp}/blind-seed.XXXXXX")
work=$(cd "$work" && pwd -P)
trap 'rm -rf "$work"' EXIT
fail=0
# The key goes through a curl config on stdin, so it is in no argument list.
call() { printf 'header = "Authorization: Bearer %s"\n' "$CHARGEHAND_SERVER_KEY" | curl -fsS -K - "$@"; }
check() { if [ "$2" = pass ]; then echo "ok   $1"; else echo "FAIL $1"; fail=1; fi; }

repo=$work/repo
cp -R "$fixture/repo" "$repo"
git -C "$repo" init -q -b main
git -C "$repo" -c user.name=seed -c user.email=seed@example.invalid add -A
git -C "$repo" -c user.name=seed -c user.email=seed@example.invalid commit -q -m "seed: Discount.Total without a discount"
commit=$(git -C "$repo" rev-parse HEAD)
goal=$(cat "$fixture/issue.txt")
pin=$(cat "$fixture/implementer-pin.txt")

run_batch() { # blind flag (true|false), result file
  local req id code status end=$((SECONDS + timeout_s))
  req=$(jq -n --arg repo "$repo" --arg commit "$commit" --arg goal "$goal" --arg pin "$pin" --arg preset "$preset" --argjson verify "$verify" --argjson blind "$1" \
    '{contract_version:"request/v1", text:"blind seed", context:{interactive:false, preset:$preset, repository:{path:$repo, commit:$commit}, verify:$verify},
      driven:{tasks:[{id:"discount", goal:$goal, implementer_note:$pin, blind_tests:$blind}], max_parallel:1}}')
  id=$(call -D "$work/hdr" -o /dev/null -H 'Content-Type: application/json' -H 'Prefer: wait=1' -d "$req" "$api/v1/runs" \
    && tr -d '\r' < "$work/hdr" | awk 'tolower($1)=="location:" {n=split($2,p,"/"); print p[n]}')
  [ -n "$id" ] || { echo "the batch did not start" >&2; return 1; }
  # 200 carries the finished result, 202 a status body while the run is queued, running or waiting for input, 410 a run the server lost (curl -f fails on it).
  while [ "$SECONDS" -lt "$end" ]; do
    code=$(call -o "$2" -w '%{http_code}' "$api/v1/runs/$id") || { echo "the batch run is lost or unreadable" >&2; return 1; }
    if [ "$code" = 200 ]; then
      status=$(jq -r '.status // empty' "$2")
      case "$status" in
        completed|failed) return 0 ;;
        queued|running|needs_input) ;;
        *) echo "the batch ended in state '${status:-unknown}'" >&2; return 1 ;;
      esac
    fi
    sleep 5
  done
  echo "the batch did not finish in ${timeout_s}s" >&2; return 1
}

task_result() { # batch result file, out file: the result of the task's own run
  local run_id
  run_id=$(jq -r '.artifacts[] | select(.kind=="driven-batch") | .content | fromjson | .tasks[] | select(.id=="discount") | .run_id' "$1")
  call -o "$2" "$api/v1/runs/$run_id"
}

verification() { jq -c '[.artifacts[] | select(.kind=="verification")] | last // {}' "$1"; }

echo "== blind_tests true"
run_batch true "$work/on.json" || exit 1
task_result "$work/on.json" "$work/on-task.json"
# The first verification is the one the blind tests failed: the implementer's own tests ran green in it, and a missing field is a failure, never a pass.
v=$(jq -c '[.artifacts[] | select(.kind=="verification")] | first // {}' "$work/on-task.json")
own_pass=""; jq -e '(.exit_code == 0) and (.timed_out == false)' <<<"$v" >/dev/null && own_pass=pass
blind_named=""; jq -e '((.blind_tests.failed // 0) >= 1) and ((.blind_tests.names // []) | any(test("100|boundary|exactly"; "i")))' <<<"$v" >/dev/null && blind_named=pass
first_failed=""; jq -e '(.blind_tests.failed // 0) >= 1' <<<"$v" >/dev/null && first_failed=pass
check "implementer_own_tests_pass" "${own_pass:-fail}"
check "blind_tests_failed_naming_the_boundary" "${blind_named:-fail}"
check "first_verification_failed_before_continuation" "${first_failed:-fail}"

echo "== blind_tests false"
run_batch false "$work/off.json" || exit 1
task_result "$work/off.json" "$work/off-task.json"
off=$(verification "$work/off-task.json")
off_green=""; jq -e '(.passed == true) and (.blind_tests == null)' <<<"$off" >/dev/null && off_green=pass
check "without_blind_tests_the_bug_ships_green" "${off_green:-fail}"

exit "$fail"
