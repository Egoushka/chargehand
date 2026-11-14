# 0100. Two daily caps: dollars for an API key, a percent for a subscription

- Status: accepted
- Date: 2026-11-14

## Context

`max_usd_per_day` (ADR 0076, 0081, 0082) reserves each admitted run's full `budget_usd` before it starts (ADR 0083), whoever pays. A run
on a subscription costs no dollars, yet an account with a $20 cap and a $5 task budget admitted four runs and refused the rest, so
`max_concurrent_runs` (8) was never reached (issue #923). The refusal said "spent" for money that was only reserved, and intake's
`queued` count showed 0 for triggers that were waiting, because it read the old `Notified` notices and not the `Waiting` list.

A subscription is bounded by its usage windows, not by dollars (ADR 0098). The owner wants to give chargehand a share of it: "10% of the
Claude subscription a day".

## Prior art

No prior-art query was run: the readings and the caps are chargehand's own (ADR 0082, 0098).

- Coverage: miss (not queried)

## Options

1. Reserve a token-equivalent dollar figure for a subscription run at a configured rate. A rate chargehand invented; ADR 0098 rejected the same guess.
2. Reserve nothing for a subscription run and leave it unbounded by money. Fixes #923, leaves the subscription with no cap.
3. Two caps: the dollar cap binds API-billed runs only; a new percent cap binds a subscription. Chosen.

## Decision

Option 3.

**API cap.** `max_usd_per_day` (and the month cap) count and reserve only runs on a priced credential (`api_key`, ADR 0082): the run's
reservation is its budget when `Priced(actor)` is true, and 0 otherwise. The refusal splits the figure: "($X spent, $Y reserved by
unfinished runs, 0 remaining)". `budget_exceeded` carries `reserved` beside `spent`, and `spent` is now only what was billed.
Intake's `queued` counts the `Waiting` list.

**Subscription cap.** Two optional settings on the account, one per usage window: `max_pct_per_day_7d` (share of the seven-day window) and
`max_pct_per_day_5h` (share of the five-hour window), each above 0 and at most 100. Unset means no cap on that window; both unset means the
subscription is uncapped. Flat keys, not a nested object, because they sit beside `max_usd_per_day` and read the same way in a profile; a
window the provider adds later is one more key. An account's subscription is the one its runs bill to: the server's own runtime credential
(claude_code on a CLI login or an OAuth token), since an account's own `model` credential is an API key. So one account is one cap pair per
subscription credential. The provider is named by the runtime: `claude` for claude_code; any other runtime (ACP agents such as Codex)
reports no utilization.

- **Measure.** Percentage points of the window used by the account's runs in the UTC day, from the `seven_day` or `five_hour` utilization the
  runs' calls carry (ADR 0098; the gateway's `anthropic-ratelimit-unified-7d-utilization` header, ADR 0088, is the same figure). For one
  window, among the account's calls since 00:00 UTC, group the readings by `resets_at`: a group adds its last reading minus its first (the
  first group) or minus 0 (a later group: the window restarted); the groups add. The seven-day window restarts about once a week, so the
  day's use is normally one group: 10 means 10 points of the week. A five-hour window restarts several times a day, so its figure is the
  sum over the day's windows: 50 means half of one five-hour window's worth of use in the day, and it can pass 100. It is a floor: the
  first call's own use before its first reading is not counted, and whatever the owner used by hand between two readings is counted, so the
  cap errs toward stopping early.
- **Which cap.** The owner's Claude Max 20x plan ($200) needs only the seven-day cap at 10; its five-hour window is left unset. A set
  five-hour cap is for a smaller plan. ADR 0096 independently makes a session wait out a five-hour rate limit and resume.
- **No reading yet today: fail open, visibly.** A cap that failed closed on it could never open, because only a run produces a reading.
  So a capped window with no reading since 00:00 UTC admits the start, and the standing says `no_reading` for it. Today only a single
  run on claude_code carries readings (ADR 0098); a driven batch, which is what intake starts, carries none until the gateway's
  `anthropic-ratelimit-unified-*` headers are written to the log (ADR 0098 option 4, still a follow-up). Until then the percent cap holds
  for single runs and shows `no_reading` for an account that only runs batches: not a silent pass, but not enforced either.
- **Not measurable.** A cap on an account whose runtime reports no utilization is refused as `subscription_not_measurable`, naming the
  account: a limit that cannot be enforced must not look enforced. The standing view shows the same state. With no cap set there is nothing
  to measure and nothing is refused.
- **Overshoot.** Runs underway keep using the window after the check, like every cap here (ADR 0050); the percent is a floor on the day's
  use and a run may pass it by its own share.
- **Refusal.** `subscription_cap_exceeded`, HTTP 429, "the daily subscription cap of 10% of the Claude seven-day window is used up (10.4
  points used, 0 remaining); it resets at <next UTC midnight>". `window` is `day`, `unit` is `pct_7d` or `pct_5h`. When both are set, the
  first one used up names the refusal.

**View.** `GET /v1/accounts/me` and the console account row gain `subscription`: `{provider, state, seven_day, five_hour}` where each window is `{cap_pct, used_pct, state}` and `state` is one of
`measured`, `no_reading`, `not_measurable`, `uncapped`, next to the API figures: "API $x of $20, Claude y% of 10%".

No database migration: readings are the run log's existing `subscription_usage` fields.

## Consequences

- A subscription-routed account admits up to `max_concurrent_runs` under the API cap.
- The seven-day share conflates chargehand's use with the owner's between readings. The cap is conservative by that much.
- A profile gains an optional key; the golden profile files change by one null.

## Reopen if

A usage endpoint appears that reports use by credential (not account-wide), or the gateway's per-run header makes a per-run delta exact.
