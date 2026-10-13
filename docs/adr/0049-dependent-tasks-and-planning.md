# 0049. Tasks depend on each other, and a plan sizes and splits them first

- Status: accepted
- Date: 2026-10-13

## Context

A driven batch ([ADR 0039](0039-driven-writing-sessions.md)) is a flat list: every task starts from the same base commit and ends in its own draft pull request. Two things follow.

1. Work that builds on other work cannot be expressed. A caller who wants "add the model, then the endpoint on top of it" sends two tasks, and the second starts without the first's code. A task that was stopped and answered ([ADR 0045](0045-governed-sessions.md)) continues in a *later* batch, never within one.
2. Independent tasks that touch the same thing collide. Two tasks that each add a database migration both take number N+1; both pull requests are green alone and break each other. Nothing before the batch looks at that, and a large task is sized only by the governor's default class (M).

## Options

1. Leave ordering to the caller: send one batch per step. Works, but the caller has to wait, read branches and name them, and the batch's caps and parallelism do not span the chain.
2. `depends_on` between tasks, run by the scheduler; each dependent starts from its dependencies' branches. No new model call.
3. Option 2, plus an optional planning step that reads the repository and proposes sizes, splits and shared numbers.
4. Make the planner a new service with its own model client. More moving parts and a second credential for something one cheap read-only run does.

## Decision

Options 2 and 3, the planner through the orchestrator that already exists.

**Dependencies.** `driven.tasks[].depends_on` lists ids of tasks of the same batch (request/v1, additive). The server refuses, as `invalid_request` before anything starts, an unknown id, a task that names itself or the same id twice, a cycle (the message names the cycle), and `depends_on` together with `continue_from` (two different starting points).

- *Scheduling.* The scheduler starts, in list order, the first task whose dependencies all ended `completed`; a task listed before its dependency waits. One that is not ready holds no slot and no cap reservation. A task whose dependency ended `failed`, `needs_input` or `cancelled`, or was itself not started, is `not_started` with `not started: the task it depends on, '<id>', failed` (or `asked a question`, `was cancelled`, `was not started`); that spreads down the chain and each such task gets a `task_finished` event. A dependency that did not resolve to a goal (a tracker reference that could not be read) leaves its dependents the same way. A partial pull request is `failed`, so it does not release its dependents: work that stopped is not a base. The batch's cap and rate-limit rules are unchanged.
- *Stacking.* A dependent's first session starts from one branch named for the dependent, built on this host outside any container, as the handover is: a scratch clone of chargehand's checkout of the base commit, each dependency's bundle (read back from its last session's output volume, which the task runner already keeps) fetched into it, the first dependency's branch checked out under the new name and every other one merged in, in the order `depends_on` lists them. The branch is bundled against the base commit and written into the session's output volume as `seed.bundle`, the same seed a continuation uses ([ADR 0044](0044-driven-sessions-continue-after-a-stop.md)); because the bundle already holds the dependent's own branch name, the session image needs no change. The prompt gains a note that the dependencies' work is already on the branch. A merge that conflicts is aborted, and the task fails as `merge_conflict` (new error code, additive) naming the first task that did not merge and up to 20 conflicting paths, before any container starts.
- *Pull request.* With exactly one dependency the draft is opened against that dependency's branch, so it shows only the dependent's change; the body says it is stacked and to merge the other first. If the host refuses that base (the branch is gone), the pull request is opened against the default branch and says so. With several dependencies a pull request has one base, so it is opened against the default branch, and the body lists the merged branches and says the diff includes their changes. A task with dependencies is not touched by the integrator ([ADR 0046](0046-integrator-and-ci-fixer.md)): merging it by policy would merge the work it sits on, unread.
- *Restart* ([ADR 0043](0043-driven-batches-survive-a-server-restart.md)). The journal keeps each task's `depends_on`, size and origin. On resume a finished dependency returns its saved outcome at once, so its dependents become ready in the same order as before; a dependent whose container still runs is taken up, one whose container is gone starts again from its dependencies' bundles.
- *Limits.* The dependent's turns and tokens are its own. A dependency's CI-fix commits ([ADR 0046](0046-integrator-and-ci-fixer.md)) are not in the bundle it hands on, only its session's last commit. The base commit is the same for the whole batch.

