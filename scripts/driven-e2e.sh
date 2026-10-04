#!/usr/bin/env bash
# End-to-end check of driven sessions (ADR 0039, goal "driven"): a batch of three tasks on a synthetic repository, one container each, draft
# pull requests for the two that can pass, and a cancel case. It starts its own `chargehand serve` with a throwaway profile and a stub for the
# GitHub API, and it calls a real model, so it runs by hand on a machine with Docker, never on every push.
# Needs: Docker, the .NET SDK, python3, git, curl, and a `claude` on PATH whose version is the one inside the session image.
# env (required):
#   CHARGEHAND_E2E_MODEL_KEY  an Anthropic API key with a low spend limit set in the console. It is read here, passed to the server through the
#                             environment and never printed or written to a file; the script fails if it shows up in any file it made.
#   CHARGEHAND_E2E_IMAGE      the session image by digest, name@sha256:<64 hex> (build images/session/Dockerfile and push it to a registry,
#                             so the digest exists; a local image id is not a digest).
# env (optional):
#   CHARGEHAND_E2E_MAX_USD    cap for the batch in dollars (default 5); the cancel case gets a fifth of it
#   CHARGEHAND_E2E_PORT       port of the throwaway server (default 4390; 4300 belongs to the always-on agent)
#   CHARGEHAND_E2E_TIMEOUT    seconds to wait for a batch (default 3000)
#   CHARGEHAND_E2E_STREAMS    a directory: each task's stored session stream is copied there as <task>.jsonl (secrets redacted), and
#                             `chargehand runs adherence` is run over them (the measurement of the plan's Task 12 step 2 needs these)
#   CHARGEHAND_E2E_KEEP=1     keep the work directory (it holds the run log, the stub's records and the server's log)
# The push credential is a random token this script makes: the remote is a local bare repository and the GitHub stub only checks that the token
# arrived. Two switches in the CLI make that possible and exist for this script: CHARGEHAND_E2E_GITHUB_API (the draft-pull-request client's base
# URL) and CHARGEHAND_E2E_LOCAL_REMOTE=1 (a file:// remote is accepted). Neither is in a profile; neither is set in a real deployment.
# Each case prints "ok   <case>" or "FAIL <case>  (<where to look>)"; exit 1 when any case fails.
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
: "${CHARGEHAND_E2E_MODEL_KEY:?set CHARGEHAND_E2E_MODEL_KEY to a capped Anthropic API key}"
: "${CHARGEHAND_E2E_IMAGE:?set CHARGEHAND_E2E_IMAGE to the session image, name@sha256:<digest>}"
case $CHARGEHAND_E2E_IMAGE in *@sha256:????????????????????????????????????????????????????????????????) ;; *) echo "CHARGEHAND_E2E_IMAGE must be name@sha256:<64 hex>" >&2; exit 2 ;; esac
max_usd=${CHARGEHAND_E2E_MAX_USD:-5}
port=${CHARGEHAND_E2E_PORT:-4390}
timeout_s=${CHARGEHAND_E2E_TIMEOUT:-3000}
claude_version=$(claude --version | awk '{print $1}')
work=$(mktemp -d "${TMPDIR:-/tmp}/driven-e2e.XXXXXX")
server_pid=""; stub_pid=""
fail=0

# shellcheck disable=SC2329  # runs from the EXIT trap
cleanup() {
  [ -n "$server_pid" ] && kill "$server_pid" 2>/dev/null || true
  [ -n "$stub_pid" ] && kill "$stub_pid" 2>/dev/null || true
  # Whatever a failed case left behind: containers, volumes and batch networks carry chargehand's label.
  dotnet "$here/src/Chargehand.Cli/bin/Debug/net10.0/Chargehand.Cli.dll" runs kill --all >/dev/null 2>&1 || true
  # The output volumes of this script's runs (the stream is kept in one); other volumes that were there before are left alone.
  docker volume ls -q --filter label=chargehand.run 2>/dev/null | sort | comm -13 "$work.volumes" - 2>/dev/null | xargs docker volume rm >/dev/null 2>&1 || true
  rm -f "$work.volumes"
  if [ "${CHARGEHAND_E2E_KEEP:-}" = 1 ]; then echo "kept $work"; else rm -rf "$work"; fi
}
trap cleanup EXIT
docker volume ls -q --filter label=chargehand.run | sort > "$work.volumes"

