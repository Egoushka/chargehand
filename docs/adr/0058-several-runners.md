# 0058. Several runners: a second machine runs driven sessions beside the server's own

- Status: accepted
- Date: 2026-10-13

## Context

A driven batch runs on one engine. `driven.runner` names one runner service ([ADR 0039](0039-driven-writing-sessions.md)), and everything the server does for a batch assumes that engine is the only one: the batch network and its egress container (`BatchNetwork`), the test services ([ADR 0048](0048-shared-services-for-driven-sessions.md)), the sweep after a restart ([ADR 0043](0043-driven-batches-survive-a-server-restart.md)), the memory admission ([ADR 0045](0045-governed-sessions.md), `HostAdmission` reads one `GET /host`) and the verification container.

The owner has a second machine that can run sessions: a laptop (Apple M1, 8 cores, 16 GB, OrbStack as its Docker engine, so linux/arm64) that is reachable from the server only over a private network, sleeps, goes offline, and whose path flaps behind a VPN client. The server and its runner stay where they are. The laptop already runs a chargehand server of its own for read-only work; that is not touched.

What is shared-disk and what is not, from the code:

- **The workspace is.** The helper that fills a session's volume mounts the server's checkout of the pinned commit read-only (`ContainerTemplate.WorkspaceArgs`, `-v <source>:/src:ro`), and `RunnerPolicy.RefuseWorkspace` accepts only a path under the runner's `--source-roots`. The checkout and the runner must see the same disk.
- **The result is not.** `EngineSessionVolumes.FetchOutputAsync` already copies the bundle, report and outcome out of the output volume over the runner's HTTP API (`GET /out/...`) into the server's scratch directory. `Handover` clones the server's own checkout, fetches that bundle into the clone, scans it and pushes from the server. The seed bundle of a continuation goes the other way over `PUT /out/...`. Only the verification container needs the engine: it mounts the session's output volume and a fresh workspace.
- **The egress forward is not.** The forwards (`listen-port=host:port`) are sent in each `POST /egress` and the runner starts only the ones it was configured for. Where "the server" is for a session depends on the runner's network, not on the server's.
- **Admission already reads the runner, not the server.** `GET /host` is the runner's `MemAvailable`, load and processors.
- **Images are linux/amd64 only.** `release.yml` runs `docker build`, so each tag is a single-platform image. `images/session/Dockerfile` already has an arm64 branch for Node.

Assumptions, not measured: an amd64 image on OrbStack (Rosetta) is too slow for a build and a test run, which is why the second machine needs arm64 images and not emulation; and a bundle of a repository's history is small enough to send per session (it is what a clone would fetch).

## Options

