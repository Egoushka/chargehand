# 0065. A default MCP wait over HTTP and a result tool

- Status: accepted; the tool error for an unfinished run changed in ADR 0078
- Date: 2026-10-18

## Context

ADR 0029 kept a call without `Prefer: wait=N` waiting for its result, so that clients with long timeouts keep getting
it over MCP. The common client turned out to have a short one: Claude Code, directly and through `mcp-remote`, ends an
MCP call after about 60 s ("Request timed out"). A read-only run on Claude Code workers often takes longer, so the
caller got neither the result nor the run id, and the run finished unseen (seen 2026-10-09 and again 2026-10-18 on a
`cheap` run). Its progress notifications every 30 s did not keep the call open. Claude Code sets no `Prefer` header per
server, so ADR 0029's opt-in never applied to it.

ADR 0029's "Reopen if" names this case: clients that need the result itself over MCP after an early return.

## Options

1. Keep waiting; document `Prefer: wait=N` for clients that can set headers. Leaves Claude Code without the run id.
2. A default wait over HTTP below 60 s, then the run id as ADR 0029's tool error; the result only over HTTP. The caller
   learns the id but a client with only MCP still cannot read the result.
3. Option 2 plus a second tool, `result`, that takes the run id, waits the same bound and returns `result/v1` or the
   run's status. Duplicates `GET /v1/runs/{id}`, which a client with only MCP cannot call.
4. `tools/call` resumption or the tasks extension only. Not in the clients this is for.

## Decision

Superseded in part by ADR 0078: where this says the run id comes back "as a tool error", it is now an ordinary answer with `run-status/v1` as `structuredContent`.

Option 3. Over HTTP a call without `Prefer: wait=N` and without a `driven` block waits 50 s (`ServerSettings.McpWait`),
then returns the run id and `run-status/v1` as ADR 0029's tool error, whose text now names the `result` tool. `result`
takes `{"run_id": "..."}`, waits the same 50 s for a run this process holds, reads a finished one from the run log, and
lets a caller read what `GET /v1/runs/{id}` lets it read. `Prefer: wait=N` and the driven default are unchanged. Over
stdio a call still waits for its result: there is no HTTP request, and the plugin's skills rely on it.

## Consequences

- A Claude Code session always gets the result or the run id, and can collect the result with one more call per 50 s.
- A client with a timeout above 50 s that relied on a long single call now needs the second call; it can send
  `Prefer: wait=60` for the longest single wait.
- `tools/list` returns two tools. No contract changes.

## Reopen if

Clients raise their default MCP timeout well above a typical run, or adopt the tasks extension, so one call suffices.
