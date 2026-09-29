# Runs use your services and memory (goal 0.6)

- Status: draft for the maintainer's review
- Date: 2026-09-29

## Goal

A run recalls from several memory servers at once, keeps only claims whose citations resolved, and gives its workers
read-only tools from MCP servers the preset names. Memory and services are MCP servers the user lists in the profile.
With none listed, a run behaves as it does today.

Done when a run recalls from two stacked memory providers, retains only claims whose citations resolved, and a preset
gives workers a service through MCP (decision 1).

## Where it stands

Evidence for every statement about current behaviour; `path:line` is at commit `b566890`.

1. **Memory is one HTTP client.** `IMemoryProvider` (recall, retain, invalidate) has one implementation, `HindsightMemory`
   (`src/Chargehand/Memory/IMemoryProvider.cs:14-22`, `src/Chargehand/Memory/HindsightMemory.cs:33`), built from a single
   `memory` object (`src/Chargehand/Config/Profile.cs:26`, `:128`; `src/Chargehand.Cli/Program.cs:261-263`). ADR 0026
   planned "category lists (memory, …)"; only `secrets` became a list (`Profile.cs:21`).
2. **Recall is unlabelled.** Once per executed run, after intake, the facts are appended to each node's prompt text under
   one header that calls them unverified (`src/Chargehand/Orchestrator.cs:171-179`, `:252-268`). The chain records one
   runtime block, `memory/recall` (`:173`).
3. **Retain writes unchecked prose.** It stores the request text, the summary and every claim as one item, `document_id`
   the run id (`Orchestrator.cs:235-240`). Claims in a completed result have resolved citations
   (`ResultAssembler.MoveUnresolved`, `src/Chargehand/Results/ResultAssembler.cs:89-111`, called at
   `src/Chargehand/Nodes/WorkerNode.cs:102`), but the summary and the request text were never checked, the item names no
   locator, repository or commit, and a claim resting only on a caller `input`, a `url` or a `session_message` is kept
   beside file-anchored ones (those kinds resolve without touching the repository,
   `src/Chargehand/Verification/GitEvidenceResolver.cs:17-26`).
4. **"Fails open" holds for one exception type.** Both memory catch blocks name `HttpRequestException`
   (`Orchestrator.cs:241`, `:263`). Scratch test, 2026-09-29, not committed, a provider throwing on recall:
   `HttpRequestException` gave a completed run; `TaskCanceledException` (what `HttpClient.Timeout` raises; the CLI sets
   30 s, `Program.cs:262`) escaped `RunAsync`, whose catch skips cancellations (`Orchestrator.cs:133`), so the caller got
   an exception and no result; `JsonException` and `IOException` gave a failed result carrying the exception message. An
   MCP client raises the last three kinds. **CONFIRMED.**
5. **No test drives a run that recalls or retains.** `HindsightMemoryTests` checks the HTTP requests only, and
   `OrchestratorActionTests` builds its orchestrator with no provider (`docs/guide/capabilities.md:166`).
6. **The MCP client is in the dependency graph, not in the core.** `ModelContextProtocol.AspNetCore` 2.2.0
   (`src/Chargehand.Server/Chargehand.Server.csproj:16`) depends on `ModelContextProtocol.Core` 2.2.0, which holds
   `McpClient`, `StdioClientTransport` (command, arguments, environment variables, `InheritEnvironmentVariables`),
   `HttpClientTransport` (endpoint, `AdditionalHeaders`), `ListToolsAsync`, and `CallToolAsync` returning `Content`,
   `StructuredContent` and `IsError`; `Tool.Annotations` carries the server's hints. `src/Chargehand/Chargehand.csproj`
   references only `YamlDotNet` and the contracts; adapters live in their own projects. Scratch test, 2026-09-29, not
   committed: a fake MCP server and the SDK client over two in-process pipes, as `StdioMcpTests` does
   (`tests/Chargehand.Tests/StdioMcpTests.cs:24-40`), listed a tool, called it and read a JSON text block. Memory adapter
   tests need no network and no child process. **CONFIRMED.**
