# 0042. Telemetry for driven sessions

- Status: accepted
- Date: 2026-10-10

## Context

An orchestrator run reaches Langfuse as `chargehand.run`, `chargehand.node` and `chargehand.call` spans (ADR 0012), with
usage and cost on call spans when no gateway records the calls (ADR 0021). A driven batch (ADR 0039) emitted no span:
`DrivenRun` and `TaskRunner` each minted a random trace id for their `result/v1`, so `trace_id` pointed at nothing.
On a subscription (`credential_delivery: environment`) Claude Code in the session container calls Anthropic directly,
so no gateway records those calls either, and Claude Code itself exported nothing.

What the host knows about a session: the outcome the in-container driver writes when the session ends, from the
stream-json `result` event (`usage`, `modelUsage` per model, `total_cost_usd`, `session_id`), and the running token
tally in `session-usage.json`. It does not see individual model calls.

## Options

1. **Chosen.** Host-side spans: a run span for the batch, a span per task, and the session as one generation with the
   final usage, priced with the profile's table and gated by `telemetry.usage_on_spans`. Optionally, Claude Code's own
   OpenTelemetry logs and metrics to an OTLP collector, joined by resource attributes.
2. A span per model call, from the stream. The stream is in the session's output volume, the volume is read once at the
   end, and per-call output on Claude Code is lumpy anyway (ADR 0021); a session's total is what a cap and a bill use.
3. Claude Code's traces into Langfuse. Its trace export is beta and would need a collector pipeline and a Langfuse
   key reachable from a container that runs hostile repository code.

## Decision

- **Spans.** `chargehand.driven.run` (root; `langfuse.trace.name`): run id, preset, repository path, task count, base
  commit, credential delivery, final status and error code, the pull-request links, total tokens. Under it
  `chargehand.driven.task` per task: task id and ref, the task's run id, state, error code, branch, pull-request URL,
  model, Claude Code's session id (`chargehand.claude_code.session_id`), turns, tokens, wall seconds; error status
  when the task did not complete. Under that `chargehand.driven.session` (client kind, so a generation), from the
  container's start to its end, with the model, the session id and, with `usage_on_spans`, `usage_details` (input,
  output, cache read, cache write) and `cost_details`.
- **One trace id.** The batch's result and every task's result carry the run span's trace id. With no listener (no
  `telemetry` block) it stays a random id, as for an orchestrator run.
- **Cost** is the profile's price table, model by model from `modelUsage`. Claude Code names a model without a provider
  (`claude-sonnet-5-5`); the table's key is that id itself, or the one key ending in `/<id>` (a date suffix may be
  dropped). Keys under several providers count only when they agree; any model without a price makes the session's cost
  unknown (ADR 0026), so no `cost_details`. Claude Code's own list figure goes beside it as
  `chargehand.claude_code.cost_usd`, for a check. On a subscription neither is a bill.
- **Claude Code's own export, behind `driven.network.otlp_url`.** An `http://host:port` OTLP/HTTP collector, reached
  through a forward on the batch's egress container on a port no other forward uses, and bypassing the proxy. The
  session gets `CLAUDE_CODE_ENABLE_TELEMETRY=1`, OTLP logs and metrics over `http/protobuf`, cumulative temporality and
  `OTEL_RESOURCE_ATTRIBUTES` with `chargehand.run_id` (the batch), `chargehand.task_id`, `chargehand.task_run_id` and
  `chargehand.trace_id` (percent-encoded, so a task id cannot add an attribute). Never `OTEL_LOG_USER_PROMPTS`, never
  `OTEL_EXPORTER_OTLP_HEADERS` (so no secret the diff scan does not know travels), no traces. `https` is refused:
  whether Claude Code's exporter goes through the proxy is unchecked.

## Consequences

- A driven run is found in Langfuse by the `trace_id` of its `result/v1`, like an orchestrator run.
- The session generation carries totals only; per-call detail is in Claude Code's own logs (with `otlp_url`) or the
  stored stream. Its session id joins the span to those logs (`session.id` there).
- Usage arrives when a session ends; the live tally stays on run events (`session_progress`), not on spans.
- Nested research and review runs a session makes through MCP are their own traces, linked through their parent run
  id in the run log, not in Langfuse.
- With a gateway (`credential_delivery: gateway_key`) that records the calls, keep `usage_on_spans` off, as ADR 0021 says.

## Reopen if

The host gets per-call events from the session (for example, the stream read live), or Claude Code's trace export
leaves beta and a collector pipeline for it exists.
