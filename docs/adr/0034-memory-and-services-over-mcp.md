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
  the session's rules, on the permission action `<server>_<tool>` (resource `*`); the last matching rule wins. A
  preset's `* * allow` reaches every MCP tool registered at the location, so every OpenCode session's rules end with
  `*_* * deny` (`OpenCodeWorkerRuntime.DenyMcpTools`, in force since the follow-up under Evidence), and the adapter
  appends one `<server>_<tool> * allow` per granted tool after it. The deny is a pattern, not a rule per listed server.
  `DELETE` takes effect at once in running sessions, so it runs only when no run holds the server at that location
  (the lifecycle is under Evidence, "Delivery to OpenCode").

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
  in the OpenCode adapter's session rules, not only task 10's grant rules.
  A checkout's servers appear only once the session exists, so the fix cannot be a rule per listed server (see the
  follow-up below).
- Claude Code needs no such fix: `--strict-mcp-config` keeps other servers out and `dontAsk` refuses an MCP tool that
  `--allowedTools` does not name (C1, C2). For task 9: pass `--mcp-config` and one `--allowedTools` per granted tool,
  leave `--tools` alone, and add `--disallowedTools` for the tools of a granted server that the grant leaves out, or accept
  about 50 tokens per schema (C6). `--bare` takes the flag (C4, to the `init` event).

### Follow-up: closing the exposure

Measured on 2026-09-29 on OpenCode 2.0.19 and 2.0.16, throwaway servers and state, a stand-in model provider that records
each request and asks for one code-execution call per turn. Sources are the OpenCode 2.0.18 tag unless said otherwise.

- **A checkout can start a command.** `packages/cli/src/server-process.ts` (2.0.13 to 2.0.19) skips project
  configuration when `OPENCODE_CONFIG_PROJECT_DISABLE ?? OPENCODE_DISABLE_PROJECT_CONFIG` is `1` or `true`; the first name
  wins when set, even to `0`. `packages/core/src/config/discovery.ts` then finds no `opencode.json`, `.opencode`,
  `.claude` or `.agents` in the checkout. The public config and CLI pages list neither name; the V2 instructions page
  mentions the second. A checkout `opencode.json` declaring a local server whose command creates a file: created by
  `POST /api/session` in 7 of 7 runs without the variables (the `.opencode/opencode.json` form in 5 of 7), in 0 of 6 with
  them, and again with `OPENCODE_CONFIG_PROJECT_DISABLE=0` set beside `OPENCODE_DISABLE_PROJECT_CONFIG=1`, so both are set.
  The same switch stops the checkout's `AGENTS.md` and `.claude` skills from reaching the model (checked in the request).
- **A listing cannot gate it.** The first `GET /api/mcp` after `POST /api/session` at a fresh location listed no server in 8
  of 8 tries; the four the checkout declared appeared 0.1 to 0.7 s later. A server's permission prefix is its name with
  characters outside `[A-Za-z0-9_-]` replaced by `_` (`McpTool.namespace`), and OpenCode's own resource tools
  (`opencode_list_mcp_resources`, `opencode_read_mcp_resource`) read every connected server under their own actions, which a
  `<server>_*` deny does not cover. `PATCH /api/session/{id}` with `permissions` replaces the whole ruleset, and a fork
  keeps its parent's.
- **A pattern can.** Rules `[* * allow, edit * deny, external_directory * deny]` with and without a last rule
  `*_* * deny`, given at session creation; a script calls the tool, the write-shaped tool, the resource list and read of
  a configured server, and a tool of a server added by `PUT` after the session began. Without the rule, answered (on
  2.0.16 the checkout server's own two tools were not reachable; the other calls were). With it, every call came back
  as an unknown tool on both versions, and with `team-docs_echo_fact * allow` after it exactly that tool was answered.
  Through the shipped code (a server chargehand started, a server in its global config, a checkout config that would
  create a file): no file, all four calls refused; the same rules without the appended deny: all four answered.
  Every shipped preset denies `external_directory` (`draft` denies everything), and none allows another action with an underscore.

What shipped: chargehand's own server and `scripts/opencode-serve.sh` set both variables, and every OpenCode session's
rules end with `*_* * deny`. Residual risk: a server the user runs another way (an `opencode` block in the profile) keeps
its project configuration unless they set the variables, so a checkout can still start a command there, though its tools
stay denied to workers; a server configured or added by an authenticated client is that client's own choice and starts;
workers no longer get the checkout's `AGENTS.md` or skills; a later OpenCode action with an underscore is denied to
workers until a grant or preset allows it after the deny; whether the built-in `browser` namespace is gated by rules is
still unknown.

### Delivery to OpenCode: the registration lifecycle (task 10)

The spike says what one registration is; it does not say who owns it. The registry belongs to a location, sessions
there share it (O2), the checkout of a repository at a commit is one location that many runs reuse (ADR 0023), and
`DELETE` takes a server from running sessions at once (O7). What the adapter does (`OpenCodeServices`):

- **Rules gate tools; registrations only make servers exist.** A run's session gets `<server>_<tool> * allow` for its
  granted tools after the `*_* * deny` (O5, O6), so runs with different grants of one server share one registration and
  never see each other's tools. Tool names are written the way OpenCode writes an action (characters outside
  `[A-Za-z0-9_-]` become `_`, which also keeps a `*` out of a rule); checked with a server whose tool is `echo.fact`.
