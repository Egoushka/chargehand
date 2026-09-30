---
title: "Driven writing sessions"
description: "A list of tasks becomes parallel headless Claude Code sessions in containers and a draft pull request per task: what is built, how it is isolated, and what is not wired yet."
order: 10
section: "Guides"
---

Driven sessions ([ADR 0039](../adr/0039-driven-writing-sessions.md)) run a list of tasks as headless Claude Code sessions, one container each, following the [`change` skill](change.md), and end each in a pushed branch `chargehand/<run>` and a **draft** pull request. chargehand never merges and never enables auto-merge. **Status: the parts below are built and tested; the request that starts a batch end to end is not wired yet** (see "Not wired yet"). Turn nothing on in a real profile until it is.

## How a task is isolated

- **One container per task**, started from one fixed template ([`ContainerTemplate`](../../src/Chargehand/Containers/ContainerTemplate.cs)): non-root, read-only root filesystem, no capabilities, `no-new-privileges`, memory, CPU and process limits, one internal network, two named volumes, no Docker socket, no host mount. No value in a request can add a flag, a mount, a network mode or a capability.
- **No route out.** The container sits on an `--internal` Docker network. One egress container per batch is on that network and on an outside one and forwards only TLS connections to an allowlist (port 443, named hosts, and never a name that resolves to a private address). A process that ignores its proxy variables still cannot get out. The proxy sees host names, not content, so an allowlisted registry can still receive data in a URL.
- **The server never holds the Docker socket.** A [runner service](server.md#runner-driven-sessions) does, behind a narrow API. The runner starts only images on its allowlist, by digest, and touches only containers, volumes and networks that carry chargehand's label or prefix. It is still a root-equivalent socket on a rootful engine, which is the security cost of this design.
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

## Not wired yet

- The path from a request with a `driven` block to these parts (the task runner that starts a session, reads its outcome and calls the handover) and `scripts/driven-e2e.sh`.
- The model credential delivery: the design prefers a per-run token that a gateway exchanges for the real credential, so the credential is never in the container. Whether Claude Code accepts that in both modes is half checked (a dummy credential reaches a base URL in both; a real one swapped in is not). Until it is, the fallback puts the credential in the container's environment, where hostile repository code could read and commit it; the diff scan is only a backstop.
- Deployment on the VPS (a compose change in the homelab repository), the session image's pull time there, and whether `--internal` isolates on that engine.
- Whether a model keeps to the skill's steps over a long headless session. The plan measures it (10 sessions, at least 7 must follow the steps in order).
- The subscription's terms for unattended parallel use are unread; the maintainer accepted that risk.

## Configure it

The profile's `driven` block (`schemas` and `profiles/profile.schema.json` document each key): `enabled` (default false), `max_parallel`, `max_parallel_total`, `images` (by digest), `network.allow`, `runner` (URL and the secret item for its key), `push_secret`, and `task_source` (how a tracker item id becomes a goal through a mapped MCP tool). A request adds `driven: { tasks: [{id, ref | goal}], max_parallel, max_tokens_total | max_usd_total }` and `context.repository`.
