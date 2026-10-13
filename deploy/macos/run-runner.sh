#!/bin/bash
# Starts `chargehand runner` on a Mac (ADR 0058). launchd runs this; `run-runner.sh --print` checks the configuration and prints the command without starting anything.
# Everything it needs is in ~/.chargehand/runner.conf (see runner.conf.example) and the login Keychain. It never prints the runner's key.
set -eu

CONF=${CHARGEHAND_RUNNER_CONF:-$HOME/.chargehand/runner.conf}
[ -f "$CONF" ] || { echo "run-runner: no $CONF; run install-runner.sh init" >&2; exit 1; }
# shellcheck disable=SC1090
. "$CONF"

: "${PORT:=4310}" "${MAX_CONTAINERS:=8}" "${MAX_BUNDLE_MB:=512}" "${DRAIN_ON=battery,lid}" "${KEYCHAIN_SERVICE:=chargehand-runner}" "${DOCKER_PROXY_PORT:=2375}"
: "${LISTEN_ADDRESS:=}" "${ALLOWED_HOSTS:=}" "${SESSION_IMAGE:=}" "${FORWARDS:=}" "${CLI:=}"
STATE_DIR=${CHARGEHAND_RUNNER_STATE:-$HOME/.chargehand/runner}
DRAIN_FILE=$STATE_DIR/drain
[ -n "$CLI" ] || CLI="dotnet $HOME/.chargehand/app/Chargehand.Cli.dll"

fail() { echo "run-runner: $*" >&2; exit 1; }

# The address the runner listens on is the private network's and nothing broader: a runner opened on Wi-Fi would take orders from anyone there who has the key.
valid_address() {
  case "$1" in
    ""|"0.0.0.0"|"::"|"*"|"[::]"|"0:0:0:0:0:0:0:0") return 1 ;;
  esac
  if echo "$1" | grep -Eq '^[0-9]{1,3}(\.[0-9]{1,3}){3}$'; then
    case "$1" in
      127.*) return 0 ;;
      100.*) second=${1#100.}; second=${second%%.*}; [ "$second" -ge 64 ] && [ "$second" -le 127 ] && return 0 ;;
    esac
  elif echo "$1" | grep -Eq '^[0-9a-fA-F:]+$' && [ "${1#*:}" != "$1" ]; then
    case "$1" in
      ::1|fd7a:115c:a1e0:*) return 0 ;;
    esac
  else
    return 1
  fi
  [ "${ALLOW_NON_TAILNET_ADDRESS:-}" = "yes" ]
}

[ -n "$LISTEN_ADDRESS" ] || fail "LISTEN_ADDRESS is not set in $CONF (this Mac's address on the private network, e.g. tailscale ip -4)"
valid_address "$LISTEN_ADDRESS" || fail "LISTEN_ADDRESS $LISTEN_ADDRESS is not on the private network (100.64/10, fd7a:115c:a1e0::/48 or loopback): the runner is never opened on another network. ALLOW_NON_TAILNET_ADDRESS=yes in $CONF overrides it for a network you trust."
echo "$SESSION_IMAGE" | grep -Eq '@sha256:[0-9a-f]{64}$' || fail "SESSION_IMAGE must be pinned by digest (name@sha256:<64 hex>) in $CONF"
echo "$PORT" | grep -Eq '^[0-9]{2,5}$' || fail "PORT is not a port number"

case "$LISTEN_ADDRESS" in *:*) endpoint="[$LISTEN_ADDRESS]:$PORT" ;; *) endpoint="$LISTEN_ADDRESS:$PORT" ;; esac
allowed=$LISTEN_ADDRESS
[ -z "$ALLOWED_HOSTS" ] || allowed="$allowed,$ALLOWED_HOSTS"

read -r -a cli <<< "$CLI"
command=("${cli[@]}" runner --listen "$endpoint" --allowed-hosts "$allowed" --images "$SESSION_IMAGE" --egress-image "$SESSION_IMAGE"
  --max-containers "$MAX_CONTAINERS" --max-bundle-mb "$MAX_BUNDLE_MB" --keep-awake on --host-source engine --drain-file "$DRAIN_FILE")
[ -z "$FORWARDS" ] || command+=(--forwards "$FORWARDS")
[ -z "$DRAIN_ON" ] || command+=(--drain-on "$DRAIN_ON")

if [ "${1:-}" = "--print" ]; then
  echo "DOCKER_HOST=tcp://127.0.0.1:$DOCKER_PROXY_PORT CHARGEHAND_RUNNER_KEY=<Keychain item $KEYCHAIN_SERVICE> ${command[*]}"
  exit 0
fi

mkdir -p "$STATE_DIR"

# The address is assigned when the private network is up, which can be after this agent starts at login.
for _ in $(seq 1 60); do
  ifconfig 2>/dev/null | grep -Eq "inet6? ${LISTEN_ADDRESS}( |%)" && break
  sleep 5
done
ifconfig 2>/dev/null | grep -Eq "inet6? ${LISTEN_ADDRESS}( |%)" || fail "$LISTEN_ADDRESS is not an address of this Mac (is the private network up?)"

# The engine is behind the socket proxy container, which starts with OrbStack.
for _ in $(seq 1 60); do
  curl -fsS "http://127.0.0.1:$DOCKER_PROXY_PORT/_ping" > /dev/null 2>&1 && break
  sleep 5
done
curl -fsS "http://127.0.0.1:$DOCKER_PROXY_PORT/_ping" > /dev/null 2>&1 || fail "the socket proxy does not answer on 127.0.0.1:$DOCKER_PROXY_PORT (is OrbStack running, and the chargehand-docker-proxy container?)"

key=$(security find-generic-password -a "$USER" -s "$KEYCHAIN_SERVICE" -w 2>/dev/null) || key=""
[ -n "$key" ] || fail "no runner key in the Keychain: security add-generic-password -a \"\$USER\" -s $KEYCHAIN_SERVICE -w"

export DOCKER_HOST="tcp://127.0.0.1:$DOCKER_PROXY_PORT"
export CHARGEHAND_RUNNER_KEY="$key"
unset key
exec "${command[@]}"
