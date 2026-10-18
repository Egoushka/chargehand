# Durable execution: what a restart still loses, and compensation for partial driven runs

- Status: draft for the maintainer's review. No code. Every decision below is the proposer's.
- Date: 2026-10-18

## Goal

Close what a server restart still loses after ADR 0061 and ADR 0063, using three ideas from durable-execution engines
(durable timers, ContinueAsNew, compensation boundaries) inside chargehand's own journal, without adopting an engine.
And decide what chargehand does with the branches and draft pull requests a driven run leaves when it fails or is
cancelled part-way.

Done when: a server killed at any moment and restarted (1) takes up every read-only run that was queued, (2) keeps
retrying a driven batch whose resume failed instead of leaving it `lost`, (3) keeps a batch's overspend stop and an
operator's cancel of a running task, and (4) after a cancelled or failed batch leaves no branch or pull request that
neither a person nor the result can account for.

## Source

"Durable Task in the real world: what problems does it actually solve?" (.NET Blog at devblogs.microsoft.com/dotnet,
2026-10-12), on the Durable Task .NET SDK with Durable Task Scheduler. What carries over, as the article reports it:

1. **A durable engine remembers steps, waits and where to resume; it does not know whether an outside effect
   happened.** The application stays the authority for payments, cloud resources, approvals and tool runs.
2. **Durable timers and external-event waits** hold no thread; a replacement worker replays to the same wait. Events are
   hints: the decision lives in the application's store and is read after the wait, also when the timer fires.
3. **ContinueAsNew** keeps the instance id and starts a fresh history at a clean cycle boundary, here after a billing
   cycle's facts are persisted; unprocessed events are dropped on purpose.
4. **Compensation is not undo.** First stop new submissions, then reconcile in-flight work (including lost responses),
   then delete only what this attempt created, in reverse dependency order. If ownership cannot be established, keep the
   resource and raise a needs-intervention state. `WhenAll` does not roll back finished siblings.
5. **Side effects need stable keys**, request fingerprints and reconciliation after a lost acknowledgement; destructive
   tools are never retried blindly; an approval is re-checked before acting on it.
6. Its decision guidance: use an engine when remembering and resuming becomes its own system; keep a checkpoint store
   with a dispatcher when it already covers the failure windows.

## Where it stands

Evidence at `17bcaf2` (0.9.6), read 2026-10-18.

The premise "in-flight batches and queued runs do not survive a restart" is half right. Driven batches do resume; read-
only runs do not.

1. **Driven batches resume.** `BatchResumer` calls `RunService.ResumeAsync` at host start
   (`src/Chargehand.Server/BatchResumer.cs`, `RunService.cs:153-167`): it reads the open `batch` steps, sweeps orphaned
   containers, networks and volumes, and runs each batch again under its run id. Every driven side effect is a journaled
   step with a stable key (`prep/<run>`, `xfer/<run>/<n>`, `handover/<task>`, `session/<task>/<attempt>`) in
   `journal.db` (ADR 0061, `src/Modules/Chargehand.Modules.Journal/Api/DrivenJournal.cs`). Task state is the fold of
   `<batch>/tasks` events; since 0.9.6 the scheduler's rate-limit pause, used-up placements and a cancel of a task not
   yet started survive too (ADR 0063, CHANGELOG 0.9.6). Handover looks a pull request up by head branch before opening
   one (`GitHubPullRequests.FindByHeadAsync`, `src/Modules/Chargehand.Modules.Forge/Api/GitHubPullRequests.cs:46`).
2. **A failed resume is final.** If `ResumeAsync` throws (the runner is not up yet), `BatchResumer` logs it and those
   batches stay `lost` (`BatchResumer.cs`, class summary). Nothing tries again.
3. **Read-only runs are lost.** `RunService` keeps runs in memory and gates them with a `SemaphoreSlim`
   (`http.max_concurrent_runs`, default 1; at most 10 unfinished, `RunService.cs:23-25`). A run with a start record and
   no end, whose process is gone, reads `lost` (`ChargehandServer.cs:339`), and its caller must resend
   (`docs/guide/server.md`, "Runs and the run log"; ADR 0018). A queued run that never started is lost the same way.
4. **Still lost in a driven batch** (ADR 0063, "Consequences"): the batch's overspend stop, a task's over-cap cancel, and
   an operator's cancel of a running task until that task ends. A restart before then takes the task up again.
5. **No compensation for pushed work.** A cancelled batch reports "nothing was pushed" for tasks it stopped before
   handover (`BatchLifecycle.cs:161`). Branches and draft pull requests already created stay. `GitHubPullRequests` has no
   method that closes, merges or approves, and its test fails if one appears (`GitHubPullRequests.cs:49-50`). A task that
   stops with commits gets a `[partial]` draft on purpose (ADR 0044). The orphan sweep removes containers, networks and
   volumes only (`src/Chargehand.Driven/Driven/DrivenRun.cs:236-239`).
