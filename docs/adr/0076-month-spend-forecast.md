# 0076. A month spend forecast per account, at the trailing 7-day rate

- Status: accepted (the forecast 2026-10-22; the pause and `automation.account` amended the same day)
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

## Amendment, 2026-10-22: automated starts are charged to an account, and may pause

Every automated start ran as the owner (`RunServiceStarter`, `actor: Principal.Owner.Name`), and the owner has no cap, so an
account's forecast could not hold one. The owner chose to charge them to an account (option A) over a monthly cap for the owner.

- **`automation.account`** names an account of `http.accounts` that must have `driven: true`. When it is set, every automated start
  (GitHub intake, Plane intake, schedules) is **admitted as that account**, through the same `RunService.AdmitAsync` a person's
  request goes through. Its credential rule, `repositories`, `max_concurrent_runs` and day and month caps apply, and its budgets
  are clamped to what the windows have left. The run is recorded with that account as its actor, so it counts in that account's
  spend and the account sees it. When the setting is unset, nothing changes: automated starts run as the owner, unrestricted.
- **`pause_on_forecast`** on an account (default false; it needs `max_usd_per_month`) holds that account's automated starts while
  the forecast passes the cap. A person's own request is never held by the forecast: only the starter of intake, Plane and
  schedules checks it.
- A refused or held start is retried later, as a halted server's is: GitHub and Plane intake defer the trigger to the next poll,
  and schedules retry the entry. Nothing is dropped. The poller's line on stderr names the account and says why.
- `accounts/v1` adds `automation` (this account carries the automated starts; the owner when none is named) and
  `pause_on_forecast`. The console's forecast panel says so, and shows "Automated starts are held" while they are.

## Consequences

- Two more spend reads per account per request of `/v1/accounts` (the 7-day window); each is a scan of the run log, like the day and
  month windows already are.
- A burst of one expensive day raises the forecast for a week, then falls off. That is intended: the forecast is a warning.
- Subscription runs are invisible to it, as they are to the dollar caps.
- With `automation.account` set, an automated batch can be refused where it ran before: for example a `repositories` list that
  leaves out an intake checkout, or a full `max_concurrent_runs`. The poller's line on stderr says which, and the trigger waits.

## Reopen if

The forecast warns often without the cap being reached (a 7-day window is too short), or a token cap needs a forecast too.
