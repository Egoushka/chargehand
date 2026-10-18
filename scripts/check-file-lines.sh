#!/bin/sh
# Usage: check-file-lines.sh [max]. Fails when a .cs file under the rewrite's folders is over the line budget (ADR 0060, default 400), naming each.
# The rule ModuleRuleTests checks, without a build, so a driven session's check gate can run it (.chargehand/checks).
set -eu
max=${1:-400}
over=$(find src/Shared src/Modules src/Runtimes src/Hosts -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -exec awk -v max="$max" '
  FNR == 1 && NR > 1 && n > max { print f ": " n " lines" }
  { f = FILENAME; n = FNR }
  END { if (n > max) print f ": " n " lines" }' {} +)
if [ -n "$over" ]; then
  echo "over $max lines (split the file):"
  echo "$over"
  exit 1
fi
