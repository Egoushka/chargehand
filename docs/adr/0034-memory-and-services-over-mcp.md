# 0034. Memory and services over MCP

- Status: accepted
- Date: 2026-09-29

## Context

Goal 0.6 (ROADMAP) makes memory and the tools workers use MCP extensions. ADR 0026 fixed the category rows (memory:
MCP memory server, fan-out, labelled by source; services in runs: MCP server per preset, union of tools; secrets: first
success wins) and coded only the secrets. What the code does today is in `docs/specs/2026-09-29-services-and-memory-design.md`,
"Where it stands". The findings that shape this decision:

- One `HindsightMemory` HTTP client behind `IMemoryProvider`, one `memory` object in the profile.
- "Memory fails open" held for `HttpRequestException` only (checked 2026-09-29 on a scripted runtime: a
  `TaskCanceledException` from a provider ended `RunAsync` with no result; a `JsonException` or `IOException` failed the
  run). #94 widened the run-level catch to any exception but the caller's own cancellation; the stack applies the same
  rule per provider.
- Retain stores the request text, the summary and all claims with no locator, repository or commit, including claims that
  rest only on caller inputs, URLs or session messages.
- Workers get no MCP tools in either runtime. The runtimes take a server differently: Claude Code `--mcp-config` (which
  `--strict-mcp-config` already scopes), OpenCode `PUT /api/experimental/mcp/{server}` per location. How each behaves
  with a granted tool is unknown until the task 1 spike.
- `ModelContextProtocol.Core` 2.2.0, already restored for the server, carries the client, including legacy SSE
  (`HttpClientTransportOptions.TransportMode`: `AutoDetect`, `StreamableHttp`, `Sse`). A fake MCP server over in-process
  pipes works in tests.
- Chronicle, an archive of the owner's own conversations (github.com/Egoushka/chronicle), is a recall-only source: its MCP
  server speaks legacy SSE, takes no credentials and has no write tool.

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
- Stdio servers get the SDK's default environment plus the declared `env`. A `url` server takes an optional `transport`
  (`auto`, the default; `streamable-http`; `sse`), so a legacy SSE server such as Chronicle is reachable.