7. **Workers have no MCP tools today.** Claude Code runs with `--strict-mcp-config` on every call
   (`src/Chargehand.ClaudeCode/ClaudeCodeWorkerRuntime.cs:220`) and a `--tools` catalog built from the preset's rules
   (`:241`; `ToolsByAction` at `:386-399` lists built-in tools only). OpenCode gets the preset's rules as the session
   ruleset (`src/Chargehand.OpenCode/OpenCodeWorkerRuntime.cs:36-41`), and the server chargehand starts has no `mcp`
   block (`src/Chargehand.OpenCode/OpenCodeServerProcess.cs:20-27`). ADR 0010 decision 2 already reads "workers run
   without MCP servers unless a preset fixes a set" (`docs/adr/0010-context-strategy.md:28`).
8. **Each runtime can take a server, in different ways.** Claude Code 2.1.283 lists `--mcp-config <configs...>` (JSON
   files or strings) and `--strict-mcp-config` in `--help`; chargehand does not pass the first. OpenCode 2.0.18's spec
   has `PUT /api/experimental/mcp/{server}` (add or replace a `local` server with `command` and `environment`, or a
   `remote` one with `url` and `headers`, for a `location`, connecting at once), `DELETE` (remove "until restart") and
   `GET /api/mcp` (name and status only) (`docs/opencode-openapi.json`: `experimental.mcp.add`,
   `experimental.mcp.remove`, `mcp.list`). None is in `docs/opencode-adapter-ops.json`, and ADR 0004's rule against
   `/api/config*` (`docs/adr/0004-opencode-major-and-runtime-adapter.md:43`) does not name these paths. How either
   runtime behaves with a granted tool is **UNKNOWN** until task 1.
9. **Presets allow first.** Every shipped preset opens with `* * allow` and denies by action (`presets/default.yaml:12`).
   If OpenCode's permission action for an MCP tool matches `*`, tools of servers already configured on a user-supplied
   OpenCode server reach workers today. **UNKNOWN**, not tested (task 1). Claude Code is the reverse: its translation
   has no entry for MCP tools and it runs in `dontAsk` mode, so an MCP tool needs an explicit `--allowedTools` entry
   (ADR 0020, mapping table).
10. **Secrets are already a first-wins chain** (ADR 0026, `Profile.cs:70-80`; default: environment variables). A command
    source has no time limit: `RunCommand` reads to the end, then waits (`Profile.cs:83-94`), so a hung store hangs
    whoever resolves. 0.6 consumes the chain and bounds it; it does not build it.
11. **`result/v1` already holds what 0.6 records.** A chain block's `name` is any non-empty string and its `source` may be
    `runtime` (`schemas/result/v1/result.schema.json:101-106`); an evidence `commit` is optional. Contracts take
    additive changes only, so 0.6 adds nothing to them (decision 7).