1. **A second chargehand server on the laptop.** Nothing to build. Two run logs, two budgets and account lists, the caller picks the machine by hand, and one batch cannot use both.
2. **A second runner over a shared disk** (the server's checkouts exported over a network file system). No transfer protocol. A bind mount from a network mount through OrbStack, over a path that flaps, is the slowest and least reliable thing in the design, and the workspace helper clones from it at every start. The first VPS batch already showed that bind-mounted checkouts break on ownership (driven guide, "First deployment, measured").
3. **Several runners; the server sends the repository to a remote one as a git bundle** and takes the result back as it does today. One server, one run log, one budget; a runner on the server's own disk keeps today's path.
4. **A pull model: the laptop asks the server for work.** It needs no inbound port on the laptop and survives NAT. It is a queue protocol, a reversed authentication and a second way to learn a task's credentials, and nothing here needs it while the private network works.

## Decision

Option 3. Each point below is built in the change named in brackets; the four changes ship in order.

**1. Profile (this change).** `driven.runner` stays as it is and means one runner named `default`. A new `driven.runners` lists runners:

```json
"runners": [
  { "name": "box", "url": "http://runner:4310", "api_key_secret": "runner-key", "max_sessions": 4 },
  { "name": "laptop", "url": "http://laptop.example.net:4310", "api_key_secret": "laptop-runner-key",
    "platform": "linux/arm64", "max_sessions": 2, "weight": 0.5, "reserve_mb": 4096,
    "workspace": "bundle", "forwards": { "mcp": "server.example.net:4300" } }
]
```

`name` is required and unique. `max_sessions` (default: no limit beyond the other limits), `platform` (`linux/amd64`, the default, or `linux/arm64`), `weight` (above 0, default 1: how much the runner's free memory counts when two runners are compared), `reserve_mb` (replaces `driven.admission.reserve_mb` for this runner: a laptop someone is using keeps more for itself), `workspace` (`shared`, the default: the runner reads the server's checkouts as today; `bundle`: the server sends the repository) and `forwards` (the address of the server as that runner's egress containers reach it: `mcp`, `model_url`, `otlp_url` and `outside` each replace the same key of `driven.network` for that runner and inherit it when left out). `runner` together with `runners` is refused. A `bundle` runner needs `forwards.mcp` while `driven.network.mcp_forward` is set, and `forwards.model_url` or `forwards.otlp_url` while those are set to an `http` address: the inherited value is a name that resolves only on the server's Docker network, and a silent mismatch is a session that cannot call back. A profile with one runner behaves as before, whichever of the two keys names it, and a profile written before this ADR needs no change.

`driven.image_variants` maps an image reference in `driven.images` or `driven.services.images` to the reference to use on another platform (`{ "<ref>": { "linux/arm64": "<ref>" } }`). A runner uses the image as written unless the map has an entry for its platform. A multi-platform index digest needs no entry, and a digest from before arm64 images existed needs one only if its runner is not amd64. The runner's own `--images` and `--egress-image` must list what it will be asked to start; the platform decides which reference the server asks for, and nothing else about the runner.

**2. Choosing a runner for a task [scheduling].** A pool holds the runners. For each task the scheduler asks it for one that is:

- *healthy*: its `GET /host` answered within 3 seconds. A snapshot is reused for 5 seconds. A runner that did not answer is skipped for 15 seconds, then 30, doubling to 2 minutes, and asked again after; a runner that is skipped never fails a batch or a task;
- *not draining*: the snapshot's `state` is not `draining` (point 8);
- *under its `max_sessions`*, counting the sessions this server has running on it;
- *admitted on its own host*: the rule of ADR 0045 evaluated against that runner's snapshot, its `reserve_mb` and the starts this server made on it in the last minute. A runner that cannot report memory is admitted by count and is the last choice.

Among the runners that pass, the one with the most free memory after the task's request, multiplied by its `weight`, wins; equal scores go to the runner listed first. If none passes and something of this server is running on some runner, the task waits for it to finish and asks again. If none passes and nothing of this server is running anywhere, the task is admitted on the runner with the most free memory, as ADR 0045 admits one task on an idle host, so a batch never waits on memory that only other tenants hold. If no runner is healthy and not draining for 15 minutes, the batch's remaining tasks end `not_started` with `container_unavailable` and the reason. A task waits with one `session_progress` event naming why each runner was passed over.

The global `max_parallel_total` and the batch's `max_parallel` still bound the tasks as a whole; the runners only decide where.

**3. A task stays on its runner [scheduling].** All of a task's sessions (the continuations of ADR 0044, the CI fix sessions of ADR 0046) and its handover run on the runner that took its first session: the output volume that holds its bundle is there. The runner is written to the batch state at placement, so a restarted server takes the task up on the same runner, and waits for that runner if it is unreachable. A continuation whose runner cannot start it ends the task with the partial handover of the work so far, as a continuation that cannot start does today. A different task, a task of a later batch that continues an earlier one, and a task that depends on another may go to any runner, and read the earlier task's bundle from the runner it ran on; the runner is in the `driven-batch` artifact's task rows. If the first session cannot start because its runner was unreachable, the task is placed again, at most twice, and the runner is skipped as in point 2. A session that is running when its runner stops answering is waited for up to 10 minutes of continuous silence (a laptop that sleeps, a path that flaps); past that the task fails `container_unavailable` and its session is swept when the runner answers again.

**4. A batch's infrastructure per runner [scheduling].** The batch network, its egress container and its test services are made on a runner when the first task is placed there, with that runner's forwards, and removed with the batch. With one runner they are made when the batch starts, as now. Cache volumes and the output volumes are the runner's own. The sweep at a server start runs on every runner that answers, with the same rule (a resource of no resumed batch goes); a runner that does not answer is swept before its first task, so a session left there does not take its container count.

**5. Remote workspace and result [remote workspace].** For a `bundle` runner the server makes one bundle of the pinned commit from its checkout (`git bundle create`, cached for the batch) and sends it with `PUT /out/<run>/source.bundle`, a new input file name next to `task.json` and `seed.bundle`. The body is streamed from disk to the helper's stdin and never held whole in memory on either side, under a size cap: the request states its length and the runner (`--max-bundle-mb`, default 512) refuses a bigger one with 413 before reading it; the server itself makes no bundle over 4 GiB. A new route, `POST /workspace/bundle`, runs the workspace helper with that output volume mounted read-only in place of a source directory, and clones from the bundle; the commit and branch are checked as for a path, and the route needs no `--source-roots`. The bundle is content-addressed: a session that rewrites `source.bundle` in its own volume cannot make the pinned commit mean something else; the worst it does is fail the verification. The verification asks for a workspace again in that volume and the bundle is sent again, so it does not rely on what the session left there; the server keeps the file per commit for a day, so it is made once. The result comes back as it does now, streamed from `GET /out/...` under a cap, into the server's scratch directory, where `Handover` does everything it did: clone the checkout, fetch the bundle, scan, verify, push. Every call carries the runner's bearer key; across a network that is not private the runner is put behind TLS, as the server is.

**6. Where verification runs [remote workspace].** chargehand's own run of the tests still happens in a fresh container before anything is pushed, and it runs **on the session's runner**. The reasons: the server holds no engine socket by design, so "the server's" can only mean another runner; the bundle is already in that runner's output volume, and verifying elsewhere means sending it again, plus the repository; the batch network (the allowlist), the services and the cache volumes of the batch are on that runner. The cost is that the verdict comes from the machine that ran the session, on its architecture, and that a runner that goes away between the end of the session and the end of the verification fails the task with the work kept in the output volume and the server's scratch copy. The trust is the same as before: a runner that lies about a verdict can also lie about a session, and the owner runs all of them. Verification on a different runner, fed from the server's copy of the bundle, is not built (see Reopen).

**7. Images [images, Mac setup].** The release workflow publishes `chargehand-session` and the JDK variant for linux/amd64 and linux/arm64 as one index each. They are built natively: the release job pushes amd64, and a job after it (behind its approval, so no second one) builds arm64 on an arm64 runner and creates the indexes. Emulation is out because the image runs dotnet, node and npm during its build, and .NET under QEMU is the known way to a crash that CI cannot show here. Both arm64 images were built natively on the Mac this was written on (aarch64, Node 26.10.0 from the pinned arm64 checksum, .NET 10.0.401, JDK 21.0.12, Gradle 9.8.1, Claude Code 2.1.283). Pinning the index digest in `driven.images` serves both runners. The workflow had no provenance or signing step before, and has none now.

**8. Reporting [scheduling].** Every run event about a task (`container_started`, `session_progress`, `verify_finished`, `pushed`, `pr_opened`, `task_finished`) carries `runner` (additive to `run-status/v1`), and `container_started` says `on runner '<name>'`. The `driven-batch` artifact's task rows carry `runner`. Both are present when the profile uses `driven.runners`; a profile with `driven.runner` reports as before.

**9. A runner on macOS [Mac setup].** `chargehand runner` runs there natively as a launchd login agent, with OrbStack's engine behind a socket proxy container bound to loopback, as the compose file does on the server. It listens only on the address it is given (the private network's, never `0.0.0.0` on a shared one). Three additions to the runner:

- `GET /host` reports the engine's machine, not the Mac (`--host-source engine`, the default off Linux). On the Mac this was written on, OrbStack's `memory_mib` is 3072 and the engine VM has 4 of the 8 processors, against a session limit of 4096 MiB: the Mac's own free memory says nothing about what a session can use, and a figure taken from `vm_stat` would admit sessions the engine cannot hold. The runner starts a throwaway container of the session image (no network, no mount, no name, no label, so it collides with nothing after a restart and does not count against `--max-containers`) that prints its `/proc/meminfo`, `loadavg`, memory pressure and `nproc`, and keeps the answer 5 seconds. The owner raises `memory_mib` first.
- `--keep-awake on`: while a session container runs, the runner holds `caffeinate -i -m -s -w <its pid>` (`-w` ends it with the runner) and releases it when none does. It counts session containers only; a batch's egress proxy and test services last the whole batch and are no reason to keep a laptop awake.
- `state`: `GET /host` adds `"state": "draining"` and a reason when the machine is on battery, its lid is closed, or the drain file (`--drain-file`) exists, each only if enabled with `--drain-on battery,lid`; otherwise the member is absent. A draining runner takes no new task and keeps the ones it has. The runner still accepts a `/start` while draining: a refusal in the middle of a placement would fail a task the pool had already chosen a runner for.

Whether `caffeinate -s` keeps a MacBook awake with the lid closed and no external display is UNKNOWN; the design does not depend on it: the lid-closed rule drains, and the outage grace of point 3 covers a sleep that happens anyway.

## Consequences

One server drives sessions on two machines with one run log, one budget and one set of accounts. The box's profile does not change until it adds the second runner, and a single-runner profile follows the same code path as today (checked by the existing tests, which are not edited).

The laptop's sessions run on arm64: a test that depends on the architecture can pass there and fail in CI, and the task's verification says on which runner it ran. The laptop's batch network is on its own engine and calls the server over the private network through a forward; if that path is down, the session cannot call back for research or review and stops and asks, as it does when the callback is not configured.

A bundle per session is transferred for a remote runner (a continuation, a CI fix session and each verification send it again; a source cache volume per commit on the runner would send it once, and is the first thing to build if the transfers hurt). This costs time on a repository with a long history. There is no shallow option: `git bundle create` does not work from a shallow repository, and a session needs the history `git log` shows.

The per-runner admission does not know what the laptop's owner is doing: `reserve_mb` and `weight` are the owner's guess, and a session started just before the owner opens something heavy is slowed or killed by the engine's memory limit like any other.

Not decided here: how the runners' hosts are secured on the private network (a tailnet ACL or a firewall rule that lets the server reach the runner's port and the runner's engine reach the server's callback port) beyond what the runners guide lists.

## Reopen if

A session's verification disagrees with CI often enough on the laptop to matter (verify on an amd64 runner from the server's copy of the bundle); the runner on the laptop is unreachable at the end of sessions often enough that tasks fail between the session and the handover (verify elsewhere, fed from the server's copy); a bundle of a real repository is too large or too slow over the private network (a shallow source with a depth setting, or a pull of the repository by the runner from the remote with a read-only token); the private network cannot take an inbound connection to the laptop reliably (the pull model, option 4); a third kind of runner appears (Linux on a laptop, a cloud host) and the settings above need a field for it.
