# 0082. Dollar caps count API-billed usage only

- Status: accepted
- Date: 2026-10-27

## Context

On 2026-10-26 the run log priced a day's runs at $85 while the gateway key that bills the API credit saw about $1. A run through a
subscription token is priced by the profile's `prices` table, so `max_usd_per_day`, `max_usd_per_month` and the forecast (ADR 0076)
would count money that was never spent and stop work that costs nothing.

## Options

1. Decide by the model's name. A model id says nothing about who pays; the same model runs on a key and on a token.
2. Record the credential on each call and run, from the runtime's auth source, and count dollars only for a billed one.
3. Stop pricing subscription runs. The price-table figure is still the useful "what would this cost" number.

## Decision

Option 2. `CallRecord.Credential` and `RunRecord.Credential` hold `api_key` or `subscription`, taken from `IWorkerRuntime.Credential`
(Claude Code: the credential it was built with; other runtimes: `api_key`). A record without the field counts as `api_key`, as it
was priced before.

`ActorSpend.Usd` sums billed records only, so day and month caps, the forecast and the pause on it count API-billed dollars.
`ActorSpend.Tokens` sums every record, so a token cap still binds a subscription. `ActorSpend.UsdAtApiPrice` sums all records at the
price table. `GET /v1/accounts/me` and the console's Accounts screen carry both: `usd` ("billed") and `usd_at_api_price` (additive).
A subscription is bounded by its rate-limit windows instead (#441).

## Consequences

- Records written before this change count in dollars as before; the past $85 day stays in the log's history of that day.
- A driven batch on a subscription already reports no dollars (`Usd` null), so it adds to `UsdAtApiPrice` nothing; its price-table
  estimate stays on the batch screens.

## Reopen if

A runtime can mix credentials inside one run (a fallback from key to token): the record then needs the credential per call everywhere.
