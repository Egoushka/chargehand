---
title: "Driven writing sessions"
description: "A list of tasks becomes parallel headless Claude Code sessions in containers and a draft pull request per task: what is built, how it is isolated, and what is not wired yet."
order: 10
section: "Guides"
---

Driven sessions ([ADR 0039](../adr/0039-driven-writing-sessions.md)) run a list of tasks as headless Claude Code sessions, one container each, following the [`change` skill](change.md), and end each in a pushed branch `chargehand/<run>` and a **draft** pull request. chargehand never merges and never enables auto-merge. **Status: a request with a `driven` block runs the batch, and the parts below are tested with fakes. The end-to-end script exists, but no real session has been run through it, so nothing here is measured** (see "Not done yet"). Turn nothing on in a real profile until it has.

## How a task is isolated

- **One container per task**, started from one fixed template ([`ContainerTemplate`](../../src/Chargehand/Containers/ContainerTemplate.cs)): non-root, read-only root filesystem, no capabilities, `no-new-privileges`, memory, CPU and process limits, one internal network, two named volumes, no Docker socket, no host mount. No value in a request can add a flag, a mount, a network mode or a capability.
- **No route out.** The container sits on an `--internal` Docker network. One egress container per batch is on that network and on an outside one and forwards only TLS connections to an allowlist (port 443, named hosts, and never a name that resolves to a private address). A process that ignores its proxy variables still cannot get out. The proxy sees host names, not content, so an allowlisted registry can still receive data in a URL.
- **The server never holds the Docker socket.** A [runner service](server.md#runner-driven-sessions) does, behind a narrow API. The runner starts only images on its allowlist, by digest, and touches only containers, volumes and networks that carry chargehand's label or prefix. The task and the results move through a session's output volume by a helper container with no network, started from the same image, that writes only `task.json` and reads only the bundle, the report and the outcome. It is still a root-equivalent socket on a rootful engine, which is the security cost of this design.
- **A session calls chargehand back** (research, review) with a run-scoped token, through a forward the batch's egress container carries; the token opens only the presets `default` and `review` on its task's repository and commit, within the task's caps ([run tokens](server.md#run-tokens-driven-sessions)).
- **The credential that pushes is never in a container.** chargehand fetches the session's bundle into a scratch clone of its own checkout, checks the branch, and pushes from there.

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

It builds a synthetic repository with a local bare remote that refuses every branch except `chargehand/*`, starts its own server on port 4390 with a throwaway profile (`driven.enabled` true, `max_parallel` 2, the key read from the environment and never written to a file) and a stub of the GitHub endpoint that opens draft pull requests, then runs three tasks: one easy, one that must implement a function and remove a test's skip, and one that cannot pass. It checks that two draft pull requests were recorded, one task failed, the batch ended `failed` with `tasks_incomplete`, the default branch did not move, a cancelled task leaves no container and no pull request, and neither the push token nor the model key appears in any file the run made. The task that cannot pass is two contradictory tests; a model that edits them has dodged it, and the script then fails `impossible_tests_untouched` (it diffs the branch against the base for test files). `CHARGEHAND_E2E_REPO=node` swaps the Python repository for a small Node one (`node --test`) with the same three tasks, and `CHARGEHAND_E2E_TASKS=<file>` replaces the tasks with a JSON list of `{id, goal}` (the default stays Python). The environment variables are listed at the top of the script. Two switches in the CLI make the local remote and the stub possible and exist for this script only (`CHARGEHAND_E2E_GITHUB_API`, `CHARGEHAND_E2E_LOCAL_REMOTE`); they are not profile keys.

With `CHARGEHAND_E2E_STREAMS=<dir>` the script copies each task's stored session stream (`stream.jsonl`, secrets redacted) out of its output volume and runs `chargehand runs adherence <dir>/*.jsonl`, which reports per session whether research, a write, a test run and a review came in that order with at most 2 fix rounds, and whether at least 7 of 10 sessions did (fewer than 10 is reported as not conclusive). Ten sessions means running the script until ten streams are collected, for example the three tasks over four runs, or the plan's five tasks twice; point the helper at the combined directory. The cancel case waits for the task's own container before it cancels, then allows 20 seconds for no container to be left.

## Not done yet

- **Adherence is not at the bar, and the feature is not ready.** Measured twice with the script on the subscription token. First, 9 sessions on the Python repository over three runs: 7 of 9 (after the session was told the repository path and base commit it must pass to `orchestrate`, `CHARGEHAND_REPOSITORY_PATH` and `CHARGEHAND_BASE_COMMIT`; before, 1 of 3), 50,443 input plus output tokens. Second, 12 sessions over four runs, two on a Node repository and two on the Python one: 7 of 12 (Node 4 of 6, Python 3 of 6); the two passable tasks 7 of 8, the task that cannot pass 0 of 4 (the model researches and stops without writing); 76,842 input plus output tokens (5.68 million with cache). The plan's bar is 7 of 10; both repositories are tiny and the goals trivial. Details in [ADR 0039](../adr/0039-driven-writing-sessions.md).
- Live dollars: tokens are live, dollars are not. The in-container driver rewrites `session-usage.json` in `/out` (input plus output tokens) as the stream grows, the host polls it through the out-volume read, and the scheduler stops a task over its token cap, and every running task once the batch total passes the batch cap (the task ends `cost_cap_reached`). The dollar cost arrives only in the stream's final result, so the per-task dollar cap still binds through the session's own `--max-budget-usd`. The script sets `network.mcp_forward` to `host.docker.internal:<port>` (Docker Desktop, OrbStack), so a session can call the throwaway server for research and review.
- The credential spike: the model credential delivery: the design prefers a per-run token that a gateway exchanges for the real credential, so the credential is never in the container. Whether Claude Code accepts that in both modes is half checked (a dummy credential reaches a base URL in both; a real one swapped in is not). Until it is, the fallback puts the credential in the container's environment, where hostile repository code could read and commit it; the diff scan is only a backstop.
- Deployment on the VPS, which is the maintainer's decision and not done by any agent (a compose change in the homelab repository), the session image's pull time there, and whether `--internal` isolates on that engine.
- Whether a model keeps to the skill's steps over a long headless session or a real repository: the figures above are for tiny goals only.
- The subscription's terms for unattended parallel use are unread; the maintainer accepted that risk.

## Configure it

The profile's `driven` block (`schemas` and `profiles/profile.schema.json` document each key): `enabled` (default false), `max_parallel`, `max_parallel_total`, `images` (by digest), `network.allow`, `runner` (URL and the secret item for its key), `push_secret`, and `task_source` (how a tracker item id becomes a goal through a mapped MCP tool). `network.outside` and `network.mcp_forward` (`host:port` of the chargehand server as the egress container reaches it) let a session call chargehand for research and review. `images[0]` is the session image and the egress image. Keep `enabled` false in any shared profile. A request adds `driven: { tasks: [{id, ref | goal}], max_parallel, max_tokens_total | max_usd_total }` and `context.repository`.
