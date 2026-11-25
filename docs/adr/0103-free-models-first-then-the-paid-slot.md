# 0103. Free models first, then the paid slot

- Status: accepted
- Date: 2026-11-25

## Context

The maintainer keeps a list of free models with a data tier and a grade per task class (workspace decision D-238), and wants chargehand to
try them before its paid slots. Spec: `docs/specs/2026-11-25-free-models-first.md`. Free models need OpenCode; the deployed server's runtime is
`claude_code` (ADR 0004 holds the OpenCode adapter, ADR 0093 the earlier free pool for public repositories through a gateway). Prompt CI
(ADR 0019, 0025) grades chargehand's own cells on real private tasks, so only zero-retention models may run on them: this ADR does not touch
the eval path.

## Prior art

Not searched: the router is the maintainer's own, and the visibility rule is ADR 0093's.

## Options

1. Call the router (`ws free route`) from the server. Rejected: the server runs in a container with neither Python nor the workspace, and a
   network path to a developer machine that is often off would make "free first" depend on it.
2. Mount the list read-only and port the tier, fence and route logic, with tests that pin the same refusals. **Chosen.**
3. Route free models through the budget router's pools (ADR 0095) over the `claude_code` runtime and a gateway. Rejected: the zero-retention
   models are not on the gateway, and tool calling was graded through OpenCode.
4. Decide the runtime per node. Rejected: a run has one preset and every node of it one slot, so a per-run choice is the same choice
   with less machinery. A node-level runtime would also need a session router inside the runtime port.

## Decision

Option 2, as in the spec: a profile `free_first` block and the `opencode` block (the OpenCode server for free attempts, whatever
the profile's `runtime`). Code: `FreeModelCatalog`, `FreeFence`, `FreeRoute` and `FreeTaskClass` in the accounts module (they share the
repository-visibility lookup of ADR 0093), `FreeFirstGate` in the core, and the attempt loop in `Orchestrator.RunAsync`, which already repeats a
run that did no work on the next pool. A free attempt that fails for any reason except a cancel or a cost cap hands the run to the next model;
the paid slot is last. Writing nodes are not offered a free model: a failed attempt would leave a half-written working tree.

The run record gets `attempts` (model, runtime, free, outcome) and `free_first` (what was offered, or why nothing).

## Consequences

- The list's copy on the server can lag the maintainer's. A stale grade sends a run to a model that has since lost it; the paid fallback
  catches the failure. The deployment's copy changes by pull request.
- The OpenCode server is another container to run, pinned to a version the adapter was checked against (the profile's `opencode.version`).
- Public text says nothing about which models are on the list; the list is configuration.
- The `critic` class is mapped but unused: no code reads a preset's critic block yet.

## Reopen if

A graded model's attempts fall back more often than they finish; `edit` grades appear and a clean-tree retry exists; the list's format changes.
