# chargehand

An orchestrator that turns a request into a typed **Task Spec**, runs it on one or more real
[OpenCode](https://opencode.ai) sessions (each with its own agent, model, permissions and working
directory), and returns a **result contract** with evidence for every claim — to people through a
CLI and to programs through HTTP and MCP.

**Status: pre-alpha.** The repository holds architecture decisions, JSON Schemas and interfaces.
There is no working orchestrator yet.

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
| `presets/` | shipped presets (`default` only for now) |
| `docs/adr/` | architecture decision records |
| `docs/opencode-api.md` | the OpenCode V2 HTTP API surface this project depends on, generated from the live spec |
| `src/Chargehand.Contracts` | the contract package: schemas, C# types, validator (versioned by schema major) |
| `src/Chargehand` | ports: worker runtime, intake, evidence resolver, prompt registry, run log, price table |
| `src/Chargehand.OpenCode` | OpenCode V2 client interface (adapter comes with v0) |
| `src/Chargehand.Cli` | CLI entry point |
| `profiles/` | profile schema and `example.json`; your own goes in the gitignored `profiles/local.json` |

Requires an OpenCode V2 server of the pinned version (2.0.16) that the orchestrator starts itself; see
[ADR 0004](docs/adr/0004-opencode-major-and-runtime-adapter.md). Worker checkouts must live outside the
OpenCode user's home directory ([ADR 0003](docs/adr/0003-where-it-runs.md)).

## Build

```bash
dotnet build
dotnet test
```

## License

[Apache-2.0](LICENSE).