## Decisions (proposed; the maintainer accepts or changes them)

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | Done when | A run recalls from two stacked memory providers, retains only claims whose citations resolved, and a preset gives workers a service through MCP. Shown by a scripted acceptance test in CI and a live run on the maintainer's machine | ROADMAP 0.6: "any MCP memory server, several at once"; ADR 0026 rows for memory, services and secrets |
| 2 | Default | Nothing is connected unless the profile says so. No shipped preset lists `services` | Naked core (ADR 0026); a shipped preset that needs a server would fail on a clean machine |
| 3 | Where the MCP client lives | A new project `src/Chargehand.Mcp` referencing `ModelContextProtocol.Core`. The core keeps `IMemoryProvider`, the stack and records, and gains no SDK reference | Adapters sit in their own projects (`Chargehand.OpenCode`, `Chargehand.ClaudeCode`); the SDK stays out of `Chargehand.Contracts` and the core |
| 4 | Where servers are declared | One profile block, `mcp_servers`, keyed by name (`[a-z][a-z0-9-]*`). `memory[]` and preset `services` refer to a name | One place for transport and secrets. A gateway that serves memory tools and service tools is one entry. The pattern keeps Claude Code's `mcp__<server>__<tool>` names unambiguous |
| 5 | Memory config | `memory` becomes an ordered list of providers: `name`, `server`, `namespace`, `tools` (a declarative mapping), optional limits, `retain` (default false). No default mapping | Servers name tools and arguments differently, and the roadmap calls for "a tool mapping". A list is ADR 0026's category shape; order is priority. The single-object form ends in task 11 |
| 6 | Stacking | Fan-out: every provider is asked at once, each under its own timeout (default 10 s). Results merge in list order, each fact labelled with its provider's name; a fact several providers return appears once with all their names. Per provider at most 10 facts and 4000 characters (defaults) | ADR 0026: "fan-out, labelled by source". The caps keep one source near 1k tokens against a 5.6k-token session base (ADR 0010 evidence table); the timeout bounds run-start latency, which recall sits on |
| 7 | Recall provenance | In the prompt (the bracketed names), as one `prompt_chain` block per contributing provider (`memory/recall/<name>`, source `runtime`), in a new optional `RunRecord.Extensions` report in the run log, and as span tags. `result/v1` does not change | Chain blocks already record runtime text by hash. The contract is additive-only and no caller has asked for the recalled text (open decision 9) |
| 8 | Retain rule | A run retains only claims that (a) are in a completed result, so their citations resolved, (b) cite at least one `file` or `commit` entry, and (c) are unchanged by the error-text scrubber. It writes the claim, its locators, the repository and the commit, and not the request text or the summary. A claim resting only on `input`, `url` or `session_message` is not retained | The rule of record: retain claims whose citations resolved, with locators, repository and commit. `input`, `url` and `session_message` anchor to a request or a session, which do not outlive the run; the summary and request text were never checked. (c) fails closed on text that looks like a key |
| 9 | Failure rules | Optional things fail open, credentials and privacy fail closed, checks that fail become open questions (table below). The stack catches every exception except the caller's own cancellation | Extends ADR 0013. Finding 4: the current catch is too narrow for MCP |
| 10 | Services | A preset's node kind may list `services`: a profile server and explicit tool names (globs allowed, a lone `*` is not). At run start chargehand connects, lists the server's tools, expands the names and hands the runtime a grant. A server, secret or tool that does not resolve drops that service and is logged; the run goes on | ADR 0010 decision 2 (tool sets fixed per preset and node kind); ADR 0026: "union of tools", "its tools missing, logged". Explicit names because tools rarely carry read-only hints and ADR 0006 keeps workers read-only |
| 11 | Delivery to workers | Direct. Claude Code gets `--mcp-config` (a 0600 file outside the checkout) plus `--allowedTools mcp__<server>__<tool>` per granted tool; OpenCode gets `PUT /api/experimental/mcp/{name}` for the run's location plus session rules generated from the grant. chargehand does not proxy tool calls | A proxy would duplicate the gateways users already run and add a listener; both runtimes accept a remote server. Task 1 checks the parts marked UNKNOWN before any adapter code |
| 12 | Secrets in MCP config | `{secret:item}` in a header or environment value, resolved through `Profile.Secret`; never in a URL. An unresolved secret means no connection, never an anonymous one. Command sources time out at 15 s and fall through to the next | ADR 0026 chain, "none left: stop"; URLs are logged and leak (privacy rule). A hung store must not hang a run |
| 13 | Environment of stdio servers | The SDK's default environment plus the declared `env`; nothing else is inherited | chargehand's own environment can hold the API key of its HTTP interface and Claude Code credentials |
| 14 | `HindsightMemory` | Kept until the MCP adapter reproduces it field for field (test) and the maintainer has run both against a live service; then deleted with its tests and the `backend` schema value (task 11). A profile still carrying the object form fails at load with the migration | The roadmap deletes the client once the MCP adapter matches. A migration error beats a silent skip; ADR 0026 set the precedent with `secret_store` |
| 15 | ADR | ADR 0034 records decisions 3 to 14. On acceptance, ADR 0008 and ADR 0026 get "amended by 0034" in their status lines | Repo convention: architecture-changing decisions get an ADR |

## Non-goals

- An own memory store, agent loop or model gateway (ROADMAP, "Not planned").
- A chargehand MCP proxy that filters tool calls. Users with a gateway filter there.
- Writing services. Workers stay read-only until the sandbox goal (0.7, ADR 0006).
- A new evidence kind for service output. A claim taken from a service cites a URL the service returned (kind `url`,
  resolved as "seen in tool output", `GitEvidenceResolver.cs:23`). Stored bytes and a support check are 0.8.
