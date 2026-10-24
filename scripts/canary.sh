#!/bin/sh
# Canary: GET /v1/health?deep=1 on a chargehand server, which calls the intake model once and reads the runners, GitHub App,
# push token and Plane key the profile configures. Exits 1 when the server is down or any of them fails, and posts the
# reason to an ntfy topic when CHARGEHAND_CANARY_NTFY is set. The process can be up while every run fails (a retired
# gateway, a refused model id, a revoked token), so liveness alone does not catch it.
#
# usage: CHARGEHAND_URL=http://127.0.0.1:4300 CHARGEHAND_API_KEY=<owner key> [CHARGEHAND_CANARY_NTFY=<topic url>] scripts/canary.sh [name]
set -u
name=${1:-chargehand}
url=${CHARGEHAND_URL:?CHARGEHAND_URL}
body=$(curl -s -m 90 -w '\n%{http_code}' -H "Authorization: Bearer ${CHARGEHAND_API_KEY:?CHARGEHAND_API_KEY}" "$url/v1/health?deep=1")
code=$(printf '%s' "$body" | tail -n1)
json=$(printf '%s' "$body" | sed '$d')
if [ "$code" = 200 ]; then
  echo "$name ok: $json"
  exit 0
fi
reason=${json:-no answer from $url}
echo "$name failing ($code): $reason" >&2
if [ -n "${CHARGEHAND_CANARY_NTFY:-}" ]; then
  curl -s -m 10 -H "Title: $name canary failing" -H "Priority: 4" -H "Tags: warning" -d "HTTP $code: $reason" "$CHARGEHAND_CANARY_NTFY" > /dev/null
fi
exit 1
