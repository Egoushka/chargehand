---
title: "Reference"
description: "Commands, HTTP routes, presets, contracts, error codes, profile fields, environment variables, and where the run log lives."
order: 7
section: "Reference"
---

## Commands

From a checkout, every command runs as `dotnet run --project src/Chargehand.Cli -- <command>`; the packed tool names itself `chargehand`. A global `--profile <path>` goes before the command and wins over `CHARGEHAND_PROFILE`. Without either, commands read `profiles/local.json`.

| command | what it does | exit codes |
|---|---|---|
| `run < request.json` | reads `request/v1` on stdin, writes `result/v1` on stdout | 0 completed, 1 failed or denied, 2 usage error, 3 needs input |
| `show <run-id>` | prints a run and its calls from the run log: tokens, cache rate, cost, latency | 1 when the log has no such run |
| `cache <run-id>` | cache reads, writes and hit rate per call, and the first block that broke a prefix the nodes should have shared | 1 when the run has no calls |
| `reconcile <run-id> < spend-rows.jsonl` | joins the run's calls to exported gateway spend rows by model, token counts and time, and prints own against gateway cost | 1 when a call stays unmatched |
| `serve` | HTTP and MCP, on `127.0.0.1` unless the profile's `http` block opens it | 2 without an `http` block |
| `mcp` | MCP over stdio, for a client that starts chargehand itself; no port, no key | |
| `routes [run-log.jsonl ...]` | routing report per preset, node kind and model, from the given logs or the default one | |
| `score <run-id> <0-1> [name]` | records a hand score for a run; the name defaults to `quality` | |
| `eval seed <cell> <run-id>...` | proposes eval items (JSONL on stdout) from runs in the log | |
| `eval push <cell>` | pushes reviewed items (JSONL on stdin) to the cell's Langfuse dataset | |
| `eval gate <base> <change> [options]` | Prompt CI's paired runs and verdict, see [Prompt CI](prompt-ci.md#the-eval-commands) | 1 when blocked |
| `prompts sync` | mirrors prompt blocks to Langfuse prompt management | |

Any other command prints the usage and exits 2. `eval` and `prompts sync` need the profile's `telemetry` block.

`routes` shows runs, completions, score, prompt tokens, cache rate, cost and latency. It names a model a candidate only when it and the default each have 20 scored runs, it scores within 0.10 of the default, and it costs at least 15% less; nothing routes on its own ([ADR 0019](../adr/0019-prompt-ci-and-routing-report.md)).

`prompts/` and `presets/` come from the current directory when it holds both (a checkout, or `/app` in the image), otherwise from the copies the build places next to the binary.

## HTTP routes

| route | behaviour |
|---|---|
| `POST /v1/runs` | takes `request/v1`. With `Prefer: wait=N` (default 10 s, at most 60): `200` and `result/v1` if the run finishes in time, else `202`, `run-status/v1` and a `Location` |
| `GET /v1/runs/{id}` | `202` while queued or running, `200` and `result/v1` once finished, `410` if the process that ran it ended first, `404` if unknown |
| `GET /v1/runs/{id}/events` | server-sent events `accepted`, `started`, `intake`, `node_started`, `node_finished`, `run_finished` |
| `/v1/mcp` | MCP over Streamable HTTP: tool `orchestrate`, `inputSchema` `request/v1`, `outputSchema` `result/v1` |

