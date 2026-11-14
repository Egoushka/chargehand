# Daily pacing of a subscription's weekly cap

- Status: accepted 2026-11-14 (owner: "7 days = 100 / 7 = ~15 percent" per day). Amends ADR 0095 (soft daily pacing) with ADR 0098's readings.
- Date: 2026-11-14

## The job

The router sees a subscription's burn (ADR 0098 records the provider's seven-day utilization on every run) but only paces pools metered in dollars. A weekly
pool without `quota_usd` cannot be paced, so one heavy day can spend the week. Goal: no day takes more than its share, and no day is locked out.

## Split with the two daily caps (issue #923, ADR 0100, branch feat/dual-daily-caps)

That work bounds what one account's runs use of the subscription in a UTC day (`max_pct_per_day_7d`, percentage points, admission in Accounts, no carry-forward, fails
open without a reading). This one is the router's pace of the whole weekly window per pool, with carry-forward and the router's time zone, and it also counts use by
hand because it reads the provider's utilization, not a delta. Different scope and unit, so no shared key: `daily_share` is a fraction of the cap on a pool, and
`max_pct_per_day_7d` is points per account. Neither reads the other's setting; this change touches Economics, the profile schema's pool block, ADR 0095 and the
guide only, and nothing in Accounts, the run log interface or `AccountSettings`. Set together, the stricter one wins at its own layer.

## Decisions

1. A weekly pool with `pace_daily: true` and no `quota_usd` is paced by the provider's seven-day utilization (a fraction of the cap). `quota_usd` keeps the dollar
   pacing of ADR 0095.
2. `daily_share` (fraction, above 0 to 1, default 1/7, about 14.3 %) is the share of the weekly cap one day may use.
3. Allowed by the end of today = `min(1, daily_share * days)`, where `days` counts the days of the window up to and including today in the router's time zone,
   from the window's start (its `resetsAt` less seven days), at most 7. Unused share of earlier days is carried forward by this cumulative sum; overuse comes off
   later days. The pool is spent when utilization is at or above that.
4. A spent pool has no room: the router falls through to the next pool in the order that has room; when none has, the call waits (`RouteWait`) until the next day
   starts, when `days` grows by one. A reading with no `resetsAt`, or one whose window has reset, paces nothing. No reading: no pacing.
5. The reading is the newest seven-day window in the run log for the pool (the run's last route). A cap change mid-week shows up as the provider's next
   utilization; the math uses the newest reading and the configured share at the time of the call.
6. `daily_share` without `pace_daily`, or on a pool that has `quota_usd`, is a profile problem.

## Acceptance checks

Run with `dotnet test tests/Chargehand.Modules.Economics.Tests --filter WeeklyPacing` (all pass):

- day 1 allows 1/7; day 3 allows 3/7 (carry); day 7 allows the whole cap; the share is capped at 1.
- utilization at the allowance is spent; just under it is not.
- day boundaries and time zones: 23:30 UTC and 01:30 UTC are different days in a zone ahead of UTC, the same in UTC; the window start counts in the zone.
- a stale window (reset passed) and a window with no `resetsAt` pace nothing.
- a lower reading (cap raised mid-week) reopens the pool; a changed `daily_share` applies at once.
- the router prefers the next pool with room, waits until the next day with none, and `PoolRouting` reads the reading from the run log.
- 14-day simulation at constant demand beyond the share: never a full day with no admitted call (0 days locked out).
