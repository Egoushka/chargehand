# 0020. Claude Code runtime adapter

- Status: accepted
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

Re-pinned to 2.1.283 (2026-09-28): every flag the adapter passes is in its `--help`, and a live two-turn session
(subscription mode, a small model, the prompt on stdin, `--session-id` then `--resume`) streamed the same event shape.
No tool call was made, so the `user`/`tool_result` event was not rechecked live.

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

## Addendum (2026-09-28): the CLI's own login

With no credential configured (neither variable in the environment, neither `api_key_secret` nor
`oauth_token_secret` in the profile), the adapter no longer fails with `runtime_unavailable`: it runs `claude` on
the login `/login` stored (the macOS keychain, or the CLI's config directory). A third mode:

| mode | profile | auth | isolation | cost |
|---|---|---|---|---|
| CLI login | neither secret | none set; the CLI reads its own stored login | `--setting-sources ""`, as the subscription mode | the signed-in account's plan |

`--bare` is out: its help text says OAuth and the keychain are never read in bare mode. The child environment gets
no credential variable, and inherited `ANTHROPIC_API_KEY`, `CLAUDE_CODE_OAUTH_TOKEN` and `ANTHROPIC_AUTH_TOKEN` are
removed, so a stray variable cannot switch the worker to another account. Both secrets set stays an error.
Connecting runs `claude auth status`, which exits 1 when signed out and calls no model; that maps to
`runtime_unavailable` with the action to sign in or set one of the two variables. Live check (2.1.283, 2026-09-28):
`claude -p --setting-sources "" --strict-mcp-config --model claude-haiku-4-5 --output-format stream-json --verbose
--tools Read` with both variables empty answered with `apiKeySource: none` in `init` and a successful `result`.

## Addendum (2026-09-29): MCP servers for services

A preset's `services` (ADR 0034) reach a Claude Code worker as MCP servers. What the goal 0.6 spike observed on 2.1.283
(rows C1 to C6 of ADR 0034) decides the flags:

- **A private `--mcp-config` file per turn.** It holds `{"mcpServers": {name: entry}}` with `{"type": "http" | "sse", "url",
  "headers"}` for a URL server and `{"command", "args", "env"}` for a stdio one, secrets already resolved, so it is
  written to a fresh directory under the system temp directory (mode 0700), the file mode 0600, never under the
  checkout and never on the command line. The turn's process is the only reader; the directory is removed when the
  process has exited, on failure, on interrupt, and if the process cannot start. A crash between writing and removing
  leaves a directory that the next `ConnectAsync` sweeps once it is a day old. The content never enters a log, an error
  or the run log; text the CLI prints on a failed turn has the grant's exact header and environment values replaced first.
  With no grants there is no `--mcp-config`, and `--strict-mcp-config` stays, so no other server is loaded.
- **`--allowedTools` names exactly the granted tools**, `mcp__<server>__<tool>` with the server name unchanged. `dontAsk`
  refuses any MCP tool not named (C2). `--tools` is left alone: it does not filter MCP tools (C1). A character outside
  letters, digits, `_` and `-` becomes `_` in both parts, as the CLI lists it (checked live: a server tool `echo.fact`
  is `mcp__team-docs__echo_fact`); the CLI's list would not match the allow list otherwise.
- **`--disallowedTools` names the server's other tools.** A connected server's ungranted tools are still listed and cost
  about 50 tokens each on every call (C1, C6), and `--disallowedTools` removes them from the catalog (C3). The resolver
  already lists the server's tools, so the grant carries the ones it left out (`ServiceGrant.Hidden`) and no call is added.
  A tool the server gains between resolving and the run is listed but refused, not callable.
- **A server that did not connect is reported.** The `init` event lists every configured server with a `status`
  (`connected`, `failed`, ...; C5). A granted server that is anything else, or absent, is kept per session and read back
  through `IServiceHealth`; the orchestrator records it as `not_connected: <status>` on that service in the run log (the line
  of `chargehand show` and the span tag `chargehand.service.<name>.not_connected`). The run goes on without the service's tools.
  Live check (2.1.283, 2026-09-29, a small model): a stdio server granted `echo_fact` with `write_note` hidden answered the
  call and the model reported `write_note` missing; a second granted server with a missing command read `failed`; no config
  directory was left behind.

`--bare` takes `--mcp-config` (C4, to the `init` event); a bare-mode call that uses a granted tool was not run, since no API
key exists on the runner.
