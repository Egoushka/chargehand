---
title: "Several runners"
description: "Run driven sessions on more than one machine: the runners list, how the server picks one for a task, how a remote runner gets the repository, images by platform, and a Mac as a runner."
order: 11
section: "Guides"
---

A driven batch can use more than one runner service ([ADR 0058](../adr/0058-several-runners.md)): the server and its runner stay where they are, and a second machine, such as a laptop with Docker, takes sessions too. One server keeps one run log, one budget and one list of accounts.

**Status: on main, not yet released. Built: the profile keys, scheduling across runners, restarts, the events, the remote workspace, the arm64 session images (published by the next release; until then there is no arm64 image on the registry) and the Mac runner (keep-awake, draining, the engine's own memory figures, the launchd scripts). The Mac setup has run here against a real OrbStack engine on loopback; it has not been installed as a login agent or reached over the private network, so the steps below end with checks.**

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

The release workflow publishes `chargehand` (the server), `chargehand-session`, `chargehand-session-jdk` and `chargehand-session-mise` for `linux/amd64` and `linux/arm64` as one multi-platform image each, so `docker pull` works on Apple silicon: the release job pushes amd64, and the `session-arm64` job builds arm64 natively on an arm64 runner (no emulation: the image runs dotnet, node and npm while it builds), then turns each tag into an index of the two and prints the index digests to pin. Releases before that one are amd64 only (the server image: every release up to 0.19.4). Pin the index digest in `driven.images` and every runner pulls the variant for its engine.

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

## A runner on another machine

A runner with `workspace: shared` mounts the server's checkout of the pinned commit read-only into the helper that fills a session's workspace, so the runner and the server must see the same disk. A runner on another machine cannot, so give it `workspace: bundle`:

1. The server makes one git bundle of the pinned commit from its own checkout (`git bundle create <file> HEAD`; the checkout is detached at the commit). It holds the commit's whole history, the same objects a clone would fetch, and no configuration or hooks. It is made once per commit for all the tasks and sessions of the batches that need it and removed a day after it was last made.
2. For each session the server sends it to the runner with `PUT /out/<run>/source.bundle`, as the request body with its length, so the runner can refuse it by size before reading a byte. The runner streams it into the run's output volume (through the same no-network helper that writes `task.json`) and never holds it in memory. The runner's limit is `--max-bundle-mb` (default 512, at most 4096); a bigger bundle is refused with 413 and the task fails with that reason.
3. `POST /workspace/bundle` runs the workspace helper with the output volume mounted read-only in place of a source directory: it clones from the bundle, creates `chargehand/<run>` at the commit and leaves the same workspace the path variant leaves. The route names no host path, so the runner needs no `--source-roots`; a runner started without them refuses the path variant, so it cannot be asked to mount a host directory.
4. The session ends as it always does: its bundle, report and outcome come back over `GET /out/...` into the server's scratch directory (streamed, refused above 256 MiB), and the server's handover does everything it did: clone its own checkout, fetch the bundle into it, scan the diff and every commit, check the branch, and push.

**Where chargehand's own test run happens.** On the runner that ran the session, in a fresh container, as before. The server has no container engine, and that runner already holds the session's bundle, the batch's network with its allowlist, the test services and the caches. It asks the runner for a workspace again, in the same output volume, and the server sends the repository bundle again (the session had that volume writable, so the verification does not rely on what the session left; a bundle is content-addressed, so a changed `source.bundle` can make the verification fail but cannot make the commit mean something else). So the verdict is the laptop's, on its architecture. If the runner is away at that moment the task waits for it as for any call (10 minutes), then fails with the work kept in the output volume and the server's scratch copy.

The runner for a remote machine, as the box's but with a listen address on the private network (see ADR 0058, "A runner on macOS"):

```bash
CHARGEHAND_RUNNER_KEY=... chargehand runner --listen <private-address>:4310 --allowed-hosts <private-address> \
  --images registry.example/chargehand-session@sha256:<digest> --egress-image registry.example/chargehand-session@sha256:<digest> \
  --forwards 4300=server.example.net:4300 --max-bundle-mb 512
```

(`--outside-networks bridge` for the default network.) The runner does not need the server's checkouts, and the server does not need the runner's engine.

## A Mac as a runner

A Mac with OrbStack runs `chargehand runner` natively, as a launchd login agent. What is different from a runner in a container (ADR 0058):

- **The engine is a virtual machine, with its own memory.** OrbStack's `memory_mib` caps it (3072 on the Mac this was written on, against a session's 4096 MiB limit, so nothing starts until it is raised: `orbctl config set memory_mib 10240`, then restart OrbStack). `GET /host` therefore reports the engine's machine, not the Mac: the runner starts a throwaway container of the session image (no network, no mount, no name) and reads its `/proc` (`--host-source engine`, the default off Linux). The session image has to be on the engine already; the install script pulls it.
- **The runner never holds the engine's socket.** A `tecnativa/docker-socket-proxy` container (the same pinned image as the box's compose file, which has an arm64 variant) mounts it read-only and listens on `127.0.0.1:2375`; the runner's `DOCKER_HOST` points there. Same security cost as on the server: the proxy filters by API section, not by request body, so the runner's fixed container template is the real gate.
- **It listens on the private network's address only.** `--listen <address>` takes an address, not `0.0.0.0`; the launcher refuses `0.0.0.0`, `::`, and any address outside 100.64/10, `fd7a:115c:a1e0::/48` and loopback (override: `ALLOW_NON_TAILNET_ADDRESS=yes`), and waits for the address to appear on an interface before it starts (the private network can come up after login).
- **The key is in the Keychain, not in a file.** The launcher reads the generic password `chargehand-runner` when it starts. The plist, the launcher and the configuration hold no secret.
- **The Mac stays awake while a session runs** (`--keep-awake on`): every 10 seconds the runner counts running session containers (not each batch's egress proxy or test services) and, while there is one, holds `caffeinate -i -m -s -w <runner pid>`; it lets go when none runs, and `-w` ends it if the runner dies. `-s` (no system sleep) only holds on AC power, and it is UNKNOWN whether it keeps a MacBook awake with the lid closed and no external display.
- **It drains instead of promising what it cannot keep** (`--drain-on battery,lid`, and the drain file): `GET /host` adds `"state": "draining"` and a reason while the Mac is on battery power, has its lid closed, or `~/.chargehand/runner/drain` exists. The server places no new task there; sessions already there go on. `install-runner.sh drain` and `undrain` make and remove the file. The runner still accepts a start that was already on its way. A Mac that sleeps anyway is an unreachable runner: skipped for new tasks, waited for up to 10 minutes by the sessions it has.

**On the Mac** (with the release that has the arm64 images, and chargehand installed under `~/.chargehand/app` the way the local server's is):

```bash
cd deploy/macos
./install-runner.sh init                       # writes ~/.chargehand/runner.conf
$EDITOR ~/.chargehand/runner.conf              # LISTEN_ADDRESS (tailscale ip -4), SESSION_IMAGE (the index digest), FORWARDS (4300=<server's private address>:4300)
security add-generic-password -a "$USER" -s chargehand-runner -w     # prompts for the runner key: the value of the server's secret item for this runner
orbctl config set memory_mib 10240             # then restart OrbStack
./install-runner.sh install --dry-run          # prints every command, runs none
./install-runner.sh install                    # pulls the image, starts the socket proxy, loads the agent
./install-runner.sh status
```

**On the server**, in the profile (the secret item is a new one; its value is the key above):

```json
"runners": [
  { "name": "box", "url": "http://runner:4310", "api_key_secret": "chargehand-runner-key", "max_sessions": 4 },
  { "name": "mac", "url": "http://<the Mac's private address or name>:4310", "api_key_secret": "chargehand-mac-runner-key",
    "platform": "linux/arm64", "max_sessions": 2, "weight": 0.5, "reserve_mb": 3072, "workspace": "bundle",
    "forwards": { "mcp": "<the server's private address>:4300" } }
]
```

`driven.images` takes the index digest (the box's runner pulls the amd64 variant, the Mac's the arm64 one); with a digest from an amd64-only release, name the Mac's image in `image_variants` instead. The server's port 4300 has to be reachable on the private network (not only on loopback) for the Mac's egress containers, and `http.allowed_hosts` keeps `chargehand-driven`, which a session calls it by.

**On the private network**: allow the server to reach the Mac's runner (TCP 4310) and the Mac to reach the server (TCP 4300; the egress containers' traffic leaves the Mac with the Mac's own address). With Tailscale tags:

```json
{ "action": "accept", "src": ["tag:chargehand-server"], "dst": ["tag:chargehand-runner:4310"] },
{ "action": "accept", "src": ["tag:chargehand-runner"], "dst": ["tag:chargehand-server:4300"] }
```

**Checks**, in this order: from the server, `curl -H "Authorization: Bearer $KEY" http://<mac>:4310/host` shows the engine's memory; from a container on the Mac, `docker run --rm busybox wget -qO- http://<server>:4300/` reaches the server (it answers 401: whether OrbStack's containers reach the private network, and what a VPN client such as WARP does to that path, is UNKNOWN until this passes); `chargehand doctor` on the server shows `runner mac` and `runner mac images`; a one-task batch shows `on runner 'mac'` in its events.
