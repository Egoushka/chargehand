# 0039. Driven writing sessions

- Status: proposed; decisions 1 to 4 were taken by the maintainer, the rest are the proposer's
- Date: 2026-09-30

## Context

The `code` preset (ADR 0035) writes a branch that passes its tests, but a worker there has no shell and chargehand never pushes. The
`change` skill (`plugins/chargehand/skills/change/SKILL.md`) does the whole job (research, write, test, review, at most 2 fix rounds)
inside a person's Claude Code session. The next step is to run that flow without the person: several tasks at once, each in its own
isolated session, ending in a draft pull request. The design spec (`docs/specs/2026-09-30-driven-sessions-design.md`, "Where it
stands") records the evidence: the server runs in a container with no Docker socket, runs one run at a time, has no cancel and no run
list, and Docker is present on the Mac.

## Options

1. Extend the sandboxed, shell-less writer (ADR 0035 option 2) to several tasks and a push. Keeps ADR 0006 true; but the agent that
   works is the read-only-shell one, feedback stays a turn slower, and the skill's flow would be rebuilt inside chargehand.
2. A full agent session with a shell, inside a sandbox on the host (`sandbox-exec`, `bwrap`). No daemon; but one mechanism per
   operating system, no limits on memory or processes, and the network rule is per-platform.
3. A container per run, headless Claude Code inside it running the skill. One mechanism on both hosts; needs Docker, an image, and a
   way for a containerised server to start containers.

## Decision

Option 3. The maintainer's four decisions, recorded:

1. **Isolation.** One container per run, on the VPS and on a Mac with Docker. No shell runs outside a container.
2. **Handover.** chargehand pushes `chargehand/<run>` and opens a draft pull request. It never merges and never enables auto-merge.
   The credential can push only non-default branches (a per-repository deploy key or a fine-grained token) and is outside the
   container except at push time.
3. **Inside the session.** Headless Claude Code (`claude -p`) runs the `change` skill's steps, so the skill is reused. OpenCode as a
   second runtime is a later goal behind the same interface.
4. **Trigger.** The caller calls `orchestrate` with a list of tasks (tracker item ids or free-text goals); chargehand runs N in
   parallel under a concurrency cap and a spend cap and reports one result per task. Picking work from the tracker's cycle is not in
   scope (roadmap: after 1.0).

What the proposer added, each with its reason in the spec: the server never holds the Docker socket, a **runner service** with a fixed
container template does (a socket mount is root on the host; a path-filtering proxy cannot see the request body); the run container sits on
an `--internal` network and reaches only an allowlist through an egress proxy; the model credential is a per-run token exchanged by the
proxy, falling back to the real credential in the environment, recorded, if a spike shows Claude Code cannot run that way; the session
ends by writing a git bundle and **chargehand** pushes from outside after running the tests itself in a fresh container; the session's
claims go through the existing resolver and support judge; every contract change is additive.

### What this supersedes

- **ADR 0035, decision 1 and option 3's rejection, for driven sessions.** ADR 0035 chose a shell-less worker with chargehand running
  the verifier in `sandbox-exec` or `bwrap` because a sandbox around a process that holds the model's credentials and network was the
  worse trade. This ADR accepts that trade inside a container by removing the two things that made it bad: the container has no route
  out except an allowlist, and it holds no push credential (and, if the spike succeeds, no real model credential). ADR 0035's other
  decisions stand: commits made with hooks off, the verifier's command resolution, the artifact shapes, the admission that a worker can
  edit the tests.
- **The `code` preset keeps working, unchanged.** It stays shell-less, pushes nothing, and uses the platform sandbox. Its tests are the
  regression guard (no task of the plan changes it; every task's `scripts/check.sh` runs them). The platform sandbox
  remains the verifier for `code`; driven sessions verify in a container.
- **ADR 0006's "no `autonomous` preset until sandboxing is designed"** was already relaxed by ADR 0035 for shell-less writers. A driven
  session is the first preset whose worker has a shell; it is permitted only through the `driven` preset, only inside a container, and
  the profile must opt in (`driven.enabled`).
- **ADR 0015's "a person merges" stands, and is now enforced by the credential:** a key that cannot push the default branch, and code
  that opens only draft pull requests.
- **ADR 0023's source-is-read-only rule stands:** the session works in a clone that the runner makes from the read-only source.

### Spike result so far (2026-09-30)

Claude Code 2.1.283 sends a dummy credential to a custom `ANTHROPIC_BASE_URL` in both API-key mode (`x-api-key`) and subscription
mode (`Authorization: Bearer`), so the per-run token design is possible. Not yet shown: a forwarded response with the real credential
swapped in, and the subscription token's refresh behaviour (plan Task 1 finishes it). The VPS engine is Docker 29.6.2, not rootless,
so the runner holds a root-equivalent socket there; that is the security cost of decision "runner service" and is why the runner's
template and review focus are the most scrutinised code in the plan.

### Amendments while building (2026-09-30)

The spec's "As built" section lists them. In short: the server's MCP endpoint is reached through operator-named forwards on the egress container, because the allowlist proxy refuses private addresses (correctly); workspaces and the fresh verification run are built by a no-network helper from chargehand's read-only checkout; a change to CI configuration and a repository with no test command are not pushed unless allowed; the handover never runs git in the session's workspace. No decision above is reopened.

### Adherence not yet measured (2026-10-04)

No real session has run end to end. `scripts/driven-e2e.sh` and `chargehand runs adherence` exist, but the 10-session measurement (five tasks, twice; at least 7 must do research, test, review and at most 2 fix rounds in order) has not been run, so this ADR states no rate. Status stays proposed. If fewer than 7 of 10 follow the steps, record it here and do not call the feature ready.

## Consequences

- A shell exists again, in a container, running repository content and model output. The boundary is the container, its network
  policy and the absence of credentials, not the session's permission rules. Anything the session can read, it can write into the
  branch: the push scan is a backstop, and the deployment's real protection is a key that is scoped, low-limit and revocable.
- The allowlist limits where data goes, not what: the proxy does not inspect TLS. A package registry on the list can still receive
  data in a URL.
- The runner is new security-sensitive code: a small program holding the Docker socket, reviewed as such (plan Task 5's review focus).
- The server's one-run-at-a-time gate must change for batches (plan Task 7).
- **The subscription is the default model credential (maintainer's decision, 2026-09-30), with an API key as the manual fallback.**
  Dollars are not measurable in that mode, so batches are bounded by a token ceiling counted from the stream, plus turns and time; the
  dollar ceiling binds only in API-key mode. Parallel sessions share one plan's limit. Whether unattended parallel server-hosted use is
  within the plan's terms is **unknown** and accepted as a risk by the maintainer; the risk is an action by the provider on the
  account. A rate limit or sign-out ends tasks as retryable and stops the batch; nothing switches to an API key on its own.
  Because the subscription token is long-lived and covers the whole plan, keeping it out of the container (the per-run token design)
  matters more here than for a limited API key; if the spike shows it cannot be kept out, the fallback delivery puts a year-long
  credential where hostile repository code runs, and the maintainer should then reconsider before enabling it.
- The image is another thing to pin, scan and keep at the profile's Claude Code version.
- A session whose model ignores the skill's steps produces a branch that chargehand's own verification and review still judge; the
  skill is followed by instruction, not enforced, so the plan measures adherence (Task 12).
- New error codes, artifact kinds, events, a list schema and three routes are additive; `Chargehand.Contracts` needs a new alpha
  version when released, and the snapshots of the 1.0 stability plan must include them.
- Docker is required for this feature on any host. A Mac without it gets `container_unavailable` and the `code` preset still works.

## Reopen if

The credential spike shows the real credential must live in the container; the push scan finds a real secret in a branch; Docker
cannot be kept on the VPS or the runner cannot be made narrow enough to review; a batch of four cannot run in the VPS's memory;
session adherence to the skill is too low to be useful (Task 12's measured rate); OpenCode's session becomes wanted before 1.0.
