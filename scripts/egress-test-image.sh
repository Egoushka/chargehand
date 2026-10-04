#!/bin/sh
# Builds a local image for the Docker egress tests: <base> with this checkout's published CLI over /app/bin, so a container can run
# `chargehand egress`. <base> is any ASP.NET 10 runtime image (mcr.microsoft.com/dotnet/aspnet:10.0, or a released chargehand image).
# Prints the image id (sha256:...), which the tests take as CHARGEHAND_TEST_EGRESS_IMAGE.
set -eu
base="${1:?usage: scripts/egress-test-image.sh <base-image>}"
cd "$(dirname "$0")/.."
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# The CLI runs in a Linux container, so publish for the engine's architecture, not the host's (a Mac would otherwise ship a Mach-O binary).
case "$(docker info --format '{{.Architecture}}')" in
  aarch64|arm64) rid=linux-arm64 ;;
  x86_64|amd64) rid=linux-x64 ;;
  *) echo "unsupported container architecture" >&2; exit 1 ;;
esac
dotnet publish src/Chargehand.Cli -c Release -r "$rid" --self-contained false -o "$work/out" >&2
printf 'FROM %s\nCOPY out /app/bin\nENTRYPOINT ["dotnet", "/app/bin/Chargehand.Cli.dll"]\n' "$base" > "$work/Dockerfile"
docker build -q "$work"
