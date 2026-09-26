# 0004. OpenCode major version and runtime adapter

- Status: accepted
- Date: 2026-09-26

## Context

OpenCode ships two API majors: v1 (documented `/doc` spec, synchronous send with per-message `system`) and V2
(`/api/*`, "Experimental HttpApi surface", spec version 0.0.1, distributed with the desktop app rather than as a
GitHub release). The report's `WorkerRuntime` port has a v1 shape. Fall back to v1 only if the spike found a V2
blocker: no isolated own server, no scriptable auth, unreliable completion detection, or instruction entries
outside the cached prefix.

## Evidence (phase 2 spike, V2 2.0.16)

| blocker check | result |
|---|---|
| Own isolated server | A pinned copy of the CLI, `opencode serve --hostname 127.0.0.1`, with its own `HOME` and XDG dirs, uses its own database and config (none of the owner's agents load); the process is detached from the desktop app. Caveat: skill discovery walks up from the session directory (ADR 0003). |
| Scriptable auth | HTTP Basic, user `opencode`, password from `OPENCODE_SERVER_PASSWORD`; every route incl. `/openapi.json` requires it. The spec declares no security scheme. |
| Completion detection | `POST /api/experimental/session/{id}/wait` returns 204 within 3–4 ms of the idle marker; polling messages 106–227 ms. The durable session log (`…/log?after=`) returned only `log.synced` and is not used. `/api/event` did not drop a slow reader at ~60 events. |
| Instruction entries in the cached prefix | Entries set before the first prompt are appended to the end of the single system message and are read from cache on every later call (94% weighted cache rate from the second call on). A mid-session change is appended as a user message, so the prefix survives. |

Other V2 behaviour the adapter relies on: session create takes agent, model, location, permission ruleset and
metadata; `fork` (≈15 ms), `interrupt` (≈40 ms, clears pending permission requests), `compact` (≈2.5 s, system
prefix stays cached), `move`, per-message tokens with cache read and write.

## Options

1. V2 on an own pinned server behind the port. 2. v1 1.18.32 pinned. 3. ACP (`opencode acp`), no .NET SDK.

## Decision

**V2, pinned to 2.0.16, on the orchestrator's own server**, behind an asynchronous-first port
(`IWorkerRuntime`): create (agent, model, location, ruleset, metadata) → set instruction entries → submit →
await idle with a client deadline, interrupt on timeout → read messages and usage → fork, compact, answer
permissions, diff. The adapter is a thin hand-written client (ADR 0002) for the operations in
`docs/opencode-adapter-ops.json`; a CI contract test fails when a regenerated spec drops one of them or one of
their request properties. The adapter refuses to start if `/api/info` reports another version.

Adapter rules learned in the spike:

- Always send `location`; session create ignores `x-opencode-directory` and falls back to the server's cwd.
- Never call `/api/model*`, `/api/provider*` or `/api/config*`; they can return provider keys in plaintext.
- After a `reject`, the model may retry and open a new permission request; keep answering or interrupt.
- Disable the hidden `title` agent in the server config (`agent.title.disable`): it adds one small-model call
  per session.
- `wait` has no timeout; the node deadline (ADR 0011) is enforced by the client.

## Consequences

The v1 fallback was not exercised (optional check skipped: no blocker to hedge). The experimental status of V2
is the main risk; the contract test and version pin contain it.

## Reopen if

A V2 update breaks the adapter in two consecutive releases, or `wait` proves unreliable under load.
