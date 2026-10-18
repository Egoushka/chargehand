# Dynamic fan-out: a lead run picks how many parts a task becomes, inside the batch's caps

- Status: draft for the maintainer's review. No code. Every decision below is the proposer's.
- Date: 2026-10-18

## Goal

Today the caller decides how many driven sessions a piece of work becomes: one per task in `driven.tasks`, at most 20,
and the planning step (ADR 0049) may only turn one large task into a serial chain of 2 to 4 parts. This design lets a
lead run read the repository and decide, per task, whether to split it into **parallel** parts and how many, while code
outside the model keeps the count, the spend and the review load within fixed caps.

Done when the spike below shows that, on large tasks, fan-out plus a join step reaches one reviewable draft pull request
at most 1.5 times the tokens of the same task run whole, with lower wall-clock time and no merge conflict at the join.
If it does not, this design is dropped and ADR 0049's serial chain stays the only split.

## Source

"Claude dynamic workflows" (Latent Space, 2026-10-12, a write-up of Anthropic's talk at AI Engineer New York,
https://www.latent.space/p/claude-dynamic-workflows). What carries over, as the article reports it:

1. The lead agent writes a plan, then a program that launches sub-agents and gathers their results; the count is the
   lead's choice, not configuration.
2. The guardrails named are a session budget the developer sets, lower default parallelism, and evals. The model is not
   told its budget; the presenters said that made agents anxious.
3. Sub-agents share a filesystem and do not talk to each other; results meet in a review phase.
4. Costs are "much more" than one agent, with no figures given. Open concerns: runaway agent counts, budget overruns,
   and the human reviewer becoming the bottleneck when many agents open pull requests.

The article gives no count rule and no numbers. This design takes the shape (the lead proposes, a budget outside the
model bounds it, a review phase joins) and supplies the rules chargehand needs.

## Where it stands

Evidence at `17bcaf2` (0.9.6), read 2026-10-18.

1. **Parallelism is the caller's and the profile's.** `driven.max_parallel` is 1 to 16
   (`schemas/request/v1/request.schema.json:85`); the profile sets `driven.max_parallel` (default 2) and
   `driven.max_parallel_total` (default 4, all batches) (`src/Chargehand/Config/Profile.cs:307-308`).
   `DrivenBatch.Limits` takes the smaller of request and profile, clamped to 1..16
   (`src/Modules/Chargehand.Modules.Batches/Api/DrivenBatch.cs:86`). A request can lower parallelism, never raise it.
2. **The scheduler reserves before it places.** `BatchMachine.Step` places a task only below `MaxParallel`, outside a
   rate-limit pause and when `Fits` holds (`.../Core/Scheduling/BatchMachine.cs:83`). `Fits` adds finished spend, each
   running task's whole cap (or its reported spend if higher) and one more task's cap, and compares that with the batch
   cap; a batch that does not fit stops with `CostCapReached`. The batch cap is `driven.max_usd_total` with an API key
   and `driven.max_tokens_total` with a subscription (`DrivenBatch.cs:81-84`).
3. **`run_cap_usd` does not bound driven batches.** It is the read-only orchestrator's per-run cap (`Profile.cs:39`,
   default 1.00), split evenly over a run's nodes (`src/Chargehand/Orchestrator.cs:283`). The driven equivalent is the
   batch cap in point 2. This design uses the batch cap; "budget cap" below means that one.
4. **The planner splits serially only.** `PlanApplier` (`BatchPlanner.cs:70-180`) may size a task with no size, split
   an L task with no caller limits into 2 to 4 parts chained by `depends_on`, and hand out shared numbers. It cannot add
   parallel parts, change `max_parallel` or touch a budget, and refuses a split past 20 tasks. Planning runs on the
   `cheap` preset, its cost outside the batch's caps (ADR 0049).
5. **Joining branches already exists.** A task with several `depends_on` starts from one branch built by merging its
   dependencies' bundles in order; a conflict fails it as `merge_conflict` before any container starts (ADR 0049,
   "Stacking"). That is the join this design needs.
