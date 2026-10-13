#!/bin/bash
# Sets up `chargehand runner` on a Mac as a launchd login agent (ADR 0058, docs/guide/runners.md "A Mac as a runner").
#
#   install-runner.sh init                  write ~/.chargehand/runner.conf from runner.conf.example if there is none
#   install-runner.sh render [--out DIR]    write the agent's files into DIR (default ./rendered) and change nothing else
#   install-runner.sh install [--dry-run]   pull the session image, start the socket proxy, install and start the agent
#   install-runner.sh status                the agent, the proxy and the end of the log
#   install-runner.sh drain | undrain       stop / resume taking new sessions by hand (a session that runs goes on)
#   install-runner.sh uninstall [--proxy]   stop and remove the agent (and the socket proxy container)
#
# `--dry-run` prints every command it would run and runs none. Nothing here holds a secret: the runner's key is read from the login Keychain by run-runner.sh when the agent starts.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
CONF=${CHARGEHAND_RUNNER_CONF:-$HOME/.chargehand/runner.conf}
STATE_DIR=${CHARGEHAND_RUNNER_STATE:-$HOME/.chargehand/runner}
LABEL=com.chargehand.runner
PLIST=$HOME/Library/LaunchAgents/$LABEL.plist
LOG_DIR=$HOME/Library/Logs/chargehand
PROXY_NAME=chargehand-docker-proxy
# The same pin as deploy/compose/compose.yaml: an index with a linux/arm64 variant.
PROXY_IMAGE=tecnativa/docker-socket-proxy@sha256:1f5038b54f06c3e18422902cf00ba21803d1c97805aae032e5e6673d532d3459

fail() { echo "install-runner: $*" >&2; exit 1; }

dry=no
run() { # prints the command; runs it unless --dry-run
  if [ "$dry" = yes ]; then echo "+ $*"; else "$@"; fi
}

render() { # DIR: the wrapper and the agent's plist, with this user's home in the paths
  local out=$1
  mkdir -p "$out"
  cp "$HERE/run-runner.sh" "$out/run-runner.sh"
  chmod +x "$out/run-runner.sh"
  sed "s#__HOME__#$HOME#g" "$HERE/com.chargehand.runner.plist.in" > "$out/$LABEL.plist"
}

load_conf() {
  [ -f "$CONF" ] || fail "no $CONF; run: $0 init"
  # shellcheck disable=SC1090
  . "$CONF"
  : "${DOCKER_PROXY_PORT:=2375}" "${SESSION_IMAGE:=}"
}

cmd=${1:-}
[ -n "$cmd" ] && shift
case "$cmd" in
  init)
    mkdir -p "$(dirname "$CONF")"
    if [ -f "$CONF" ]; then echo "$CONF exists; left as it is"; else cp "$HERE/runner.conf.example" "$CONF"; echo "wrote $CONF: fill in LISTEN_ADDRESS, SESSION_IMAGE and FORWARDS"; fi
    ;;

  render)
    out=./rendered
    [ "${1:-}" != "--out" ] || { out=${2:?--out needs a directory}; }
    render "$out"
    echo "wrote $out/run-runner.sh and $out/$LABEL.plist"
    ;;

  install)
    [ "${1:-}" != "--dry-run" ] || dry=yes
    load_conf
    # The configuration is checked first, with nothing started: a wrong address or an unpinned image stops here.
    "$HERE/run-runner.sh" --print > /dev/null
    command -v docker > /dev/null 2>&1 || [ "$dry" = yes ] || fail "docker is not on PATH (OrbStack provides it)"
    # The runner asks the engine about memory with a container of the session image, and a pull in the middle of that is slow: pull it now.
    run docker pull "$SESSION_IMAGE"
    if [ "$dry" = yes ] || ! docker inspect "$PROXY_NAME" > /dev/null 2>&1; then
      # Loopback only, the engine's API sections the runner needs, and the socket mounted read-only into a container that does nothing else. It restarts with the engine.
      run docker run -d --name "$PROXY_NAME" --restart unless-stopped -p "127.0.0.1:$DOCKER_PROXY_PORT:2375" \
        -e CONTAINERS=1 -e POST=1 -e IMAGES=1 -e VOLUMES=1 -e NETWORKS=1 -v /var/run/docker.sock:/var/run/docker.sock:ro "$PROXY_IMAGE"
    else
      echo "the socket proxy container $PROXY_NAME exists; left as it is"
    fi
    run mkdir -p "$STATE_DIR" "$LOG_DIR" "$(dirname "$PLIST")"
    if [ "$dry" = yes ]; then
      echo "+ render $STATE_DIR (run-runner.sh) and $PLIST"
    else
      render "$STATE_DIR/render"
      cp "$STATE_DIR/render/run-runner.sh" "$STATE_DIR/run-runner.sh"
      cp "$STATE_DIR/render/$LABEL.plist" "$PLIST"
    fi
    run launchctl bootout "gui/$(id -u)/$LABEL" 2> /dev/null || true
    run launchctl bootstrap "gui/$(id -u)" "$PLIST"
    if [ "$dry" = yes ]; then
      echo "dry run: nothing was changed"
    else
      echo "installed $LABEL; the runner key must be in the Keychain: security add-generic-password -a \"\$USER\" -s ${KEYCHAIN_SERVICE:-chargehand-runner} -w"
    fi
    ;;

  status)
    launchctl print "gui/$(id -u)/$LABEL" 2> /dev/null | grep -E "state|pid|last exit" || echo "$LABEL is not loaded"
    docker ps --filter "name=$PROXY_NAME" --format 'socket proxy: {{.Names}} {{.Status}}' 2> /dev/null || true
    [ ! -f "$STATE_DIR/drain" ] || echo "draining: $STATE_DIR/drain exists"
    tail -n 15 "$LOG_DIR/runner.log" 2> /dev/null || true
    ;;

  drain)
    mkdir -p "$STATE_DIR"
    : > "$STATE_DIR/drain"
    echo "draining: no new session is placed here; the ones that run go on (undrain to resume)"
    ;;

  undrain)
    rm -f "$STATE_DIR/drain"
    echo "resumed"
    ;;

  uninstall)
    launchctl bootout "gui/$(id -u)/$LABEL" 2> /dev/null || true
    rm -f "$PLIST" "$STATE_DIR/run-runner.sh"
    if [ "${1:-}" = "--proxy" ]; then docker rm -f "$PROXY_NAME" > /dev/null 2>&1 || true; fi
    echo "removed $LABEL"
    ;;

  *)
    sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
    ;;
esac
