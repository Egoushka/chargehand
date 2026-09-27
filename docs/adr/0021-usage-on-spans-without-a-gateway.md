# 0021. Usage on call spans when no gateway records the calls

- Status: accepted
- Date: 2026-09-27

## Context

ADR 0012 keeps usage and cost on one side only, the gateway's generations, so Langfuse never counts a call twice.
Claude Code on a subscription (ADR 0020) reaches Anthropic without that gateway: LiteLLM cannot relay a
subscription token on `/v1/messages` (BerriAI/litellm#42170, open), and a masking proxy's `/anthropic` route
forwards to Anthropic directly. A live run through such a proxy reached Langfuse with 11 observations and a total
cost of 0, while the run log priced it at $0.155.

## Options

1. Usage and cost on call spans, switched on per profile.
2. Wait for LiteLLM to relay subscription tokens and route those calls through it.
3. Claude Code's own OpenTelemetry export to a metrics backend: cost per session, not per orchestrator call, and
   outside the run's trace.

## Decision

**Option 1.** `telemetry.usage_on_spans` (default false) puts `langfuse.observation.usage_details` (input, output,
cache read, cache write) and `langfuse.observation.cost_details` (`total`, from the profile's price table) on every
`chargehand.call` span. Only the profile owner knows whether a gateway records the calls, so it is a flag, not
inferred from the runtime or the URL.

## Consequences

- Set it only for calls no gateway records. With a LiteLLM gateway it double-counts; ADR 0012 still holds there.
- Cost is the price table's: for a subscription it is the API-equivalent cost, not a bill.
- Output tokens per call follow the runtime's reporting; for Claude Code a turn's output lands on its last call
  (ADR 0020), so per-call output is lumpy while totals are exact.

## Reopen if

LiteLLM relays subscription tokens on `/v1/messages`: route through it and drop the flag from those profiles.
