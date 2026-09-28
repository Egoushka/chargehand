# 0029. The MCP run id before a client timeout

- Status: accepted
- Date: 2026-09-28

## Context

Outside a task, the tool `orchestrate` answers only once the run finishes (ADR 0018). A run can take minutes; MCP
clients bound a call with their own request timeout. When the timeout wins, the call fails without the run id, while
the run goes on server-side and its result lands in the run log where nobody looks. Clients that opt in to the tasks
extension already get a task id to poll; this is about the others.

MCP C# SDK 2.2.0 (checked 2026-09-28): a call's `progressToken` reaches the tool as `RequestParams.ProgressToken`, and
`McpServer.NotifyProgressAsync` delivers the notification on the call's response stream, stateless mode included.
The tasks filter runs a call as a task exactly when the request declares the `io.modelcontextprotocol/tasks` client
extension. Handlers run on the HTTP request's execution context, so `IHttpContextAccessor` reads its headers.

## Options

1. A progress notification carrying the run id and its routes, when the client sent a progress token. No change to
   results; a client that shows progress lets its user find the run, and clients that reset their timeout on progress
   gain time. A model behind a client that only reports "timed out" still never sees the id.
2. Return early after a bounded wait, as HTTP does with `Prefer: wait=N`. By default (10 s), every run longer than
   that would come back unfinished to every client, including those with long timeouts, which then have no MCP way to
   get the result: a regression for them.
3. Option 2, opt-in: only when the MCP request carries `Prefer: wait=N`. Clients whose configuration sets HTTP headers
   choose a wait below their timeout.
4. A second tool to poll a run. Duplicates `GET /v1/runs/{id}` and the tasks extension.

## Decision

Options 1 and 3, both outside tasks only. The notification says `chargehand run <id> started` with
`GET /v1/runs/<id>` and `chargehand show <id>`. When `Prefer: wait=N` (at most 60 s) runs out first, the call returns
a tool error, because `structuredContent` must match `outputSchema` `result/v1` and an unfinished run has none: the
first text block names the run and its routes, the second is the run's latest `run-status/v1`. Without the header the
call waits for the result as before.

## Consequences

- A client that sends a progress token or the header always learns the run id before its timeout, if it sets the wait
  below that timeout.
- An early return reads as a failed call to clients that do not read the text. The run is unaffected and stays
  readable over HTTP; resending the request starts a second run.
- No contract changes.

## Reopen if

The tasks extension becomes common enough among clients to drop both paths, or clients need the result itself over MCP
after an early return (then a poll tool or `tools/call` resumption).
