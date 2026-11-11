# 0098. A subscription run records how much of its usage windows it had used

- Status: proposed
- Date: 2026-11-09

## Context

A subscription is not paid per token: it is a share of a five-hour window and a seven-day window. ADR 0082 records the credential and
counts API dollars only, and keeps the price-table figure (`usd_at_api_price`) as "what it would cost". That figure is not what a
subscription runs out of, so nothing shows the thing that binds: how far into its windows the subscription was when a run ended. A
daily view of subscription use next to API dollars needs that share, from the provider, not from us.

The Claude Code CLI already reports it. Its stream-json output carries a `rate_limit_event` whose `rate_limit_info` has `status`,
`resetsAt` (epoch seconds), `rateLimitType`, `utilization` and `unifiedWindows.{five_hour,seven_day}.{utilization,resetsAt}`;
utilization is a fraction of 1 and can pass 1. The field is in the schema of the pinned CLI and of the two newer versions checked,
read from the packaged binaries, not from a live stream (see "Not run"). It is absent when the session is on an API key. The egress
gateway reads the same numbers from the `anthropic-ratelimit-unified-5h/7d-utilization` response headers, for driven batches only
(ADR 0088).

## Prior art

No prior-art query was run: the source is the runtime's own event stream.

- Coverage: miss (not queried)

## Options

1. Read the CLI's `rate_limit_event` from the stream the runtime already parses. No new call, no polling, and the same figures as the
   headers. Only the runtime that is on a subscription sees it. Chosen.
2. Poll a usage endpoint. It adds a request and a credential use per poll, a second place to hold the subscription's token, and a
   reading taken at a different moment from the run it is attached to.
3. Derive a percent from the run's tokens against a quota. chargehand knows no token quota for either window, so the divisor would be
   a guess presented as a measurement. Rejected: a percent that chargehand invented is worse than none.
4. Read the gateway's headers only. Right for driven batches, which run in containers behind it, but it says nothing about a run on
   a Claude Code runtime that does not go through it. Kept as the follow-up for batches.

## Decision

Option 1.

- `SubscriptionUsage` (contracts) holds `five_hour` and `seven_day`, each `{utilization, resets_at}`, and two derived fields: `used`,
  the larger of the present windows, and `window`, the name of that window (the weekly one on a tie). Utilization is a fraction of 1;
  multiply by 100 for a percent. A window that was not reported is absent, never `0`.
- The Claude Code runtime reads each `rate_limit_event` and keeps the latest value of each window. Each assistant message is stamped
  with the reading in force when it began (`WorkerMessage.Subscription`). The `unifiedWindows` object is marked internal in the CLI's
  schema, so the top-level `utilization` stands in for the window named by `rateLimitType` when it is missing. An event that names any
  other window (an Opus-only one, the overage one) is no reading.
- A session on the fallback API key reads no events and stamps none: its calls are billed, and a share of the subscription's window
  would be wrong for them. A run records the field only when its runtime's credential is `subscription`.
- `CallRecord.SubscriptionUsage` is the reading on that call. `RunRecord.SubscriptionUsage` is the reading on the run's latest call,
  which the nodes of a split plan report side by side and the later call wins. Both are optional and omitted when null, so older
  records read as before.
- No new endpoint. `GET /v1/runs` rows (`run-summary/v1`) gain optional `credential` and `subscription_usage`, additive; the signed
  `result/v1` is unchanged. The JSON is `{"used":0.61,"window":"seven_day","five_hour":{"utilization":0.18,"resets_at":"…"},"seven_day":{…}}`.
- A per-day share is the change in `seven_day.utilization` between a day's first and last reading that carry the same
  `seven_day.resets_at`; a new `resets_at` starts a new window. A reader must not add `used` across runs: each is a level, not a spend.

## Consequences

- A subscription's use can be shown as a percent of its window, per day, beside the API dollars that `credential != subscription`
  rows add up. This change records the data; the ledger and its dashboards read it in their own change.
- A run on the subscription whose stream carried no event records no field. The reader treats that as unknown, not as `0`.
- A `rate_limit_event` that arrives after a run's last assistant message is not kept: messages are stamped forward only. The window
  shows up in the next run's records.
- Driven batches record nothing yet. The follow-up is to read the event in the session driver's stream, or to have the gateway export
  the last utilization it saw.
- The reading is the provider's own number at the time of the call, so it includes use that did not come from chargehand (an
  interactive session on the same plan). That is what the window is, and the right number for "how close to the limit".
- Records and rows written before this change have no field.

### Acceptance checks

- `SubscriptionUsageTests.Both_windows_are_read_with_their_reset_times`
- `SubscriptionUsageTests.The_top_level_fields_stand_in_for_the_window_that_is_limiting`
- `SubscriptionUsageTests.A_utilization_past_one_is_kept_for_a_window_run_past_its_cap`
- `SubscriptionUsageTests.An_assistant_message_carries_the_reading_in_force_when_it_began`
- `SubscriptionUsageTests.A_turn_on_the_fallback_key_carries_no_reading_and_a_stray_event_is_not_kept`
- `SubscriptionUsageTests.A_subscription_run_records_its_latest_reading_on_the_run_and_on_each_call_and_the_run_list_serves_it`
- `SubscriptionUsageTests.A_subscription_run_that_was_told_no_reading_records_none_and_not_a_zero`
- `SubscriptionUsageTests.An_api_key_run_records_no_reading_even_when_a_message_carries_one`
- `SubscriptionUsageTests.A_record_with_no_reading_writes_no_field_and_reads_back_as_none`
- `SchemaFixtureTests`: the four `subscription` fixtures under `schemas/run-summary/v1/examples/`

### Not run

No live stream was captured. The event's shape is taken from the zod schema inside the pinned CLI binary (and two newer ones), so a
CLI that renames a field would give no reading, not a wrong one. First check after merge: a run on the subscription, then read its
`subscription_usage` from `GET /v1/runs`.

## Reopen if

The CLI drops or renames `rate_limit_event` or `unifiedWindows` (the readings stop and records go null); a plan adds a window the
runtime should record (the model-specific weekly windows are ignored now); driven batches need the number per call rather than per run;
or the provider publishes a quota in tokens, which would make a derived percent honest.

The number is 0098 because 0097 was taken by earned autonomy while this was written.
