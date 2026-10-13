# 0061. Driven side effects run as recorded steps in a durable journal

- Status: accepted
- Date: 2026-10-14

## Context

ADR 0043 made driven batches survive a restart with a JSON snapshot per batch (`BatchState`, rewritten on every change),
Docker resources found by fixed names and a label, and an orphan sweep at start. It rejected "a database for run state"
as heavy for one process.

The restart bugs since then share one cause: a side effect done in two parts with no record between them.

- #261: a transfer helper cancelled between `docker create` and `docker start` left a created container under its
  fixed name; every later transfer for the run failed on the name.
- #266 (CHARGEHAND-215): the same flaw one day later on the workspace-prep helper.
- ADR 0043's own hole: a crash between the push and the saved `handed_over` hands over again (a second draft pull
  request or an existing-branch error).
- #215: liveness read from a pid in a shared log; a container's server is pid 1 every time.

Each fix was a retry-and-clean-up patch on one call site. The snapshot records phases, not steps, so nothing tells a
restarted server which side effect of a phase was in flight. Events are a list in memory and gone after a restart.

## Options

1. Keep snapshots and patch each call site. No new parts; the class stays open, and every new side effect repeats it.
2. An external durable-execution engine (Temporal, Dapr Workflow, DurableTask). Temporal is the only GA .NET option
   for hours-long work, and needs a server and PostgreSQL beside chargehand; chargehand also ships as a local
   `dotnet tool`, which would then need a cluster. Steps must still be safe to repeat: an engine retries them, it
   does not make `docker create` under a fixed name idempotent.
3. A step journal in-process: every side effect is a step with a stable key; its intent is recorded before it runs and
   its result after. A completed key never runs again; a key with an intent and no outcome runs again marked resumed,
   so the step adopts or removes what the interrupted attempt left. Events go in the same store and replay.

## Decision

Option 3, with `Switchknife.Journal` (the maintainer's MIT library, SQLite, Native AOT compatible) behind the Journal
module (`src/Modules/Chargehand.Modules.Journal`, ADR 0060). This replaces ADR 0043's rejection of a database: one
SQLite file in the server's state directory is lighter than the snapshot, label and sweep machinery it retires.

- Flow = a batch id; step key = what the side effect acts on (`prep/<run>`, `xfer/<run>/<n>`, `handover/<task>`,
  `session/<task>/<attempt>`).
- Every step is safe to repeat for its key: deterministic resource names, and when resumed, "adopt or remove" before
  acting. A push records the branch and commit; a pull request is looked up by head branch before one is opened.
- Events (progress, governor decisions, transcript markers) append to the batch's flow and replay to a client that
  reconnects after a restart.
- Order of migration: helper containers (prep, transfer), then handover (push, pull request), then session start, then
  events. `BatchState` stays the source of phases until the last step moves, then goes.
- A test kills the server between every recorded step of a driven task and restarts it; it must end with no leftover
  container and no second pull request.

## Consequences

- One more file in the state directory (`journal.db`, WAL), synced on every commit.
- Each new side effect in the driven engine is written as a step with a key, or the kill-at-every-step test fails.
- The orphan sweep shrinks to resources whose step is not in the journal.
- chargehand depends on a nuget.org package of the maintainer's; its version is pinned like any other.

## Reopen if

A second server must share one runner or one journal (a single-writer SQLite file no longer fits); the journal's
write latency shows in batch throughput; or the kill-at-every-step test cannot be made to pass for a step.