6. **Intake already persists** its cursor, handled triggers and tasks in a JSON file, so a restart starts nothing twice
   (`src/Chargehand.Server/IntakeService.cs`, CHANGELOG 0.9.x).

ADR 0061 already weighed and rejected an external engine (Temporal, Dapr Workflow, DurableTask) for a server that also
ships as a local `dotnet tool`. The article's own guidance (point 6) agrees: chargehand has a checkpoint store with a
dispatcher. This design keeps `Switchknife.Journal` and borrows the patterns.

## Mapping

| Durable Task idea | chargehand today | Gap | Proposal |
|---|---|---|---|
| Step history, replay | Journal steps with intent and outcome; a completed key never reruns (ADR 0061) | None for driven work; read-only runs are not journaled | Read-only runs get a small flow (below, part 1) |
| Durable timer | Rate-limit pause derived again from the journaled `ended` event and its reset time (ADR 0063); `Task.Delay` only in memory | Resume after a failed start has no timer at all | `resume_retry` as a journaled timer (part 2) |
| External event as hint | Console cancel answers once the cancel is on disk (ADR 0063) | A cancel of a running task is not on disk | `cancel_requested` event (part 3) |
| ContinueAsNew | A continuation (`continue_from`, ADR 0044/0045) starts a new batch with a new flow that points at the old run | None measured | No change; record the analogue and a reopen trigger (part 4) |
| Compensation boundary | Containers, networks, volumes swept; branches and drafts kept | No owner-checked cleanup of branches; a cancelled batch's drafts are not marked | `compensate/<task>` step (part 5) |
| Stable keys for side effects | Deterministic names; pull request looked up by head | Branch deletion would need a fence | Delete only when the remote head is the commit the journal recorded |

## Design

### Part 1. Queued and running read-only runs survive a restart

A read-only run has no outside effect except its cost: workers cannot write, and citations are resolved locally. So
resuming one is running it again, which is safe if bounded.

- **Flow** `run/<id>` in `journal.db`, opened when `RunService.Start` accepts the run, before the `queued` event: one
  step `accepted` with the request (already in the start record; the step stores only the run id and the gate it waits
  on), then `started` when it passes the gate, then the run record ends it.
