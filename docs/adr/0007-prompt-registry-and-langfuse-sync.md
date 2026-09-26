# 0007. Prompt registry and Langfuse sync

- Status: accepted
- Date: 2026-09-26

## Context

Git is the source of truth for prompts: versioned, content-hashed blocks in a fixed order (core system → preset
→ project context → task → retrieved facts). Langfuse prompt management is a sync target. OpenCode adds its own
text that the orchestrator never hashes. Programs send their own blocks (content engine).

## Evidence (phase 2 spike)

- OpenCode V2's system message, in order: harness text → `<env>` (working dir, platform, **session ID**) →
  date → tool catalog → skills → instruction entries (`<context key="…">`). V2 keeps per-session content hashes
  of its blocks, but only in its internal SQLite schema; the HTTP API does not expose them.
- The tool catalog depends on the agent and the ruleset (a denied action removes its tool; ADR 0006).
- A Langfuse span attribute `langfuse.observation.prompt.name` only links when the prompt exists in the project.

## Decision

- Blocks live in `prompts/<name>.md` with front matter `version` (SemVer); sha256 over normalised UTF-8 (LF line
  endings, trailing whitespace stripped). Caller blocks (`request/v1` `caller_blocks`) enter `prompt_chain` with
  `source: caller`; the registry never owns them.
- `prompt_chain.as_sent` is computed without reading OpenCode internals: `opencode_version` from `/api/info`,
  `agent`, `model`, `date` (UTC), `tools_sha256` = sha256 of the canonical JSON of (OpenCode version, agent,
  ruleset) — a proxy for the tool catalog, which is a function of those; `instruction_files_sha256` = sha256 of the
  instruction entries set on the node. A logging proxy between OpenCode and the gateway is a diagnostic mode for
  the cache report (phase 4), never on by default.
- Sync: CI pushes block versions to Langfuse prompt management (project of the orchestrator) on merge to main;
  spans carry `langfuse.observation.prompt.name` and `.version`.

## Reopen if

OpenCode exposes its instruction hashes over the API, or the tool catalog is shown to vary with anything other
than version, agent and ruleset (e.g. MCP servers connecting mid-session).