- OAuth for MCP servers: the client has an OAuth option, but a stdio host has no browser. Static headers only.
- MCP resources, prompts, sampling and elicitation on the client side. Tools only; chargehand declares no client
  capabilities, so a server that asks for sampling is refused.
- Memory tools handed to workers. A memory server can be listed under `services` for that, and nothing here prevents it,
  but recall and retain stay chargehand's own step.
- Changes to `request/v1`, `result/v1` or `run-status/v1`.

## Design

### Components

| Unit | Where | What it does |
|---|---|---|
| Connections | `src/Chargehand.Mcp` (new), `McpConnectionPool` | One lazily opened `McpClient` per profile server, shared by memory and services, reopened after its transport closes, disposed at exit. Stdio servers get the declared `env` only |
| Secret templates | `SecretTemplate` | Replaces `{secret:item}` with `Profile.Secret(item)`; refuses it in a URL |
| Memory adapter | `McpMemoryProvider` | An `IMemoryProvider` that calls the mapped tools |
| Memory stack | `src/Chargehand/Memory/MemoryStack.cs` | Fan-out recall, merge, labels and caps; fan-out retain |
| Retain rule | `src/Chargehand/Memory/RetainableClaims.cs` | Pure function from a result to the claims that qualify |
| Service resolver | `IServiceResolver` (core), `ServiceResolver` (`Chargehand.Mcp`) | A preset's `services` to `ServiceGrant`s and a list of issues |
| Preset | `schemas/preset/v1`, `Preset.cs` | Optional `services` on a node kind (additive) |
| Delivery | `ClaudeCodeWorkerRuntime`, `OpenCodeWorkerRuntime` | Read `NodeSpec.Services` |
| Check command | `chargehand extensions check` | Connects each server, lists tools, validates each mapping, exits 1 on a problem |

### The profile

```json
"mcp_servers": {
  "memory-gateway": {
    "url": "https://mcp.example.internal/mcp",
    "headers": { "Authorization": "Bearer {secret:memory-gateway-token}" }
  },
  "team-notes": {
    "command": ["npx", "-y", "example-notes-mcp"],
    "env": { "NOTES_TOKEN": "{secret:team-notes-token}" }
  }
},
"memory": [
  {
    "name": "hindsight",
    "server": "memory-gateway",
    "namespace": "chargehand",
    "tools": {
      "recall": { "tool": "recall", "arguments": { "query": "{query}", "bank_id": "{namespace}", "budget": "low", "max_tokens": 1024 } },
      "retain": { "tool": "retain", "arguments": { "content": "{text}", "context": "{context}", "document_id": "{document_id}", "timestamp": "{timestamp}", "tags": "{tags}", "bank_id": "{namespace}" } },
      "invalidate": { "tool": "invalidate_memory", "arguments": { "memory_id": "{id}", "reason": "{reason}", "bank_id": "{namespace}" } }
    },
    "retain": false
  },
  {
    "name": "notes",
    "server": "team-notes",
    "namespace": "notes",
    "tools": {
      "recall": { "tool": "search_notes", "arguments": { "q": "{query}", "limit": 10 }, "results": { "path": "notes", "id": "id", "text": "body" } }
    }
  }
]
```

`mcp_servers.<name>` has exactly one of `url` (Streamable HTTP, optional `headers`) or `command` (stdio argv, optional
`env`). Memory entry fields besides those shown: `max_facts` (10), `max_chars` (4000), `timeout_seconds` (10),
`retain_tags` (`["chargehand"]`).

The mapping is declarative:

- **Arguments.** A JSON string that is exactly one placeholder is replaced by the value with its type (`{tags}` an array,
  `{timestamp}` an ISO 8601 string); a null value omits the argument. A placeholder inside longer text is replaced as
  text. Numbers and booleans are literal. Placeholders: `{query}` and `{namespace}` (recall); `{namespace}`, `{text}`,
  `{context}`, `{document_id}`, `{timestamp}` and `{tags}` (retain); `{namespace}`, `{id}` and `{reason}` (invalidate).
  An unknown placeholder fails at load.
