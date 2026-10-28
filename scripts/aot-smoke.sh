#!/bin/sh
# ADR 0060: every host of the rewrite publishes as a Native AOT binary with no warnings, and the binary runs. The ACP client library
# (ADR 0086) is published and driven through samples/AcpSmoke.
# Usage: scripts/aot-smoke.sh [rid]   (default: the machine's own). Healthy: exit 0 and one "ok" line per host.
# Prints size and, for the HTTP hosts, startup to the first /healthz answer and resident memory after it.
set -eu
cd "$(dirname "$0")/.."
rid=${1:-$(dotnet --info | sed -n 's/^ *RID: *//p' | head -n 1)}
out=$(mktemp -d)
trap 'kill "$pid" 2>/dev/null || true; rm -rf "$out"' EXIT
pid=

publish() { # <host>
  dotnet publish "src/Hosts/Chargehand.Hosts.$1" -c Release -r "$rid" -warnaserror -o "$out/$1" -v quiet -nologo
  bin="$out/$1/Chargehand.Hosts.$1"
  echo "$1: $(du -k "$bin" | cut -f1) KiB"
}

for host in Cli Session; do
  publish "$host"
  test "$("$out/$host/Chargehand.Hosts.$host" --version)" != ""
  echo "ok $host --version"
done

# The ACP client library (ADR 0086, decision 7): a sample publishes it with Native AOT, starts itself as an agent and drives it.
dotnet publish samples/AcpSmoke -c Release -r "$rid" -warnaserror -o "$out/AcpSmoke" -v quiet -nologo
"$out/AcpSmoke/AcpSmoke"

port=4791
for host in Server Runner; do
  publish "$host"
  start=$(date +%s%N 2>/dev/null || date +%s)
  ASPNETCORE_URLS="http://127.0.0.1:$port" "$out/$host/Chargehand.Hosts.$host" > "$out/$host.log" 2>&1 &
  pid=$!
  tries=0
  until curl -fsS "http://127.0.0.1:$port/healthz" > "$out/$host.health" 2>/dev/null; do
    tries=$((tries + 1))
    if [ "$tries" -gt 100 ]; then cat "$out/$host.log" >&2; exit 1; fi
    sleep 0.05
  done
  end=$(date +%s%N 2>/dev/null || date +%s)
  grep -q '"status":"ok"' "$out/$host.health"
  case "$start$end" in *N*) ms="?" ;; *) ms=$(((end - start) / 1000000)) ;; esac
  echo "ok $host /healthz in ${ms} ms, rss $(ps -o rss= -p "$pid" | tr -d ' ') KiB"
  kill "$pid"
  wait "$pid" 2>/dev/null || true
  pid=
  port=$((port + 1))
done
