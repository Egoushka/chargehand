# 0005. Plan and Task Spec format

- Status: accepted
- Date: 2026-09-26

## Context

Intake turns every request into one Task Spec with one action (answer, split, improve, ask, deny). v0 executes
`answer` only and logs the action intake would have taken. Non-interactive callers get `needs_input` instead of
a blocking `ask` or `improve`. Programs send caller inputs and their own prompt blocks.

## Evidence

- Intake through OpenCode's stateless `POST /api/experimental/generate`: OpenCode adds nothing to the request
  (one user message, no system prompt, no tools); 749 prompt tokens for a prompt carrying the full task-spec
  schema; 2.7–6.8 s; 3 of 3 outputs were valid `task-spec/v1` with action `answer`. The response carries no
  usage, so intake cost is taken from the gateway (ADR 0011). One unexplained burst of three 400 responses
  (40 ms, identical later calls succeeded) is logged as a risk.

## Decision

- `request/v1` (`schemas/request/v1`): `text`, `context {interactive, preset, budget_usd?, repository?}`,
  `inputs[] {id, kind, text, source_url?}`, `caller_blocks[] {name, version, sha256, text}`.
- `task-spec/v1` (`schemas/task-spec/v1`): `id, goal, constraints[], acceptance_criteria[], risk, estimate
  {tokens_low, tokens_high, usd_low, usd_high, basis}, action, action_detail`; `action_detail` is null for
  `answer` and typed per action otherwise (split needs ≥2 subtasks with `read_only`).
- Intake: deterministic checks, then one `generate` call on a pinned small model, schema-validated, one retry
  on invalid output, cost booked to the run. The estimate gates nothing until the run log calibrates it.
- Plan graph (v1, not built in v0): nodes carry `agent, model, brief, context budget, token budget,
  depends_on, tool scope, result contract`; validation rejects cycles, writing nodes sharing a worktree, and
  `split` subtasks that are neither read-only nor in separate worktrees.

## Reopen if

The phase 4 run log shows `split` paying off beyond independent read-only or separate-worktree subtasks, or
`generate` adds more than 2,000 tokens or 3 s of overhead.
