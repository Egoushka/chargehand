# The task pool: one queue for every source, a governor that starts scoped workers

- Status: accepted 2026-11-12 (owner approved the design in conversation: chargehand is the pool, scopes run as batches, the valve counts decisions). Builds on ADR 0099 (chargehand is the task pool), ADR 0081 (a queue with reasons), ADR 0090 (the event log).
- Date: 2026-11-12

## The job

Work reaches chargehand from an issue label, a review round, a CI failure, a schedule, the console and the owner's agent
conversations. Today each source starts its own work; the owner's chats are the distribution point and nothing answers
"what is queued, what runs, why did nothing start" for all of them. The pool is that one answer: every source enqueues
an item, nothing but the governor starts, and a worker takes every item of one scope in one context until a token
ceiling, then writes a resume card for its successor.

## Decisions

1. **A module, `Chargehand.Modules.Pool`, layer 3** (Contracts and the Kernel only; Sessions and Intake sit above and take
   work from it). One SQLite file, `pool.db`, beside the event log; schema versioned by `PRAGMA user_version` like
   `SqliteEventLog`. Public types in `Chargehand.Modules.Pool.Api` only; source-generated JSON; no reflection.
2. **An item** is `key` (opaque, generated), `source` (who enqueued: `enqueue`, later `intake`), `repository`
   (`owner/name`), `area` (a short label the producer chooses), `title`, `body`, `priority` (0 normal, higher first),
   `pull_request` (a number when the item is work on an open pull request, else null), `enqueued_at`, `status`
   (`queued`, `claimed`, `done`), `worker` (the live worker holding it), `reason` (the check that last held it, for a
   queued item), `claimed_at`, `done_at`.
3. **Scope** is `repository` + `area`. One live worker per scope. A worker claims every queued item of its scope at start
   and each time it asks for the next item (`NextAsync`), oldest first; so an item enqueued into a live scope goes to that
   worker. A scope with a live worker is never started again.
4. **The governor** ticks. On a tick it orders startable scopes (no live worker, at least one queued item): scopes with
   any pull-request item first, then highest priority, then oldest `enqueued_at`. It takes the first and runs the checks
   in registration order; the first failing check's name becomes the `reason` on the scope's queued items and the tick
   ends with no start. When every check passes it asks the starter for a worker; a refusal from the starter is a reason
   too (`starter`). One start per tick is the start-rate breaker. "Slots" is not a setting: the checks are the capacity.
