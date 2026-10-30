#!/bin/bash
# Updates the chargehand build under ~/.chargehand/app that the local server and the runner use (CHARGEHAND-328):
#
#   update.sh [--dry-run]
#
# Refuses a dirty or non-main checkout before anything is built: a half-committed tree never reaches the Mac. It
# then publishes into a new directory beside the current one and swaps a symlink into place with one rename, so a
# reader never sees a half-written app directory and the previous build stays on disk for a rollback. It restarts
# the Mac agent and only calls the update done once /v1/health answers; if it does not, it rolls the symlink and
# the agent back and exits non-zero. `--dry-run` prints every command it would run and runs none; the dirty/branch
# check still runs first, as in install-runner.sh.
set -eu

REPO=${CHARGEHAND_UPDATE_REPO:-$(cd "$(dirname "$0")/../.." && pwd)}
APP_DIR=${CHARGEHAND_APP_DIR:-$HOME/.chargehand/app}
RELEASES_DIR=${CHARGEHAND_RELEASES_DIR:-$HOME/.chargehand/releases}
BRANCH=${CHARGEHAND_UPDATE_BRANCH:-main}
LABEL=${CHARGEHAND_UPDATE_LABEL:-com.chargehand.runner}
HEALTH_URL=${CHARGEHAND_HEALTH_URL:-http://127.0.0.1:4300/v1/health}

fail() { echo "update: $*" >&2; exit 1; }

dry=no
[ "${1:-}" != "--dry-run" ] || dry=yes
run() { if [ "$dry" = yes ]; then echo "+ $*"; else "$@"; fi; }

cd "$REPO"
[ -z "$(git status --porcelain)" ] || fail "the checkout at $REPO is dirty; commit or stash first"
branch=$(git rev-parse --abbrev-ref HEAD)
[ "$branch" = "$BRANCH" ] || fail "the checkout at $REPO is on '$branch', not '$BRANCH'; switch first"

commit=$(git rev-parse --short HEAD)
release="$RELEASES_DIR/$(date +%Y%m%d%H%M%S)-$commit"
run mkdir -p "$release"
run dotnet publish "$REPO/src/Chargehand.Cli" -c Release -o "$release"

previous=$(readlink "$APP_DIR" 2> /dev/null || true)
run mkdir -p "$(dirname "$APP_DIR")"
run ln -sfn "$release" "$APP_DIR"

run launchctl kickstart -k "gui/$(id -u)/$LABEL"

probe() {
  if [ "$dry" = yes ]; then
    echo "+ curl -fsS $HEALTH_URL"
    return 0
  fi
  for _ in $(seq 1 10); do
    curl -fsS -m 5 "$HEALTH_URL" > /dev/null 2>&1 && return 0
    sleep 1
  done
  return 1
}

if probe; then
  if [ "$dry" = yes ]; then echo "dry run: nothing was changed"; else echo "updated $APP_DIR -> $release"; fi
  exit 0
fi

echo "update: $HEALTH_URL did not answer after the restart; rolling back to ${previous:-no previous release}" >&2
if [ -n "$previous" ]; then
  run ln -sfn "$previous" "$APP_DIR"
  run launchctl kickstart -k "gui/$(id -u)/$LABEL"
fi
exit 1
