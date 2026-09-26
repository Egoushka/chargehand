# 0011. Concurrency, budgets and cost accounting

- Status: accepted
- Date: 2026-09-26

## Evidence (phase 2 spike)

- 132 gateway requests: the orchestrator's own price table (cache write = 1.25× input on GPT-5.6+-style models)
  matched the gateway's in-band `usage.cost` and its spend log to 0.0%. OpenCode's own `cost` summed to 1.81×
  less, because its config priced cache writes at zero and stateless `generate` calls belong to no session.
- Gateway spend-log rows equal proxy-captured requests (132 = 132), so reconciliation per run is possible
  (join: ADR 0012).
- The cheapest model's provider limit is 200,000 tokens per minute; three failures cool a model down for 30 s for
  every gateway client.

## Decision

- **Billing truth:** the gateway spend log. **In-run budgets:** OpenCode's per-message token counts priced by the
  orchestrator's own table (profile `prices`). OpenCode's `cost` is never used.
- Per run: hard cap $1.00 in v0. Per node: `max_input_tokens`, `max_usd` from the preset; a 15-minute deadline,
  then `interrupt`.
- Concurrency: at most 2 concurrent nodes per model; a per-model tokens-per-minute budget below the provider
  limit; back off on 429 in the scheduler; one retry on transport errors and on the masking proxy's 503, none on
  other 4xx; a failed node stops its dependents while siblings finish.

## Reopen if

OpenCode's cost matches the gateway within 5% for a week; the provider's rate-limit tier rises; measured node
contexts stay below 30k tokens.
