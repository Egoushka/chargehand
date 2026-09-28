#!/bin/sh
# The one check before a push, as CI runs it: format, build with warnings as errors, tests.
# Healthy: exit 0, and the last test line reads "Passed!  - Failed:     0".
set -eu
cd "$(dirname "$0")/.."
dotnet restore
dotnet format --verify-no-changes --no-restore
dotnet build --no-restore -c Release -warnaserror
dotnet test --no-build -c Release
