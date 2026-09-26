# chargehand

An orchestrator that turns a request into a typed **Task Spec**, runs it on one or more real
[OpenCode](https://opencode.ai) sessions (each with its own agent, model, permissions and working
directory), and returns a **result contract** with evidence for every claim — to people through a
CLI and to programs through HTTP and MCP.

**Status: pre-alpha (v1).** Intake → an action: stop with `deny`, `ask`, `improve` or an approval request, or run
one OpenCode session (`answer`) or a graph of 2–4 read-only sessions (`split`) → `result/v1` with resolved
evidence. Presets `default`, `cheap`, `thorough`, `strict`; optional long-term memory; a cache report per run.
Writing nodes in worktrees, HTTP and MCP come later.

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
| `src/Chargehand.Cli` | CLI entry point |
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
```

## Build

```bash
dotnet build
dotnet test
```

## License

[Apache-2.0](LICENSE).
