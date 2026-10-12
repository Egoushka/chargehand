# 0043. Driven batches survive a server restart

- Status: accepted
- Date: 2026-10-12

## Context

A driven batch ([ADR 0039](0039-driven-writing-sessions.md)) runs for hours: one session container per task, each ending in a draft pull request. Any server restart (a deploy, an out-of-memory kill) marked every unfinished batch `lost` ([ADR 0018](0018-callable-interface-http-mcp-run-store.md)) and left session containers, egress containers, networks and volumes behind, which then filled the runner's container cap (HTTP 429, "N session containers exist already"). The owner has to deploy while batches run.

What already helps: session containers are started `--detach` without `--rm`, so they outlive the server; every name is fixed by the run or batch id (`chargehand-<run>`, `chargehand-egress-<batch>`, `chargehand-net-<batch>`, `chargehand-work-<run>`, `chargehand-out-<run>`); the run log is a persistent volume on the box. What is lost with the process: the batch's request and task list, which task is at which step, the egress container's model-token key, and the run-token key.

## Options

1. A database for run state. Heavy for one process; the run log is the only store today.
2. Rebuild state from the run log and Docker alone. The run log has no record of a batch's tasks until each task ends, and the egress key exists nowhere else.
3. A JSON state file per unfinished batch beside the run log, plus a persisted run-token key. Small, and nothing else changes shape.

## Decision

Option 3.

- **Batch state.** `<run log directory>/batches/<batch run id>.json` holds the request, the tasks as resolved (goal included, so a tracker is not asked again), the egress token key, and per task a phase: `pending`, `started` (run id, branch, container and start time fixed), `finished` (session ended, output read), `handed_over` or `failed` (both carry the task's outcome). It is written at each change by writing a temporary file, flushing it and renaming it over the old one, so a crash leaves the old state or the new, never half. The file is mode 0600 (it holds the token key). The names of the batch's network and egress container are not stored: they follow from the batch id. The real model credential is not stored either; the profile supplies it again. The file is deleted once the batch's run record is written; a file whose run record exists is a leftover and is deleted at the next start.
- **Run-token key.** `RunTokenService.LoadOrCreate` keeps its 32-byte key in `<run log directory>/batches/run-token.key` (0600), so a token a running session holds still validates after a restart. Tokens still expire. The per-run spend ledger is in memory and starts at zero after a restart: a resumed session's research calls get their cap again.
- **Stopping is not cancelling.** When the server stops (its shutdown token fires and no caller cancelled the run), a batch with saved state detaches: session containers keep running, nothing is removed, no run record is written. A caller's cancel or the halt switch behaves as before.
- **Resume at start**, before the listener opens (a hosted service): each state with no run record is registered under its own run id as a running run (it appears in `GET /v1/runs/<id>`, its events and the list, holds a main-gate slot and counts toward the unfinished limit). The batch network is kept if its egress container runs, otherwise the egress container is made again with the saved key. Tasks then go through the normal scheduler under the usual limits, and the task runner takes each up by phase: `handed_over`/`failed` return the saved outcome; `started`/`finished` re-attach to the container if it runs (the wall clock counts from the saved start), read its output if it ended, and hand over; if the container is gone and the output volume holds no outcome, the old attempt's resources are removed and the task runs again under a new run id and branch; `pending` is scheduled normally. The batch then ends as usual.
- **Orphan sweep**, after the resumed batches are registered: every container, network and volume that carries the `chargehand.run` label and a chargehand name, and whose batch or run id belongs to no resumed batch, is removed through the engine (the runner over HTTP gets a new `GET /resources`; `IContainerEngine.ListAsync`). A resource the engine will not release is skipped. A runner that cannot be reached at start is reported on stderr and the server still starts.
- **Non-driven runs** keep today's `lost` semantics.

## Consequences

A deploy no longer costs a batch; the box needs no compose change as long as the run log directory (`/app/runs`) is a persistent volume, which it is. The restart gap costs time only.

The sweep assumes this server owns every chargehand-labelled resource on its engine. A second chargehand server, or a CLI driven run, sharing one runner or Docker daemon would have its live resources removed at this server's start.

A crash between a task's push and the saved `handed_over` leaves the task `finished`: the resume hands over again, which can fail on the existing branch or open a second draft pull request. The window is the handover's end to the task's run record, seconds. A restart while the runner is down at start marks no batch lost but fails the resumed batch on its first engine call (its run record then says why).

## Reopen if

A second server shares a runner (then the sweep needs an owner label); the handover's repeat-safety shows up as duplicate pull requests; batches need to survive losing the run log volume.