- **A registration is what its config is.** Its name is the profile's server name and eight hex digits of a keyed hash of
  the config (`team-docs-3fa9c21b`), so a run whose secret has rotated gets a second server beside the first instead of
  replacing it under a session that still uses the old one, and two chargehand processes on one OpenCode server never
  meet in a name. The key is random per process, so the digits reveal nothing about a credential. The grant's own hash
  (`Sha256`) covers tool names and schemas, not the transport, so it is not the name.
- **It lives as long as the run, held by count.** The nodes of a split run share it, and a fork made after its parent
  ended still finds it. The orchestrator ends the run when its nodes are done, completed, failed or cancelled
  (`IRunCleanup`, in a `finally`, with its own 10 s bound per `DELETE`); the last run to let go removes the server. A
  run that starts while the last holder's `DELETE` is in flight waits for it, so the `DELETE` cannot land after the
  new `PUT`. A `DELETE` that fails leaves the server until OpenCode restarts and is noted on the run's span
  (`chargehand.service.<server>.remove_failed`); a `404` is fine.
- **A server that does not connect is dropped and said so.** The adapter polls `GET /api/mcp` until the status leaves
  `pending` (30 s at most), keeps no allow rules for a server that is not `connected`, removes it (the next run tries it
  afresh), and reports it through `IServiceHealth` for every session of the run
  (`not_connected: failed: connection refused`, `needs_auth: …`, `pending: no connection within 30 s`,
  `rejected: …` when the `PUT` itself failed). The run goes on without the service. Credentials are taken out of the
  reason before it is kept.
- **A moment after `connected`.** OpenCode reports `connected` before the server's tools can be called: with a stand-in
  model, a session created at once saw the tool as unknown, and the first call succeeded 140 to 205 ms after the
  status in three probes (2.0.19, a local stdio server). The adapter waits 500 ms after `connected` (once per
  registration; runs joining it later do not).

Live check on 2.0.19 (throwaway server and state, a stand-in OpenAI-compatible model that calls the tools its prompt
names, `scripts/fake-mcp-server.py` over stdio and a small Streamable HTTP stand-in with a bearer key): a granted tool
answered and an ungranted tool of the same server was "Unknown tool"; two runs at one location with different grants
kept their own tools, the server stayed while one of them ended, and `GET /api/mcp` was empty after the second;
a server that exits at start reported `failed: Connection closed` and was gone; a `remote` entry with the right key
connected and answered, with a wrong key it reported `needs_auth`.

Two limits found on the way, neither fixed here:

- **Presets that deny `*` cannot use services.** Workers reach a server through OpenCode's code-execution tool
  (`execute`), and `* * deny` (the `draft` preset) removes it; with the deny-all rules the catalog was empty and the
  granted tool unreachable. With `"codemode": false` in the server's config the granted tool becomes a function
  named `<server>_<tool>` and worked under `* * deny` too (the catalog held exactly that function). It is not used: model
  providers cap a function name at 64 characters (not tried here), and this name carries the registration's suffix, so
  a long server or tool name could fail the whole run. No shipped preset lists services; a preset that does must allow
  `execute`.

## Reopen if

The spike shows OpenCode cannot gate MCP tools per session (then services ship on Claude Code first); two providers'
recall proves too slow or too large with the default caps; a memory server needs an argument or result shape the mapping
cannot express; or a caller needs the recalled text in `result/v1`.
