# Cycle A: one core, doors in and doors out

- Status: accepted by the maintainer on 2026-10-28 (direction and scope); the decisions below are the proposer's
- Date: 2026-10-28
- ADR: [0086](../adr/0086-one-core-doors-in-and-doors-out.md)
- Builds on: [a client of your own](2026-10-02-a-client-of-your-own-design.md) (Stage 6, Tasks 1 to 3, now ahead of 1.0)

## Goal

Make chargehand reachable from where work starts and able to drive more than two agents, by building the one seam
both need: a conversation a person can steer, and the agent client protocol (ACP) in .NET.

Done when:

1. `POST /v1/conversations` starts a conversation on a fake runtime. A client streams its events, sends a message,
   answers an approval and ends it, with every step covered by tests.
2. A .NET ACP client library, with no reference to chargehand, drives an ACP agent over stdio. It publishes with
   Native AOT and no trim or AOT warnings.
3. One code question from `evals/` is answered by `chargehand run` three times: on Claude Code, on Codex through ACP and
   on Gemini CLI through ACP. Each `result/v1` has its citations resolved against the pinned commit, and each run
   records its runtime, model and usage.
4. A guide shows how to connect chargehand from at least ten MCP clients. The Claude Code plugin installs as a VS Code
   agent plugin.

## Where it stands

Evidence: `main` at 9c5029fd, `acp-spike` report of 2026-10-27, research of 2026-10-28.

1. **Every door is a one-shot run.** MCP `orchestrate`, `POST /v1/runs`, the CLI, GitHub intake, the console and phone
   answers all start a run that a person cannot message or approve while it works (`docs/guide/server.md`, Runs).
2. **Two runtimes, each a full adapter.** `IWorkerRuntime` (`src/Chargehand/Runtime/IWorkerRuntime.cs`) is
   implemented by `ClaudeCodeWorkerRuntime` (809 lines) and the OpenCode runtime (960 lines in
   `src/Chargehand.OpenCode`). `WorkerNode` calls `ForkAsync`, `CompactAsync` and `DiffAsync`.
3. **ACP works for Claude Code.** The spike drove `@agentclientprotocol/claude-agent-acp` 0.97.0. Approve, deny, diffs,
   resume, cancel, images and mid-turn messages all ran live. OpenCode's ACP could not route a model and is UNKNOWN.
4. **ACP reaches many agents.** The ACP registry lists 43 agents. Gemini CLI (`--acp`), Copilot CLI, Cline, Qwen Code,
   Goose, Mistral Vibe, Junie and Factory Droid speak it natively; Codex speaks it through `codex-acp`. Official SDKs
   exist for TypeScript, Rust, Python, Kotlin and Java, but not for .NET, where four community packages exist and the
   largest has 20 stars.
5. **"Session" is taken.** In this codebase a session is a driven session (ADR 0039, `Chargehand.Modules.Sessions`,
   the codes `session_failed` and `session_stalled`). ACP's `session` is a different thing.
6. **MCP clients are everywhere, but the guide covers few.** `docs/guide/mcp.md` shows Claude Code and VS Code.
   Cursor, Visual Studio, JetBrains AI Assistant, Junie, Codex, Gemini CLI, Warp, Zed, Android Studio (HTTP only) and
   ChatGPT Business all take MCP servers. VS Code agent plugins accept the Claude plugin format.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Name of the steerable resource | **Conversation**: `/v1/conversations`, schema `conversation/v1`, module `Chargehand.Modules.Conversations`. ACP's `session` maps to one conversation. The word "session" keeps its ADR 0039 meaning. This replaces `/v1/sessions` from the Stage 6 spec, decision 3. |
