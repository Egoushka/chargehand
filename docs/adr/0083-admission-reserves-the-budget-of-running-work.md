# 0083. Admission reserves the budget of running work

- Status: accepted
- Date: 2026-10-27

## Context

`AccountPolicy` compared a window's spend with the cap, and a running batch added nothing to spend until its result was written. Four
batches admitted together could each be clamped to the same remaining day budget, so the exposure was four times what was left.

## Decision

At admission an account's day and month dollar spend is raised by `RunService.ReservedUsd`: the `max_usd_total` of each unfinished
batch of the account and the `budget_usd` of each unfinished run. The existing refusal and clamp then see committed money:

- A person's run over the cap is refused with `budget_exceeded`, as before; one under it is clamped to what is left after the reservations.
- An automated start (`RunServiceStarter`) whose batch budget was cut by reservations waits (intake defers to the next poll); cut by
  spend alone it still runs on what is left, so a lone large batch never starves.
- With `pause_on_forecast` the month forecast includes the reservations and the next start's budget.
- A run's reservation ends when it leaves the run service; its real cost is then in the log. `GET /v1/accounts/me` and the console carry
  `reserved_usd` beside the spend.

The pre-flight forecast (#438) is not used for the reservation: the declared budget is a bound, the forecast a prediction, and the
budget is what the batch may actually spend.

## Consequences

- A run that spent part of its budget is counted twice while it runs (its calls so far, and its full budget). The error is on the safe side.
- Reservations live in the process: after a restart the resumed batches take them up again from their requests.

## Reopen if

Batches routinely finish at a fraction of their budget and the cap then idles: reserve the forecast's p90 instead.