- **Results.** For recall, the tool's `structuredContent` if present, else its first text block parsed as JSON.
  `results.path` is a dotted path to the array (empty: the root), `id` and `text` name the fields (defaults `id`,
  `text`); a missing id becomes the first 12 hex characters of the text's SHA-256. `results.format: "text"` takes the
  whole first text block as one fact. `isError: true`, a path that does not resolve or unparsable JSON is a provider
  failure.
- **Required tools.** `recall` for a provider to take part in recall; `retain` when `retain` is true; `invalidate` is
  optional and unused by a run (nothing under `src` calls `InvalidateAsync` outside the adapters).

The recall shape above matches a Hindsight service seen through an MCP gateway on 2026-09-29: one text block holding
`{"results":[{"id":…,"text":…,…}]}`, which is what `HindsightMemory.RecallAsync` reads (`HindsightMemory.cs:45`).

### Stacking

`MemoryStack` holds `MemorySource(name, provider, scope, limits, retain, retainTags)` in profile order.

- **Recall** runs every source at once under `timeout_seconds`. A source that throws, times out or returns something the
  mapping cannot read is skipped with a scrubbed reason; only the run's own cancellation propagates.
- **Merge** walks sources in order. Per source it takes items until `max_facts` or `max_chars`, collapses whitespace (a
  fact is one line), and drops an item whose text (trimmed, case-insensitive) an earlier item already carried, adding
  its source's name to that item's label.
- **Prompt** (appended after the task, as today, computed once per run):

```
Facts from long-term memory (unverified; check them in the repository and cite files, never these). The name in brackets is the memory each came from:
- [hindsight] Deploys go through GitOps.
- [hindsight, notes] The API registers handlers with MediatR.
```

- **Provenance.** One chain block `memory/recall/<name>` per source that contributed, hashed over that source's lines;
  `RunRecord.Extensions` lists, per source, the count recalled or the skip reason; span tags
  `chargehand.memory.<name>.recalled` and `.error`. `chargehand show` prints the report.
- **Evals** still run without memory (`Program.cs:197-198`, ADR 0008).

### Retain

After the result is assembled, `RetainableClaims.From(result, checkout)` returns the qualifying claims, and each source
with `retain: true` gets one item:

```
Repository: <label>, commit <12 hex> (citations checked at this commit)
- <claim text> [src/Api/Startup.cs:41-58; src/Api/Handlers/Ping.cs:1-20] (confidence 0.90)
```

`Context` is `chargehand run result`, `DocumentId` the run id (as today, so one run is one document), `Timestamp` the
run's finish, `Tags` the source's `retain_tags`. No qualifying claim, no call; the run log says why.

- **Locators** are the `file` evidence locators (`path:line` or `path:start-end`) and `commit <sha>` for commit evidence,
  taken from the result's evidence list. The commit written is the run's pinned commit (`Orchestrator.cs:159-161`), not
  what the worker typed.
- **Repository label.** The checkout's `origin` URL without scheme, user information and a trailing `.git`; the directory
  name when there is no `origin` (open decision 6). Computed where the checkout is made (`Orchestrator.cs:283`).
- **Scrubber.** `ChargehandException.Scrub` (`src/Chargehand/ChargehandException.cs:32`) must leave a claim's text and
  locators unchanged. The scrubber is loose (it matches words like `token:`), so some plain claims are skipped; retain is
  off by default and best effort, and a skipped claim is counted in the report.
- **What "resolved" means.** The cited path and lines exist at the commit (ADR 0009). The resolver does not compare the
  cited text with the claim; that check is 0.8. The retained item says "citations checked", and no more.
- **A split run** retains the merged result's claims; each part ran at the same commit
  (`src/Chargehand/Results/ResultMerger.cs:27-28`).

### Services in runs

A preset node kind lists what its workers may use:

```yaml
node_kinds:
  worker:
    # model, permissions, budget as usual
    services:
      - server: team-docs
        tools: [search_docs, read_doc]
```

