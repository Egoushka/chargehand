#!/bin/sh
# Starts the orchestrator's own OpenCode server (ADR 0004, ADR 0016): a pinned binary with its own HOME and
# XDG directories, bound to 127.0.0.1, never the desktop app's background server.
#
# usage: scripts/opencode-serve.sh <opencode-binary> <opencode-config.json> [port]
# env:   CHARGEHAND_STATE          server state directory (default: $HOME/.chargehand/opencode)
#        OPENCODE_SERVER_PASSWORD  HTTP Basic password; if unset, read from the macOS Keychain item
#                                  chargehand-opencode-password
# Worker checkouts must still live outside your home directory: OpenCode discovers skills by walking up
# from a session's directory, whatever HOME says (ADR 0003).
set -eu

bin=${1:?opencode binary}
config=${2:?opencode config json}
port=${3:-4296}
state=${CHARGEHAND_STATE:-$HOME/.chargehand/opencode}

if [ -z "${OPENCODE_SERVER_PASSWORD:-}" ]; then
  OPENCODE_SERVER_PASSWORD=$(security find-generic-password -s chargehand-opencode-password -w)
fi
export OPENCODE_SERVER_PASSWORD

mkdir -p "$state/home" "$state/xdg/config/opencode" "$state/xdg/data" "$state/xdg/state" "$state/xdg/cache"
cp "$config" "$state/xdg/config/opencode/opencode.json"

export HOME="$state/home"
export XDG_CONFIG_HOME="$state/xdg/config" XDG_DATA_HOME="$state/xdg/data"
export XDG_STATE_HOME="$state/xdg/state" XDG_CACHE_HOME="$state/xdg/cache"
export OPENCODE_DISABLE_AUTOUPDATE=1
# A checkout's opencode.json or .opencode can register an MCP server whose command a session then starts. OpenCode
# reads the second name only when the first is unset, so set both.
export OPENCODE_CONFIG_PROJECT_DISABLE=1 OPENCODE_DISABLE_PROJECT_CONFIG=1

exec "$bin" serve --hostname 127.0.0.1 --port "$port"
