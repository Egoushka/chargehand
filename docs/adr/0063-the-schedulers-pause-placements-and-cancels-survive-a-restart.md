# 0063. The scheduler's pause, placements and operator cancels survive a restart

- Status: accepted
- Date: 2026-10-14

## Context

The batch scheduler's state (`ScheduleState`, `BatchMachine` in Batches) lives only in the process that runs the batch.
A server restart resumes the batch (ADR 0043) with the scheduler at `BatchMachine.Begin(plan)`. Tasks that ended come
back one by one when the scheduler places them again: `SiteRouter` returns the end the task journal folded (ADR 0061,
amendment of 2026-10-14), and the scheduler takes it at the restart's time. Pinned by the characterization tests of the
C4 slice S1 (`DrivenRestartCharacterizationTests`):

- A rate-limit pause is not lost outright. It is derived again from the saved end, timed from the restart instead of
  the original reset, and the count of rate limits in a row starts over.
- An operator's cancel of a task that has not started is kept in memory until the scheduler's next pass. A server that
  stops before that pass loses it, and the task runs after the restart.
- The placements a task used up (`MaxPlacements`, ADR 0058) start over at zero: a task gets three new placements.

The owner decided on 2026-10-14 that these become durable in C4. Behaviour changes on purpose.

## Options

1. Journal the machine's inputs that set this state as events in the batch's `<batch>/tasks` flow, and fold them again
   with `BatchMachine.Decide` before the resumed scheduler's first look. The machine is pure and every event that needs
   time carries it (#324), so a fold gives the same state. One flow, one writer (`TaskJournal`), and the task fold
   already skips kinds it does not know (#334), so the release before reads such a flow as it did.
2. Save `ScheduleState` as a snapshot. A second source of truth beside the flow, with a format of its own to version,
   while C4 moves the batch off its snapshot.
3. A flow of its own, `<batch>/schedule`. A second order to reconcile with the task events, and a third flow to forget.

## Decision

Option 1.

**Events.** In the flow `<batch>/tasks`, beside the task events. The kind is snake_case. The payload is a JSON object,
snake_case, nulls left out, with the task's id under `task` and the time the scheduler's shell decided at under `at`
(its clock, `TimeProvider`). Source-generated JSON (`ScheduleEventJson`, Batches).

| Kind | Payload | Written | Folded as |
|---|---|---|---|
| `operator_cancelled` | `task`, `at` | `BatchControl.CancelTaskAsync`, for a task that has not started, before it returns; a cancel asked before the scheduler runs is written as the scheduler starts | `OperatorCancelled` |
| `ended` | `task`, `at`, `outcome` (a task outcome) | the scheduler's shell, when a started task's run returns, before the caller is told of the end | `Ended(task, outcome, at)` |

An `ended` that places the task again (`replace: true`) keeps the outcome as the runner returned it. A final one keeps
the outcome as the task's and the batch's caps left it: what rewrote it (live usage) is not journaled, so a fold must
not rewrite it a second time. The fold of a final end is then the same as the decision it records.

**Not journaled.** `Tick`, `Admitted`, `Held`, `Refused`, `Blocked`, `Reported` and `Halt`.
- An admission. A task that runs at a restart is placed again, as before this decision, and the runner its session is on
  is in the task's `placed` event. A folded admission would only have to be undone.
- A halt is never persisted. It is a server that stops or a caller who cancels. Nothing the shell decides once the batch
  is halted is journaled, so the end of a task the stop cut short is not kept, and that task is taken up again after the
  restart, as before.
- Live usage, the admission interval and the task the host holds.

**Replay.** On a resumed batch the shell folds the flow's scheduler events with `BatchMachine.Replay`, from
`Begin(plan)`, in the order they were appended, before its first `Tick`. Their commands are not carried out: nothing
starts, finishes or is told. A pause that still holds is told again, so the console's rate-limit pauses and the batch's
"held until" event name the original reset. An event of a kind it does not know, a payload that does not parse, a task
the plan does not have and a second end of a task that ended are skipped.

**Ordering and keys.** Events name tasks by id. The plan of a resumed batch is its saved tasks in their saved order, the
order the batch started with, so a task's position is the same. The flow's sequence is the order of the decisions:
`TaskJournal` appends one at a time, and the shell appends an end before the next step.

**Upgrade.** A batch started on the release before has no scheduler events. Its replay is `Begin(plan)`, and it resumes
as it did before this decision. A rollback to that release skips the two kinds.

**Secrets.** None: an outcome carries ids, a branch, a pull request URL and detail text, as the `finished` event does.

## Consequences

What a user sees after a server restart:
- A batch paused on a usage or rate limit stays paused until the reset it was given, and the console shows that time.
  Before, the pause started again from the restart.
- The count of rate limits in a row is kept: a second one right after the restart stops the batch, where before it
  paused again.
- A task whose runner did not answer gets only the placements it had left.
- An operator's cancel of a task that had not started holds: the task is cancelled and never starts. The console's cancel
  answers once the cancel is on disk.

Also:
- A task that ended keeps its end from the scheduler's event and is not placed again, so its runner is not asked again.
- A crash between `ended` and the scheduler's `finished` task event leaves the task's entry with the task runner's own
  outcome, while the batch reports the end the scheduler kept. The two differ only where a cap rewrote it.
- Still lost on a restart: the batch's overspend stop and a task's over-cap cancel (the ended tasks' spend does count
  again, from their outcomes), and an operator's cancel of a running task until that task ends. A restart before then
  takes the task up again.
- `TaskJournal` (Tasks) takes another writer's kinds (`AppendAsync`, `Others`) and refuses a task event's kind from it.

## Reopen if

The overspend stop or a running task's cancel must survive a restart. Either becomes one more kind in the same flow
(an `overspent` event, or a cancel of a running task folded so the resumed task is not taken up again).