At run start, before the node's session exists, `ServiceResolver` does this per entry: find the server in `mcp_servers`
(else issue `unknown_server`); resolve its secrets (else `secret_unresolved`); connect through the pool and list tools
(else `unreachable`); expand names and globs against the list, and report a requested name that is not listed
(`tool_missing`); keep a grant if any tool remains. Issues go to `RunRecord.Extensions` and a span tag. A grant is
`ServiceGrant(server, transport, tools, sha256 of the tool descriptions and input schemas)`; its `ToString` hides header
and environment values. `NodeSpec` gains an optional `Services` list, so a forked session (Claude Code copies the source
spec, `ClaudeCodeWorkerRuntime.cs:142-156`) keeps the tools of its source. The grant's names and hash join
`as_sent.tools_sha256`, which is unchanged when nothing is granted, so cache reports keep comparing.

**Claude Code.** In `Start`, when the spec has grants: write `{"mcpServers": {…}}` to a 0600 file in a per-session
directory under the system temp directory, outside the checkout; pass `--mcp-config <file>` (the existing
`--strict-mcp-config` keeps every other server out) and one `--allowedTools mcp__<server>__<tool>` per granted tool. The
mode stays `dontAsk`, so any other tool is denied (ADR 0020). The file goes when the session's last turn ends. UNKNOWN
until task 1: that `--tools` leaves MCP tools alone, that a non-granted tool of a connected server stays out of the
catalog (token cost, ADR 0010), and that `--bare` accepts the flag as its help says.

**OpenCode.** Before the session is created, `PUT /api/experimental/mcp/{name}` with
`{"config": {"type": "remote", "url", "headers"}}` or `{"type": "local", "command", "environment"}` for the run's
`location`, then `GET /api/mcp` to see the status. Session rules are the preset's plus rules generated from the grant.
`DELETE` runs when no other node holds the same server at that location (the checkout, so the registry, is shared
between runs, ADR 0023). UNKNOWN until task 1: the permission action OpenCode gives an MCP tool, whether the leading
`* * allow` reaches it, whether a server added at a location is visible to every session there, and whether `PUT` returns
before the server is connected. If tools cannot be gated per session, the adapter refuses a preset with services on a
server it did not start (open decision 1).

### Secrets

`mcp_servers` may hold `{secret:item}` in `headers` values and `env` values only. `SecretTemplate` calls
`Profile.Secret(item)` when a connection opens, so the first source of the chain that resolves the item wins, and none
left means no connection. Messages name the item, never the value, and pass through `ChargehandException.Scrub`. The
command source gets a 15 s limit (decision 12), so a locked keychain lets the next source answer or the connection fail,
and never stalls a run.

### Failure behaviour (extends ADR 0013)

| Situation | Rule | What happens |
|---|---|---|
| A memory server is down, times out, errors, or returns what the mapping cannot read | Fail open | Skipped for this run, reason in the report; other providers still contribute; the run continues, with no facts if all fail |
| A memory server needs a secret no source resolves | Fail closed for the credential | No connection is made; the provider is skipped as above |
| Retain fails | Fail open | Logged; the result is unchanged |
| A claim does not qualify for retain (unresolved, not repository-anchored, redacted text) | Checks become open questions, or the claim is left out of retain | Unresolved claims are already in `open_questions` (`WorkerNode.cs:102`); the others are counted as skipped |
| The commit or the citations of a run cannot be established | Fail closed | Nothing is retained |
| A service's server, secret or tool does not resolve | Fail open | That service's tools are missing for the run, logged; the run continues |
| A granted service tool errors during a run | The worker's business | The worker sees a tool error; an uncited claim becomes an open question as usual |
| The caller cancels the run | Propagate | Not a memory failure; nothing swallows it |

### `HindsightMemory`

The mapping in "The profile" reproduces the HTTP client: recall sends the query, the namespace as the bank, budget `low`
and 1024 tokens (`HindsightMemory.cs:43`); retain is the asynchronous tool with content, context, timestamp, document id
and tags (`:50-51`); invalidate sets the reversible state with a reason (`:56`); results read `id` and `text` (`:45`).
`McpMemoryProviderTests.Hindsight_mapping_sends_what_HindsightMemory_sends` drives both adapters against recording fakes
and compares the field sets. The deletion (task 11) waits for that test and for the maintainer's live run of
`chargehand extensions check` and one recall against the running service. `IMemoryProvider` stays, so a later adapter
that is not MCP still fits.

### Testing

