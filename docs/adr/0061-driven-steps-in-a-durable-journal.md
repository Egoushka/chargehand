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

## Amendment, 2026-10-14: task state in its own flow, `<batch>/tasks`

The last step of the migration moves where each task stands out of the snapshot. The owner chose a flow of its own for
the task events, beside the batch's flow of steps and events, so the event replay to a reconnecting client (which reads
every payload of the batch's flow as a run status) and its tests stay as they are.

- **Flow** `<batch>/tasks`. Each change of a task's entry is one event, appended before the entry in memory changes;
  the entry is the fold of its events (`TaskMachine` in Tasks) over the task as the batch resolved and planned it. Tasks
  owns the flow (`TaskJournal`): the task runner writes a run's events, the scheduler the placement, the replace and the
  end. `ITaskState` (P3) is deleted, with the batch code that implemented it; P4 is the only upward port left.
- **Event.** The kind names the event, snake_case; the payload is a JSON object with the task's id under `task` first,
  then the event's own fields, snake_case, nulls left out. Kinds: `placed` (runner), `began` (run_id, branch, started,
  session_started, best), `run_renamed` (run_id, runs), `session_starting` (at), `container_started` (container),
  `session_read`, `continuation_began` (run_id, runs, continuation, prior, session_started, best, session_goal,
  start_detail), `continuation_failed` (run_id, runs, continuation), `finished` (outcome), `replaced`, and `imported`
  (entry) for one release.
- **Open to later writers.** A kind the fold does not know, a member it does not know, an event for a task the batch does
  not have and a payload that does not parse are skipped. The durability of the scheduler's pause, placements and
  operator cancels is a later decision; its events (an operator's cancel, an admission with its runner, an end with a
  re-place) can go into the same flow under kinds of their own, each carrying the time the scheduler decided at.
- **The snapshot** keeps the batch's own fields (request, token key, unresolved tasks, plan, actor) and the tasks as
  planned, written once, with `task_flow` naming the flow. Those batch fields leave it in the next slice.
- **Batches in flight at the deploy** are carried by a one-release importer at start. A snapshot without `task_flow` is
  copied to `<batch>.imported`, each task's entry is appended as one `imported` event, and the snapshot is saved again
  with the tasks as planned and `task_flow` set. Each step can be cut and repeated: the copy is made once and read on a
  repeat, and an imported entry stands for the whole entry. The `.imported` file is deleted with the snapshot when the
  batch ends. A rollback to the release before reads its tasks as not started, unless the `.imported` file is renamed
  back over the snapshot.
- TODO, in the release after the one that ships this (from 2026-10-14): remove the importer, the `imported` kind and the
  `.imported` file, and treat a snapshot without `task_flow` as unreadable.
