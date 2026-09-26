# 0014. Triggers and callable interface

- Status: accepted
- Date: 2026-09-26

## Decision

- v0: CLI only. `chargehand run` reads `request/v1` on stdin and writes `result/v1` on stdout; `chargehand show
  <run>` prints a run. Exit codes: 0 completed, 1 failed, 2 usage error, 3 needs input, 70 not implemented.
- Phase 5: HTTP `POST /v1/runs`, `GET /v1/runs/{id}`, `GET /v1/runs/{id}/events`; an MCP tool `orchestrate`
  with `outputSchema` = `result/v1`, the tasks extension for long runs, `input_required` for `ask`.
- Contract-first: schemas ship in `Chargehand.Contracts` before the HTTP interface (ADR 0009).
- Callers in order: the content service (non-interactive, one call per draft, gets `needs_input`, sends facts as
  `inputs[]` and its generator prompt as `caller_blocks`); later coding agents, chat clients, a chat bot and
  workflow tools. An OpenAI-compatible facade is rejected: chat completions have no field for evidence or
  confidence.
- v0 listens on localhost only.

## Evidence

MCP tool with `outputSchema` = `result/v1` served and called from .NET (ADR 0002). The rest is an assumption until
phase 5.

## Reopen if

A consumer cannot adopt `request/v1` and `result/v1`.
