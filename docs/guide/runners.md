---
title: "Several runners"
description: "Run driven sessions on more than one machine: the runners list, how the server picks one for a task, how a remote runner gets the repository, images by platform, and a Mac as a runner."
order: 11
section: "Guides"
---

A driven batch can use more than one runner service ([ADR 0058](../adr/0058-several-runners.md)): the server and its runner stay where they are, and a second machine, such as a laptop with Docker, takes sessions too. One server keeps one run log, one budget and one list of accounts.

**Status: on main, not yet released. The profile keys, the scheduling across runners with a shared disk, restarts and the events are built. A runner with `workspace: bundle` (a machine that does not share the server's disk) is refused at start until the remote workspace change; the arm64 images and the Mac setup come after it.**

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

A runner with `workspace: bundle` is refused at `chargehand serve` in this build: sending it the repository is the next change. Until then every runner shares the server's checkouts, as `--source-roots` has it.

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

## How a task gets a runner

The server keeps a pool of the runners. When a task is ready to start, the pool picks one that is all of:

- **Answering.** `GET /host` returned within 3 seconds; the answer is reused for 5 seconds. A runner that did not answer is skipped for 15 seconds, then 30, doubling to 2 minutes, and asked again after that. A skipped runner never fails a batch or a task; the log says when a runner is skipped and when it answers again.
- **Not draining.** The snapshot's `state` is not `draining` (a laptop that is on battery or has its lid closed asks for no new session; sessions it already has go on).
- **Under `max_sessions`**, counting the sessions this server has running on it.
- **Admitted on its own host.** The memory and load rule of [admission](driven.md) against that runner's snapshot and its `reserve_mb`, counting the sessions started on it in the last minute. A runner whose host reports neither memory nor load is admitted by count and comes last.

Of those, the runner with the most free memory times its `weight` wins; equal scores go to the one listed first. When none passes, the task waits and asks again every 5 seconds, with one `session_progress` event that names why each runner was passed over (`task t2: waiting for a runner (box: waiting for memory (...); laptop: draining (on battery))`). If nothing of this server is running on any runner and none has room, the runner with the most free memory takes one task anyway, as one host does. If no runner has been answering and not draining for 15 minutes, the batch stops starting tasks: the rest end `not_started` with `container_unavailable` and the reason, and what runs goes on.

`max_parallel` of the batch and `max_parallel_total` of the server still bound the tasks as a whole; the runners decide where, not how many.

## A task stays on its runner

All sessions of a task (a continuation after a stop, a CI fix session) and its handover, including chargehand's own run of the tests, happen on the runner that took its first session: that is where its output volume, the batch's network, test services and caches are. A task that depends on another, or continues one of an earlier batch, may go to any runner and reads the earlier task's bundle from the runner that ran it (the `runner` in the `driven-batch` artifact).

If a runner does not answer when a task tries to start there (before its session container exists), the task is placed again, at most twice more, and the runner is skipped as above. If it stops answering during a session, the server waits for it for up to 10 minutes of continuous silence (a laptop that sleeps, a path that flaps), says so in one `session_progress` event, and carries on when it answers; past that the task fails `container_unavailable`, and the session is swept when the runner is next reachable. A refusal (a wrong key, an image the runner does not list) is not waited for.

## Restarts

Each task's runner is saved with the batch before anything starts there, so a restarted server finds a running session on the runner it was placed on and does not start it again; it does not ask the pool, and does not wait for memory, for that task. The batch network and egress container are made again on each runner the batch uses, with the batch's own key, so tokens the sessions already hold still work. The sweep after a restart runs on every runner that answers, with the same rule as one runner (what no resumed batch owns goes); a runner that did not answer is swept before its first task, so a session left there does not take its container count. A task whose runner is no longer in the profile fails with that reason.

## What the events and the result say

When the profile uses `driven.runners`, every event about a task (`container_started`, `session_progress`, `verify_finished`, `pushed`, `pr_opened`, `task_finished`) carries `runner`, `container_started` ends with `on runner '<name>'`, and each row of the `driven-batch` artifact has `runner`. A runner no task goes to has no network, egress container or test service made for the batch. With `driven.runner` nothing is added.
