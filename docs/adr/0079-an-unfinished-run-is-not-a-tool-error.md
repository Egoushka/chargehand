# 0079. An unfinished run is a normal MCP answer, not a tool error

- Status: accepted
- Date: 2026-10-24

## Context

ADR 0029 and ADR 0065 answer an `orchestrate` or `result` call whose run is still going with the run id and
`run-status/v1` as a tool error (`isError: true`), because both tools declared `outputSchema` `result/v1` and an
unfinished run has no result: `structuredContent` that does not match the schema fails a validating client.

Over seven days on the production server `orchestrate` ended `isError: true` 65 times and `false` once, and `result`
94 against 52: nearly every call waits out the 50 s MCP wait. The MCP specification reserves `isError` for a failed
call, and clients show these as red failed calls. The plugin's run skill had to say "that error is expected".

## Options

1. Keep the error. Clients keep showing a routine state as a failure.
2. No `structuredContent` and `isError: false`. A client that validates sees a missing `structuredContent` for a tool
   with an `outputSchema`, and fails the call anyway.
3. Declare `outputSchema` as `oneOf` result/v1 and run-status/v1, and return `run-status/v1` as `structuredContent`
   with `isError: false`. No new schema; both published schemas stay as they are.

## Decision

Option 3. Both tools declare `{"type":"object","oneOf":[result/v1, run-status/v1]}` (`ContractSchemas.McpOutputText`);
each member keeps its `$id`, and the two differ by `contract_version`, so a body matches exactly one. A run that has
not finished (queued or running) is `run-status/v1` in `structuredContent`, with the text blocks as before: the first
names the run and the `result` tool, the second repeats the JSON. A refusal, an invalid request, a lost run or an
unknown run id stay tool errors. The tasks extension path is unchanged: a client that opted in waits for the result
inside the task.

This supersedes the "as a tool error" part of ADR 0029 and the same part of ADR 0065. Their waits and the `result`
tool stand.

## Consequences

- A client shows a still-running call as a success and reads `structuredContent.status` (`queued` or `running`).
- `tools/list` changes the `outputSchema` of both tools: a client that cached `result/v1` as the only shape must also
  accept `run-status/v1`.
- The plugin's skills no longer tell the model to expect an error.
- No change to `schemas/`.

## Reopen if

A major client rejects `oneOf` in an `outputSchema`, or the MCP specification gives a status for a call that continues.
