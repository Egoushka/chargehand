# Two daily caps: dollars for an API key, a percent for a subscription

- Status: accepted 2026-11-14 (maintainer's words: "two of em, first is api based and second is % based"). Issue #923.
- Decision: [ADR 0100](../adr/0100-two-daily-caps-dollars-for-api-keys-percent-for-a-subscription.md)

## Problem

A subscription run reserves its dollar budget against `max_usd_per_day` though it costs none, so a $20 cap admits 4 runs of $5 and
`max_concurrent_runs` is never reached. The refusal says "spent" for reserved money; `queued` shows 0 for waiting triggers.

## Design

See the ADR. Touch points: `AccountSettings`, `AccountPolicy.Refusal`, `RunService.AdmitAsync` and `Start` (reservation), `IRunLog`
(`SubscriptionReadingsAsync`), `AccountStanding`, `ConsoleAccounts`, `ConsoleSources`, `profiles/profile.schema.json`, `docs/guide/server.md`.

## Acceptance checks (machine-run)

| # | Check | How it runs |
|---|---|---|
| A1 | An account on a subscription runtime with `max_usd_per_day: 20` and a $5 budget admits `max_concurrent_runs` runs; a priced (API key) one still stops at 4 | `TwoDailyCapsTests` |
| A2 | The dollar refusal says "spent" for billed dollars and "reserved" for unfinished runs' budgets, and names both amounts | `TwoDailyCapsTests` |
| A3 | With seven-day readings worth the cap used since 00:00 UTC the next start is refused as `subscription_cap_exceeded`, naming the points used and the cap; below it the start is admitted | `TwoDailyCapsTests` |
| A4 | The five-hour figure sums the day's windows; a window that restarted (new `resets_at`) adds its own reading; yesterday's readings count for nothing | `SubscriptionShareTests` |
| A5 | A capped window with no reading today admits the start and the standing says `no_reading` |
| A5b | A subscription with only the 7d cap, one with both and one with neither: only a used-up capped window refuses, an unset window never does, neither set admits everything | `TwoDailyCapsTests` | `TwoDailyCapsTests` |
| A6 | A cap on a runtime that reports no utilization refuses as `subscription_not_measurable`; the standing shows `not_measurable` | `TwoDailyCapsTests` |
| A7 | The account standing shows API dollars, the subscription percent, the cap and the state | `TwoDailyCapsTests` |
| A8 | `max_pct_per_day_7d` or `max_pct_per_day_5h` outside (0, 100] is a profile problem | `TwoDailyCapsTests` |
| A9 | Intake's `queued` counts triggers on the `Waiting` list | `TwoDailyCapsTests` |
| A10 | `scripts/check.sh` (or the devbox form in CLAUDE.md) passes | CI |