dotnet build "$here/src/Chargehand.Cli" -v q -nologo >/dev/null
cli="dotnet $here/src/Chargehand.Cli/bin/Debug/net10.0/Chargehand.Cli.dll"

# The push credential and the server's key are made here; the model key stays the caller's. Profile secrets are read from these variables.
export CHARGEHAND_E2E_PUSH_KEY; CHARGEHAND_E2E_PUSH_KEY="canary-$(openssl rand -hex 16)"
export CHARGEHAND_E2E_SERVER_KEY; CHARGEHAND_E2E_SERVER_KEY=$(openssl rand -hex 16)

# The repository: three tasks' worth of code, tests green at the base (one test is skipped on purpose). Once main is seeded, the bare remote
# takes only branches under chargehand/, so a push to the default branch is refused by the remote itself, not by chargehand's good manners.
git init -q --bare -b main "$work/remote.git"
repo="$work/repo"
git init -q -b main "$repo"
git -C "$repo" config user.name e2e && git -C "$repo" config user.email e2e@example.com
printf 'def greet(name):\n    return "Hello " + name.capitalize()\n' > "$repo/greet.py"
printf 'def add(a, b):\n    return a + b\n' > "$repo/mathx.py"
printf 'def slugify(text):\n    raise NotImplementedError\n' > "$repo/text.py"
cat > "$repo/test_all.py" <<'PY'
import unittest
from greet import greet
from mathx import add
from text import slugify


class AllTest(unittest.TestCase):
    def test_greet(self):
        self.assertEqual(greet("ada"), "Hello Ada")

    def test_add(self):
        self.assertEqual(add(2, 2), 4)

    @unittest.skip("slugify is not written yet")
    def test_slugify(self):
        self.assertEqual(slugify("Hello, World!"), "hello-world")