6. **Every task ends in its own draft pull request.** Handover pushes `chargehand/<run>` and opens a draft for each task
   (ADR 0039); there is no task that only pushes a branch.
7. **Read-only runs fan out to at most 2.** `GraphRunner` runs forked siblings (ADR 0017) with concurrency
   `Clamp(max_parallel_nodes, 1, 2)` (`Orchestrator.cs:391`), from the 2 to 4 subtasks intake put in the task spec
   (`src/Chargehand/Plans/SplitPlan.cs:11-24`). ADR 0011's two-calls-per-model limit is why. Out of scope here.

## Design

### Two levels, shipped in order

1. **Plan-time fan-out** (this design's subject). Before the batch starts, the existing planning run may propose, for a
   task that allows it, N parallel parts and one join. The scheduler then runs them as ordinary tasks. Nothing in the
   scheduler, the journal or the session image changes.
2. **Run-time fan-out** (later, only if level 1 pays off). A running session calls a tool to propose more tasks for the
   batch; the scheduler admits them through the same applier. It needs a journaled `tasks_added` event so a restart
   folds them back (see the durable-execution design, 2026-10-18), and a depth rule. Not designed further here.

Level 1 is the article's "lead decides the count" with the lead being the planning run, which already reads the
repository at the batch's commit. It costs one more cheap read-only run and no new model client.

### Interface (request/v1, additive)

A task opts in; the batch sets the ceiling.

| Field | Where | Meaning |
|---|---|---|
| `fanout` | `driven.tasks[]` | `true` lets the planner split this task into parallel parts. Off by default. Ignored with `continue_from`, `depends_on` from the caller is kept on the join (below). |
| `max_parts` | `driven` | Most parts one task may become, 2 to 8, default 4. The profile's `driven.fanout.max_parts` caps it, as `max_parallel` is capped. |

A task with `fanout: true` and no `size` is planned as today first; only an L task is considered for fan-out.

The planner's JSON answer (in `summary`, as ADR 0049) gains, per task it fans out: `parts`, each with a `goal` (one
paragraph), a `size` (S, M, L) and `paths` (the files or directories the part expects to change, at most 20 globs), and a
`join` note (what the join must check). The model is not shown dollars or tokens, following the article; it is told the
most parts it may propose (`max_parts`) and that fewer is better when parts would touch the same files.

### What the plan may change: `FanoutApplier`

As with `PlanApplier`, a pure function decides, not the model. For a task `t` with N proposed parts:

1. **Count.** N must be 2..`max_parts`. Parts with an empty goal or no `paths` are dropped first.
2. **Overlap.** Two parts whose `paths` overlap (one glob matches a path the other names, checked against the file list
   at the base commit) are not parallel: the later one depends on the earlier. If that leaves a single chain, the result
   is ADR 0049's chain, not a fan-out.
3. **Budget.** Let `c` be the per-task cap of a part's size class and `B` the batch cap left after the caller's other
   tasks are reserved at their caps. The join is reserved at its own cap `j`. Parts kept = the largest k ≤ N with
   `k·c + j ≤ B`. If k < 2, the task runs whole and the artifact says `dropped: budget`. This is the same arithmetic
   `BatchMachine.Fits` does at placement, done once up front, so a fan-out never plans what the scheduler would refuse.
4. **Task ceiling.** The batch stays within 20 tasks (parts plus join count).
5. **Shape.** Parts `t-p1 … t-pk` take `t`'s caller `depends_on`; the join keeps the id `t`, its tracker reference and
   every task that depended on `t`, and depends on all parts. So the caller's graph holds, as ADR 0049's chain does.
6. **Handover.** Parts push a branch and open **no** pull request (new internal handover mode `branch`); only the join
   opens a draft, based on the default branch, whose body lists the parts. One pull request per task the caller sent,
   which answers the article's reviewer bottleneck.

The `plan` artifact gains `fanout` per task: `proposed`, `kept`, `dropped` with reasons (`overlap`, `budget`, `ceiling`,
`empty`), and the parts' paths.

### The join task

