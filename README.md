# chargehand

An orchestrator that turns a request into a typed **Task Spec**, runs it on one or more real
[OpenCode](https://opencode.ai) sessions (each with its own agent, model, permissions and working
directory), and returns a **result contract** with evidence for every claim — to people through a
CLI and to programs through HTTP and MCP.

**Status: pre-alpha (v2).** Intake → an action: stop with `deny`, `ask`, `improve` or an approval request, or run
one OpenCode session (`answer`) or a graph of 2–4 read-only sessions (`split`) → `result/v1` with resolved
evidence. Callable over a CLI, HTTP (`/v1/runs`, with server-sent events) and MCP (tool `orchestrate`, with tasks).
Presets `default`, `cheap`, `thorough`, `strict`, and `draft` for program callers that bring their own facts; optional
long-term memory; a cache report per run; Prompt CI that gates prompt changes on paired evals; a routing report.
Writing nodes in worktrees come later.

## Why

Coding agents answer in prose. Programs that call them need something they can check: claims tied
to files at a commit, diffs, session messages or caller-supplied inputs, plus a confidence and the
exact prompt chain behind the answer. chargehand keeps control flow — task graph, budgets,
retries, parallelism — in code rather than in a prompt, and keeps every worker's context small,
cached and disposable.

Non-goals: its own agent loop, direct calls to model providers, parallelism for its own sake.

## Layout

| path | what |
|---|---|
| `schemas/` | JSON Schemas `request/v1`, `task-spec/v1`, `result/v1`, `preset/v1`, with valid/invalid examples |
| `presets/` | shipped presets: `default`, `cheap`, `thorough`, `strict` (all read-only for now) |
| `docs/adr/` | architecture decision records |
| `docs/opencode-api.md` | the OpenCode V2 HTTP API surface this project depends on, generated from the live spec |
| `src/Chargehand.Contracts` | the contract package: schemas, C# types, validator (versioned by schema major) |
| `src/Chargehand` | orchestrator: intake, task graph, worker node, evidence resolver, prompt registry, memory, run log |
| `src/Chargehand.OpenCode` | OpenCode V2 client and worker-runtime adapter |
| `src/Chargehand.Server` | HTTP interface and MCP server (`chargehand serve`) |
| `src/Chargehand.Cli` | CLI entry point |
| `evals/` | Prompt CI cells (`cells.json`) and an example item file; real items live in Langfuse datasets |
| `samples/ContentEngineCall` | a program caller built from `Chargehand.Contracts` only |
| `profiles/` | profile schema and `example.json`; your own goes in the gitignored `profiles/local.json` |

Requires an OpenCode V2 server of the pinned version (2.0.16) that the orchestrator starts itself; see
[ADR 0004](docs/adr/0004-opencode-major-and-runtime-adapter.md). Worker checkouts must live outside the
OpenCode user's home directory ([ADR 0003](docs/adr/0003-where-it-runs.md)).

## Running

1. Copy `profiles/example.json` to `profiles/local.json` and `profiles/opencode.example.json` to
   `profiles/local.opencode.json`; fill in your gateway, models, prices and secret-store item names.
2. Start the orchestrator's own OpenCode server (pinned version, own state directory, loopback only):
   `scripts/opencode-serve.sh <opencode-binary> profiles/local.opencode.json 4296`
3. Put a checkout at the commit you want answered under `worker_root` (outside your home directory).
4. Run a request:

```bash
dotnet run --project src/Chargehand.Cli -- run < request.json      # request/v1 in, result/v1 out
dotnet run --project src/Chargehand.Cli -- show <run-id>           # calls, tokens, cache %, cost
dotnet run --project src/Chargehand.Cli -- cache <run-id>          # cache reads/writes per call, first changed block
dotnet run --project src/Chargehand.Cli -- reconcile <run-id> < spend-rows.jsonl
dotnet run --project src/Chargehand.Cli -- prompts sync            # mirror prompt blocks to Langfuse
dotnet run --project src/Chargehand.Cli -- serve                   # HTTP and MCP on 127.0.0.1 (profile "http")
dotnet run --project src/Chargehand.Cli -- routes                  # routing report per preset, node kind and model
scripts/prompt-ci.sh <pr-number>                                   # Prompt CI: paired evals, then the commit status
```

### HTTP and MCP

`chargehand serve` binds 127.0.0.1 and requires `Authorization: Bearer <key>` on every route; the key comes from the
secret-store item the profile's `http.api_key_secret` names ([ADR 0018](docs/adr/0018-callable-interface-http-mcp-run-store.md)).

- `POST /v1/runs` takes `request/v1`. With `Prefer: wait=N` (default 10 s, at most 60) it answers `200` and `result/v1`
  if the run finishes in time, else `202` and `run-status/v1` with a `Location`.
- `GET /v1/runs/{id}`: `202` while queued or running, `200` and `result/v1` once finished, `410` if the process that ran
  it ended first.
- `GET /v1/runs/{id}/events`: server-sent events `accepted`, `started`, `intake`, `node_started`, `node_finished`,
  `run_finished`.
- MCP (Streamable HTTP) at `/v1/mcp`: tool `orchestrate`, `inputSchema` `request/v1`, `outputSchema` `result/v1`. Clients
  that opt in to the tasks extension get long runs as tasks; an interactive request that intake answers with questions
  comes back as `input_required`.

## Build

```bash
dotnet build
dotnet test
```

## License

[Apache-2.0](LICENSE).
