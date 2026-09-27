# 0020. Claude Code runtime adapter

- Status: proposed
- Date: 2026-09-27

## Context

`IWorkerRuntime` (ADR 0004) has one implementation, OpenCode V2 over HTTP. Claude Code is a second coding-agent
runtime with Anthropic models, prompt caching and built-in tools, but no server: its scriptable surface is the CLI in
print mode (`claude -p`), which streams events as JSON lines and resumes a session by id. Verified against 2.1.195's
`--help` and its stream shape (`system`/`init`, `assistant` with `message.id`, `model`, `usage`, `content`, `user`
with `tool_result`, `result` with `is_error`). A live turn was not run while writing this ADR (no API key in the
sandbox); the fake-CLI test covers the process handling, not the model.

## Options

1. CLI print mode, one process per turn. 2. Agent SDK (TypeScript/Python) behind a sidecar. 3. ACP bridge.

## Decision

**CLI print mode behind `IWorkerRuntime`, pinned by `claude --version`**, selected by profile `claude_code`
(used instead of `opencode` when set). Mapping:

| port | Claude Code |
|---|---|
| create | a session id (UUID) held in the orchestrator process; the base commit for the diff |
| instruction entries | `--append-system-prompt`, the same text on every turn so the prefix stays cached |
| submit / await idle | a `claude -p` process (`--session-id`, later `--resume`), prompt on stdin; idle = `result` event or exit |
| interrupt | kill the process tree |
| messages | stream events folded per API message id; tool uses and results form the evidence scope |
| permissions | `--permission-mode dontAsk` plus `--tools`, `--allowedTools`, `--disallowedTools` from the preset rules; nothing is ever pending |
| fork | only before the first user message: a fresh session with the same spec and entries (the cache is content-keyed) |
| compact | `/compact` as its own turn before the next prompt; not mid-turn |
| diff | `git diff` against the base commit plus untracked files |
| generate | `claude -p --tools "" --no-session-persistence --output-format json` |

Every process runs with `--bare`: no hooks, plugins, CLAUDE.md discovery, auto-memory or keychain, the isolation
ADR 0003 gets from an own OpenCode server. Auth is therefore `ANTHROPIC_API_KEY` from the secret store
(`claude_code.api_key_secret`).

## Consequences

- Preset rules are last-match-wins; Claude Code's are deny-wins. A specific allow inside an earlier specific deny
  (`read *.env.* deny`, then `*.env.example allow`) stays denied: the translation fails closed.
- `ask` is a deny; `opencode_agent` and `external_directory` have no counterpart and are ignored.
- Steered compaction runs between turns only; Claude Code's own auto-compaction covers the mid-turn case.
- Sessions do not survive the orchestrator process (the CLI keeps the transcript, the adapter keeps the mapping).
- `as_sent.opencode_version` carries `claude-code/<version>`, so cache reports tell the runtimes apart.

## Reopen if

A live run shows the stream shape differs from the one mapped here, or mid-turn compaction or cross-process resume
becomes a requirement.
