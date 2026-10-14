#!/bin/sh
# Pins a release to digests: ./pin-digests.sh <version>   (for example 0.8.8)
# Resolves the server image and the session image of that release, writes CHARGEHAND_IMAGE and CHARGEHAND_SESSION_IMAGE into .env, and
# replaces the session image in profile.json (driven.images) when the profile has one. Needs docker with buildx and no registry login for public images.
set -eu
cd "$(dirname "$0")"
version=${1:?usage: pin-digests.sh <version>}
owner=${CHARGEHAND_IMAGE_OWNER:-egoushka}
[ -f .env ] || { echo "no .env here: copy .env.example to .env first" >&2; exit 1; }

digest() { docker buildx imagetools inspect "$1" --format '{{.Manifest.Digest}}'; }
server_digest=$(digest "ghcr.io/$owner/chargehand:$version")
session_digest=$(digest "ghcr.io/$owner/chargehand-session:$version")
case "$server_digest$session_digest" in
  *sha256:*sha256:*) ;;
  *) echo "could not resolve both digests for $version" >&2; exit 1 ;;
esac

set_var() { # name value: replaces the line (commented or not), or appends it
  awk -v n="$1" -v v="$2" '!done && $0 ~ "^#?" n "=" { print n "=" v; done = 1; next } { print } END { if (!done) print n "=" v }' .env > .env.tmp
  mv .env.tmp .env
}
set_var CHARGEHAND_IMAGE "ghcr.io/$owner/chargehand:$version@$server_digest"
set_var CHARGEHAND_SESSION_IMAGE "ghcr.io/$owner/chargehand-session@$session_digest"

if [ -f profile.json ]; then
  sed -E "s#/chargehand-session@sha256:[0-9a-f]{64}#/chargehand-session@$session_digest#" profile.json > profile.json.tmp
  mv profile.json.tmp profile.json
fi
echo "CHARGEHAND_IMAGE=ghcr.io/$owner/chargehand:$version@$server_digest"
echo "CHARGEHAND_SESSION_IMAGE=ghcr.io/$owner/chargehand-session@$session_digest"