5. **Checks** are `IPoolCheck { Name; HoldAsync(scope) → detail or null }`. The module defines the shape and one check,
   `decisions`: open decisions waiting on the owner must be ≤ `pool.max_open_decisions` (default 5, system-wide), counted
   by a function the host supplies (the console inbox's `decide` lane). Money, subscription headroom and the box arrive
   through the starter in this cut: the server's starter admits the run the way intake's does (ADR 0076, 0083, 0092) and
   its refusal text is the detail. Owner's rule: the valve counts decisions, not pull requests; he reviews specs and
   research, not code.
6. **The ceiling.** `pool.worker_tokens` (default 150000). The worker reports its usage (`ReportAsync(worker, tokens)`);
   past the ceiling `NextAsync` hands out nothing more and says why (`ceiling`). The worker finishes the item it is on,
   writes a resume card (`what is done`, `what is left`, `where it stopped`) and releases itself. The scope is then
   startable again; the governor's next start for it carries the latest card to the successor. The ceiling, not a timer,
   bounds a context.
7. **Durability.** Items, claims and cards are rows. On start the host calls `ReleaseLostAsync(liveWorkers)`: a claim
   whose worker is not in the set goes back to `queued` with reason `lost`; nothing done is redone, nothing claimed by a
   live worker is started twice.
8. **The `enqueue` tool** (MCP, `schemas/enqueue/v1/`): `repository`, `area`, `title`, `body`, optional `priority`
   and `pull_request`. It writes one item and returns `{key, repository, area, position}`; it never starts work.
   Refusals carry stable codes and never quote the refused value: `400 invalid` (a field missing, too long, or
   `repository` not `owner/name`), `413 too_large` (body over 16 KiB), `422 secret` (`DiffScan.ScanText` finds a
   credential in title or body), and `400 invalid` for a repository not in `automation.github.repositories`. The caller is
   the owner's key only: an account key and a session's run token (which also acts for the owner) are refused with
   `forbidden`, so a run cannot queue work for workers outside its own grant. No new authentication surface. The tool is
   listed only when `http.pool` is set.
9. **The worker is a run.** The server's starter composes a non-interactive run request from the scope's claimed items
   and the card, starts it through the same admission as intake, and binds the run to the worker: when the run ends,
   its usage is reported, the items it carried are done and the worker is released. A run stopped at the worker's token
   ceiling (`cost_cap_reached` with tokens used) instead releases with a card built from its summary, so its items go back
   to the queue for a successor. After a restart, a worker whose batch the run service took up again stays live and is
   watched as before; every other claim is released as `lost`. A worker that pulls items one by
   one from inside the run needs a worker-facing tool; that is a later change (see Left for later).

## Acceptance checks (machine-run)

`tests/Chargehand.Modules.Pool.Tests`, each a test by name; `scripts/check.sh` exits 0.

- `Enqueue_writes_a_queued_item_and_never_starts`: after `EnqueueAsync` the item is `queued`, no worker exists, the
  governor has not been asked.
- `Scope_claims_every_queued_item_oldest_first`: 6 items in scope A and 2 in scope B, two ticks → two workers; A's worker
  receives all 6 in enqueue order; B's worker receives its 2; no item is handed out twice.
- `Item_enqueued_into_a_live_scope_goes_to_its_worker`: with A's worker live, a new A item is returned by A's `NextAsync`
  and no new worker starts for A.
- `Governor_orders_pull_requests_then_priority_then_age`: scopes {old, priority 0}, {new, priority 2}, {newer, pull
  request} start in the order pull request, priority, age.
- `Failed_check_leaves_the_scope_queued_with_its_name`: a check returning a detail → no start, every queued item of the
  scope carries `reason` = the check's name, nothing dropped or handled.
- `Decisions_valve_holds_every_source_and_frees_one_when_one_closes`: 6 open decisions → no start from any scope; 5 →
  exactly one start.
- `Starter_refusal_is_a_reason`: the starter refuses → items carry `reason` = `starter`, next tick asks again.
- `One_start_per_tick`: two startable scopes, one tick → one worker.
- `Ceiling_ends_the_worker_with_a_card_and_the_successor_gets_it`: report 150001 tokens → `NextAsync` returns `ceiling`;
  the worker writes a card and releases; the next tick starts a successor whose start request carries that card; the
  successor receives the items left.
- `Restart_releases_lost_claims_and_starts_nothing_twice`: items claimed by worker w1, then `ReleaseLostAsync([])` →
  they are `queued` with reason `lost`; done items stay done; a claim held by a live worker (`ReleaseLostAsync([w1])`)
  is kept and the scope is not started.
- `Store_survives_reopen`: close and reopen the file → items, claims and the card are still there.
- `Enqueue_tool_refuses_bad_input_with_stable_codes` (server tests): missing field → `invalid`; 16 KiB + 1 body →
  `too_large`; a body holding a credential-shaped literal → `secret`, and the refusal text does not contain it.
- Architecture: `Chargehand.Modules.Pool` at layer 3 references only Contracts and the Kernel; public types in `.Api`.

## Measured (to be run on the first ten scopes the pool runs)

Tokens per item in a scope batch against one fresh context per item, and pull-request outcome per item. The ceiling
moves on this number, not on opinion (ADR 0099, Reopen if).

## Left for later

- Intake's label triggers, review rounds and CI fixes as producers (`source: intake`); until then two paths start work.
- A worker-facing tool (`pool_next`, `pool_done`) so a run pulls items one by one and reports usage mid-run; this cut
  hands the worker its items at start and settles them when the run ends.
- The console's overview of the pool: queued, live, reasons.
- Money, headroom and box as named checks of their own, once intake is a producer and the starter's admission can move
  into them.
