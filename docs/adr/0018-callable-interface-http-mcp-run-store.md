# 0018. Callable interface: HTTP, MCP, the run store and the draft preset

- Status: accepted
- Date: 2026-09-27

## Context

ADR 0014 fixed the phase 5 surfaces: `POST /v1/runs`, `GET /v1/runs/{id}`, `GET /v1/runs/{id}/events`, and an MCP tool
`orchestrate` whose `outputSchema` is `result/v1`, with the tasks extension for long runs and `input_required` for `ask`.
Runs must outlive the request that starts them, without a durable-execution framework (ADR 0002). The first consumer,
the content service, sends drafts that carry their facts as `inputs[]` and have no repository; v1's intake answered
such a request with `needs_input`.

## Evidence

- MCP C# SDK (checked 2026-09-26): `ModelContextProtocol` 2.2.0 (2026-08-13) follows MCP 2026-07-28 since 2.0.0
  (`server/discover`, multi-round-trip requests through `InputRequiredException`); tasks are the stable package
  `ModelContextProtocol.Extensions.Tasks` 2.2.0 (SEP-2663). Inside a task, `ElicitAsync` becomes the task's
  `input_required`; a tool that throws `InputRequiredException` inside a task fails it ("MRTR and tasks cannot be
  composed via [McpServerTool] yet"). `Tool.InputSchema` accepts any object schema, so `request/v1` serves unchanged.
- Live check: an SDK client negotiated 2026-07-28, listed `orchestrate` with both schemas, and ran a draft as a task
  (working, then completed; $0.0003).
- On Unix, .NET opens `FileMode.Append` without `O_APPEND` and writes at the length it read when opening
  (`SafeFileHandle.Unix.cs`), so two processes appending to the run log can overwrite each other's lines.
  `FileShare.None` takes an exclusive `flock`.
- A tool-less session's first call read a 1.5k-token prompt, against about 5k for the read-only worker: the tool
  catalog is most of OpenCode's base. A draft on the small model cost $0.0003–0.0007 (16 calibration runs).
- The first live draft cited its inputs as `[project]`, because the task text listed them as `[id] (kind) text`; no
  input id matched, and after the repair turn the worker gave up with `needs_input`. Intake, which saw only the request
  text, chose `ask` for the same request.

## Options

1. Run store: the run log plus a start record per run; a separate store (a file per run, SQLite); a durable-execution
   framework (ruled out for v0–v1).
2. Where the surfaces live: the CLI project; the core library; a new adapter project beside `Chargehand.OpenCode`.
3. A draft: the `worker` node kind under a draft preset; a `draft` node kind with its own core block and no checkout.

## Decision

- **Run store: the run log.** `Orchestrator.RunAsync` appends a `start` record (the request as received, trace id,
  owning pid, parent run id) before intake; the `run` record at the end holds the result. A start without a run record
  is running while its process lives, and `lost` once it has ended. Appends hold an exclusive lock on `<log>.lock`;
  readers skip an unterminated last line. A run that throws ends with a failed `result/v1` and a run record.
- **`chargehand serve`** hosts both surfaces from `src/Chargehand.Server` (ASP.NET Core) on 127.0.0.1 (ADR 0003).
  Every route requires a bearer key from the secret store (profile `http.api_key_secret`), because any local process
  can reach the port; the Host header must be loopback (DNS rebinding); no CORS; bodies up to 1 MB.
- **HTTP.** `POST /v1/runs` checks `request/v1`, the preset and caller-block hashes (400 otherwise), waits up to
  `Prefer: wait=N` (default 10 s, at most 60), then answers 200 with `result/v1`, or 202 with `run-status/v1` and a
  `Location`. `GET /v1/runs/{id}`: 202 while queued or running, 200 with `result/v1`, 410 when lost, 404 unknown.
  `GET /v1/runs/{id}/events`: server-sent events `accepted`, `started`, `intake`, `node_started`, `node_finished`,
  `run_finished` (which carries the result). One run at a time on the host's lifetime token: ADR 0011 allows two
  nodes per model and a split uses both. At most 10 unfinished runs, then 429.
- **Contracts, additive.** `run-status/v1` joins `Chargehand.Contracts` (1.1.0-alpha), with
  `PromptBlock.Create(name, version, text)` so a caller hashes its blocks with the contract package alone.
- **MCP** at `/v1/mcp` over Streamable HTTP, with hybrid sessions: 2026-07-28 clients run stateless, `initialize`
  clients get a session. Tool `orchestrate`: `inputSchema` `request/v1`, `outputSchema` `result/v1`, the result in
  `structuredContent`; long runs are tasks when the client opts in. When intake answers an interactive request with
  `ask`, the tool asks the client one field per question (elicitation inside a task, `InputRequiredException`
  outside one), resends the request with the answers appended, and records the first run as the parent. A
  non-interactive request gets `needs_input`; `improve` and approval stops come back as `needs_input` unchanged. A
  call that is cancelled stops waiting; the run continues and stays readable over HTTP.
- **Preset `draft` 0.1.0**, node kind `draft`: small model, one `* * deny` rule (no tools), and the new optional
  `preset/v1` field `checkout: false`, so no repository: the node runs in an empty `<worker_root>/.chargehand-empty`
  (outside the home, ADR 0003), and intake does not ask for `context.repository`. Allowed actions: `answer`, `deny`.
  Blocks `core/draft` and `preset/draft` 0.1.0. The answering node kind is `worker`, or a preset's only kind.
- The orchestrator hashes inline artifacts itself and bounds them at 64 KiB in bytes (ADR 0009's rule, until now
  checked in characters only).
- Inputs appear in the task text as `- id "<id>" (<kind>): <text>`, a failed `input` reference lists the ids that
  exist, and intake sees each input's id and kind.
- The content engine does not exist yet (research phase 0, no repository). `samples/ContentEngineCall` makes exactly
  its call with `Chargehand.Contracts` alone and stands in for it.

## Consequences

The CLI and both surfaces share one run log: `chargehand show` reads HTTP and MCP runs, and `GET /v1/runs/{id}` reads
CLI runs. A server restart leaves unfinished runs `lost`, and their callers resend. A CLI run next to the server can
exceed ADR 0011's two nodes per model. Every future run keeps its request, so eval sets can grow from real traffic
(ADR 0019).

## Addendum (2026-09-29): intake reads the inputs' text

Intake sees each input's text, not only its id and kind (the last clause of the inputs bullet under Decision): told
just that a diff exists, it asked the caller for the diff the request carried. The text is cut at
`GenerateIntake.MaxInputChars` (2000) characters per input and at `GenerateIntake.MaxTotalInputChars` (8000) across all
inputs, in request order, so a request with many inputs cannot grow the small model's prompt up to the server's
request limit. A cut input says how far it was cut; an input after the budget is listed with its id, kind and size and
a note that its text is left out here. Both notes say the worker gets all of it, and the worker's task text is
unchanged.

## Reopen if

Callers need runs to survive a restart (then durable execution, ADR 0002); a scan of the run log per request becomes
slow; the SDK composes tasks with `input_required` results; a second program caller needs a node kind other than
`draft`.
