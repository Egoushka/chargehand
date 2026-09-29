# chargehand

[![ci](https://github.com/Egoushka/chargehand/actions/workflows/ci.yml/badge.svg)](https://github.com/Egoushka/chargehand/actions/workflows/ci.yml)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/Egoushka/chargehand/badge)](https://scorecard.dev/viewer/?uri=github.com/Egoushka/chargehand)
[![release](https://img.shields.io/github/v/release/Egoushka/chargehand)](https://github.com/Egoushka/chargehand/releases)
[![license](https://img.shields.io/github/license/Egoushka/chargehand)](LICENSE)

chargehand turns a request into a typed **Task Spec**, runs it on one or more coding-agent sessions
([OpenCode](https://opencode.ai) or Claude Code), and returns a **result contract** with evidence for every claim.
People call it from a CLI; programs call it over HTTP or MCP.

**Status: before 1.0** (the release badge shows the version). Workers are read-only for now; writing nodes in
worktrees come later. See the [changelog](CHANGELOG.md) for what shipped, the [roadmap](ROADMAP.md) for what comes
next, and [benchmarks](docs/benchmarks.md) for how it performs. The [guide](docs/guide/index.md) walks through
using it, and [what it does, and how we know](docs/guide/capabilities.md) gives each capability's status with its evidence.

## Why

Coding agents answer in prose. A program that calls one needs something it can check: claims tied to files at a
commit, diffs, session messages or caller inputs, plus a confidence and the exact prompt chain behind the answer.

chargehand keeps control flow (task graph, budgets, retries, parallelism) in code, not in a prompt. Each worker's
context stays small, cached and disposable.

Non-goals: its own agent loop, direct calls to model providers, parallelism for its own sake.

## How a run works

1. **Intake** reads `request/v1` and writes a Task Spec with one action.
2. The action decides what happens next:
   - `answer` runs one worker session.
   - `split` runs 2–4 read-only subtasks as a task graph; later nodes fork the first node's cached prefix.
   - `deny`, `ask` and `improve` stop and return a reason, questions or an improved request.
   - A preset can require approval above a risk or cost estimate.
3. The **evidence resolver** checks every claim. Claims that do not resolve move to `open_questions`.
4. You get `result/v1`. The run log records tokens, cache hits, cost and the prompt chain of every call.

**Presets** set tools, budgets and allowed actions: `default`, `cheap`, `thorough`, `strict`, and `draft` for program
callers that bring their own facts. Optional long-term **memory**, a **cache report** per run, **Prompt CI** that gates
prompt changes on paired evals, and a **routing report** round it out.

## Quick start

You need the .NET 10 SDK and one worker runtime.

1. Copy `profiles/example.json` to `profiles/local.json` and fill in your gateway, models, prices and secret-store
   item names. Profiles reference secrets by item name and never hold them.
2. Pick a runtime:
   - **OpenCode**: with `opencode` on `PATH` (pinned 2.0.18), no profile block is needed. `run`, `serve` and `mcp`
     start their own `opencode serve` on 127.0.0.1 and a free port, with a random password and their own state
     under `chargehand/opencode` in the per-user data directory, and stop it on exit. Providers come from the
     environment variables OpenCode reads (e.g. `ANTHROPIC_API_KEY`); edit `xdg/config/opencode/opencode.json` in
     that directory for more, chargehand never overwrites it. To use a server you run yourself, start it with
     `scripts/opencode-serve.sh <opencode-binary> profiles/local.opencode.json 4296` (config from
     `profiles/opencode.example.json`) and add the profile's `opencode` block (`url`, `password_secret`, `version`);
     then nothing is started. The script turns off OpenCode's project configuration (a checkout's own `opencode.json`
     could start a command); set `OPENCODE_DISABLE_PROJECT_CONFIG=1` and `OPENCODE_CONFIG_PROJECT_DISABLE=1` if you start
     the server another way. See [ADR 0030](docs/adr/0030-default-opencode-server.md) and
     [ADR 0004](docs/adr/0004-opencode-major-and-runtime-adapter.md).
   - **Claude Code**: with `claude` on `PATH` (pinned 2.1.283) and signed in (run `claude` once and log in),
     no login setup is needed: workers use the CLI's own login. Placeholder models the profile's `models` map does not
     name (all of them with no profile) fall back to the CLI's default model. To use another credential, set one of
     `ANTHROPIC_API_KEY` or `CLAUDE_CODE_OAUTH_TOKEN` (from `claude setup-token`), not both. The profile's
     `claude_code` block (`version`, `binary`, and at most one of `api_key_secret` or `oauth_token_secret`) overrides that.
     See [ADR 0020](docs/adr/0020-claude-code-runtime-adapter.md).
3. List the directories your repositories live in as `repository_roots` (`/` allows any). A request names a
   repository and a commit; the worker reads a clone of it at that commit under `worker_root`, which stays outside
   the OpenCode user's home directory ([ADR 0023](docs/adr/0023-repository-roots-and-worker-clones.md),
   [ADR 0003](docs/adr/0003-where-it-runs.md)). Without `repository_roots`, `run` and `mcp` also allow the directory
   they were launched in; `serve` allows only `worker_root` ([ADR 0028](docs/adr/0028-default-repository-roots.md)).
4. Run a request:

```bash
dotnet run --project src/Chargehand.Cli -- run < request.json
```

## Commands

All commands run as `dotnet run --project src/Chargehand.Cli -- <command>` and read `profiles/local.json`.

`prompts/` and `presets/` come from the current directory when it holds both (this checkout, or `/app` in the image),
otherwise from the ones the build copies next to the binary. The run log is the profile's `run_log`; unset, it is
`runs/run-log.jsonl` in a checkout and `chargehand/run-log.jsonl` under the per-user data directory
(`~/.local/share` on Linux, `~/Library/Application Support` on macOS) anywhere else.

| command | what it does |
|---|---|
| `run < request.json` | `request/v1` in, `result/v1` out |
| `show <run-id>` | calls, tokens, cache %, cost |
| `cache <run-id>` | cache reads and writes per call, and the first block that broke a shared prefix |
| `reconcile <run-id> < spend-rows.jsonl` | joins calls to exported gateway spend rows |
| `serve` | HTTP and MCP, on 127.0.0.1 unless the profile opens it (profile `http`) |
| `mcp` | MCP over stdio, for a client that starts chargehand itself; no port, no key |
| `routes` | routing report per preset, node kind and model |
| `score <run-id> <0-1> [name]` | records a hand score for a run |
| `eval seed\|push\|gate` | Prompt CI: propose items, push them to Langfuse, gate a change |
| `prompts sync` | mirrors prompt blocks to Langfuse |

Prompt CI runs on its own for a pull request that changes `prompts/` or `presets/`: `.github/workflows/prompt-ci.yml`
hands it to a self-hosted runner, which posts the commit status. A fork's pull request or a preset change waits for an
approval in the `prompt-ci-review` environment. The runner has one eval profile per runtime and a default; the label
`prompt-ci:<runtime>` picks another. `scripts/prompt-ci.sh <pr-number>` runs it by hand
([ADR 0019](docs/adr/0019-prompt-ci-and-routing-report.md), [ADR 0025](docs/adr/0025-prompt-ci-on-a-self-hosted-runner.md)).

## HTTP and MCP

`chargehand serve` binds 127.0.0.1 and requires `Authorization: Bearer <key>` on every route. The key comes from the
secret-store item named by the profile's `http.api_key_secret`
([ADR 0018](docs/adr/0018-callable-interface-http-mcp-run-store.md)). On a private network, `http.listen` binds another
address and `http.allowed_hosts` names the host clients use; a tag `v<Version>` publishes the server image
`ghcr.io/<owner>/chargehand:<Version>` with the Claude Code runtime
([ADR 0024](docs/adr/0024-server-on-a-private-network-and-release-images.md)):

```bash
docker run -p <private-ip>:4300:4300 -v <dir-with-profile.json>:/config:ro -e CHARGEHAND_API_KEY=... \
  ghcr.io/<owner>/chargehand:<Version>
```

| route | behaviour |
|---|---|
| `POST /v1/runs` | takes `request/v1`. With `Prefer: wait=N` (default 10 s, at most 60): `200` and `result/v1` if the run finishes in time, else `202`, `run-status/v1` and a `Location` |
| `GET /v1/runs/{id}` | `202` while queued or running, `200` and `result/v1` once finished, `410` if the process that ran it ended first |
| `GET /v1/runs/{id}/events` | server-sent events `accepted`, `started`, `intake`, `node_started`, `node_finished`, `run_finished` |
| `/v1/mcp` | MCP over Streamable HTTP: tool `orchestrate`, `inputSchema` `request/v1`, `outputSchema` `result/v1` |

MCP clients that opt in to the tasks extension get long runs as tasks. When intake answers with questions, the call
comes back as `input_required`. Outside a task, a call with a progress token gets the run id as a progress
notification when the run starts, and a call whose HTTP request carries `Prefer: wait=N` (at most 60) returns after N
seconds as a tool error holding the run id and its `run-status/v1`, while the run goes on
([ADR 0029](docs/adr/0029-mcp-run-id-before-a-client-timeout.md)).

`chargehand mcp` serves the same tool, tasks and questions over stdio: the client starts the process, and stdout
carries only MCP messages, logs go to stderr ([ADR 0027](docs/adr/0027-dnx-package-and-mcp-registry.md)). For
Claude Code, from a checkout:

```bash
claude mcp add chargehand -- dotnet run --project <checkout>/src/Chargehand.Cli -- mcp
```

### From the package

The package is a .NET tool that `dnx` (.NET 10 SDK) fetches from [nuget.org](https://www.nuget.org/packages/Chargehand)
and runs; no install step, no port, no key. Pin `<version>` to one of its versions. Pass the Claude Code credential as
one of `CLAUDE_CODE_OAUTH_TOKEN` or `ANTHROPIC_API_KEY`; `CHARGEHAND_RUNTIME` and `CHARGEHAND_PROFILE` are optional. If
a desktop app does not see your shell's `PATH`, give the full path to `dnx`.

Claude Code:

```bash
claude mcp add chargehand -e CLAUDE_CODE_OAUTH_TOKEN=<token> -- dnx Chargehand@<version> --yes -- mcp
```

VS Code, `.vscode/mcp.json`:

```json
{
  "inputs": [{ "id": "claude-token", "type": "promptString", "description": "claude setup-token", "password": true }],
  "servers": {
    "chargehand": {
      "type": "stdio",
      "command": "dnx",
      "args": ["Chargehand@<version>", "--yes", "--", "mcp"],
      "env": { "CLAUDE_CODE_OAUTH_TOKEN": "${input:claude-token}" }
    }
  }
}
```

Claude Desktop, `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "chargehand": {
      "command": "dnx",
      "args": ["Chargehand@<version>", "--yes", "--", "mcp"],
      "env": { "CLAUDE_CODE_OAUTH_TOKEN": "<token>" }
    }
  }
}
```

`scripts/mcp-smoke.py <dir>` runs a locally packed tool (`dotnet pack src/Chargehand.Cli -o <dir>`) the same way and
lists its tools; CI runs it on every change.

## Claude Code plugin

`/chargehand:change <goal>` takes one prompt to a reviewed change on a local branch `change/<slug>`: chargehand
researches the goal with citations checked against the current commit, your session writes the change and runs the
tests, chargehand reviews the diff with the `review` preset, the session fixes what holds (at most 2 fix rounds), and a
report lands in `.chargehand/reports/<slug>.md` as its own commit. Nothing is pushed.

```text
/plugin marketplace add Egoushka/chargehand
/plugin install chargehand@chargehand
```

The plugin starts chargehand through `dnx`, so it needs the .NET 10 SDK; the `Chargehand` package it runs is on
nuget.org. To run a checkout of chargehand instead of the package, point a `chargehand` MCP server at it:

```json
{"mcpServers": {"chargehand": {"command": "dotnet", "args": ["run", "--project", "<checkout>/src/Chargehand.Cli", "--", "mcp"]}}}
```

With no profile the workers run on the agent CLI's default model. To pick models, set `CHARGEHAND_PROFILE` to a
profile whose `models` map names them; `scripts/change-e2e.sh` writes a minimal one. `--budget` applies to each
chargehand call, and a run makes up to four (one research, up to three reviews).

## Repository layout

| path | contents |
|---|---|
| `src/Chargehand` | orchestrator: intake, task graph, worker node, evidence resolver, prompt registry, memory, run log |
| `src/Chargehand.Contracts` | contract package: schemas, C# types, validator (versioned by schema major) |
| `src/Chargehand.OpenCode` | OpenCode V2 client and worker-runtime adapter |
| `src/Chargehand.ClaudeCode` | Claude Code worker-runtime adapter |
| `src/Chargehand.Mcp` | MCP client: one connection per profile `mcp_servers` entry (Streamable HTTP, SSE or stdio), secret placeholders |
| `src/Chargehand.Server` | HTTP interface and MCP server |
| `src/Chargehand.Cli` | CLI entry point, packed as the `Chargehand` dnx tool and MCP server |
| `.mcp/server.json` | MCP Registry entry, packed into the tool; the pack stamps the version over `0.0.0` |
| `schemas/` | JSON Schemas `request`, `task-spec`, `result`, `run-status`, `preset`, with valid and invalid examples |
| `presets/` | shipped presets |
| `prompts/` | versioned prompt blocks (core, intake, preset) |
| `evals/` | Prompt CI cells and a synthetic example; real items live in Langfuse datasets |
| `profiles/` | profile schema and examples; your own goes in the gitignored `profiles/local.*` |
| `samples/ContentEngineCall` | a program caller built from `Chargehand.Contracts` only |
| `docs/adr/` | architecture decision records |
| `docs/mcp-server-2025-12-11.schema.json` | the MCP Registry `server.json` schema the entry is tested against |
| `docs/opencode-api.md` | the OpenCode V2 HTTP API surface this project uses, generated from the live spec |

## Build and test

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes
```

Contributions: see [CONTRIBUTING.md](CONTRIBUTING.md). Security reports: see [SECURITY.md](SECURITY.md).

## License

[Apache-2.0](LICENSE).
