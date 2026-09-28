#!/usr/bin/env bash
# End-to-end check of /chargehand:change (goal 0.5). Needs a signed-in `claude` and the .NET SDK; calls a real model,
# so it runs by hand or on the maintainer's runner (.github/workflows/change-e2e.yml), never on every push.
# The chargehand server is this checkout's build (scripts/change-e2e.mcp.json), not the published package.
# env: CHARGEHAND_RUNTIME  worker runtime of that server (default claude_code: the signed-in claude, no profile needed)
#      CHARGEHAND_PROFILE  profile for that server; unset, a throwaway one that only maps the presets' placeholder
#                          models to Claude Code's sonnet and haiku (with no profile they reach the CLI unresolved)
# Each case prints "ok   <case>" or "FAIL <case>  (log: <file>)"; exit 1 when any case fails.
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
export CHARGEHAND_RUNTIME=${CHARGEHAND_RUNTIME:-claude_code}
if [ -n "${CHARGEHAND_PROFILE:-}" ]; then
  CHARGEHAND_PROFILE=$(cd "$(dirname "$CHARGEHAND_PROFILE")" && pwd)/$(basename "$CHARGEHAND_PROFILE")
else
  CHARGEHAND_PROFILE=$(mktemp "${TMPDIR:-/tmp}/change-e2e.XXXXXX")
  cat > "$CHARGEHAND_PROFILE" <<'EOF'
{ "schema": "profile/v1",
  "models": { "provider/worker-model": "anthropic/sonnet", "provider/critic-model": "anthropic/sonnet",
              "provider/small-model": "anthropic/haiku" } }
EOF
fi
export CHARGEHAND_PROFILE
dotnet build "$here/src/Chargehand.Cli" -v q -nologo >/dev/null
# claude -p runs in the sample repository, so the project path must be absolute.
mcp=$(mktemp "${TMPDIR:-/tmp}/change-e2e.XXXXXX")
sed "s|CHECKOUT|$here|" "$here/scripts/change-e2e.mcp.json" > "$mcp"
fail=0

sample() {
  local d; d=$(mktemp -d "${TMPDIR:-/tmp}/change-e2e.XXXXXX")
  git -C "$d" init -q -b main
  printf 'def greet(name):\n    return "hello " + name\n' > "$d/greet.py"
  printf 'import unittest\nfrom greet import greet\n\n\nclass GreetTest(unittest.TestCase):\n    def test_greet(self):\n        self.assertEqual(greet("a"), "hello a")\n' > "$d/test_greet.py"
  printf '# sample\nTests: \140python3 -m unittest -q\140\n' > "$d/CLAUDE.md"
  git -C "$d" add . && git -C "$d" -c user.name=e2e -c user.email=e2e@example.com commit -q -m init
  echo "$d"
}

# --strict-mcp-config keeps only the servers in the given file: the plugin's own .mcp.json server is not loaded, so
# the tool is mcp__chargehand__orchestrate from the checkout's server. --setting-sources "" (as the worker runtime
# does) keeps the user's own hooks and plugins out: a hook that writes into the sample repository fails preflight.
# --add-dir lets the session read the skill's report template, which -p would otherwise deny.
run() { # dir, mcp-config, goal
  (cd "$1" && GIT_AUTHOR_NAME=e2e GIT_AUTHOR_EMAIL=e2e@example.com GIT_COMMITTER_NAME=e2e GIT_COMMITTER_EMAIL=e2e@example.com \
    claude -p "/chargehand:change $3" --plugin-dir "$here/plugins/chargehand" --add-dir "$here/plugins/chargehand" \
     --mcp-config "$2" --strict-mcp-config --setting-sources "" --permission-mode acceptEdits \
     --allowedTools "Bash(git:*),Bash(python3 -m unittest:*),mcp__chargehand__orchestrate" >"$1.log" 2>&1) || true
}

check() { # case, dir, "pass" when the case held
  if [ "$3" = pass ]; then echo "ok   $1"; else echo "FAIL $1  (log: $2.log)"; fail=1; fi
}
changes() { git -C "$1" branch --list 'change/*' --format '%(refname:short)'; }
reviewed() { # a change branch with the change and the report committed on it
  local b; b=$(changes "$1" | head -1)
  [ -n "$b" ] && [ "$(git -C "$1" rev-list --count main.."$b")" -ge 2 ] && git -C "$1" cat-file -e "$b:.chargehand/reports/${b#change/}.md"
}
untouched() { [ -z "$(changes "$1")" ]; }
has_branch() { [ -n "$(git -C "$1" branch --list "$2")" ]; }

d=$(sample); run "$d" "$mcp" "make greet() capitalise the name"
check happy "$d" "$(reviewed "$d" && echo pass)"

d=$(sample); echo dirty >> "$d/greet.py"; run "$d" "$mcp" "make greet() capitalise the name"
check dirty "$d" "$(untouched "$d" && echo pass)"

d=$(sample); echo '{"mcpServers":{}}' > "$d.none.json"; run "$d" "$d.none.json" "make greet() capitalise the name"
check no-server "$d" "$(untouched "$d" && echo pass)"

d=$(sample); git -C "$d" branch change/make-greet-capitalise-the-name; run "$d" "$mcp" "make greet capitalise the name"
check branch-exists "$d" "$(has_branch "$d" change/make-greet-capitalise-the-name-2 && echo pass)"

exit $fail
