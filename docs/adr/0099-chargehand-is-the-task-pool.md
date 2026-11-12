# 0099. Chargehand is the task pool

- Status: accepted
- Date: 2026-11-12

## Context

Work reaches chargehand from several places: an issue label (ADR 0051), a review comment on a pull request it opened, a CI failure, a schedule, the console, and the owner's own agent conversations. Each source starts its work itself: intake polls and starts a batch when a slot is free (`max_concurrent`, ADR 0051; the waiting queue with reasons, ADR 0081); the console starts a run on request; a conversation that finds a task runs it in its own context or asks the owner to. Three things follow, all observed in the owner's use of this server over October and November 2026.

1. The owner's conversations are the distribution point. A chat that found three tasks either does them in its own, already loaded context, or hands them to the owner to re-dispatch. Tokens, speed and the owner's attention are spent unevenly: a chat does more than its scope, another sits idle.
2. There is no one answer to "what is the system doing and why did nothing start". Intake has its waiting reasons (ADR 0081); runs started another way have none; the owner has said he cannot tell what step a session is on.
3. A worker is spent one task at a time. Related tasks in one repository and one area each start a fresh context and re-derive the same orientation, while the one thing worth keeping, a loaded context, is thrown away after one task.

ADR 0094 (proposed) adds a planning pass before a task; it assumes the task has already been chosen and started. ADR 0081 removed the start cap in favour of a queue with reasons, but only for intake's own triggers.

Assumption, not measured: a worker's context is useful for related tasks until it holds about 150k tokens, after which quality drops faster than re-orientation costs. The ceiling is a setting so the measurement can move it.

## Prior art

- Query: "job queue with scope-affinity workers, fairness governor, durable claims, restart-safe"
- Coverage: hit
- Facts: the shape is a classic durable work queue (one row per item, a claim column, a worker lease) as SQL-backed queues implement it; scope affinity is the "partition key" of such queues. The governor-with-named-checks shape is this repository's own (ADR 0081, ADR 0084, ADR 0095). Nothing here is novel; the decision is where it lives.

## Options

1. **Each chat keeps its own queue.** Pros: nothing to build. Cons: no fair distribution, no single view, no durability beyond the chat; this is the status quo that fails.
2. **A long-lived worker pool that takes items as they come.** Pros: warm workers, no cold start. Cons: a worker's context is the asset and it rots; a pool with mixed scopes loads every worker with every scope. Rejected by the owner.
3. **Chargehand is the pool.** One durable queue for every source; items are scoped; a governor starts a worker per scope when its checks pass; the worker takes every item of its scope in one context until a token ceiling, then writes a resume card and ends; a successor claims the scope from the card. The event log (ADR 0090) makes the cold start cheap: a successor reads what was done. Pros: one place, one view, fair by construction, durable across a restart. Cons: a new module and a governor to keep honest; the first consumer (the `enqueue` tool) does nothing visible until the governor starts work.
4. **Put the pool in intake's state file.** Pros: reuses the waiting queue (ADR 0081). Cons: the file is one JSON document written whole, reflection-serialised, owned by intake; the pool outlives intake's poll and has consumers that are not intake.

## Decision

Option 3. A module `Chargehand.Modules.Pool` (layer 3 in `Skeleton.Layers`: on Contracts and the Kernel only, below Sessions and Intake, which take work from it) holds a SQLite queue and a governor.

- **One queue.** Every source enqueues; nothing but the governor starts. The MCP tool `enqueue(repository, area, title, body, priority?)` is the first producer; it never starts work. Intake's label triggers, review rounds and CI fixes become producers in a later change (see Consequences).
- **Scope.** An item's scope is `repository` + `area` (a short label the producer chooses: a feature, a directory, a theme). One live worker per scope. The worker claims every queued item of its scope and does them in one context, oldest first; an item enqueued into a live scope is claimed by the same worker.
- **Governor.** On each tick, the governor takes the first startable scope in order (work on open pull requests first, then priority, then oldest item) and starts one worker if every check passes, in this order: money (API-billed accounts only), subscription headroom, the box (memory, container count), and **decisions waiting on the owner ≤ 5 system-wide**. A failed check leaves the scope queued with the check's name as its reason; nothing is handled or dropped. "Slots" is not a setting: the checks are the capacity.
- **Ceiling.** A worker that has used 150k tokens (`pool.worker_tokens`) finishes the item it is on, writes a resume card (what is done, what is left, where it stopped) and ends; the governor starts a successor for the scope, which gets the card in its request. The ceiling, not a timer, bounds a context's life.
- **Durability.** Queue, claims and cards are rows in one SQLite file; a restart releases claims whose worker is gone and starts nothing twice.
- **Decisions, not reviews.** The owner reviews research and specs, not code; the valve that keeps the system from outrunning him counts open items in the inbox's `decide` lane, not pull requests.

## Consequences

- New module, new test project, a row in `Skeleton.Layers`, an input schema under `schemas/enqueue/v1/`, a tool in the server's MCP surface, a hosted service beside intake that ticks the governor.
- Intake keeps starting its own triggers until it is made a producer; until then two paths start work and the governor's checks bound only the pool's. That change is the next one, not this one: it touches intake's state file and its tests.
- The console gains the pool as the one answer to "what is queued, what runs, why": a later change adds it to the overview.
- Measured before the ceiling moves: tokens per item in a scope batch against one fresh context per item, on the first ten scopes the pool runs.

## Reopen if

- A scope batch measures worse than one context per item on tokens or on pull-request outcome.
- The owner needs the valve to count something other than the `decide` lane.
- Intake's triggers cannot be expressed as pool items without losing a waiting reason the console shows today.
