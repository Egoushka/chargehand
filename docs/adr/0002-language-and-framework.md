# 0002. Language and framework

- Status: accepted
- Date: 2026-09-26

## Context

The orchestrator reaches OpenCode, the model gateway and the tracing backend over HTTP. The first consumer is
a .NET 10 service. The decision to use .NET 10 with no general agent framework was taken before the spike,
with a reopen condition: switch to TypeScript (not Python) if the OpenCode client, OTLP export or an MCP tool
with `outputSchema` needs a sidecar.

## Evidence (phase 2 spike, OpenCode 2.0.16)

- **OpenCode client.** Kiota 1.35 generates a C# client from the OpenAPI 3.1 spec (750 files) and create,
  prompt and wait work against a live server. But the spec's unions (`anyOf` of 11 message variants) carry no
  `discriminator` (0 in 136 operations), and Kiota populates all variants at once, so typed message reading
  is unusable. A thin hand-written client for the ~16 operations used (`docs/opencode-adapter-ops.json`),
  guarded by the spec contract test, avoids it. No sidecar needed.
- **OTLP export.** OpenTelemetry .NET 1.19.1, OTLP/HTTP protobuf with Basic auth: spans land in Langfuse;
  `langfuse.session.id` and `langfuse.trace.name` map onto Langfuse fields.
- **MCP.** ModelContextProtocol C# SDK 2.2.0 serves a tool whose `OutputSchema` is `result/v1` and returns
  structured content over stdio.
- Evidence gaps carried from the research: contributor-pool evidence is one GitHub report; Python was weighed
  less carefully than TypeScript.

## Decision

.NET 10. No general agent framework in v0–v1; libraries only: OpenTelemetry .NET, JsonSchema.Net,
ModelContextProtocol, and a hand-written OpenCode client (ADR 0004, deviation from "generated client" recorded
there). The reopen condition was not met.

## Reopen if

A future OpenCode, OTLP or MCP requirement cannot be met in-process; then TypeScript, not Python. The task
graph needs durable execution; then MAF workflows or Temporal.
