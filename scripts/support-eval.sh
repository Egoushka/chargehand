#!/usr/bin/env bash
# Measures the support judge (goal 0.8, ADR 0036) on evals/support-examples.jsonl: 30 claims labelled supported, partial or
# unsupported against code from this repository. Needs a signed-in `claude` (or CHARGEHAND_RUNTIME and a profile) and the .NET SDK;
# it calls a real model, so it runs by hand. It prints agreement per class and every miss.
# usage: scripts/support-eval.sh [intake-model, e.g. anthropic/haiku]   (unset: the runtime's default model)
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
export CHARGEHAND_RUNTIME=${CHARGEHAND_RUNTIME:-claude_code}
profile=$(mktemp "${TMPDIR:-/tmp}/support-eval.XXXXXX")
printf '{ "schema": "profile/v1"%s }\n' "${1:+, \"intake_model\": \"$1\"}" > "$profile"
dotnet build "$here/src/Chargehand.Cli" -v q -nologo >/dev/null
dotnet "$here/src/Chargehand.Cli/bin/Debug/net10.0/Chargehand.Cli.dll" --profile "$profile" eval support "$here/evals/support-examples.jsonl"
