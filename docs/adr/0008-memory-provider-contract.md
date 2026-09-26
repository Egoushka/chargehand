# 0008. Memory provider contract

- Status: accepted
- Date: 2026-09-26

## Decision

`recall(query, scope)`, `retain(item, scope)`, `invalidate(id | query, scope)` with `scope {backend, namespace}`.
Two stores behind it: the run blackboard (in-process in v0 and v1; contracts keyed by node id) and long-term
knowledge (one adapter in phase 4). Vendor vocabulary ("bank", "reflect", "reverse") stays inside adapters.
Prompts, telemetry and the run log are not memory. v0 needs none; this ADR fixes the interface only.

## Phase 4

- `IMemoryProvider` in `src/Chargehand/Memory`; adapter `HindsightMemory` (HTTP API 0.10.0): namespace = bank;
  recall = `POST …/memories/recall` (budget low, a token cap); retain = `POST …/memories` (async); invalidate =
  `PATCH …/memories/{id}` with the reversible `state: invalidated`. Invalidate-by-query is recall, then invalidate.
- Use in a run: recall once per executed run with the request text; facts go in the prompt text after the task,
  never in instruction entries, so split siblings keep one forkable prefix (ADR 0017). Memory fails open: it is
  context, not a gate. Retain is off by default. Benchmark arms run without memory, or earlier runs would leak
  answers into later ones.
- The run blackboard is not behind this interface: upstream contracts pass typed, by node id (`GraphRunner`).

## Evidence

Assumption; no spike item. The phase 4 candidate backend is a self-hosted agent-memory service with its own
namespaces.

## Reopen if

A standard memory interface appears that two or more backends implement.
