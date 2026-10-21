# Architecture

Where the code lives. The [guide](docs/guide/index.md) explains how a run works; the
[decisions](docs/guide/decisions.md) say why.

## Two trees

The projects under `src/Chargehand*` are the code that ships today. A rewrite ([ADR 0060](docs/adr/0060-feature-modules-and-native-aot-hosts.md))
ports features into `src/Shared`, `src/Modules`, `src/Runtimes` and `src/Hosts`: one project per feature module,
references that point down the layers (`tests/Chargehand.Architecture.Tests` checks them), public types only in a
module's `.Api` namespace, and hosts that publish with Native AOT. New code never references the old projects.

## Repository layout

| path | contents |
|---|---|
| `src/Chargehand` | orchestrator: intake, task graph, worker node, evidence resolver, prompt registry, memory, run log |
| `src/Chargehand.Driven` | driven writing sessions: containers, egress proxy, runner client, sessions, batches, governor, integrator (references the orchestrator; [ADR 0047](docs/adr/0047-core-and-driven-assemblies.md)) |
| `src/Chargehand.Contracts` | contract package: schemas, C# types, validator (versioned by schema major) |
| `src/Chargehand.OpenCode` | OpenCode V2 client and worker-runtime adapter |
| `src/Chargehand.ClaudeCode` | Claude Code worker-runtime adapter |
| `src/Chargehand.Mcp` | MCP client: one connection per profile `mcp_servers` entry (Streamable HTTP, SSE or stdio), secret placeholders |
| `src/Chargehand.Automation` | work that starts without a caller: GitHub intake from issue labels and mentions ([ADR 0051](docs/adr/0051-intake-from-github.md)), and the quality benchmark `chargehand bench` ([ADR 0052](docs/adr/0052-quality-benchmark-on-public-repositories.md)); references Driven |
| `src/Chargehand.Server` | HTTP interface and MCP server |
| `src/Chargehand.Cli` | CLI entry point, packed as the `Chargehand` dnx tool and MCP server |
| `src/Shared`, `src/Modules`, `src/Runtimes`, `src/Hosts` | the rewrite's kernel, feature modules, runtime adapters and hosts (CLI, server, runner, session) |
| `.mcp/server.json` | MCP Registry entry, packed into the tool; the pack stamps the version over `0.0.0` |
| `schemas/` | JSON Schemas `request`, `task-spec`, `result`, `run-status`, `preset`, with valid and invalid examples |
| `presets/` | shipped presets |
| `prompts/` | versioned prompt blocks (core, intake, preset) |
| `evals/` | Prompt CI cells and a synthetic example; real items live in Langfuse datasets |
| `profiles/` | profile schema and examples; your own goes in the gitignored `profiles/local.*` |
| `samples/ContentEngineCall` | a program caller built from `Chargehand.Contracts` only |
| `web/console/` | the operator console served at `/console/` |
| `plugins/chargehand/` | the Claude Code plugin with `/chargehand:change` |
| `docs/adr/` | architecture decision records |
| `docs/mcp-server-2025-12-11.schema.json` | the MCP Registry `server.json` schema the entry is tested against |
| `docs/opencode-api.md` | the OpenCode V2 HTTP API surface this project uses, generated from the live spec |
