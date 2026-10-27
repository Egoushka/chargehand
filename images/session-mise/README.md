# The mise base image

The image of [ADR 0078](../../docs/adr/0078-the-repository-declares-its-environment-and-its-ci-verifies.md) behind `driven.environment: "mise"`: Debian slim, git, make, `build-essential`,
curl, unzip, xz, ICU, Python 3, the session user (uid 10001, home `/home/session`) and the mise binary pinned by version and SHA-256. It holds no SDK. An install helper (the same image, no model)
runs `mise install` in the pinned checkout and writes the toolchains into the repository's home volume, mounted at `/home/session`.

It also holds Claude Code (its native binary, no Node on PATH), the chargehand plugin and the `chargehand session` driver, published self-contained so no .NET runtime sits beside the toolchains a repository pins. The release workflow publishes it as `ghcr.io/<owner>/chargehand-session-mise:<Version>` for linux/amd64 and linux/arm64. With the switch on, the repository's CI verifies a change; before the push the driver runs the check the repository declares (`context.verify`, `driven.services.verify`, then `scripts/check.sh`) in the session's container.

```bash
docker build -f images/session-mise/Dockerfile -t chargehand-session-mise .
```

**Size:** 1.58 GB on disk with the driver and Claude Code (1.04 GB for the base alone; measured 2026-10-27, local build on an arm64 Mac with OrbStack), against 2.1 to 2.4 GB for `images/session`. It holds no SDK; the toolchains live in the repository's home volume.
