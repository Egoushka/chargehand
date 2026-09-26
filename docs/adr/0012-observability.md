# 0012. Observability

- Status: accepted
- Date: 2026-09-26

## Context

Usage and cost live on one side only, the gateway's generations. Orchestrator spans carry structure,
`prompt_chain`, contract status and node metadata without `gen_ai.usage.*`. The two sides are joined on the
OpenCode session ID. Proposal A5 was to make OpenCode send its session ID as `prompt_cache_key`, which the
gateway already maps to its trace session.

## Evidence (phase 2 spike)

- **A5 does not work as written.** With `setCacheKey: true` on an `@ai-sdk/openai-compatible` provider, OpenCode
  2.0.16 sent no `prompt_cache_key`; every gateway row got the gateway's per-key-per-day fallback session.
  OpenCode does send the session ID as the HTTP headers `x-session-id`, `x-opencode-session` and
  `x-session-affinity`, which the gateway's trace hook does not read. Session `metadata` does not reach the
  gateway.
- OpenCode sends `traceparent`, but with one trace id per server process; a caller's `traceparent` on the prompt
  request is ignored. The "no propagation" premise stands.
- OpenCode's own OTLP exporter (`OTEL_EXPORTER_OTLP_ENDPOINT`) works but emitted 481 internal spans for one
  session, none with a session ID, usage or a generation — not useful.
- OpenTelemetry .NET → Langfuse OTLP/HTTP works; `langfuse.session.id` maps to the Langfuse session.

## Decision

- Spans: run → node → call, OTel GenAI semantic conventions pinned at 1.44.0, exported over OTLP/HTTP to the
  orchestrator's own Langfuse project. Node spans set `langfuse.session.id` = OpenCode session ID.
- Gateway join, in the run log (Langfuse sessions don't span projects): the orchestrator's gateway key is
  dedicated to it, so each OpenCode assistant message joins to one spend-log row on (key, model, prompt tokens,
  completion tokens, start time ± 10 s). Node spans link to the gateway's rows by that join.
- Preferred upgrade, outside this repository: the gateway's trace hook also reads `x-session-id` — only if the
  masking proxy forwards that header (UNKNOWN). Then the join key becomes exact.
- OpenCode's own exporter stays off.

## Reopen if

OpenCode starts honouring a caller's `traceparent` or sending `prompt_cache_key` for OpenAI-compatible
providers; the gateway hook reads `x-session-id`; the token/time join mismatches more than 1% of rows.
