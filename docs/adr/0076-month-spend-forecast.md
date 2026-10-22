# 0076. A month spend forecast per account, at the trailing 7-day rate

- Status: accepted for the forecast; the pause of automated starts is proposed (see Open)
- Date: 2026-10-22

## Context

The owner asked for a cap forecast (CHARGEHAND-216): see that an account is heading past its monthly cap before admission starts
refusing it, and pause new starts in time. The only monthly caps are per account: `max_usd_per_month` and `max_tokens_per_month`,
checked at admission against the account's spend since the start of the UTC month (ADR 0050, `AccountPolicy`). The owner has no caps.
Spend is priced usage: a run on a subscription adds no dollars.

## Options

1. Forecast against each account's `max_usd_per_month`, or add a server-wide monthly cap. The account caps exist and are enforced; a
   server-wide cap would be a new setting and a new admission rule.
2. Project at the month-to-date average, or at the trailing 7-day rate. The month-to-date average lags a burst by weeks; the 7-day
   rate follows it within days and still smooths a single expensive run.
3. When the forecast passes the cap, refuse every new run of the account, pause only automated starts (intake, Plane, schedules), or
   only warn. Refusing a person's run before the cap is reached changes what admission promises; automated starts are the ones nobody
   is watching.

## Decision

The owner chose all three recommendations (1 per account, 2 trailing 7 days, 3 automated starts only, opt-in per account).

- `SpendForecasts.Of` (Accounts module) is pure: `rate_per_day` = spend of the last 7 days / 7; `projected_usd` = month so far +
  rate × days left in the UTC month; `over_cap` when the projection passes `max_usd_per_month`; `cap_reached_at` when the rate
  reaches the cap within the month, or now when it is reached already.
- `GET /v1/accounts` and `GET /v1/accounts/me` carry it as `forecast`, additively. Dollars only: a token forecast can follow if a token
  cap needs one.
- The console's Accounts screen shows it for the selected account, with a warning when it passes the cap.

## Open: the pause

Every automated start runs as the owner (`RunServiceStarter`, `actor: Principal.Owner.Name`), and the owner has no cap, so an
account's forecast cannot hold one. Either automated starts are charged to a named account (`automation.account`), so its caps,
forecast and opt-in apply, or the owner gets a monthly cap of its own for them. The pause lands with that choice, in its own change.

## Consequences

- Two more spend reads per account per request of `/v1/accounts` (the 7-day window); each is a scan of the run log, like the day and
  month windows already are.
- A burst of one expensive day raises the forecast for a week, then falls off. That is intended: the forecast is a warning.
- Subscription runs are invisible to it, as they are to the dollar caps.

## Reopen if

The forecast warns often without the cap being reached (a 7-day window is too short), or a token cap needs a forecast too.