Every route needs `Authorization: Bearer <key>`. [Run the HTTP server](server.md#routes) has the details.

## Presets

Each preset is a `preset/v1` file in `presets/`. Intake picks one action; when the preset does not allow it, the run falls back to `answer`.

| preset | version | allowed actions | node kind | model placeholder |
|---|---|---|---|---|
| `default` | 0.5.0 | `answer` | `worker` | `provider/worker-model` |
| `cheap` | 0.3.0 | `answer`, `split`, `improve`, `ask`, `deny` | `worker` | `provider/small-model` |
| `thorough` | 0.3.0 | `answer`, `split`, `improve`, `ask`, `deny` | `worker` | `provider/worker-model` |
| `strict` | 0.2.0 | `answer`, `split`, `improve`, `ask`, `deny` | `worker` | `provider/worker-model` |
| `draft` | 0.1.0 | `answer`, `deny` | `draft`, with `checkout: false` | `provider/small-model` |
| `review` | 0.1.0 | `answer` | `worker` | `provider/worker-model` |

| preset | input tokens per node | USD per node | compaction trigger | critic | approval |
|---|---|---|---|---|---|
| `default` | 400,000 | 1.00 | none | off | none |
| `cheap` | 400,000 | 0.25 | 60,000 | off | none |
| `thorough` | 400,000 | 1.00 | 100,000 | `provider/critic-model` | none |
| `strict` | 400,000 | 1.00 | 100,000 | `provider/critic-model` | above risk `low`, or an estimate above $0.50 |
| `draft` | 60,000 | 0.05 | none | off | none |
| `review` | 400,000 | 1.00 | none | off | none |

The profile's `models` map turns the placeholders into real model ids.

`draft` writes a draft for a program caller from the caller's own inputs and returns it as an inline artifact; its claims cite those inputs. `review` reviews a change: the caller sends the goal, the diff and the test output as inputs, and each finding is a claim that cites the diff input or a file at the commit.

### Permissions

Rules are ordered and the last matching rule wins; a trailing deny for an action removes that tool from the worker's catalog. Every preset except `draft` starts with `* * allow`, then denies `edit`, reading `*.env` and `*.env.*` (allowing `*.env.example` again), `shell`, `webfetch`, `external_directory`, `question` and `subagent`. On OpenCode the workers read and search with the `read`, `grep` and `glob` tools and have no git history ([ADR 0006](../adr/0006-presets.md)). `draft` has one rule, `* * deny`: no tools, and no repository, in an empty directory under `worker_root`.

On Claude Code, chargehand translates the rules into `--tools`, `--allowedTools` and `--disallowedTools`. Claude Code's rules are deny-wins, so an allow inside an earlier deny stays denied: `*.env.example` stays unreadable there ([ADR 0020](../adr/0020-claude-code-runtime-adapter.md)).

### Budgets and stops

- A node whose prompt tokens, summed over its calls, pass `max_input_tokens` gets interrupted, then gets one turn to answer from what it has read. The result says so in `open_questions`.
- A node stops at its USD cap: the smallest of the preset's `max_usd`, the profile's `run_cap_usd` (default 1.00) and the request's `budget_usd`, where the last two are divided evenly across a split's nodes. A USD cap cannot fire on a model with no price.
- Every node has a 15-minute deadline.
- Above `trigger_tokens` of context in a call, the orchestrator compacts the session; on Claude Code it compacts between turns. `auto`, `keep_tokens` and `buffer` describe the OpenCode server's own settings.
- The critic reviews writing nodes only, and no writing node runs yet.
- Approval thresholds stop a run with `needs_input` unless the request sets `context.approved: true`. Intake's estimate is uncalibrated, so only `strict` sets thresholds.

## Contracts

| schema | what it describes | file |
|---|---|---|
| `request/v1` | what a caller sends: `text`, `context` (`interactive`, `preset`, `budget_usd`, `approved`, `repository`), `inputs`, `caller_blocks` | [request.schema.json](../../schemas/request/v1/request.schema.json) |
| `task-spec/v1` | intake's output: goal, constraints, acceptance criteria, risk, estimate, and one action with its detail | [task-spec.schema.json](../../schemas/task-spec/v1/task-spec.schema.json) |
| `result/v1` | what a node and a run return | [result.schema.json](../../schemas/result/v1/result.schema.json) |
| `run-status/v1` | an unfinished run (the HTTP `202` and `410` bodies) and each progress event | [run-status.schema.json](../../schemas/run-status/v1/run-status.schema.json) |
| `preset/v1` | a preset file | [preset.schema.json](../../schemas/preset/v1/preset.schema.json) |
| `profile/v1` | the profile | [profile.schema.json](../../profiles/profile.schema.json) |

Each schema under `schemas/` sits in `schemas/<name>/v<major>/`, next to valid and invalid examples. A published major takes additive changes only: `SchemaCompatTests` fails the build on a breaking change since the latest `v*` tag. `Chargehand.Contracts` packs the five schemas under `schemas/` with C# types and a validator. It versions by schema major (1.2.0-alpha) and is on nuget.org. `samples/ContentEngineCall` is a program caller built from it alone.

## Error codes

A failed `result/v1` carries `error`: a fixed `code`, the `message`, `retryable`, and an `action` when there is something to do. Branch on the code, not on the summary text. Keys, key aliases, bearer tokens and spend figures are redacted from messages before they reach `result/v1` or the run log.

| code | raised when | retryable |
|---|---|---|
| `runtime_unavailable` | the OpenCode server refuses the connection or cannot start; the Claude Code binary cannot start or `--version` fails; `claude` is signed out and no credential is set; no agent CLI is on `PATH` | yes |
| `runtime_version_mismatch` | the runtime's version differs from the pin | no |
| `runtime_ambiguous` | more than one agent CLI is on `PATH` and nothing names one | no |
| `provider_unavailable` | OpenCode answers 502, 503 or 504 | yes |
| `rate_limited` | OpenCode answers 429, or an error's text is a rate limit | yes |
| `repository_not_allowed` | the repository is outside `repository_roots`, or a worker checkout would sit under the home directory | no |
| `checkout_invalid` | the path is not a git checkout, the commit is not in it, the clone fails, or `git ls-files` fails | no |
| `checkout_has_secrets` | the clone tracks files the preset denies reading | no |
| `cost_cap_reached` | the watcher interrupted a node above its USD cap or its input-token budget | no |
| `deadline_exceeded` | a node's turn was interrupted for any other reason; the deadline is the only other interrupt | yes |
| `invalid_result` | a node's turns succeeded but left no valid result contract after the repair turn | no |
| `intake_failed` | intake returned no valid Task Spec after one retry | no |
| `invalid_request` | an unknown preset, a caller block whose sha256 does not match its text, an unknown runtime name, or both Claude Code credentials set | no |
| `internal` | anything else | no |

Over HTTP, a request the server refuses before a run exists stays a `400` with `errors` ([ADR 0022](../adr/0022-error-codes-in-result-v1.md)).

## Profile fields

The profile is `profile/v1` JSON; `profiles/example.json` fills in most fields with placeholders. The defaults for `worker_root`, `default_preset`, `intake_model`, `prices` and `secrets` come from [ADR 0026](../adr/0026-extension-model.md).

| field | default | what it does |
|---|---|---|
| `schema` | required | always `profile/v1` |
| `runtime` | unset | `opencode` or `claude_code`; wins over `CHARGEHAND_RUNTIME`. Unset, chargehand probes `PATH` |
| `secrets` | `[{ "env": true }]` | ordered secret sources, first success wins. `{ "env": true }` reads the item as an environment variable, upper-cased with `-` turned into `_`. `{ "command": [...] }` runs an argv with `{item}` substituted, no shell. It replaces `secret_store` |
| `opencode` | unset | `url`, `password_secret`, `version` (required) and `binary`: connect to your own OpenCode server. Unset, chargehand starts one |
| `claude_code` | unset | `version` (required), `binary` (default `claude`), at most one of `api_key_secret` and `oauth_token_secret`, and `base_url` for an Anthropic-compatible gateway |
| `worker_root` | `/var/tmp/chargehand/work` | where worker clones live; must be outside the home directory |
| `repository_roots` | `worker_root` alone; `run` and `mcp` add their launch directory | the directories a request's repository may sit under; `"/"` allows any |
| `default_preset` | `cheap` | `request/v1` requires `context.preset`, so each request names its own |
| `intake_model` | unset: the runtime's default model | the model intake runs on |
| `run_cap_usd` | 1.00 | hard cap per run in USD, divided evenly across a split's nodes |
| `run_log` | see [Run log](#run-log) | the JSONL run log's path |
| `telemetry` | unset | `otlp_endpoint`, `public_key_secret` and `secret_key_secret` send OTLP traces to Langfuse; `usage_on_spans` (default false) puts tokens and cost on call spans for calls no gateway records |
| `prices` | empty: cost unknown | USD per 1M tokens per `provider/model`: `input`, `output`, `cache_read`, `cache_write` (with the provider's cache-write surcharge) |
| `models` | empty | maps the presets' placeholder models to real `provider/model` ids |
| `http` | unset | `api_key_secret` (required), `port` (4300), `listen` (`127.0.0.1`), `allowed_hosts`; `serve` needs it |
| `memory` | unset | `backend` (`hindsight`), `url` and `namespace` (required), `api_key_secret`, `max_tokens` (1024), `retain` (false) |

A profile still carrying `"secret_store": "keychain"` needs a one-line migration to `"secrets": [{"env": true}, {"command": ["security", "find-generic-password", "-s", "{item}", "-w"]}]` ([changelog 0.4.0](../../CHANGELOG.md#040---2026-09-29)).

## Environment variables

| variable | read by | effect |
|---|---|---|
| `CHARGEHAND_PROFILE` | every command | the profile's path; `--profile` wins; default `profiles/local.json`. The image sets `/config/profile.json` |
| `CHARGEHAND_RUNTIME` | runtime selection | `opencode` or `claude_code`; the profile's `runtime` wins over it |
| `ANTHROPIC_API_KEY` | Claude Code with no `claude_code` block | API-key mode, billed per token; workers run with `--bare`. Set it or `CLAUDE_CODE_OAUTH_TOKEN`, not both. A started OpenCode server can read it as a provider key |
| `CLAUDE_CODE_OAUTH_TOKEN` | Claude Code with no `claude_code` block | subscription mode with a token from `claude setup-token`; workers run with `--setting-sources ""` |
| `CHARGEHAND_API_KEY` | the default `env` secret source | the variable that an item named `chargehand-api-key` reads, the name `profiles/example.json` gives `http.api_key_secret`. `samples/ContentEngineCall` sends it as its bearer key |
| `OPENCODE_SERVER_PASSWORD` | OpenCode | the server's password; chargehand generates one for a server it starts, and `scripts/opencode-serve.sh` reads it or a macOS Keychain item |
| `CHARGEHAND_STATE` | `scripts/opencode-serve.sh` | the server's state directory, default `$HOME/.chargehand/opencode` |

A Claude Code worker gets only the credential and base URL the profile decides: chargehand removes an inherited `ANTHROPIC_AUTH_TOKEN` and sets or removes `ANTHROPIC_BASE_URL` from `claude_code.base_url`. With the CLI's own login, it removes both credential variables too.

Scripts and CI read a few more:

| variable | where | effect |
|---|---|---|
| `HEAD` | `scripts/prompt-ci.sh` | the commit to evaluate; default the pull request's head |
| `PROMPT_CI_RUNTIME`, `PROMPT_CI_PROFILE_<RUNTIME>` | the eval runner | its default runtime, and one eval profile per runtime |
| `SELF_REVIEW_PROFILE` | the eval runner, `.github/workflows/self-review.yml` | the profile for reviews of this repository's own pull requests |
| `CLAUDE_CODE_VERSION` | Docker build argument | the Claude Code version in the image, default 2.1.283 |

## Run log

chargehand picks the run log in this order:

1. The profile's `run_log`.
2. `runs/run-log.jsonl` when the current directory holds both `prompts/` and `presets/` (a checkout, or `/app` in the image).
3. `chargehand/run-log.jsonl` under the per-user data directory: `~/.local/share` on Linux, `~/Library/Application Support` on macOS.

The log holds one JSON object per line, tagged `start` (the request as received, the trace id, the owning process id, and a parent run for a resend with answers), `call` (tokens, cache reads and writes, cost, latency and the prompt chain of each model call), `run` (the Task Spec and the result) and `score` (hand scores). CLI runs, the server and evals append to the same file under an exclusive lock on `<log>.lock`, and readers skip a half-written last line. A `start` with no `run` is a run in progress while its process lives, and `lost` after.
