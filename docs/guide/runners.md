---
title: "Several runners"
description: "Run driven sessions on more than one machine: the runners list, how the server picks one for a task, how a remote runner gets the repository, images by platform, and a Mac as a runner."
order: 11
section: "Guides"
---

A driven batch can use more than one runner service ([ADR 0058](../adr/0058-several-runners.md)): the server and its runner stay where they are, and a second machine, such as a laptop with Docker, takes sessions too. One server keeps one run log, one budget and one list of accounts.

**Status: on main, not yet released. This change adds the profile keys, their validation and `doctor`'s checks. A server refuses to start with more than one runner configured until the scheduling change lands; the sections below say which change builds each part.**

## The profile

`driven.runner` names one runner and stays as it is. `driven.runners` replaces it with a list; a profile names one or the other.

```json
"driven": {
  "images": ["registry.example/chargehand-session@sha256:<index digest>"],
  "runners": [
    { "name": "box", "url": "http://runner:4310", "api_key_secret": "runner-key", "max_sessions": 4 },
    { "name": "laptop", "url": "http://laptop.example.net:4310", "api_key_secret": "laptop-runner-key",
      "platform": "linux/arm64", "max_sessions": 2, "weight": 0.5, "reserve_mb": 4096,
      "workspace": "bundle", "forwards": { "mcp": "server.example.net:4300" } }
  ]
}
```

| key | default | meaning |
|---|---|---|
| `name` | required | Unique; 1 to 32 lowercase letters, digits and hyphens. It is in run events and in the batch result. |
| `url`, `api_key_secret` | required | The runner's address as the server reaches it, and the secret item for its bearer key. |
| `max_sessions` | none | Sessions this server runs on the runner at once. |
| `platform` | `linux/amd64` | The architecture of the runner's engine (`linux/amd64` or `linux/arm64`). It picks the image, see below. |
| `weight` | 1 | Multiplies the runner's free memory when two runners are compared. Above 0, at most 100. |
| `reserve_mb` | `driven.admission.reserve_mb` | Memory left for everything else on this runner's host. A laptop someone is using needs more than a server. |
| `workspace` | `shared` | `shared`: the runner reads the server's checkouts from a disk both see (the runner's `--source-roots`), as one runner does today. `bundle`: the runner is not on the server's host, and the server sends it the repository as a git bundle. |
| `forwards` | none | The server's address as this runner's egress containers reach it. `mcp` (`host:port`), `model_url`, `otlp_url` and `outside` each replace the same key of `driven.network` for this runner. |

`forwards` exists because `driven.network.mcp_forward` is a name on the server's Docker network (`chargehand:4300`), which means nothing on another machine. A `bundle` runner needs `forwards.mcp` while `network.mcp_forward` is set, and `forwards.model_url` or `forwards.otlp_url` while those name an `http` address; the profile is refused without them, because the alternative is a session that silently cannot call the server back. The runner's own `--forwards` must list each forward it will be asked to carry (`4300=server.example.net:4300`), as on the server's runner.

A profile with `driven.runner` is the same as `driven.runners` with one entry named `default` and every other key at its default; it needs no change, and it reports as before (no `runner` on events).

## Images by platform

The release workflow publishes `chargehand-session` for `linux/amd64` and `linux/arm64` as one multi-platform image (from the change that adds arm64 images; before it the images are amd64 only). Pin the index digest in `driven.images` and every runner pulls the variant for its engine.

A digest from before that, or an image built for one platform, is used as written on a runner of any platform. To start another image on a runner of another platform, map it:

```json
"image_variants": {
  "registry.example/chargehand-session@sha256:<amd64 digest>": { "linux/arm64": "registry.example/chargehand-session@sha256:<arm64 digest>" }
}
```

A key must be an image of `driven.images`; a value must be pinned by digest. The runner's `--images` (and `--egress-image`, which is the session image on the server's runner) must list the reference the server will ask it to start: for an arm64 runner, the mapped one. `platform` must match the engine (`docker info --format '{{.OSType}}/{{.Architecture}}'` prints `linux/aarch64` on Apple silicon; that is `linux/arm64`); an amd64 image on an arm64 engine runs under emulation, slowly, or not at all.

`chargehand doctor` probes each runner (`runner <name>`), checks each runner's key item, and for a runner that is not amd64 says how many of `driven.images` have a variant for it.
