# The mise base image

The image of [ADR 0078](../../docs/adr/0078-the-repository-declares-its-environment-and-its-ci-verifies.md) behind `driven.environment: "mise"`: Debian slim, git, make, `build-essential`,
curl, unzip, xz, ICU, Python 3, the session user (uid 10001, home `/home/session`) and the mise binary pinned by version and SHA-256. It holds no SDK. An install helper (the same image, no model)
runs `mise install` in the pinned checkout and writes the toolchains into the repository's home volume, mounted at `/home/session`.

This step holds the base only. Claude Code and the `chargehand session` driver are layered on it in the step that makes CI the verifier; the release workflow does not publish it yet.

```bash
docker build -f images/session-mise/Dockerfile -t chargehand-session-mise .
```

**Size:** 1.04 GB on disk (measured 2026-10-23, local build on an arm64 Mac with OrbStack), against 2.1 to 2.4 GB for `images/session`. It holds no SDK; the toolchains live in the repository's home volume.