**Planning.** Off by default. `driven.planning.enabled` in the profile or `driven.plan: true` on a request turns it on; `plan: false` turns it off for a request. When on, before the batch's network and journal exist, the server sends one request through `Orchestrator.RunAsync` as a child of the batch run: read-only preset `cheap` (profile key `driven.planning.preset`), the batch's repository and commit, `interactive: false`. No new model client, credential or sandbox; the run is in the run log, priced and cached like any other, and its cost is **not** part of the batch's caps. The request text lists each task (id, goal cut at 3000 characters, the size the caller set) and asks for one JSON object in `summary`: per task a size (S, M, L), for an L task 2 to 4 ordered parts, and for tasks that would take the same next number (migrations, ADR files) a `what`/`value` each; `checked` lists, one line each, what it read. The prompt is code in `BatchPlanner.cs`, not a registry block under `prompts/`, so no file under `prompts/` or `presets/` changes and Prompt CI ([ADR 0019](0019-prompt-ci-and-routing-report.md), [ADR 0033](0033-ci-policy.md)) has nothing new to gate; the run itself goes through the gated `intake` and `cheap` cells unchanged.

What the plan may change is decided by `PlanApplier`, a pure function, not by the model:

- A task with a `size` from the caller keeps it. The plan sizes the rest (an unusable size is left to the default, M).
- A task becomes a chain only when the caller gave it no `size`, `budget_usd`, `max_turns` or `max_minutes`, it does not continue another task, the plan sized it L with 2 to 4 non-empty parts, the batch stays within 20 tasks and the part ids are free. The chain is `t-1 → … → t`: the first link takes the task's own `depends_on`, each later link depends on the one before, and the last keeps the id, the tracker reference and everything that depends on `t`, so the caller's graph holds. Parts get their own size (S, M or L; M when missing) and a goal that is the part plus a note that it is part N of M, built on the branch, with the whole goal for context.
- A shared number is appended to the goal of the task it was given to (`Plan: use migration number 0042 for this task. Other tasks of this batch were given different ones...`). `what` and `value` must be plain text of at most 80 characters; a value given to two tasks stays with the first. A number for a split task goes to the part it names, the first by default.
- The plan never adds a dependency between the caller's tasks.

The result is the batch result's `plan` artifact (`status` `applied` or `skipped` with `reason`, `source`, the planning `run_id`, `checked`, `note`, per task `size`, `size_from` (`caller`, `plan` or absent), `depends_on`, `split_from`, `assigned`, `kept`, and `dropped` proposals with their reasons) and a `plan` run event with one line. A planner that is missing, fails, or answers with nothing usable leaves the batch exactly as the caller sent it and says why; a cancel is not swallowed. The plan is saved in the batch state, so a restart reports it and does not plan again.

**What the plan does not know.** Whether two tasks conflict beyond the numbers it assigned (two migrations that alter one table, two edits to one function) is not checked, and the artifact says so in `not_checked`. It reads the repository at the base commit only; migrations that exist on other branches, or that tasks of the batch create under a different scheme, are invisible to it. A model can also misjudge a size; the caller's `size` is the way to pin one.

## Consequences

A chain runs one link at a time, so it is slower than the same work in parallel, and a chain stops at its first failure: the links after it are `not_started`. Each link pays for reading the repository again. A dependent's pull request can be merged only after its dependency's.

A pull request stacked on another's branch shows the right diff only while GitHub keeps that base; after the dependency is squash-merged and its branch deleted, GitHub retargets the dependent to the default branch, and the squash makes the dependent's history look as if it re-adds the dependency's changes until it is rebased. That is the usual cost of stacked pull requests, and the body says so.

Planning is advice from the cheapest preset: it costs a read-only run, it can be wrong, and its only authority is what `PlanApplier` lets through. The assigned numbers matter only if the sessions obey the goal; chargehand does not check that two branches ended with different numbers.

Nothing changes for a batch without `depends_on` and without a plan. The session image is unchanged; no profile key is required.

## Reopen if

Chains are common and their serial time hurts (then merge-ready dependencies could start the next link from the pushed branch while the first one's CI runs); plans are often wrong or ignored (then drop `planning` or let the session itself reserve numbers through a tool); a forge needs a different stacking rule than "base on the dependency's branch"; the integrator should merge a chain bottom-up.