| 2 | Routes | `POST /v1/conversations` (repository, commit, runtime, optional model and preset; `201` with id and `Location`). `GET /v1/conversations/{id}` (state, runtime, model, usage, caps). `GET /v1/conversations/{id}/events` (server-sent events with `id:` per event; `Last-Event-ID` resumes). `POST /v1/conversations/{id}/messages` (text; `202`; a message while a turn runs is queued, which matches ACP). `POST /v1/conversations/{id}/approvals/{call}` (`allow_once` or `reject`, never a saved permission, as `IWorkerRuntime.AnswerPermissionAsync` already rules). `DELETE /v1/conversations/{id}` (cancel and end). All of them are additive: `request/v1` and `result/v1` do not change. |
| 3 | Events | One `conversation-event/v1` envelope `{id, at, type, data}`. The types are `state`, `message_chunk`, `tool_call`, `tool_call_update`, `approval_requested`, `approval_answered`, `usage`, `error` and `ended`. A tool call carries ACP's `locations` (path and line) unchanged, so a client can show citations as locations. |
| 4 | Storage | Events are appended to `conversations/<id>.jsonl` beside the run log, through the journal pattern intake and batches use. A restart ends an open conversation as `lost`. Resuming a runtime's session after a restart comes later. |
| 5 | Who may call | The owner and accounts, scoped as runs are: an account sees only its own conversations. A run token is refused (`403`). The account caps apply at start and on each message. A message that would exceed a cap is refused `429` with the same body as a run. |
| 6 | Runtime seam for conversations | A new port, `IConversationRuntime` (start, prompt, cancel, answer permission, stream updates), in the module's `Api`. This cycle has a fake implementation and the ACP one. `IWorkerRuntime` stays for research workers. |
| 7 | The ACP library | A project in `src/Shared/` with its own namespace, `AgentClientProtocol`. It references nothing in chargehand, is AOT-clean with source-generated System.Text.Json, and is packable to NuGet under an id that is free on nuget.org (the worker checks). It covers protocol version 1 as a client: `initialize`, `authenticate`, `session/new`, `session/load`, `session/prompt`, `session/cancel`, `session/set_mode`, `session/set_config_option`, `session/update` notifications, and `session/request_permission` handled by a callback. `fs/*` and `terminal/*` are declined by default, with callbacks to opt in. The agent side (so chargehand can be an ACP agent) is Cycle B, in the same library. Extraction to its own repository happens once a second consumer exists. |
| 8 | The ACP worker runtime | `Chargehand.Runtimes.Acp` in `src/Runtimes/`, implementing both `IWorkerRuntime` and `IConversationRuntime` over the library. The profile names an agent from a pinned list in `acp-agents.json` (id, launch command, version, sha256 where the registry gives one), refreshed by a script from `registry.json`, never fetched at run time. Read-only research workers are enforced three ways: every `session/request_permission` for an edit or execute kind is rejected, the agent's plan or read-only mode is set when it has one, and the process runs in the existing sandbox. `ForkAsync` and `CompactAsync` throw `NotSupportedException` when the agent does not advertise them, and `WorkerNode` falls back to a fresh session. `DiffAsync` returns the working tree's diff. |
| 9 | Credentials | API keys or the model gateway only, by secret item name (ADR 0082 counts them as `api_key`). Never a subscription OAuth token through ACP: Anthropic's terms say products built on the Agent SDK use API keys, and Google's say Gemini CLI's OAuth is not to be piggybacked. The Claude Code path through the unmodified `claude` binary is unchanged. |
| 10 | Usage and cost | Usage comes from what the agent reports (`usage_update` and turn results). An agent that reports none records `usage: null` with a `not_recorded` sentence, and a dollar cap on it is refused at admission (`runtime_uncapped`) unless the profile sets `acp.allow_uncapped: true`. Silent unmetered spend is not allowed. |
| 11 | MCP quick wins | `docs/guide/clients.md`, with per-client configuration for Claude Code, Claude Desktop, Cursor, VS Code, Visual Studio, JetBrains AI Assistant, Junie, Codex, Gemini CLI, Warp, Zed, Android Studio and ChatGPT. Each snippet says whether it was run or comes from the vendor's docs. The plugin under `plugins/chargehand` installs as a VS Code agent plugin, with a test that its manifest is valid for both formats. Mail to GitHub for its MCP registry, and any marketplace publishing, are the maintainer's steps: the worker drafts them and does not send. |
| 12 | Not in this cycle | Chargehand as an ACP agent, the MCP Tasks extension, an MCP App result viewer (Cycle B); A2A (Cycle C); the web client steering screen (Stage 6, Task 5); turning one-shot runs into conversations. |

## Tasks (each one pull request)

1. **ADR 0086** and this spec, with the roadmap reordered (the desk).
2. **Conversations, read and write, on a fake runtime** (decisions 1 to 6): the module, the routes, `conversation/v1`
   and `conversation-event/v1` schemas, journal storage, scoping and caps, and `IConversationRuntime` with a fake.
3. **The ACP client library** (decision 7), tested against a fake agent process in the test project. Transcripts
   recorded from real agents are scrubbed of ids and prompts before commit.
