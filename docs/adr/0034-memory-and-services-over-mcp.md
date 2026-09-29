# 0034. Memory and services over MCP

- Status: proposed
- Date: 2026-09-29

## Context

Goal 0.6 (ROADMAP) makes memory and the tools workers use MCP extensions. ADR 0026 fixed the category rows (memory:
MCP memory server, fan-out, labelled by source; services in runs: MCP server per preset, union of tools; secrets: first
success wins) and coded only the secrets. What the code does today is in `docs/specs/2026-09-29-services-and-memory-design.md`,
"Where it stands". The findings that shape this decision:

- One `HindsightMemory` HTTP client behind `IMemoryProvider`, one `memory` object in the profile.
- "Memory fails open" holds for `HttpRequestException` only. Checked 2026-09-29 with a scratch test on a scripted
  runtime: a `TaskCanceledException` from a provider ends `RunAsync` with an exception and no result; a `JsonException` or
  `IOException` fails the run.
- Retain stores the request text, the summary and all claims with no locator, repository or commit, including claims that
  rest only on caller inputs, URLs or session messages.
- Workers get no MCP tools in either runtime. The runtimes take a server differently: Claude Code `--mcp-config` (which
  `--strict-mcp-config` already scopes), OpenCode `PUT /api/experimental/mcp/{server}` per location. How each behaves
  with a granted tool is unknown until the task 1 spike.
- `ModelContextProtocol.Core` 2.2.0, already restored for the server, carries the client. A fake MCP server over
  in-process pipes works in tests.

## Options

**Where servers are declared**
1. Each memory entry embeds its transport; presets embed theirs. Nothing shared; a gateway serving both kinds is written twice
   with its secrets.
2. **Chosen.** One profile registry, `mcp_servers`, referenced by name.

**How a memory server is mapped to `IMemoryProvider`**
1. A fixed convention (tools named `recall` and `retain`). Fits one product; every other server needs a shim.
2. **Chosen.** A declarative mapping per provider: tool names, argument templates with placeholders, and how to read the
   result. Validated at load and by `chargehand extensions check`.

**How workers get a service**
1. chargehand proxies the calls and exposes one filtered server. One code path, its own audit log; a listener, a duplicate
   of the gateway the user may already run, and the "own gateway" the roadmap rules out.
2. **Chosen.** Direct delivery per runtime from a grant chargehand resolves per run: Claude Code `--mcp-config` plus
   `--allowedTools`, OpenCode `PUT` plus generated session rules. Explicit tool names in the preset are the gate.

**What memory retains**
1. Keep today's item (request text, summary, claims). Includes prose nobody checked.
2. **Chosen.** Claims in a completed result that cite at least one `file` or `commit` entry (all evidence in a completed
   result resolved), with locators, repository label and the pinned commit; text the error-text scrubber would change is
   left out. Request text and summary are not retained.

**The single `memory` object**
1. Keep it beside the list. Two shapes to document and test for as long as they coexist.
2. **Chosen.** The list replaces it. `HindsightMemory` stays until the MCP adapter reproduces it field for field and the
   maintainer has run both against a live service; then it is deleted and the object form fails at load with a migration
   message (the `secret_store` precedent, ADR 0026).

## Decision

The choices marked above, and:

- A new project, `Chargehand.Mcp`, holds the MCP client code; the core gains records and interfaces only.
- Recall fans out to every provider with a timeout of 10 s, merges in list order with source labels and per-provider caps
  (10 facts, 4000 characters), and records one `memory/recall/<name>` chain block per contributing source. No contract
  changes; the run log gets an optional `Extensions` report.
- The stack catches every exception except the caller's cancellation. Optional things fail open; a secret that does not
  resolve means no connection; nothing is retained when the commit or citations cannot be established (extends ADR 0013).
- `{secret:item}` in `mcp_servers` headers and environment values resolves through `Profile.Secret`; command sources time
  out at 15 s.
- Stdio servers get the SDK's default environment plus the declared `env`.
- Delivery of services to OpenCode is provisional until the spike records the permission action OpenCode gives an MCP tool.

## Consequences

- `Orchestrator` takes a `MemoryStack` instead of an `IMemoryProvider`; `Program.cs` builds it from the profile.
- `profile/v1` changes: `mcp_servers` is new, `memory` is a list. Profiles with the object form need a migration, listed in
  the changelog when it lands.
- `preset/v1` gains an optional `services` on a node kind (additive; `SchemaCompatTests` passes).
- `as_sent.tools_sha256` includes the granted tools when there are any, and is unchanged otherwise.
- Prompt CI is unaffected: evals run without memory and shipped presets list no services.
- On acceptance, the status lines of ADR 0008 ("amended by 0034": the Hindsight adapter and the retain content) and
  ADR 0026 (the memory and services rows now coded) are updated.
- Retained facts about a repository go to the store the provider points at. Retain stays off by default and per provider.

## Reopen if

The spike shows OpenCode cannot gate MCP tools per session (then services ship on Claude Code first); two providers'
recall proves too slow or too large with the default caps; a memory server needs an argument or result shape the mapping
cannot express; or a caller needs the recalled text in `result/v1`.