PY
git -C "$repo" add . && git -C "$repo" commit -q -m init
git -C "$repo" remote add origin "file://$work/remote.git"
git -C "$repo" push -q origin main
git -C "$repo" fetch -q origin && git -C "$repo" remote set-head origin main >/dev/null
cat > "$work/remote.git/hooks/pre-receive" <<'HOOK'
#!/bin/sh
while read -r _old _new ref; do
  case $ref in refs/heads/chargehand/*) ;; *) echo "e2e remote: only chargehand/* may be pushed" >&2; exit 1 ;; esac
done
HOOK
chmod +x "$work/remote.git/hooks/pre-receive"
base=$(git -C "$repo" rev-parse HEAD)
main_before=$(git -C "$work/remote.git" rev-parse main)

# A stub of POST /repos/{owner}/{repo}/pulls: it records what the client sent (never the token, only whether the right one arrived).
cat > "$work/stub.py" <<'PY'
import http.server, json, os, sys
out, token = sys.argv[1], os.environ["CHARGEHAND_E2E_PUSH_KEY"]
class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
        with open(out + "/prs.jsonl", "a") as f:
            f.write(json.dumps({"path": self.path, "draft": body.get("draft"), "head": body.get("head"), "base": body.get("base"),
                                "token_ok": self.headers.get("Authorization") == "Bearer " + token}) + "\n")
        n = sum(1 for _ in open(out + "/prs.jsonl"))
        data = json.dumps({"html_url": "https://example.test/pull/%d" % n, "number": n}).encode()
        self.send_response(201); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(data))); self.end_headers(); self.wfile.write(data)
s = http.server.HTTPServer(("127.0.0.1", 0), H)
open(out + "/stub.port", "w").write(str(s.server_port))
s.serve_forever()
PY
: > "$work/prs.jsonl"
python3 "$work/stub.py" "$work" & stub_pid=$!
for _ in $(seq 50); do [ -s "$work/stub.port" ] && break; sleep 0.1; done
[ -s "$work/stub.port" ] || { echo "the GitHub stub did not start" >&2; exit 1; }

# The profile: the model key and the push token are item names, never values; secrets come from the environment.
export CHARGEHAND_E2E_MODEL_KEY
python3 - "$work" "$port" "$claude_version" "$CHARGEHAND_E2E_IMAGE" <<'PY'
import json, sys
work, port, version, image = sys.argv[1:5]
profile = {"schema": "profile/v1", "runtime": "claude_code", "secrets": [{"env": True}],
  "claude_code": {"version": version, "api_key_secret": "chargehand-e2e-model-key"},
  "models": {"provider/worker-model": "anthropic/sonnet", "provider/small-model": "anthropic/haiku"},
  "worker_root": work + "/worker", "repository_roots": [work], "run_log": work + "/run-log.jsonl",
  "http": {"port": int(port), "api_key_secret": "chargehand-e2e-server-key"},
  "driven": {"enabled": True, "max_parallel": 2, "images": [image], "push_secret": "chargehand-e2e-push-key"}}
json.dump(profile, open(work + "/profile.json", "w"), indent=2)
PY
CHARGEHAND_E2E_GITHUB_API="http://127.0.0.1:$(cat "$work/stub.port")/" CHARGEHAND_E2E_LOCAL_REMOTE=1 \
  $cli --profile "$work/profile.json" serve > "$work/server.log" 2>&1 & server_pid=$!
api="http://127.0.0.1:$port"
auth=(-H "Authorization: Bearer $CHARGEHAND_E2E_SERVER_KEY")
for _ in $(seq 100); do curl -fs "${auth[@]}" "$api/v1/runs" >/dev/null 2>&1 && break; sleep 0.3; done
curl -fs "${auth[@]}" "$api/v1/runs" >/dev/null || { echo "the server did not start (log: $work/server.log)" >&2; exit 1; }

request() { # tasks as JSON, max_usd_total
  python3 - "$repo" "$base" "$1" "$2" <<'PY'
import json, sys
repo, commit, tasks, usd = sys.argv[1:5]
print(json.dumps({"contract_version": "request/v1", "text": "driven e2e",
  "context": {"interactive": False, "preset": "driven", "repository": {"path": repo, "commit": commit}, "verify": ["python3", "-m", "unittest", "-q"]},
  "driven": {"tasks": json.loads(tasks), "max_parallel": 2, "max_usd_total": float(usd)}}))
PY
}
start() { # request json -> run id
  curl -fsS -D "$work/hdr" -o /dev/null "${auth[@]}" -H 'Content-Type: application/json' -H 'Prefer: wait=1' -d "$1" "$api/v1/runs"
  tr -d '\r' < "$work/hdr" | awk 'tolower($1)=="location:" {n=split($2,p,"/"); print p[n]}'
}
state() { curl -fs "${auth[@]}" "$api/v1/runs/$1" -o "$2" -w '%{http_code}'; }
field() { python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(eval(sys.argv[2]))" "$1" "$2" 2>/dev/null || echo ""; }
check() { # case, where to look, "pass" when the case held
  if [ "$3" = pass ]; then echo "ok   $1"; else echo "FAIL $1  ($2)"; fail=1; fi
}
await() { # run id, result file: waits for the final result
  local end=$((SECONDS + timeout_s))
  while [ "$SECONDS" -lt "$end" ]; do
    [ "$(state "$1" "$2")" = 200 ] && return 0
    sleep 5
  done
  return 1
}

# batch: three tasks, two can pass, one cannot (two tests that contradict each other; a model that edits them has dodged the task, which this case reports).
tasks='[{"id":"easy","goal":"Add a function farewell(name) to greet.py that returns \"Goodbye \" plus the capitalised name, and a test for it in test_all.py"},
        {"id":"unskip","goal":"Implement slugify(text) in text.py (lower case, words joined by a hyphen, punctuation dropped) and remove the skip from test_slugify in test_all.py so it runs and passes"},
        {"id":"impossible","goal":"Make add(2, 2) return 5 while the existing test_add, which expects 4, stays unchanged and passing. Do not edit any test"}]'
id=$(start "$(request "$tasks" "$max_usd")")
[ -n "$id" ] || { echo "the batch did not start (log: $work/server.log)" >&2; exit 1; }
if await "$id" "$work/batch.json"; then done_ok=pass; else done_ok=""; fi
check batch_finished "$work/server.log" "$done_ok"
rows="json.loads([a for a in d['artifacts'] if a['kind']=='driven-batch'][0]['content'])['tasks']"
check two_draft_prs "$work/prs.jsonl" "$([ "$(wc -l < "$work/prs.jsonl" | tr -d ' ')" = 2 ] &&
  [ "$(python3 -c "import json; r=[json.loads(l) for l in open('$work/prs.jsonl')]; print(all(x['draft'] is True and x['token_ok'] and x['head'].startswith('chargehand/') for x in r))")" = True ] && echo pass)"
check two_tasks_have_pr_urls "$work/batch.json" "$([ "$(field "$work/batch.json" "sum(1 for t in $rows if t['pr_url'])")" = 2 ] && echo pass)"
check one_task_failed "$work/batch.json" "$([ "$(field "$work/batch.json" "[t['id'] for t in $rows if not t['pr_url']]")" = "['impossible']" ] &&
  [ -n "$(field "$work/batch.json" "[t['error_code'] for t in $rows if t['id']=='impossible'][0] or ''")" ] && echo pass)"
check batch_tasks_incomplete "$work/batch.json" "$([ "$(field "$work/batch.json" "d['status']")" = failed ] && [ "$(field "$work/batch.json" "d['error']['code']")" = tasks_incomplete ] && echo pass)"
check default_branch_untouched "$work/remote.git" "$([ "$(git -C "$work/remote.git" rev-parse main 2>/dev/null || echo none)" = "$main_before" ] &&
  [ "$(git -C "$work/remote.git" for-each-ref --format='%(refname)' refs/heads | grep -vc -e '^refs/heads/main$' -e '^refs/heads/chargehand/' || true)" = 0 ] && echo pass)"

# streams: copy each task's session stream out of its output volume, then measure adherence over them.
if [ -n "${CHARGEHAND_E2E_STREAMS:-}" ] && [ -s "$work/batch.json" ]; then
  mkdir -p "$CHARGEHAND_E2E_STREAMS"
  python3 -c "import json,sys; d=json.load(open(sys.argv[1])); [print(t['id'], t['run_id']) for t in json.loads([a for a in d['artifacts'] if a['kind']=='driven-batch'][0]['content'])['tasks']]" "$work/batch.json" |
  while read -r task run; do
    docker run --rm -v "chargehand-out-$run:/o:ro" --entrypoint cat "$CHARGEHAND_E2E_IMAGE" /o/stream.jsonl > "$CHARGEHAND_E2E_STREAMS/$task.jsonl" 2>/dev/null || rm -f "$CHARGEHAND_E2E_STREAMS/$task.jsonl"
  done
  $cli runs adherence "$CHARGEHAND_E2E_STREAMS"/*.jsonl || true
fi

# cancel: one task, cancelled while it runs; nothing is pushed, no draft is opened, no container is left.
prs_before=$(wc -l < "$work/prs.jsonl" | tr -d ' ')
cid=$(start "$(request '[{"id":"cancelled","goal":"Add a function farewell(name) to greet.py that returns \"Goodbye \" plus the capitalised name, and a test for it in test_all.py"}]' "$(python3 -c "print($max_usd/5)")")")
running=""
for _ in $(seq 60); do
  [ "$(docker ps -q --filter label=chargehand.run | wc -l | tr -d ' ')" != 0 ] && { running=pass; break; }
  sleep 2
done
check cancel_task_started "$work/server.log" "$running"
curl -fs -X POST "${auth[@]}" "$api/v1/runs/$cid/cancel" >/dev/null || true
await "$cid" "$work/cancel.json" || true
gone=""
for _ in $(seq 30); do
  [ "$(docker ps -aq --filter label=chargehand.run | wc -l | tr -d ' ')" = 0 ] && { gone=pass; break; }
  sleep 2
done
check cancel "$work/cancel.json" "$([ "$(field "$work/cancel.json" "d['error']['code']")" = cancelled ] && [ "$gone" = pass ] &&
  [ "$(wc -l < "$work/prs.jsonl" | tr -d ' ')" = "$prs_before" ] && echo pass)"

# no credential in anything this script or the server wrote: the push token, and the model key (searched for, never printed).
leak=""
for secret in "$CHARGEHAND_E2E_PUSH_KEY" "$CHARGEHAND_E2E_MODEL_KEY"; do
  if grep -rqF -e "$secret" "$work" 2>/dev/null; then leak=1; fi
done
check no_credential_in_output "$work" "$([ -z "$leak" ] && echo pass)"

exit $fail