4. **The ACP runtime for research workers** (decisions 8 to 10), proven on Codex (`codex-acp`) and Gemini CLI
   (`--acp`) through the gateway or an API key.
5. **The ACP runtime behind conversations**: Task 2's fake swapped for ACP under a profile switch
   (`http.conversations.runtime: "acp"`), with an end-to-end test that runs a conversation on the scripted fake ACP agent.
   The follow-ups of Task 2 ride along: the credential-paused check. A list route is not added: no decision asks for one.
6. **Clients guide and VS Code agent plugin** (decision 11).

Tasks 2, 3 and 6 can run in parallel. Task 4 needs 3, and Task 5 needs 2 and 4.

## Acceptance checks

These run in `scripts/check.sh` unless marked live. A live check runs on the maintainer's machine, and its output is
attached to the pull request with ids scrubbed.

- `ConversationRoutesTests`:
  - start, stream, message, approve, deny, cancel and end on the fake runtime;
  - `Last-Event-ID` resumes with no event lost or repeated;
  - an account cannot read another account's conversation (`404`);
  - a run token gets `403`;
  - a message over a cap gets `429`.
- `SchemaCompatTests` stays green. The new schemas live under `schemas/conversation/v1/` and
  `schemas/conversation-event/v1/`, and `request/v1` and `result/v1` are unchanged.
- `ArchitectureTests`: the ACP library references no `Chargehand.*` project, and `Chargehand.Runtimes.Acp` references
  the library and module `Api` namespaces only.
- `scripts/aot-smoke.sh` publishes a host that uses the library with no IL2xxx or IL3xxx warning.
- `AcpClientTests` against the fake agent:
  - initialize negotiates version 1;
  - a prompt streams chunks in order;
  - a permission request reaches the callback, and its answer is sent;
  - cancel ends the turn with `cancelled`;
  - a message sent mid-turn is queued;
  - an agent that dies mid-turn surfaces as an error, not a hang, within the deadline.
- `AcpRuntimeReadOnlyTests`: an edit or execute permission request on a research worker is rejected, and the run
  records it.
- `AcpUsageTests`: an agent without usage and a dollar cap is refused `runtime_uncapped`, unless
  `acp.allow_uncapped` is set.
- **Live, three runtimes:** `chargehand run` with one synthetic question from `evals/` against this repository, on
  `claude_code`, `acp:codex` and `acp:gemini`.
  - Each `result/v1` validates and has at least one citation, and every citation resolves at the pinned commit.
  - The run summary shows the runtime, model and usage.
  - Total spend is under $5.
- `AcpConversationTests` (in `scripts/check.sh`): start, stream, a second message, an approval allowed and one rejected,
  usage, end, an agent that dies and a rejected key, over the HTTP routes with the scripted fake agent as a child process.
- **Live, conversation, by a maintainer with Codex access:** `scripts/acp-conversation-live-check.py --agent codex`
  (steps and expected output in `docs/guide/acp.md`, "Live check on your own Codex access"); the output is attached.
  The owner declined to pay for live runs, so this has not run. A conversation on `acp:gemini` is **UNKNOWN**.
- `ClientsGuideTests`: every JSON and TOML snippet in `docs/guide/clients.md` parses. The VS Code agent plugin manifest
  validates.

## Risks

- **Agent churn.** CLI flags and ACP adapters change often: Gemini CLI's consumer tiers moved to the closed `agy`,
  Copilot's ACP is in preview, and the Kimi CLI was archived. Mitigation: pinned versions and hashes, and the
  three-runtime live check repeated before each release.
- **No usage reporting.** This can break the caps. Mitigation: decision 10.
- **Gateway routing for Codex and Gemini is unverified.** Codex's custom `base_url` and Gemini's base URL setting are
  UNKNOWN until Task 4 tries them. The fallback is an API key with a provider-side spend limit, under a secret item name
  the maintainer creates. The worker does not ask for a key value.
- **The .NET ACP library becomes a second product.** Mitigation: the client side only this cycle, and only what the
  runtime uses, with the agent side in Cycle B.
- **Name confusion.** "Conversation" means the API resource, "session" means a driven session and ACP's session is
  internal. `docs/guide/reference.md` gets one paragraph that says so.

## Out of scope

An own agent loop, a model gateway, OpenCode over ACP (it stays on HTTP until the spike's OpenCode rows pass), and
subscription logins through ACP.
