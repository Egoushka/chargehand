# 0095. A budget-aware model router drains pools in order and paces each day

- Status: proposed; amended 2026-11-08 (D-113), see the last section
- Date: 2026-11-08

## Context

A run's model comes from the preset's placeholder and the profile's `models` map (ADR 0026). Routing stays fixed per preset
and node kind (ADR 0019). Nothing in chargehand knows what a model costs against what is left to spend, when a balance
refills, or which models are free. The owner's evidence (gateway spend log, first week of November 2026) shows one day
taking about half of the week's spend. About half of a three-day window went to the top model, which the profile never
names but the gateway key allows.

This change found the likely cause by reading the code. A driven session never passes `--model`. `TaskRunner` builds the
`SessionTask` without `Model`, so `SessionDriver` adds no flag and the Claude Code CLI picks its own default. The batch
also never resolves the preset's placeholder through the profile's `models`, so it records and prices the session under
the placeholder, not the model that ran. Unmapped placeholders and an unset `intake_model` lead to the same CLI default
in the worker, intake, support-check and judge calls.

The owner wants model choice to be deliberate. There is to be no blanket pin or block (decision of 2026-11-08, chargehand#773): every model,
the top one included, stays available. The router picks it per call, the top model when the task warrants it and a
cheaper one when it does not.

The money comes from four sources, used in this order (owner input, 2026-11-08, corrected the same day on chargehand#773):

1. **Free models.** Their quotas are unknown.
2. **An Anthropic credit.** Refills monthly. No refill day is given, so a calendar month is assumed.
3. **The subscription.** Rate-limited rather than priced. It resets on Sundays at 09:00, and the timezone is not yet
   confirmed. Chargehand reaches it through the Claude Code runtime directly, so the gateway's spend log never sees it,
   and the router has to count this pool itself.
4. **Bought credit.** A one-off sum, never refilled, no expiry given. Credit bought directly comes last: it is spent
   only when the free models, the monthly credit and the subscription have no room.

The amounts are in the owner's gitignored profile, not in this repository.

## Prior art

Not searched. The prior-art tool (`prior_art_search`) did not answer from the session that wrote this ADR. What follows
is general knowledge, not facts from the base:

- Gateway-side routers spend against key budgets and fall back on errors: LiteLLM's router with budgets and fallbacks,
  and Bifrost's virtual-key budgets. They see only traffic through the gateway, so the subscription pool is invisible
  to them.
- Quality routers such as RouteLLM choose between a strong and a weak model per prompt. They do not pace a budget.

Coverage: miss. Recorded for a later `prior_art_ingest`.

## Options

1. **Pin one model everywhere** (the CLI flag, or a block at the gateway). This is the cheapest to build. The owner
   rejected it on 2026-11-08: it buys predictability by giving up the top model where it pays for itself.
2. **Keep fixed routing per preset (ADR 0019)** and tune the presets by hand. Nothing here knows the balance or the day,
   so one heavy day still empties the month.
3. **Route at the gateway** with per-key budgets and fallbacks. It reuses what the gateway already has, but it cannot
   see the subscription, does not know the task kind or size, and cannot escalate a task after a failed check.
4. **A router in chargehand** that knows the task kind and size, the pools, their balances and refills, and today's
   allowance, and that names the model on every call.

## Decision

Option 4. It is off by default: a profile without an enabled `pools` block runs exactly as before.

**Pools (`pools.order`)** are drained in the order listed. Each pool names the models it pays for, as gateway ids
(`provider/model`), and has one kind:

| kind | balance | refill | pacing horizon |
|---|---|---|---|
| `free` | none: always has room unless `daily_requests` is set and used up, or the caller has marked it exhausted | none | none, never banked |
| `fixed` | `total_usd`, spent from `start` on | none; after `expires`, empty | `start + spread_days`, or `expires` if that is earlier; with neither, the whole remainder is available |
| `monthly` | `total_usd` per period | day `refill_day` (default 1) at `refill_time` (default `00:00`) | the next refill |
| `weekly` | `quota_usd` per period, in API-price dollars; none means unmetered | `refill_weekday` at `refill_time` | the next refill |

Days, refills and expiries are read in `pools.time_zone` (an IANA id, default `UTC`).

**Daily allowance** for a paced pool:

`allowance = remaining at the start of today / days from the start of today to the horizon`

The division rounds the days up, with at least one. `remaining` is the balance less everything spent in the current
period before today. Today's room is `allowance - spent today`. Carry-over follows from the formula: a day that spends
less leaves more remaining, so every later day's allowance rises. A day that overshoots by one run shrinks the later
allowances by the same amount. A refill that happens during today counts only the spend after it. A pool past its
expiry, or with no balance left, has no room.

**Choosing.** The task's tier comes from `pools.routes`. The route key is the preset name the call runs under
(`driven`, `review`, `code`), or `intake` for intake and the support check. The key `key/size` (`driven/L`) wins if
present, else the key alone, else `pools.default_tier`. Tiers (`pools.tiers`) run from cheapest to strongest, and each lists its models in order of
preference. Tiers are about capability and pools are about who pays: one tier can hold a free model and a paid one.

The router walks the pools in drain order and takes the first pool that both has room today and pays for a model of
the tier. The model it picks is the first of the tier's list that the pool pays for. A pool with no room today falls
through to the next pool. When no pool has room, the call does not run: the decision is `wait`, until the earliest
moment a pool regains room (the next day boundary or the next refill). A tier that no pool pays for is refused when
the profile loads.

**Escalation.** A task whose verify step fails `escalate_after` times in a row (default 2) on its tier moves one tier
up for the rest of the task. It never moves down mid-task, and it stays at the top tier once there. The governor's
continuation (ADR 0045) carries the new tier on the same branch.

**An explicit model on every call.** With the router on, every runtime call is to carry the router's model, so the
Claude Code runtime never chooses one on its own. The pool block stays off by default; with it off every call keeps the
model its caller resolved. The call sites wired so far:

- the orchestrator's intake model and support check (key `intake`);
- the orchestrator's worker (key: the preset);
- a driven batch (key: the preset, size from the batch), asked once at its start, so a batch that no pool can take
  waits or fails before anything is created, and again before each session of each of its tasks, which also sets
  `CLAUDE_CODE_SUBAGENT_MODEL` so the session's nested research and review calls run the same model;
- GitHub intake, which holds a new issue in its queue (reason `pools`) while no pool has room, instead of starting it.

A run the router paid for records its pool, tier and model on the run record; spend per pool is read back from those
records. Direct API and MCP calls that find no room get a retryable `RateLimited`. Still on the caller's own model: the
probe, the eval judges, conversations and ACP, intake's continuation paths, and the `TaskTier` escalation. Separately,
a driven session with the router off now passes the model its preset resolves to through the profile's `models`, the
defect described under Context.

**Measuring.** The router reads spend per pool from records chargehand keeps. It does not track spend by hand, and for
the subscription it does not depend on the gateway log. `scripts/router-replay.py` replays finished tasks from the run
log and the learning records offline, with no model calls. Per pool it prints the projected spend, the days the
allowance would have run out, and the projected and actual cost per merged pull request. It exits non-zero when the
projected cost of the merged set is above the actual cost.

**Stop condition.** Before a task kind's cheap tier is used for real, about ten past issues of that kind are run on the
cheap tier. If its CI pass rate is more than 15 percentage points below the current model's (a starting value), that
kind leaves the cheap tier.

## Consequences

- The defect is fixed even with the router off: a driven session runs, and is priced as, the model its profile maps,
  never a CLI default.
- Drain order is configuration. The owner's order spends what lapses or resets first (the monthly credit at each
  refill, the subscription each Sunday) and keeps the bought credit, which never expires, for last. Any part of the
  monthly credit left unspent at a refill is still lost; the pacing makes this less likely but does not rule it out.
- An unmetered subscription always has room, so the bought credit after it is reached only for a tier the
  subscription does not pay for, or on a day its caller marks the subscription exhausted (a rate limit). Setting
  `quota_usd` on the subscription makes it pace like the other pools.
- The router decides; it holds no balance. A pool that a provider refuses (a 429, or a quota) is marked exhausted by
  its caller for the day, which is how unknown free quotas show up.
- Routing still never switches models within a node (ADR 0019). Escalation changes the tier only between a task's
  attempts.

## Reopen if

- The runtime cannot report subscription usage per run. The weekly pool then cannot be metered and stays unmetered,
  as the last resort.
- The replay's projected cost per merged pull request comes out above the actual cost.
- A cheap tier fails the stop condition for most task kinds.
- The owner gives a refill day, an expiry or a timezone that the calendar-month and UTC assumptions got wrong.
- A provider refuses without text the router recognises (`ChargehandException.BudgetRefused`, the rate-limit rule): the pool then never leaves the order.

## Amended 2026-11-08 (D-113)

The first version refused a call when a pool's configured amount or daily allowance was spent. Those figures are the owner's estimates, and the
subscription cannot be metered by chargehand at all, so refusing on them stops work the provider would still have served, while the provider's own
refusal is the one signal that is never wrong. This section replaces the parts of Decision, Choosing and Consequences that say otherwise; the rest stands.

**Order.** Pools drain in the order listed, and the order is configuration. The owner's: the subscription first, then the monthly credit of the plan's
API account, then the credit of the other providers. Free models sit in the cheap tier ahead of the paid ones and take mechanical work.

**Limits only warn.** `total_usd`, `quota_usd`, `daily_requests` and the paced daily allowance no longer remove a pool. A pool is out only when it has
expired, has not started, or its provider refused a call on it. A pool over a limit still takes the call: its standing carries a warning (`empty`,
`today's allowance is spent`, `today's requests are used`), `Routed.Warning` holds it, and one line `chargehand: pool '<name>': <warning>` goes to the
operator's console for each routed call.

**A provider's refusal drains the pool, and the call falls through.** A run that ends in a spent budget (a 402, "payment required", "budget has been
exceeded") takes its pool out until the pool's next refill, or the next day when it has none. Any other rate limit (a 429, a used quota) takes it out
until the next day. The call then goes to the next eligible pool:

- the orchestrator's single run starts again on the next pool when the refused try produced no artifacts, with the same run id and one run record
  (the last try's), at most as many tries as there are pools;
- a driven task asks the router before each of its sessions. A session that ends on a refusal marks its pool, and the task goes on in a new session
  on the next eligible pool, seeded from the commits so far (the stall continuation of ADR 0044, with the pool's change as its reason), at most as many
  moves as there are pools. When every eligible pool is out, the task ends as a retryable rate limit, `[partial]` when it has commits, and the batch's
  start gate halts until the router's `until`. While another pool can still take the batch, a refusal does not close the global start gate;
- the router answers `wait` only when every eligible pool of the first tier that has any is out.

**Data classes.** Every routed call carries the class of the data it touches, read from the repository path the run was asked about, and a lookup
that fails or finds nothing is the safe class:

| class | what it is | may reach |
|---|---|---|
| `public` | GitHub reports the repository public (the visibility lookup of ADR 0093) | any model |
| `owner_private` | named in `pools.owner_private_repos` (`owner/name` or `owner/*`), which the owner keeps for repositories with no personal or employer data | any model |
| `restricted` | everything else: a checkout with a directory from `pools.restricted_path_segments` in its path (default `work`), other people's words, secrets, a private repository nobody vouched for, an unknown | only the models a pool lists in `no_training_models` (`*`: all of the pool's models), those whose provider does not train on prompts through that pool |

A pool that does not keep a model private is skipped for restricted data as if it did not pay for the tier. A tier with no eligible model moves the
call one tier up, never down. With none anywhere the router answers `blocked`: the call fails with `repository_not_allowed`, is never held or retried,
and names `no_training_models` and `owner_private_repos` as the fix. An enabled block must list `no_training_models` on some pool, or the profile is
refused. The class is recorded on the run's route. The support check reads no repository and always runs as `restricted`; GitHub intake classifies an
issue by its repository before it holds it.

**Per session (D-113).** The task runner asks the router before each session, through `ISessionRouter`, which the batch gives it. A task's sessions can
therefore run on different pools and models, and the run record of the task (one per task, as before) carries the route of its last session in `route`
and, when the sessions used more than one route, every route in order with what the sessions on it cost in `routes`. The router charges those costs to
their pools, not the whole result's cost to the last pool. The batch's own run record carries no route any more: its cost is the sum of its tasks'
records, which carry theirs. The batch's data class is the one its start classified; a session never classifies again, so restricted data reaches
only the pools that keep it private, after a refusal as before it.

What a session of a task chooses by:

- the first session and the moves after a refusal use the batch's preset and its largest task's size, as the batch's start did; a task's own size
  could choose a cheaper tier for a small task, and does not yet;
- the CI-fix sessions of a task (ADR 0046) are routed as well: a fix session that finds every pool out ends the fix as failed, with the pull request
  as it stands, and a task's own outcome is not changed by it;
- a session with no commits before the refusal has nothing to continue from: the next one starts again from the base commit on the next pool, with the
  task's goal as it was.

Where the record is short of the truth, and why it is left:

- a task that throws before its record is written (a cancel, a crash) has no run record, so its sessions' spend is not charged to a pool; the pools
  warn on limits and never refuse on spend, so the cost of this is a late warning;
- a session a restarted server takes up again keeps running on the pool it started on, but carries no route in the record, so its cost is not charged to
  that pool. The tasks after a restart route as usual;
- the cost of the CI-fix sessions is not in the task's run record today (ADR 0046), so it is charged to no pool either.
- the number of moves a task has made lives in memory, so a task a restart carries on may move once more than the router has pools; each move still asks the
  router, which answers for the pools that are out, so it ends when none can take it;
- a restarted task cut between two sessions that finds every pool out ends retryable, without a draft for its earlier commits: the draft is made from the
  last session's output, which the restart no longer holds.

The subagents of a session (`CLAUDE_CODE_SUBAGENT_MODEL`) follow the session's own route now, set per session by the task runner. They were set once from the
batch's route, in the environment that holds the batch's one credential: that made the environment two variables, which the runner's route (one credential,
read as the only value) and the handover's secret scan (which was given the model's name as a secret) did not expect.

## Amended 2026-11-11 (soft daily pacing)

A pool may set `pace_daily: true`. Once its paced daily allowance is spent, the router passes over it for the next pool in the order that has room, so one
day cannot eat a weekly subscription cap (about a seventh of it per day). It stays soft: when no other pool can take the call, the paced pool is still
chosen, with its warning. Nothing leaves the order because of it, and the provider's refusal remains the only thing that drains a pool. The setting needs an
amount to pace, `total_usd` or `quota_usd` on a weekly pool; a profile that sets it on a pool without one is refused.

"Has room" means the pool is not out (expired, not started, refused). A later pool that is itself over a limit still takes the call ahead of a paced pool,
because limits only warn; pacing moves work only between pools the router would otherwise use, and within the current tier.