- **At start**, after `BatchResumer`, a `RunResumer` reads open `run/<id>` flows. A run that never `started` is queued
  again in its original order, as `queued`. A run that `started` is run again once, marked `attempt: 2` in its events,
  if its preset is not `thorough` or `strict` and its cost so far (from the run log's node records) is under half the
  run cap; otherwise it ends `lost` as today, with the reason. A run already at `attempt: 2` ends `lost`.
- **The caller** that reconnects to `GET /v1/runs/{id}/events` sees the run as `queued` or `running` again instead of
  `410 lost`. MCP callers poll the same route.
- **Limit**: the 10-unfinished cap counts resumed runs first; a resumed run beyond it ends `lost` with
  `refused: queue full`.
- **Not changed**: the response cache. A rerun of a pinned-commit question may hit it, which only helps.

### Part 2. A failed resume retries on a durable timer

- When `ResumeAsync` fails for a batch, append `resume_failed` to the batch's flow with `at`, the error code and
  `retry_at`. Back-off 30 s, 1, 2, 5, 10 min, then every 10 min, for at most 1 hour from the first failure.
- A hosted timer wakes at the earliest `retry_at` of all such batches. As in the article, the timer is a hint: on waking
  it reads the flow again, and a batch that another path already resumed or ended is skipped.
- After the hour the batch ends `lost` with `resume_gave_up` and the last error, and the console inbox shows it as a
  failure (ADR 0064 rules unchanged).
- A restart during the hour folds `resume_failed` events and keeps the original first-failure time, so the hour does not
  start over. This is the same fold rule ADR 0063 uses for the rate-limit pause.

### Part 3. Overspend stop and a running task's cancel survive

ADR 0063's "Reopen if" names both as one more kind each in `<batch>/tasks`. Proposed kinds:

| Kind | Payload | Written | Folded as |
|---|---|---|---|
| `overspent` | `at`, `spent`, `cap` | the scheduler's shell when `Fits` stops the batch, before the stop is acted on | the batch's stop reason; no new placement after replay |
| `cancel_requested` | `task`, `at` | `BatchControl.CancelTaskAsync` for a running task, before it returns | the task is not taken up again on resume; its container is stopped by the orphan sweep and the task ends `cancelled` |

A task's over-cap cancel is an `ended` with the capped outcome, which ADR 0063 already journals once the task's run
returns; the gap is only the window before it returns, and `cancel_requested` written by the cap watcher closes it.

### Part 4. ContinueAsNew: the analogue exists; do not build one

ContinueAsNew exists because an orchestration's history grows without bound in a loop. A chargehand batch is bounded:
at most 20 tasks, each with a bounded number of attempts and continuations. The loop that would grow, a task that keeps
continuing, already starts a new batch with a new flow (`continue_from`), the old flow is forgotten when its batch's run
record is written, and the pointer to the prior run carries what the next cycle needs. That is ContinueAsNew by
construction, with the same deliberate choice the article makes: nothing in flight is carried over, only facts already
recorded.

Reopen if a single batch's flow passes 10,000 events, or replaying one at start takes over 1 s on the box. Then the
fix is a snapshot event per batch that the fold starts from, not a new engine.

### Part 5. Compensation for partial driven runs

The article's rule applied to chargehand: stop new work, reconcile what is in flight, remove only what this attempt
created and can prove it created, keep everything else and tell a person. chargehand's own rule on top: work a person
might want is never destroyed, and pull requests are never closed by chargehand (ADR 0039; `GitHubPullRequests` test).

**Boundary.** Compensation runs when a batch ends `cancelled` or `failed`, or a fan-out join fails (dynamic fan-out
design, 2026-10-18). It never runs for a task that ended `completed` or `needs_input`, nor for a `[partial]` draft.

**Order** (one journaled step per task, `compensate/<task>`, so a restart in the middle neither repeats nor skips):

1. **Stop.** The batch takes no new placement (already true on cancel and on `overspent`).
2. **Reconcile.** For each task with an open `handover/<task>` step (intent, no outcome): look the pull request up by
   head branch, and the branch by name, and record what exists. This is the "lost response" window: a push or a pull
   request that happened but whose outcome was not saved.
3. **Decide, per task, from the journal only:**

| What exists | Owned (journal has the branch and commit) | Action |
|---|---|---|
| Branch, no pull request, task ended without useful work (cancelled before its session committed, or a fan-out part whose join will not run) | yes, and the remote head equals the recorded commit | delete the branch |
| Branch, no pull request, the session committed work | yes | keep; the result lists it under `kept_branches` with the reason |
| Draft pull request | yes | keep open; add one comment saying the batch was cancelled or failed and why, at most once (keyed by the step) |
| Branch or pull request whose head moved since the recorded commit | n/a | keep; someone pushed to it |
| Anything not in the journal | no | keep; open an inbox item `needs_intervention` naming it |

4. **Report.** The batch result gains a `compensation` artifact: per task, what was found, deleted, commented or kept,
   and why.

**Fence.** A branch is deleted with a conditional ref update (GitHub's update-ref with the expected old SHA, or
`git push --force-with-lease=<branch>:<sha> origin :<branch>`), so a branch someone pushed to after the run is never
deleted. The push credential already may push non-default branches; deletion of a `chargehand/` branch is the only new
write, and the forge class gains a `DeleteBranchIfAtAsync` that refuses any name outside `chargehand/`. Close, merge and
approve stay absent.

**Opt-in.** `driven.compensation` in the profile: `report` (default: reconcile and report, change nothing remote),
`clean` (delete owned empty branches and comment on drafts). Starting with `report` lets the maintainer read a few
`compensation` artifacts before anything is deleted.

### Failure points, checked

The article closes with a table of where a crash can land. The same for a driven task after this design:

| Crash between | On restart |
|---|---|
| Session commit and push intent | `handover/<task>` has no intent; the step runs fresh |
| Push and its outcome | Intent without outcome: resumed, finds the branch at the commit, records it, goes on (ADR 0061) |
| Draft opened and its outcome | Resumed, `FindByHeadAsync` finds it, no second draft |
| Operator cancel of a running task and the task's end | `cancel_requested` folded; task not taken up (part 3) |
| Overspend decision and the stop | `overspent` folded; no placement (part 3) |
| Batch failure and compensation | `compensate/<task>` open steps resume in order; a deleted branch is not looked for twice (part 5) |
| Resume failure and the next try | `resume_failed` folded; timer resumes on the original clock (part 2) |
| A read-only run queued and started | `run/<id>` open: queued again or run once more (part 1) |

## Order of work

1. Part 3 (two event kinds, the pattern ADR 0063 set; S).
2. Part 2 (resume retry timer; S).
3. Part 1 (read-only run flow and `RunResumer`; M).
4. Part 5 with `report` only (reconcile and artifact; M), then `clean` after the maintainer has read some reports (S).

Each part gets an ADR when it is built; this note is the shared reasoning. The kill-at-every-step test from ADR 0061
extends to `compensate/<task>` and `run/<id>` steps.

## Not decided here

- Whether a resumed read-only run should reuse the first attempt's finished nodes instead of starting over; needs node
  results journaled, which ADR 0061 did not cover.
- Closing draft pull requests of cancelled batches; kept out by the no-close rule, which this design does not reopen.
- A second server sharing one journal (ADR 0061 "Reopen if").
