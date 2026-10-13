#!/bin/sh
# The one check before a push, as CI runs it: the operator console (web/console, when Node is installed), format, build with warnings as errors, tests.
# Healthy: exit 0, and the last test line reads "Passed!  - Failed:     0".
set -eu
cd "$(dirname "$0")/.."

# The console goes first: the server embeds its build, so the .NET tests then check what the server serves (ADR 0056).
# Without Node the web steps are skipped, said out loud, and the server serves a "console not built" page.
if command -v node >/dev/null 2>&1 && command -v npm >/dev/null 2>&1; then
  (
    cd web/console
    echo "check: web/console with node $(node --version) (CI uses $(cat .nvmrc))"
    if [ ! -d node_modules ] || [ package-lock.json -nt node_modules/.package-lock.json ]; then
      npm ci --ignore-scripts
    fi
    npm run lint
    npm run typecheck
    npm test
    npm run build
    if node -e "process.exit(require('fs').existsSync(require('@playwright/test').chromium.executablePath()) ? 0 : 1)" 2>/dev/null; then
      npm run test:e2e
    else
      echo "check: SKIPPED the console smoke test: Chromium for Playwright is not installed (cd web/console && npx playwright install chromium). CI runs it." >&2
    fi
  )
else
  echo "check: SKIPPED the console steps (web/console): node and npm are not installed (CI uses Node $(cat web/console/.nvmrc)). The server will serve a 'console not built' page." >&2
fi

dotnet restore
dotnet format --verify-no-changes --no-restore
dotnet build --no-restore -c Release -warnaserror
dotnet test --no-build -c Release