- A memory entry may be recall-only (no `retain` tool, `retain` false) and may leave out `namespace` (it defaults to the
  entry's name). Recall arguments may use `{max_facts}`; `results.text` is a field name, a template over the result's
  fields, or an ordered list of these (the first whose fields are all present and non-empty wins); each fact is cut at `max_fact_chars` (600).
- Delivery of services to OpenCode (observed in the task 1 spike, see Evidence): `PUT /api/experimental/mcp/{name}` at the
  run's location, then poll `GET /api/mcp` until `connected`, since the `PUT` returns before the connection. The gate is
  the session's rules, on the permission action `<server>_<tool>` (resource `*`); the last matching rule wins, so the
  adapter appends `<server>_* * deny` and then one `<server>_<tool> * allow` per granted tool. A preset's `* * allow`
  reaches every MCP tool registered at the location, so the adapter also denies `<name>_*` for every other server
  `GET /api/mcp` lists there, granted or not. `DELETE` takes effect at once in running sessions, so it runs only when
  no other node holds the server at that location.

## Consequences

- `Orchestrator` takes a `MemoryStack` instead of an `IMemoryProvider`; `Program.cs` builds it from the profile.
- `profile/v1` changes: `mcp_servers` is new, `memory` is a list. Profiles with the object form need a migration, listed in
  the changelog when it lands.
- `preset/v1` gains an optional `services` on a node kind (additive; `SchemaCompatTests` passes).
- `as_sent.tools_sha256` includes the granted tools when there are any, and is unchanged otherwise.
- Prompt CI is unaffected: evals run without memory and shipped presets list no services.
- ADR 0008's status line reads "amended by 0034" (the Hindsight adapter and the retain content) and ADR 0026's says its
  memory and services rows are detailed by 0034; both changed with this acceptance.
- Retained facts about a repository go to the store the provider points at. Retain stays off by default and per provider.

## Evidence (task 1 spike)

Run by hand on 2026-09-29 with throwaway configs and state under the system temp directory and about eighty calls to
small models, under ten cents by the tools' own reports. The stand-in server is `scripts/fake-mcp-server.py`, unchanged from the plan: both pinned clients accepted
it as written. Placeholders: `<cfg>` an MCP config file, `<dirA>` and `<dirB>` scratch git checkouts outside the home
directory, `<key>` a random test key, `<port>` a free loopback port, `<path>` the scripts directory.

Versions: Claude Code 2.1.283 (the pin). OpenCode 2.0.19: the pinned 2.0.18 was not installed on the runner and 2.0.19 was
the `opencode` on `PATH`. O3, O4 and O5 were repeated on 2.0.16 with the same results; the other rows ran on 2.0.19 only.

### Claude Code 2.1.283

Every row runs `CC` with the prompt on stdin, in a scratch git repository, where `CC` is
`claude -p --output-format stream-json --verbose --strict-mcp-config --permission-mode dontAsk --setting-sources "" --model haiku`,
and `<cfg>` is `{"mcpServers":{"fake":{"command":"python3","args":["<path>/fake-mcp-server.py"]}}}`. The prompt "P" is
"Call the echo_fact tool, then the write_note tool, then say DONE. If a tool does not exist or is refused, say so and
continue."

| # | Question | Command | Observation |
|---|---|---|---|
| C1 | Which tools does `init.tools` list, and how are they spelled? | `CC --tools Read --mcp-config <cfg> --allowedTools mcp__fake__echo_fact`; again with the server named `team-docs` | `Read`, `mcp__fake__echo_fact` and `mcp__fake__write_note`: `--tools` does not filter MCP tools, and a tool that is not allowed is still listed. The spelling is `mcp__<server>__<tool>` with the server name unchanged (`mcp__team-docs__echo_fact`). `init.mcp_servers` reads `connected`. `--tools "Read,mcp__fake__echo_fact"` changed nothing |
| C2 | Is the allowed call answered and the other refused? | prompt P with the C1 flags | `echo_fact` was called and answered (`echo_fact ok: spike fact`). `write_note` was called and came back `is_error: true`: "Permission to use mcp__fake__write_note has been denied because Claude Code is running in don't ask mode." followed by advice to use other tools. The run went on and ended `success` after 3 turns. With both names in `--allowedTools` both calls were answered |
| C3 | Does `--disallowedTools` remove the tool from `init.tools`? | `CC --tools Read --mcp-config <cfg> --allowedTools mcp__fake__echo_fact --disallowedTools mcp__fake__write_note` | `init.tools` lists `Read` and `mcp__fake__echo_fact`; the model reported that `write_note` does not exist. `--disallowedTools 'mcp__fake__*'` and `--disallowedTools mcp__fake` (server level) remove both tools; `--allowedTools 'mcp__fake__*'` alone leaves both listed |
| C4 | Does `--bare` accept `--mcp-config`? Does `--setting-sources ""`? | `claude -p --bare --model haiku --tools Read --mcp-config <cfg> --allowedTools mcp__fake__echo_fact`, stopped after the `init` event; `--setting-sources ""` is in `CC` | `--setting-sources ""` (subscription mode) is accepted and connected in every model run here. No API key exists on the runner, so `--bare` made no model call: its `init` event, emitted before any API call, listed both tools and `connected`. A bare-mode call that uses a granted tool was not run |
| C5 | Does an `http` entry with `headers` connect? | `<cfg>` = `{"mcpServers":{"ch":{"type":"http","url":"http://127.0.0.1:<port>/v1/mcp","headers":{"Authorization":"Bearer <key>"}}}}` against an own `chargehand serve` with a throwaway profile; `init` read, process stopped, no model call | Right key: `connected`, `init.tools` lists `mcp__ch__orchestrate`. Wrong key: `failed`, no tool |
| C6 | How many tokens do the tool schemas add to the first call? | prompt P; first assistant event's `usage` (input + cache creation + cache read) | No config 8034; both tools listed with `echo_fact` allowed 8139; both allowed 8139; `write_note` disallowed 8085. The two schemas add 105 tokens (51 and 54); granting makes no difference because ungranted tools of a connected server ship too, and `--disallowedTools` takes a tool's share back |

### OpenCode 2.0.19

The server is `scripts/opencode-serve.sh <binary> <config> <port>` with `CHARGEHAND_STATE` in a temp directory, a random
`OPENCODE_SERVER_PASSWORD` and a config holding only a provider for a small model; HTTP Basic auth as user `opencode`.
A session is `POST /api/session` with `location.directory`, `model` and `permissions`, then
`POST /api/session/{id}/prompt`, `GET /api/session/{id}/message` and `GET /api/session/{id}/permission`.
`FAKE` is `{"type":"local","command":["python3","<path>/fake-mcp-server.py"]}`. Prompts ask the worker to call
`echo_fact` (and `write_note`) and to report what happened.

| # | Question | Command | Observation |
|---|---|---|---|
| O1 | Does `PUT` return 204, and when is the server connected? | `PUT /api/experimental/mcp/fake?location[directory]=<dirA>` with `{"config": FAKE}`, then `GET /api/mcp?location[directory]=<dirA>` every 0.5 s | 204 with no body at once. The first `GET` read `pending`, the second (about 0.5 s later) `connected`. The `PUT` does not wait for the connection |
| O2 | How is the location given, and is the server visible from another one? | `GET /api/mcp` with `location[directory]=<dirA>`, `<dirB>`, nothing, and with the header `x-opencode-directory` | Both the query form and the header work. `fake` is listed for `<dirA>` and not for `<dirB>`: the registry is per location, and every session at a location shares it. With no location the server's own working directory is used |
| O3 | Which `action` and `resources` does the pending request carry? | session at `<dirA>` with `[* * ask]`; prompt to call `echo_fact`; `GET /api/session/{id}/permission`; then reply `reject` | `action: "fake_echo_fact"`, `resources: ["*"]`, `save: ["*"]`: the action is `<server>_<tool>`, and a hyphenated server keeps its hyphen (`team-docs_echo_fact`). Rejecting ended the turn `succeeded` with the tool answering `Permission.DeclinedError`. The worker reaches MCP tools through OpenCode's code-execution tool (`execute` running `tools.<server>.<tool>(...)`, with `search` to list them), not one function per tool; with `"codemode": false` in the server config they are functions named `<server>_<tool>` and the action is the same. Also 2.0.16 |
| O4 | With `[* * allow]` alone, is the MCP tool callable? | session with `[* * allow]`; same prompt | **Yes.** `fake.echo_fact` completed and returned its text. With the default preset's whole rule list (`* * allow`, then deny `edit`, `.env` reads, `shell`, `webfetch`, `external_directory`, `question`, `subagent`), both `echo_fact` and the write-shaped `write_note` were called. With the `draft` preset's `* * deny` the worker had neither `search` nor `execute` and made no tool call. Also 2.0.16 |
| O5 | With a deny on the action, is the tool gone or refused? | session with `[* * allow, fake_echo_fact * deny]`; prompt for both tools; then a call by exact name | Gone: `search` lists no `echo_fact`, and `return await tools.fake.echo_fact({})` answers "Unknown tool 'fake.echo_fact'". `write_note`, which no rule names, was called. The last matching rule wins: `[* * allow, team-docs_* * deny, team-docs_echo_fact * allow]` left exactly `echo_fact`. With `"codemode": false`, `plain_echo_fact * deny` removed the function. Also 2.0.16 |
| O6 | Do two sessions at one location keep separate rulesets? | two sessions at `<dirA>`, `[* * allow]` and `[* * allow, fake_echo_fact * deny]`, prompted at the same time | Separate: the allowing session called `echo_fact`, the denying one reported it missing |
| O7 | Does `DELETE` end availability in a running session? | `DELETE /api/experimental/mcp/fake?location[directory]=<dirA>`, then the prompt again in the same session | 204, and `GET /api/mcp` is empty at once. In the running session `search` finds nothing and a call by name answers `MCP server "fake" is not available`. No restart needed |
| O8 | Does a `remote` entry send `headers`? | `PUT` of `{"config":{"type":"remote","url":"http://127.0.0.1:<port>/v1/mcp","headers":{"Authorization":"Bearer <key>"}}}` against an own `chargehand serve`; once with the right key, once with a wrong one, once with no headers | Sent: the right key is `connected`; the wrong key and no headers are `needs_auth` ("requires a login before it can register a client") |

Two things the plan did not ask for turned up:

- **A checkout registers its own servers.** A checkout whose root holds `opencode.json` with an `mcp` entry
  (`{"mcp":{"x":{"type":"local","command":[...]}}}`) has that server started and `connected` as soon as a session is
  created at the location (2.0.19; `GET /api/mcp` alone did not do it, `POST /api/session` did, and the command ran as a
  child of the OpenCode server). No model call used it; by O4 its tools reach a worker under `* * allow`.
- **Built-in tool namespaces.** With the default preset's rules and no MCP server at the location, the worker's catalog
  still lists `tools.browser.*` and an `opencode` namespace (2.0.16 and 2.0.19). A `browser` call answered
  "No desktop browser is connected to this session", after argument validation and before any permission request appeared,
  so whether those actions are gated by rules is not known.

### What the evidence settles

- OpenCode can gate MCP tools per session (O3 to O6): a known action, a deny rule that removes the tool from the
  session's catalog, separate rulesets at one location, and last match wins. Open decision 1 resolves to covering both
  runtimes; task 10 stays a separate task.
- Under today's presets `* * allow` reaches MCP tools on OpenCode (O4): five of the six shipped presets (`default`,
  `cheap`, `thorough`, `strict`, `review`) allow first and deny by action, none of which names an MCP action. Nothing
  chargehand starts registers a server (the default server of ADR 0030 has no `mcp` block, and chargehand never calls the
  `PUT`), so the exposure needs a server registered from outside: a user-supplied OpenCode server with an `mcp` block, a
  `PUT` by another client, or a checkout's own `opencode.json`. It is independent of `services`, so it needs its own fix
  in the preset compile step (deny `<name>_*` for the servers listed at the location), not only task 10's grant rules.
  Whether a rule can be added after the session exists (the spec lists `permissions` on `PATCH /api/session/{id}`) is
  untested; a checkout's servers appear only once the session exists.
- Claude Code needs no such fix: `--strict-mcp-config` keeps other servers out and `dontAsk` refuses an MCP tool that
  `--allowedTools` does not name (C1, C2). For task 9: pass `--mcp-config` and one `--allowedTools` per granted tool,
  leave `--tools` alone, and add `--disallowedTools` for the tools of a granted server that the grant leaves out, or accept
  about 50 tokens per schema (C6). `--bare` takes the flag (C4, to the `init` event).

## Reopen if

The spike shows OpenCode cannot gate MCP tools per session (then services ship on Claude Code first); two providers'
recall proves too slow or too large with the default caps; a memory server needs an argument or result shape the mapping
cannot express; or a caller needs the recalled text in `result/v1`.
