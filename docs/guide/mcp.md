---
title: "Use it from an MCP client"
description: "The orchestrate tool over stdio or Streamable HTTP: setup from a checkout, tasks, intake's questions, and the run id before a client timeout."
order: 3
section: "Guides"
---

chargehand serves one MCP tool, `orchestrate`, over two transports:

| transport | how it starts | auth | notes |
|---|---|---|---|
| stdio | `chargehand mcp`, started by the client | none | no port and no key; stdout carries only MCP messages, logs go to stderr |
| Streamable HTTP | `chargehand serve`, path `/v1/mcp` | `Authorization: Bearer <key>` | the same server as the HTTP routes, see [Run the HTTP server](server.md) |

Both hosts share the tool, the tasks store and the server instructions, which tell a client on `initialize` when to call chargehand. [McpTests](../../tests/Chargehand.Tests/McpTests.cs) and [StdioMcpTests](../../tests/Chargehand.Tests/StdioMcpTests.cs) cover the tool, tasks, the question paths, the progress notification and `Prefer: wait` with the MCP SDK's own client and a scripted runtime.

## Over stdio, from a checkout

The README's line for Claude Code:

```bash
claude mcp add chargehand -- dotnet run --project <checkout>/src/Chargehand.Cli -- mcp
```

The process reads its profile like every command: `CHARGEHAND_PROFILE`, else `profiles/local.json` relative to its working directory. The client picks that directory, so point `CHARGEHAND_PROFILE` at your profile, as [the change command's checkout example](change.md#point-it-at-a-checkout-instead) does:

```bash
claude mcp add chargehand -e CHARGEHAND_PROFILE=<checkout>/profiles/local.json -- dotnet run --project <checkout>/src/Chargehand.Cli -- mcp
```

A `models` map in the profile picks the models the presets' placeholders stand for; without one the runtime's default model runs ([Quickstart](quickstart.md#write-a-profile)). The working directory decides three more things:

- **Repositories.** Without `repository_roots`, `mcp` allows `worker_root` and the directory it was launched in.
- **Prompts and presets.** They come from the working directory when it holds both `prompts/` and `presets/`, else from the copies the build places next to the binary.
- **Run log.** The profile's `run_log`; else `runs/run-log.jsonl` when the working directory is a checkout; else `chargehand/run-log.jsonl` under the per-user data directory. Set `run_log` to one absolute path if you want `chargehand show` in your checkout to read the runs an MCP client started elsewhere.

## Over HTTP

`chargehand serve` exposes the same tool at `/v1/mcp` over Streamable HTTP, behind the bearer key that guards every route. Clients on MCP 2026-07-28 run stateless; clients that send `initialize` get a session ([ADR 0018](../adr/0018-callable-interface-http-mcp-run-store.md)). [Run the HTTP server](server.md) covers the profile's `http` block, the key and the allowed hosts.

## The orchestrate tool

- `inputSchema` is `request/v1` and `outputSchema` is `result/v1`. The result arrives in `structuredContent` and as JSON text.
- An invalid request, an unknown preset or a caller block whose sha256 does not match comes back as a tool error whose text starts `invalid request/v1:`.
- Runs execute one at a time. With 10 unfinished runs a new call fails with `10 runs are unfinished; retry later`.
- A run outlives its call. A cancelled call stops waiting and the run goes on; read it later with `chargehand show <run-id>` or, on a server, `GET /v1/runs/{id}`.

## Long runs as tasks

A client that opts in to the tasks extension (`io.modelcontextprotocol/tasks`) gets long runs as tasks to poll, over either transport. A task ends in `result/v1`.

## Questions from intake

When intake answers an interactive request (`context.interactive: true`) with `ask`, the tool asks the client one required string field per question (`q1`, `q2` and so on). It then resends the request with an "Answers to the orchestrator's questions" section appended, as a new run that records the first run as its parent.

| where the call runs | how the questions arrive |
|---|---|
| inside a task | an elicitation, which the task reports as `input_required` |
| outside a task, with a session (stdio, or an HTTP client that sent `initialize`) | a plain `elicitation/create` |
| outside a task, stateless (HTTP, MCP 2026-07-28) | an `input_required` result; the client retries the call with the answers |

A client that declines gets the `needs_input` result as it stands. A non-interactive request (`context.interactive: false`) gets `result/v1` with status `needs_input` and the questions in `open_questions`, and the tool asks nothing. `improve` and approval stops come back as `needs_input` unchanged.

## The run id before a client timeout

Outside a task, the tool answers once the run finishes, and a run can take minutes. Two paths give the client the run id first ([ADR 0029](../adr/0029-mcp-run-id-before-a-client-timeout.md)).

A call that carries a progress token gets one progress notification when the run starts:

```text
chargehand run <run-id> started; GET /v1/runs/<run-id> or `chargehand show <run-id>` reads its result
```

When the HTTP request of an MCP call carries `Prefer: wait=N` (N at most 60) and the run has not finished after N seconds, the call returns a tool error. Its first text block names the run and its routes; its second is the run's latest `run-status/v1`. The run goes on, and resending the request would start a second run. Without the header the call waits for the result. Over stdio there is no HTTP request, so no header.

## From the package

The package is a .NET tool that `dnx` (.NET 10 SDK) fetches from [nuget.org](https://www.nuget.org/packages/Chargehand) and runs: no install step, no port, no key. Pin `<version>` to one of its versions. Pass the Claude Code credential as one of `CLAUDE_CODE_OAUTH_TOKEN` or `ANTHROPIC_API_KEY`; `CHARGEHAND_RUNTIME` and `CHARGEHAND_PROFILE` are optional. If a desktop app does not see your shell's `PATH`, give the full path to `dnx`. A profile is optional: without one the workers run on the agent CLI's default model ([package README](../../src/Chargehand.Cli/README.package.md)).

Claude Code:

```bash
claude mcp add chargehand -e CLAUDE_CODE_OAUTH_TOKEN=<token> -- dnx Chargehand@<version> --yes -- mcp
```

VS Code, `.vscode/mcp.json`:

```json
{
  "inputs": [{ "id": "claude-token", "type": "promptString", "description": "claude setup-token", "password": true }],
  "servers": {
    "chargehand": {
      "type": "stdio",
      "command": "dnx",
      "args": ["Chargehand@<version>", "--yes", "--", "mcp"],
      "env": { "CLAUDE_CODE_OAUTH_TOKEN": "${input:claude-token}" }
    }
  }
}
```

Claude Desktop, `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "chargehand": {
      "command": "dnx",
      "args": ["Chargehand@<version>", "--yes", "--", "mcp"],
      "env": { "CLAUDE_CODE_OAUTH_TOKEN": "<token>" }
    }
  }
}
```

`scripts/mcp-smoke.py <dir>` runs a locally packed tool (`dotnet pack src/Chargehand.Cli -o <dir>`) the same way and lists its tools; CI runs it on every change.
