# 0008. Memory provider contract

- Status: accepted
- Date: 2026-09-26

## Decision

`recall(query, scope)`, `retain(item, scope)`, `invalidate(id | query, scope)` with `scope {backend, namespace}`.
Two stores behind it: the run blackboard (in-process in v0 and v1; contracts keyed by node id) and long-term
knowledge (one adapter in phase 4). Vendor vocabulary ("bank", "reflect", "reverse") stays inside adapters.
Prompts, telemetry and the run log are not memory. v0 needs none; this ADR fixes the interface only.

## Evidence

Assumption; no spike item. The phase 4 candidate backend is a self-hosted agent-memory service with its own
namespaces.

## Reopen if

A standard memory interface appears that two or more backends implement.
