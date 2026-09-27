# 0022. Error codes in result/v1

- Status: accepted
- Date: 2026-09-27

## Context

Every failure reaches the caller the same way: `status: failed`, the exception message as `summary` and as the only
open question (the orchestrator's catch in `Orchestrator.RunAsync`, `WorkerNode.Failed`, the graph runner's failed
node, and through them `POST /v1/runs`, `GET /v1/runs/{id}` and the `orchestrate` tool). A client that wants to retry,
tell the user to start a server, or fix its request has to parse text. Messages seen live:

- "repository ... is not under worker_root ..." (a request the caller must change);
- "OpenCode 503 ServiceUnavailableError: ConnectionRefused ..." (the provider behind OpenCode is down);
- an unhandled `HttpRequestException` "Connection refused" from the CLI when the OpenCode server is not running;
- "worker ended interrupted" (the USD cap, the token budget or the deadline; the text does not say which).

The eval gate already parses text for one case: `EvalRunner.RateLimited` matched "rate limit" or "429" in a failed
result's summary to decide whether to run an arm again.

## Options

1. An optional `error` object in `result/v1`: a fixed code, the message, whether a retry may help, and an action.
   Additive, so the schema stays v1.
2. A new major, `result/v2`, with `error` required on failed results. Cleaner, but every caller migrates for one field.
3. Error codes only on the HTTP interface (status codes, problem details). MCP and the CLI would still see text, and
   a run that fails after it started still returns 200 with `result/v1`.

## Decision

**Option 1.** `result/v1` gets an optional `error`, present only when `status` is `failed` (the schema enforces it):
`code`, `message`, `retryable`, and an optional `action` saying what the user or client should do. No `needs_input`
result is caused by an error: intake's questions, an improved request and the approval gate are all intended stops.

The codes are the causes the code can tell apart today:

| code | raised when | retryable |
|---|---|---|
| `runtime_unavailable` | the OpenCode server refuses the connection (action: the `scripts/opencode-serve.sh` command with the server's port); the Claude Code binary cannot start or `--version` fails | yes |
| `runtime_version_mismatch` | the runtime's version differs from the pin | no |
| `provider_unavailable` | OpenCode answers 502, 503 or 504 (its 503 ServiceUnavailableError relays a provider or gateway it cannot reach) | yes |
| `rate_limited` | OpenCode answers 429, or any error's text is a rate limit (OpenCode's "Rate limit exceeded", Claude Code's `rate_limit_error`) | yes |
| `repository_not_allowed` | the checkout is outside `worker_root`, or under the home directory (ADR 0003) | no |
| `checkout_invalid` | the checkout is at another commit than the request pins (a directory that is not a git checkout has no commit), or `git ls-files` fails | no |
| `checkout_has_secrets` | the checkout holds files the preset denies reading (ADR 0006) | no |
| `cost_cap_reached` | the watcher interrupted a node above its USD cap or its input-token budget | no |
| `deadline_exceeded` | a node's turn was interrupted by anything else: the deadline is the only other interrupt | yes |
| `invalid_result` | a node's turns succeeded but left no valid result contract after the repair turn | no |
| `intake_failed` | intake returned no valid Task Spec after its retry | no |
| `invalid_request` | an unknown preset, or a caller block whose sha256 does not match its text | no |
| `internal` | anything else | no |

A `ChargehandException(code, message, action)` carries the code from where it is known; `retryable` follows from the
code. `OpenCodeException` is one, with its code from the HTTP status and text. The orchestrator's catch, and the graph
runner's for a node that throws, put the exception's error in the result; any other exception is `internal`, or
`rate_limited` by its text. A skipped node carries the error of the node it waited on; a split run where no node
completed carries the first node error. `chargehand run` returns `result/v1` with `error` when connecting to the
runtime fails, before any run starts, instead of an unhandled exception.

## Consequences

- Clients branch on `error.code`; `summary` and `open_questions` keep the message, so text readers see no change.
- Adding a code is additive for a client that falls back on unknown codes; removing or renaming one is a new major.
- The eval gate retries an arm on `rate_limited` instead of matching text. The text match remains, in one place, for
  runtimes that report a rate limit only as text.
- A failure the HTTP interface refuses before a run exists (schema-invalid request, unknown preset, a caller block
  hash mismatch) stays a `400` with `errors`: there is no run, trace or prompt chain to put in a result. The same
  checks inside a run, where the CLI or a direct caller skips the server's validation, give `invalid_request`.
- `chargehand serve` connects once at startup, so a runtime that is down then stops the server; a runtime that goes
  down later fails each run with `runtime_unavailable`.
- A session OpenCode aborts from outside (its own UI) reads as `deadline_exceeded`: the node cannot tell that
  interrupt from its own deadline.

## Reopen if

A client needs a cause the table does not name (for example a provider's content filter), or `deadline_exceeded`
turns out to hide external aborts often enough to matter: then the node records why it interrupted.