The join is a driven session like any other, starting from the merge of the parts' branches (ADR 0049 stacking). Its
goal is `t`'s whole goal plus the planner's join note and the instruction to reconcile, test and finish, not to redo the
parts. A merge conflict fails the join as `merge_conflict` with the paths, before a container starts; the parts'
branches stay pushed and the task's result names them, so a person or a continuation can take over.

Concurrency is unchanged: parts run under `max_parallel` and `max_parallel_total`. Fan-out raises the task count, not
the number of containers at once.

### Guardrails

| Risk | Guardrail |
|---|---|
| Runaway count | `max_parts` (request ≤ profile ≤ 8), 20 tasks per batch, one level only: a part never fans out again. |
| Budget overrun | `FanoutApplier`'s reservation (above) plus the scheduler's unchanged `Fits` at every placement. The planning run stays outside the batch caps, as today. |
| Parts collide | Declared `paths` with the overlap rule; the join's merge fails fast on conflict. |
| Part does nothing | A part whose session ends with an empty diff is `completed` with no branch; the join merges what exists. Counted in the spike as over-splitting. |
| Reviewer flood | One draft pull request per caller task (parts push branches only). |
| Restart | Parts and join are ordinary tasks in the plan, saved by the `plan` step (ADR 0061), so a restart resumes them as any chain. |
| Bad split | The integrator (ADR 0046) leaves a join alone, as it does every task with dependencies. |

### Fit with the Managed Agents runtime spike (ADR 0031)

Fan-out sits above the session runtime: a part is one session on whatever runtime the batch uses. Nothing here assumes a
container per part beyond what the driven engine already does, so a Managed Agents adapter, if ADR 0031's live check
leads to one, runs parts unchanged. Two points for that spike:

1. **Use one Managed Agents session per part, not multiagent threads.** Threads share one sandbox, filesystem and
   budget (ADR 0031, [S12][S10]); chargehand would lose the per-part cap, the per-part branch and the overlap rule. This
   matches ADR 0031's own call that independent siblings stay separate sessions.
2. **Per-part caps map directly.** A part's cap becomes `budget.max_list_cost` at session creation, enforced by the
   platform between model requests (ADR 0031, [S10]); `FanoutApplier`'s arithmetic stays chargehand's. The spike could
   add one check at no extra session: that a session ended by `budget_reached` reports `list_cost` close to the cap.

The fan-out spike itself runs on the Claude Code runtime (subscription), so it needs no API spend and does not wait for
ADR 0031.

## Spike

Small and offline-first, on chargehand's own repository or the public benchmark repositories (ADR 0052), never on
private code.

1. **Proposal quality (no writing, about 10 cheap runs).** Prototype the planner prompt with the `parts`/`paths`/`join`
   answer and run it read-only on 10 L-sized goals with known good outcomes. Measure: parts proposed, parts surviving
   `FanoutApplier` (a unit-tested prototype, not wired), and overlaps caught. Stop if fewer than 5 of 10 goals yield ≥ 2
   non-overlapping parts.
2. **End-to-end (3 goals, 6 batches).** For 3 goals that passed step 1, run each twice on the local runner: once whole,
   once as parts plus join, same model, same commit, `max_parallel: 2`.

Success metric (all three, on the 3 goals):

- the join's draft pull request passes the task's verification and review in 3 of 3;
- total tokens of parts plus join ≤ 1.5 × the whole run's, median over the 3;
- wall-clock time from batch start to draft ≥ 30% lower, median.

Also recorded: join merge conflicts (target 0 of 3), parts with an empty diff (target ≤ 1 of all parts), and the
planning run's tokens. Kill rule: token ratio above 2 in any goal, or a merge conflict at the join in 2 of 3.

Estimated size: step 1 S (prompt plus applier prototype), step 2 S (runs, notes). Build after a pass: request fields and
schema, `FanoutApplier` with tests, handover mode `branch`, join goal text, `plan` artifact members (M to L).

## Not decided here

- Run-time fan-out (level 2) and its tool, depth and journal event.
- Whether the read-only orchestrator should ever go above two siblings (ADR 0011 says no today).
- Whether parts should share a cache prefix (forking a primed session as ADR 0017 does for read-only nodes); measure in
  the spike before designing it.
