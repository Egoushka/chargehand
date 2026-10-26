# 0081. Automated work is bounded by money and slots, not by a count of starts

- Status: accepted
- Date: 2026-10-26

## Context

GitHub intake (ADR 0051) and Plane intake (ADR 0067) capped triggered batches at `max_starts_per_day` (default 10) in any
24 hours, and pull request reviews at `max_reviews_per_day` (default 20). A trigger over the start cap was refused with
one comment and marked handled: it never started unless someone applied the label again.

Evidence from a server that ran it (Egoushka/chargehand#529):

- 27 issues were labelled at once with `max_concurrent` 4. The state file holds 25 `wait:` notices; 5 issues started in
  the next 1 h 45 min. Then the label was taken off the waiting issues by hand (the issues' event lists show it; checked
  on three), because nothing showed what was still queued: the waiting triggers had no record of their own.
- 16 were labelled again that evening: 4 started and 12 were refused by the cap, logged `ignored: cap` and marked
  handled. The same cap later refused another repository's issue at 10/10 and was raised by hand to 15, then 40.

A count of starts is a poor proxy for what it was meant to limit. A small task and a large one count the same, and the
count refuses work outright when it should hold it. Money already has a bound: ADR 0076 admits and charges every
automated start (GitHub, Plane, schedules, reviews; all through the same `RunServiceStarter`) to `automation.account`,
with `max_usd_per_day`, `max_usd_per_month` and an opt-in `pause_on_forecast`, and a refused start is retried at the
next poll. Runs that go through a priced gateway make those dollar caps bind. Slots have bounds too: intake's
`max_concurrent`, `driven.max_parallel_total`, and memory admission (ADR 0045).

## Options

1. Keep the count, make it wait instead of refusing. Keeps a second budget beside the dollar one, in a unit that does
   not measure cost; the owner would still raise it by hand.
2. Remove the count; bound money by the account and concurrency by the slots. One budget, in dollars, where cost is.
   A server without `automation.account` has no daily bound on starts, only per-batch token and dollar caps and
   `max_concurrent`.
3. Convert the count to dollars inside intake. Duplicates ADR 0076.

## Decision

Option 2.

- `max_starts_per_day` (GitHub and Plane) and `max_reviews_per_day` are ignored. Profile schema v1 is published, so the
  keys stay accepted (any integer), are marked `deprecated`, and the server writes one stderr line per key at startup.
- A trigger that cannot start yet waits in a queue in the intake state file (`waiting`, next to the run log), with why:
  `max_concurrent`, `admission` (the server or the account refused it: halted, too many unfinished runs, a dollar cap,
  a forecast pause) or `checkout`. It is not marked handled until it starts. A restart reads the queue back, and the
  console's `/v1/intake` lists it. A waiting trigger whose label, comment or issue is gone leaves the queue and is
  logged `ignored: withdrawn`.
- Plane intake keeps its wait (the item stays in the trigger state and is read again) without a queue of its own.

## Consequences

- Turn on `automation.account` with `max_usd_per_day` and `max_usd_per_month` (and `pause_on_forecast` if wanted) on
  any server that runs intake; without it, nothing bounds a day's spend but the per-batch caps times the slots.
- `/v1/intake` drops `caps.max_starts_per_day` and `starts`, and gains `waiting`. The console's starts panel lists the
  waiting triggers in place of the cap meter.
- The state files drop the start times (`starts`, `review_starts`); older files still load.
- Removing the label from a waiting issue is the way to take it out of the queue.

## Reopen if

A day's spend passes the account's daily cap while every start was admitted, or a burst of cheap starts harms
something the dollar caps do not see (GitHub rate limits, the runner's disk).
