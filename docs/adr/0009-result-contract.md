# 0009. Result contract, shared with the first consumer

- Status: accepted
- Date: 2026-09-26

## Context

Workers return contracts, never transcripts. Every claim carries evidence and a confidence; every evidence
reference resolves before the contract leaves its node. The first consumer (a content service) is
non-interactive, cites caller-supplied facts, needs the draft body in the contract and records the prompt hash
behind every draft.

## Evidence (phase 2 spike)

- Workers emitting `result/v1` as a fenced JSON block in the final message: 10 of 10 small-model runs were
  schema-valid with every file reference resolved on the first try; one further node elsewhere emitted
  `"commit": ""`, which the schema rejects (1 failure in 12 overall, repairable). A custom `submit_result` tool was
  not tested.
- The MCP C# SDK serves `result/v1` as a tool `outputSchema` (ADR 0002).

## Decision

- `result/v1` (`schemas/result/v1`): `contract_version, task_id, node_id, trace_id, prompt_chain, status
  (completed | needs_input | failed | denied), summary, claims[] {text, evidence[≥1], confidence},
  evidence[] {id, kind, locator, commit?, sha256?}, artifacts[] {kind, uri? | content? (≤64 KiB), media_type,
  sha256}, open_questions[], confidence, usage {input, output, cache_read, cache_write, usd}`.
- Evidence kinds: `file`, `diff`, `url`, `session_message`, `commit`, `input` (a caller input id; replaces
  `signal`).
- Workers emit only `status … confidence`; the orchestrator adds ids, `prompt_chain` and `usage`, validates,
  and allows one repair turn.
- Resolution in v0 (no network): `file` = path and line range exist at the stated commit; `diff` = range in the
  node's session diff; `commit` = exists; `session_message` = exists in the node's session; `url` = appears in
  the node's inputs or tool output; `input` = an id the caller sent. A claim that still fails after the repair
  turn moves to `open_questions`.
- Sessions and worktrees cited as evidence are never deleted by the orchestrator.
- Contract package `Chargehand.Contracts` (schemas embedded + C# types + validator), versioned by schema major
  (1.x for v1), published to NuGet after the bootstrap ADRs; a local feed until then. Schemas are published
  before the HTTP interface.

## Reopen if

More than 1 in 10 runs fails after repair; drafts exceed 64 KiB; a second consumer needs an evidence kind
`input` cannot express.
