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
next, and [benchmarks](docs/benchmarks.md) for how it performs.

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
   - **OpenCode**: copy `profiles/opencode.example.json` to `profiles/local.opencode.json`, then start the
     orchestrator's own server (pinned 2.0.16, own state directory, loopback only):
     `scripts/opencode-serve.sh <opencode-binary> profiles/local.opencode.json 4296`.
     See [ADR 0004](docs/adr/0004-opencode-major-and-runtime-adapter.md).
   - **Claude Code**: with `claude` on `PATH` (pinned 2.1.283), set exactly one of `ANTHROPIC_API_KEY` or
     `CLAUDE_CODE_OAUTH_TOKEN` (from `claude setup-token`); no profile block is needed. The profile's `claude_code`
     block (`version`, `binary`, and one of `api_key_secret` or `oauth_token_secret`) overrides that.
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

## Repository layout

| path | contents |
|---|---|
| `src/Chargehand` | orchestrator: intake, task graph, worker node, evidence resolver, prompt registry, memory, run log |
| `src/Chargehand.Contracts` | contract package: schemas, C# types, validator (versioned by schema major) |
| `src/Chargehand.OpenCode` | OpenCode V2 client and worker-runtime adapter |
| `src/Chargehand.ClaudeCode` | Claude Code worker-runtime adapter |
| `src/Chargehand.Server` | HTTP interface and MCP server |
| `src/Chargehand.Cli` | CLI entry point |
| `schemas/` | JSON Schemas `request`, `task-spec`, `result`, `run-status`, `preset`, with valid and invalid examples |
| `presets/` | shipped presets |
| `prompts/` | versioned prompt blocks (core, intake, preset) |
| `evals/` | Prompt CI cells and a synthetic example; real items live in Langfuse datasets |
| `profiles/` | profile schema and examples; your own goes in the gitignored `profiles/local.*` |
| `samples/ContentEngineCall` | a program caller built from `Chargehand.Contracts` only |
| `docs/adr/` | architecture decision records |
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
