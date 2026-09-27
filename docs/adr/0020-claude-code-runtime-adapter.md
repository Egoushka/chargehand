# 0020. Claude Code runtime adapter

- Status: proposed
- Date: 2026-09-27

## Context

`IWorkerRuntime` (ADR 0004) has one implementation, OpenCode V2 over HTTP. Claude Code is a second coding-agent
runtime with Anthropic models, prompt caching and built-in tools, but no server: its scriptable surface is the CLI in
print mode (`claude -p`), which streams events as JSON lines and resumes a session by id. Verified against 2.1.195's
`--help` and its stream shape (`system`/`init`, `assistant` with `message.id`, `model`, `usage`, `content`, `user`
with `tool_result`, `result` with `is_error`). Live check (2.1.195, subscription mode, 2026-09-27): a direct
two-turn session on a small model, and one `chargehand run` on the default preset against this repository with a
large model as worker. The run completed with 6 claims, all 6 file evidence references resolved, and a cache read
rate of 81–99% on every call after the first. Edit and unlisted shell commands were denied without prompting. The
API-key mode was not run live.

Found live: assistant events carry the `message_start` usage, so their output count is a 1–3 token stub; only the
`result` event totals a turn's output. The adapter adds the shortfall to the turn's last call.

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

Two credential modes, exactly one per profile:

| mode | profile | auth | isolation | cost |
|---|---|---|---|---|
| API client | `api_key_secret` | `ANTHROPIC_API_KEY` | `--bare`: no hooks, plugins, CLAUDE.md discovery, auto-memory or keychain | per token |
| subscription | `oauth_token_secret` | `CLAUDE_CODE_OAUTH_TOKEN` from `claude setup-token` | `--setting-sources ""`: no user, project or local settings, so no hooks or plugins | the owner's plan limits |

`claude_code.base_url` routes either mode through an Anthropic-compatible gateway (`ANTHROPIC_BASE_URL`); unset,
workers call Anthropic directly. The child environment gets only what the profile sets: an inherited base URL or
auth token is removed. `--bare` never reads OAuth, so the subscription mode cannot use it. Each mode removes the other's variable from the
child environment, because an inherited API key outranks the OAuth token. Price a subscription model at zero in
`prices` (give it its own provider prefix); `max_input_tokens` still bounds a node.

## Consequences

- Preset rules are last-match-wins; Claude Code's are deny-wins. A specific allow inside an earlier specific deny
  (`read *.env.* deny`, then `*.env.example allow`) stays denied: the translation fails closed.
- `ask` is a deny; `opencode_agent` and `external_directory` have no counterpart and are ignored.
- Steered compaction runs between turns only; Claude Code's own auto-compaction covers the mid-turn case.
- Subscription runs share the owner's plan limits with interactive use. Whether `--setting-sources ""` also
  keeps the user's CLAUDE.md out is not verified; the first live run shows it.
- A gateway that forwards to Anthropic directly (a masking proxy's `/anthropic` route) records no generations in
  LiteLLM; `telemetry.usage_on_spans` (ADR 0021) puts their usage and cost on the orchestrator's call spans. LiteLLM cannot relay a subscription token on `/v1/messages` yet
  (BerriAI/litellm#42170).
- Sessions do not survive the orchestrator process (the CLI keeps the transcript, the adapter keeps the mapping).
- `as_sent.opencode_version` carries `claude-code/<version>`, so cache reports tell the runtimes apart.

## Reopen if

A live run shows the stream shape differs from the one mapped here, or mid-turn compaction or cross-process resume
becomes a requirement.