- A `FakeMcpServer` test helper (a real MCP server over in-process pipes, the shape of finding 6) serves scripted tools;
  it backs the adapter, stack, resolver and check-command tests.
- Unit tests: mapping (arguments, results, errors), stack (order, caps, duplicates, timeout, every exception kind of
  finding 4), retain rule (a table of claims), secret templates, profile and preset schema.
- Integration on a `ScriptedRuntime` with a real git repository: a run with two stacked fake providers, one failing; a
  retain call that carries only qualifying claims; a run whose profile lists no memory or services is identical in
  prompt, chain and tools hash to today's.
- Runtime arguments: the fake `claude` script of `ClaudeCodeRuntimeTests` records argv; OpenCode's recording HTTP handler
  records the `PUT` and the session body; `OpenCodeSpecContractTests` guards the new routes.
- Acceptance test for the done bar (task 12), then a live run by the maintainer: two real providers and one real service,
  on both runtimes if open decision 1 covers both.

## Open decisions for the owner

The evidence does not settle these. Each has a recommendation; none is applied.

1. **OpenCode in 0.6.** The done bar needs one runtime to give a service. Cover both if task 1 shows OpenCode can gate MCP
   tools per session; else ship Claude Code first and refuse, with a clear error, a preset with services on OpenCode.
   Recommendation: decide after task 1; the plan keeps task 10 separate so it can slip without moving the goal.
2. **Memory config shape.** `memory` as a list under the same key, with the object form failing at load (proposed), or a
   new key (`memories`) while the object form still works until task 11. Recommendation: same key; the profile is
   pre-1.0 and the migration is one paragraph.
3. **One server registry for memory and services** (proposed), or a memory entry that embeds its own transport.
   Recommendation: registry.
4. **What counts as resolved for retain.** At least one surviving `file` or `commit` citation (proposed; a claim that lost
   a citation in the repair turn still qualifies), or every citation the worker first gave. The stricter form needs the
   pre-repair evidence, which `WorkerNode` discards (`WorkerNode.cs:100-102`). Whether `diff` citations count once 0.7
   writes commits. Recommendation: the proposed form now, revisit with 0.8's support check.
5. **Retain scope.** On or off per provider (proposed), or also an allowlist of repositories, since retained facts about
   private code go to whatever store the provider points at. Recommendation: per provider now; add the allowlist when a
   second store is shared.
6. **Repository label.** Normalised `origin` URL (proposed) or the directory name. The URL identifies the repository
   across machines and may hold an organisation's name, which the user's own store then holds.
7. **Service gates.** Explicit tool names only (proposed). Also: require the server's `readOnlyHint` (MCP treats an absent
   hint as "may write", so most servers would fail it); and a `required: true` on a service so a missing server fails the
   run closed instead of dropping the service (needs one new `error.code` value, additive). Recommendation: no hint
   requirement and no `required` flag in 0.6.
8. **Preset opt-out of memory.** Whether a preset (say `review`) can turn recall and retain off, since recalled facts may
   bias a review. Recommendation: no; the profile decides, and evals already run without memory.
9. **Recalled text in `result/v1`.** Keep it in the run log and the chain hashes (proposed), or add an optional `memory`
   object to `result/v1` (additive, but fixed once published). Recommendation: run log until a consumer asks.
10. **Retain confidence floor.** Retain every qualifying claim (proposed) or add a `retain_min_confidence` (say 0.5).
    Recommendation: none until retained noise shows up.
11. **Defaults.** 10 s timeout, 10 facts, 4000 characters and a 15 s secret command limit are starting values from the
    session-base arithmetic and the current 30 s HTTP timeout, not measurements. Recommendation: accept, then read the run
    log after a week of use.
12. **What "matches" for deleting `HindsightMemory`.** The field-for-field test plus one live run by the maintainer
    (proposed), or a longer soak with both adapters configured. Recommendation: the test and one live run.
13. **The check command.** `chargehand extensions check` covers memory and services (proposed), or two commands.
    Recommendation: one.

## Out of scope for 0.6

Task sources and issue trackers (after 1.0); writing services and shell access for workers (0.7); support checking of
service output and stored bytes (0.8); signed results (0.8); an MCP proxy in chargehand; interactive OAuth for MCP
servers.
