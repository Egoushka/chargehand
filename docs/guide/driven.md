---
title: "Driven writing sessions"
description: "A list of tasks becomes parallel headless Claude Code sessions in containers and a draft pull request per task: what is built, how it is isolated, and what is not wired yet."
order: 10
section: "Guides"
---

Driven sessions ([ADR 0039](../adr/0039-driven-writing-sessions.md)) run a list of tasks as headless Claude Code sessions, one container each, following the [`change` skill](change.md), and end each in a pushed branch `chargehand/<run>` and a **draft** pull request. chargehand never merges and never enables auto-merge. **Status: a request with a `driven` block runs the batch, and `scripts/driven-e2e.sh` has run it with a real model on synthetic repositories and on a real TypeScript-on-Node repository; it is deployed on one VPS and has run there once, to a draft pull request; the model credential is still delivered in the container's environment** (see "Not done yet"). Keep `driven.enabled` false in a real profile until the credential gateway exists.

## How a task is isolated

- **One container per task**, started from one fixed template ([`ContainerTemplate`](../../src/Chargehand/Containers/ContainerTemplate.cs)): non-root, read-only root filesystem, no capabilities, `no-new-privileges`, memory, CPU and process limits, one internal network, two named volumes, no Docker socket, no host mount. No value in a request can add a flag, a mount, a network mode or a capability.
- **No route out.** The container sits on an `--internal` Docker network. One egress container per batch is on that network and on an outside one and forwards only TLS connections to an allowlist (port 443, named hosts, and never a name that resolves to a private address). A process that ignores its proxy variables still cannot get out. The proxy sees host names, not content, so an allowlisted registry can still receive data in a URL.
- **The server never holds the Docker socket.** A [runner service](server.md#runner-driven-sessions) does, behind a narrow API. The runner starts only images on its allowlist, by digest, and touches only containers, volumes and networks that carry chargehand's label or prefix. The task and the results move through a session's output volume by a helper container with no network, started from the same image, that writes only `task.json` and reads only the bundle, the report and the outcome. It is still a root-equivalent socket on a rootful engine, which is the security cost of this design.
- **A session calls chargehand back** (research, review) with a run-scoped token, through a forward the batch's egress container carries; the token opens only the presets `default` and `review` on its task's repository and commit, within the task's caps ([run tokens](server.md#run-tokens-driven-sessions)).
- **The credential that pushes is never in a container.** chargehand fetches the session's bundle into a scratch clone of its own checkout, checks the branch, and pushes from there.

A session's and the verification run's `/tmp` is a `noexec` tmpfs, so a repository whose tests write a script there and run it fail with `EACCES`. The template stays as it is; the repository names a directory under `/work` instead, in its verification command: `context.verify: ["sh","-c","mkdir -p /work/.tmp && export TMPDIR=/work/.tmp && npm ci && scripts/check.sh"]`. Measured on a TypeScript-on-Node repository: 3 of 217 tests failed on `spawn ... EACCES` with the default `/tmp`, 0 with `TMPDIR` in `/work`.

## What happens after a session

chargehand checks the branch itself and pushes only if every check passes:

| check | refused with |
|---|---|
| the session made a bundle | `session_failed` |
| the branch descends from the base commit and changes something | `session_failed` |
| the diff carries no secret-shaped text, none of the credentials the session had, no secret-like file names | `session_failed`, naming the kind and the path, never the value |
| the diff changes no CI configuration (`.github/workflows`, `.gitlab-ci.yml`, ...) unless allowed, because pushing such a branch can run it with the repository's secrets | `session_failed` |
| the repository's tests pass in a **fresh** container on the bundle's branch (chargehand's own run; the session's claim is kept apart) | `verification_failed`; a repository with no test command is not pushed unless allowed |
| the push of `chargehand/<run>` (one ref, never forced) is accepted by a credential that cannot push the default branch | `push_rejected` |
| the draft pull request opens | `pr_failed`, with the pushed branch named |

A green draft pull request is evidence that a command passed in a container, not that the change is right. The session can still edit the tests or the build script so the command passes; the `verification` artifact lists those paths.

## Watching a batch

While a batch runs, its own run (`GET /v1/runs/{batch}`, a `202` with `run-status/v1`, and `GET /v1/runs/{batch}/events`) carries each task's steps, every event with `task_id` and a `detail` line such as `task t1: 48200 tokens, 12 turns, stage write`:

| Event | When | Fields |
|---|---|---|
| `container_started` | the task's session container is up | |
| `session_progress` | the session's tally changed since the last poll (every 5 s) | `tokens` (input plus output so far), `turns` (assistant messages), `stage` (`research`, `write`, `test` or `review`: the step of the latest tool call that names one, the order `chargehand runs adherence` checks) |
| `verify_finished` | chargehand's own test run ended, pass or fail | |
| `pushed` | the branch is pushed | `branch` |
| `pr_opened` | the draft pull request is open | `pr_url` |
| `task_finished` | the task ended, after the batch's caps had their say | `branch` and `pr_url` when there are some, `tokens` in all; `detail` names the state |

A task's own run id is known only when it ends, so `GET /v1/runs/{task}` is `404` until then; follow the batch. `turns` and `stage` come from the session image's driver: an image built before they existed reports `tokens` only.

## The result

Each task has its own `result/v1` under its own run id (`GET /v1/runs/{id}`); the batch result lists them. Its claims are the session's, cited against the pushed commit and put through the same evidence resolver and [support check](support-and-signing.md) as any result. Artifacts: `branch`, `pull-request`, `verification` (chargehand's run), `session-tests` (the session's claim), `review` (the nested runs), `session-log` (a reference and a hash, never inline). A batch is `completed` only when every task ended in a draft pull request; otherwise it is `failed` with `tasks_incomplete` naming the tasks that did not.

## Limits and the kill switch

`max_parallel` (default 2, never above 4), and two ceilings because a subscription has no dollar price: a **token** ceiling that binds in both credential modes, and a **dollar** ceiling that binds with an API key. A batch starts no task it could not afford, and stops running tasks that overshoot its cap by more than one task's cap. A rate-limited or unavailable subscription stops new starts and says how to switch the profile to an API key; nothing switches on its own. Every session also ends on no progress, a repeated identical tool call, too many turns, or a wall clock.

`POST /v1/runs/{id}/cancel` cancels a run; `POST /v1/halt` cancels every run and refuses new ones until `POST /v1/resume`; `GET /v1/runs` lists runs with state, cost, branch and pull-request link. `chargehand runs kill --all` removes the containers by label with no server. Cancel never pushes.

## Try it end to end

`scripts/driven-e2e.sh` runs the whole path against a real model, by hand, never in CI. It needs Docker, the .NET SDK, python3, `claude` on `PATH` at the version inside the session image, a session image pushed to a registry (build `images/session/Dockerfile`; the profile takes the image by digest), and an Anthropic API key with a **low spend limit set in the console**:

```bash
export CHARGEHAND_E2E_MODEL_KEY=<capped key>
export CHARGEHAND_E2E_IMAGE=<registry>/<image>@sha256:<digest>
scripts/driven-e2e.sh
```

It builds a synthetic repository with a local bare remote that refuses every branch except `chargehand/*`, starts its own server on port 4390 with a throwaway profile (`driven.enabled` true, `max_parallel` 2, the key read from the environment and never written to a file) and a stub of the GitHub endpoint that opens draft pull requests, then runs three tasks: one easy, one that must implement a function and remove a test's skip, and one that cannot pass. It checks that two draft pull requests were recorded, one task failed, the batch ended `failed` with `tasks_incomplete`, the default branch did not move, a cancelled task leaves no container and no pull request, and neither the push token nor the model key appears in any file the run made. The task that cannot pass is two contradictory tests; a model that edits them has dodged it, and the script then fails `impossible_tests_untouched` (it diffs the branch against the base for test files). `CHARGEHAND_E2E_REPO=node` swaps the Python repository for a small Node one (`node --test`) with the same three tasks, and `CHARGEHAND_E2E_TASKS=<file>` replaces the tasks with a JSON list of `{id, goal}` (the default stays Python). `CHARGEHAND_E2E_REPO=<path of a git checkout>` runs on a real repository instead: the script clones its committed history (no untracked files, no hooks), makes its current branch `main` of the local remote and never touches the checkout's own remote; it then needs `CHARGEHAND_E2E_TASKS` and `CHARGEHAND_E2E_VERIFY`, the verification command as a JSON argument vector (for a Node project with dependencies, `["sh","-c","npm ci && scripts/check.sh"]`; the registries the preset allows are reachable through the egress container). The environment variables are listed at the top of the script. Two switches in the CLI make the local remote and the stub possible and exist for this script only (`CHARGEHAND_E2E_GITHUB_API`, `CHARGEHAND_E2E_LOCAL_REMOTE`); they are not profile keys.

With `CHARGEHAND_E2E_STREAMS=<dir>` the script copies each task's stored session stream (`stream.jsonl`, secrets redacted) out of its output volume and runs `chargehand runs adherence <dir>/*.jsonl`, which reports per session whether research, a write, a test run and a review came in that order with at most 2 fix rounds, and whether at least 7 of 10 sessions did (fewer than 10 is reported as not conclusive). It prints two numbers: `adherence` (the sessions that followed) and `honest_stop` (sessions that did research, a write and a test run in order, made no review and edited no test file: a task that could not be done and was stopped with a reason). `honest_stop` is not counted in the 7 of 10 bar. Ten sessions means running the script until ten streams are collected, for example the three tasks over four runs, or the plan's five tasks twice; point the helper at the combined directory. The cancel case waits for the task's own container before it cancels, then allows 20 seconds for no container to be left.

## Not done yet

- **First deployment, measured (2026-10-05).** On a VPS (a runner and a socket proxy in front of the engine, the release images by digest, a private target repository): the first batch, one test-only task, ended `completed` on the fourth attempt, with a draft pull request on the private repository (one file, +27 lines, base the pinned commit), chargehand's own run of the repository's check in a fresh container exiting 0 in 42 s, 9,560 tokens. Each earlier attempt found one defect that no run on a Mac or with fakes could show: (1) the workspace helper was refused the checkout (`dubious ownership`; a Mac's bind mounts hide the owner) and a 404 on cleanup hid that error; (2) the runner's egress request carried no forward, so a session's call back to the server was refused; (3) the host's policy rejected a raw socket mount, so a socket proxy now sits in front (it cannot see request bodies, so the runner's own API stays the gate); (4) `repository_roots` did not cover the checkouts a session names. All four are fixed and in the [server guide](server.md#runner-driven-sessions). Still open: the credential gateway; the per-task output volume is kept (the stream is read from it) and nothing removes it, about 36 to 304 KB per task; and the server could accept its own checkouts for a run token's calls without the roots entry.
- **A real repository, measured (2026-10-04).** `scripts/driven-e2e.sh` on a clone of a TypeScript-on-Node repository (`npm ci && scripts/check.sh` as the verify command), five tasks over two batches: 10 of 10 sessions followed the steps (research, write, test, review, one fix round). In the second batch the four tasks that can pass each ended in a draft pull request on the local remote (14 to 24 added lines, one source file and its test, or the test alone), and the task that cannot pass (its goal contradicts an existing test) ended `verification_failed` with no test edited. In the first batch no task reached a pull request, because the repository's tests run a script from `/tmp`, which is `noexec` (see above); the sessions themselves followed the steps. 101,000 input plus output tokens over the 10 sessions. One repository, five goals; not a rate for other repositories.
- **Adherence measured, and the feature is still not ready.** After the skill and driver prompt were made explicit about the order (research, write, test, review; a best attempt before stopping on a task that cannot be done), two sets of 12 sessions over four runs each (two Node, two Python) all followed the steps. Set A, with the instruction as a bullet in the skill's driven section (interim wording): 12 of 12, `honest_stop` 0, 74,206 input plus output tokens (about 6.4 million with cache). Set B, with the committed wording (steps 4 and 7 of the skill): 12 of 12, `honest_stop` 0, 72,442 tokens (about 6.6 million with cache). `cancel_leaves_no_container` passed in all eight runs. The two repositories are tiny and the goals trivial. The earlier measurements follow.
- **Earlier, before the change:** Measured twice with the script on the subscription token. First, 9 sessions on the Python repository over three runs: 7 of 9 (after the session was told the repository path and base commit it must pass to `orchestrate`, `CHARGEHAND_REPOSITORY_PATH` and `CHARGEHAND_BASE_COMMIT`; before, 1 of 3), 50,443 input plus output tokens. Second, 12 sessions over four runs, two on a Node repository and two on the Python one: 7 of 12 (Node 4 of 6, Python 3 of 6); the two passable tasks 7 of 8, the task that cannot pass 0 of 4 (the model researches and stops without writing); 76,842 input plus output tokens (5.68 million with cache). The plan's bar is 7 of 10; both repositories are tiny and the goals trivial. Details in [ADR 0039](../adr/0039-driven-writing-sessions.md).
- Live dollars: tokens are live, dollars are not. The in-container driver rewrites `session-usage.json` in `/out` (input plus output tokens) as the stream grows, the host polls it through the out-volume read, and the scheduler stops a task over its token cap, and every running task once the batch total passes the batch cap (the task ends `cost_cap_reached`). The dollar cost arrives only in the stream's final result, so the per-task dollar cap still binds through the session's own `--max-budget-usd`. The script sets `network.mcp_forward` to `host.docker.internal:<port>` (Docker Desktop, OrbStack), so a session can call the throwaway server for research and review.
- The credential spike: the model credential delivery: the design prefers a per-run token that a gateway exchanges for the real credential, so the credential is never in the container. Whether Claude Code accepts that in both modes is half checked (a dummy credential reaches a base URL in both; a real one swapped in is not). Until it is, the fallback puts the credential in the container's environment, where hostile repository code could read and commit it; the diff scan is only a backstop.
- Deployment on the VPS, which is the maintainer's decision and not done by any agent (a compose change in the homelab repository), the session image's pull time there, and whether `--internal` isolates on that engine.
- Whether a model keeps to the skill's steps over a long headless session or a real repository: the figures above are for tiny goals only.
- The subscription's terms for unattended parallel use are unread; the maintainer accepted that risk.

## Configure it

The profile's `driven` block (`schemas` and `profiles/profile.schema.json` document each key): `enabled` (default false), `max_parallel`, `max_parallel_total`, `images` (by digest), `network.allow`, `runner` (URL and the secret item for its key), `push_secret`, and `task_source` (how a tracker item id becomes a goal through a mapped MCP tool). `network.outside` and `network.mcp_forward` (`host:port` of the chargehand server as the egress container reaches it) let a session call chargehand for research and review. `images[0]` is the session image and the egress image. Keep `enabled` false in any shared profile. A request adds `driven: { tasks: [{id, ref | goal}], max_parallel, max_tokens_total | max_usd_total }` and `context.repository`.
