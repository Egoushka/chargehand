# The session image

The image a driven writing session runs in ([ADR 0039](../../docs/adr/0039-driven-writing-sessions.md)): one container per task, started by the runner
from a fixed template (non-root, read-only root filesystem, no capabilities, no Docker socket, one internal network).

| Holds | Version |
|---|---|
| .NET SDK | 10 (the base image's) |
| Node | the base image's distribution package |
| Python | 3, with `pip` and `venv` |
| git | the distribution's |
| Claude Code | `CLAUDE_CODE_VERSION`, which must equal `claude_code.version` in the profile |
| chargehand | this checkout's published CLI at `/opt/chargehand`, and the plugin at `/opt/chargehand-plugin` |

It holds no Docker client, no cloud CLI and no credential. The model credential, the proxy and the run token arrive as environment variables from
the runner's env file, and the goal as `/out/task.json`.

```bash
docker build -f images/session/Dockerfile -t chargehand-session .
```

A profile lists the image by digest (`driven.images`); the runner starts nothing else. A repository picks a toolchain by detection, and may name
an image from that list in `.chargehand/session.json`. A repository that needs Java, Go or Rust needs its own image on the list: the design does
not build images on demand.

**Size (measured 2026-09-30, local build on an arm64 Mac): 2.4 GB on disk.** The .NET SDK base is most of it. A leaner split by toolchain would save pull time but multiplies what to pin, scan and keep at one Claude Code version; the decision is to start with this one image (spec, "The image"). Pull time on the VPS is not measured.

`SessionImageTests` checks the toolchains, the pinned Claude Code version and the absence of a Docker client on a real engine
(`CHARGEHAND_TEST_SESSION_IMAGE`, the id `docker build -q` prints).
