# 0006. Presets

- Status: accepted
- Date: 2026-09-26

## Context

Presets `cheap`, `thorough` and `strict` are versioned, schema-validated files compiled into an OpenCode agent
plus the permission ruleset passed at session creation, with budgets enforced by the orchestrator. No
`autonomous` preset until sandboxing is designed.

## Evidence (phase 2 spike)

- Per-session rulesets work: with the v0 default, `git log` ran, `touch` failed with `permission.rejected` and
  left no pending request.
- **Last matching rule wins.** `[allow git log*, deny *]` for `shell` removed the shell tool entirely.
- A trailing `<action> * deny` **removes the tool from the catalog** (the model reported no edit tool). Tool sets
  therefore differ per preset, which is fine because they are fixed per preset (ADR 0010).
- `ask` + `once` applied the edit; `ask` + `reject` made the model retry; `always` was never sent, and saved
  permissions stayed at zero.
- V2 compaction keys are `auto`, `keep.tokens`, `buffer` (v1's `preserve_recent_tokens` and `reserved` map onto
  them).

## Decision

- `preset/v1` (`schemas/preset/v1`); files in `presets/<name>.yaml`; v0 ships `default` only.
- Rulesets are ordered, last match wins; write the broad rule first, then exceptions.
- The orchestrator answers pending permissions only with `once` or `reject`, never `always`.
- Defaults: `cheap` = small-model workers, no critic. `thorough` = large-model workers plus a critic from
  another model family on writing nodes only. `strict` = `thorough` plus approval above risk `low` or an
  estimate above $0.50. v0 `default` = edits denied, shell denied except a read-only allowlist, web fetch,
  external directories, questions and subagents denied.

## Reopen if

A sandboxing ADR exists (then `autonomous`), or phase 4 shows a cheaper model meeting the bar in a cell.
