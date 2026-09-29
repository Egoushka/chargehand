#!/usr/bin/env bash
# End-to-end check of the code preset (goal 0.7, ADR 0035). Needs a signed-in `claude` (or CHARGEHAND_RUNTIME and a profile
# for another runtime), the .NET SDK, python3 and a platform sandbox (sandbox-exec on macOS, bwrap on Linux); the green
# case calls a real model, so this runs by hand or on the maintainer's runner, never on every push.
# env: CHARGEHAND_RUNTIME  worker runtime (default claude_code: the signed-in claude, no profile needed)
#      CHARGEHAND_PROFILE  profile for the runs; unset, a throwaway one that maps the presets' placeholder models to
#                          Claude Code's sonnet and haiku
# Each case prints "ok   <case>" or "FAIL <case>  (log: <file>)"; exit 1 when any case fails.
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
export CHARGEHAND_RUNTIME=${CHARGEHAND_RUNTIME:-claude_code}
work=$(mktemp -d "${TMPDIR:-/tmp}/write-e2e.XXXXXX")
profile() { # worker root, extra profile members, e.g. '"sandbox": {"kind": "bubblewrap"}'
  cat > "$work/profile.json" <<JSON
{ "schema": "profile/v1", "worker_root": "$1",
  "models": { "provider/worker-model": "anthropic/sonnet", "provider/small-model": "anthropic/haiku" }${2:+, $2} }
JSON
}
dotnet build "$here/src/Chargehand.Cli" -v q -nologo >/dev/null
# The dll, not the apphost: the apphost looks for the runtime in the system dotnet directory only.
cli="dotnet $here/src/Chargehand.Cli/bin/Debug/net10.0/Chargehand.Cli.dll"
fail=0

sample() {
  local d; d=$(mktemp -d "$work/sample.XXXXXX")
  git -C "$d" init -q -b main
  printf 'def greet(name):\n    return "hello " + name\n' > "$d/greet.py"
  printf 'import unittest\nfrom greet import greet\n\n\nclass GreetTest(unittest.TestCase):\n    def test_greet(self):\n        self.assertEqual(greet("ada"), "Hello Ada")\n' > "$d/test_greet.py"
  git -C "$d" add . && git -C "$d" -c user.name=e2e -c user.email=e2e@example.com commit -q -m init
  echo "$d"
}
request() { # dir, goal
  python3 - "$1" "$(git -C "$1" rev-parse HEAD)" "$2" <<'PY'
import json, sys
path, commit, goal = sys.argv[1:4]
print(json.dumps({"contract_version": "request/v1", "text": goal,
  "context": {"interactive": False, "preset": "code", "repository": {"path": path, "commit": commit},
              "verify": ["python3", "-m", "unittest", "-q"], "budget_usd": 1.0}}))
PY
}
run() { # dir, goal, log
  (cd "$1" && request "$1" "$2" | $cli --profile "$work/profile.json" run > "$3" 2> "$3.err") || true
}
field() { python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(eval(sys.argv[2]))" "$1" "$2" 2>/dev/null || echo ""; }
check() { # case, log, "pass" when the case held
  if [ "$3" = pass ]; then echo "ok   $1"; else echo "FAIL $1  (log: $2)"; fail=1; fi
}

# green: a failing test, a change that fixes it, checked in the sandbox, on a branch the source does not have.
profile "$work/root-green"
d=$(sample); log="$work/green.json"
run "$d" "make greet() return 'Hello <Name>' with the name capitalised, so the failing test passes" "$log"
branch=$(field "$log" "[json.loads(a['content'])['repository']+' '+json.loads(a['content'])['branch'] for a in d['artifacts'] if a['kind']=='branch'][0]")
ok=""
if [ "$(field "$log" "d['status']")" = completed ] && [ -n "$branch" ]; then
  read -r clone name <<<"$branch"
  git -C "$d" fetch -q "$clone" "$name" && git -C "$d" -c advice.detachedHead=false checkout -q FETCH_HEAD &&
    (cd "$d" && python3 -m unittest -q >/dev/null 2>&1) &&
    [ "$(field "$log" "[json.loads(a['content'])['passed'] for a in d['artifacts'] if a['kind']=='verification'][0]")" = True ] && ok=pass
fi
check green "$log" "$ok"

# refused: a sandbox kind this machine cannot have stops the run before intake, with an action.
case $(uname) in Darwin) missing=bubblewrap ;; *) missing=seatbelt ;; esac
profile "$work/root-refused" "\"sandbox\": {\"kind\": \"$missing\"}"
d=$(sample); log="$work/refused.json"
run "$d" "make greet() capitalise the name" "$log"
check refused "$log" "$([ "$(field "$log" "d['error']['code']")" = sandbox_unavailable ] && [ -n "$(field "$log" "d['error']['action']")" ] &&
  [ ! -d "$work/root-refused/.runs" ] && echo pass)"

exit $fail
