---
title: "Reference"
description: "Commands, HTTP routes, presets, contracts, error codes, profile fields, environment variables, and where the run log lives."
order: 8
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
| `extensions check [--preset <name>] [--probe <query>]` | connects every `mcp_servers` entry, lists its tools, and checks each `memory` mapping, the `prompt_enhancer` and each preset's `services` against them; one line per item, with an action on every problem. It reads every preset in `presets/`, or only `--preset`. `--probe` also runs one recall per memory and prints how many facts came back | 0 nothing wrong, 1 a problem, 2 usage error or a profile that does not load |

Any other command prints the usage and exits 2. `eval` and `prompts sync` need the profile's `telemetry` block.

`extensions check` prints one line per item and never a URL, a credential, an argument value or a recalled fact. A server line reads `server gw: connected, 3 tools` or `server gw: unreachable: <reason>; action: <what to do>`. A memory line reads `memory hindsight: recall -> recall ok` (operation, then the tool it calls), or `memory hindsight: retain -> tool 'retain' is not listed by server 'gw'; action: ...` for a tool the server lacks, an argument its schema does not declare or a required one the mapping leaves out; with `--probe`, `memory hindsight: probe returned 3 fact(s)`. A service line reads `preset docs: service team-docs: search_docs ok`, or `tool 'x' is not listed; action: ...`. A memory or service on a server that is down says it was not checked; the server's own line says why.

`routes` shows runs, completions, score, prompt tokens, cache rate, cost and latency. It names a model a candidate only when it and the default each have 20 scored runs, it scores within 0.10 of the default, and it costs at least 15% less; nothing routes on its own ([ADR 0019](../adr/0019-prompt-ci-and-routing-report.md)).

`prompts/` and `presets/` come from the current directory when it holds both (a checkout, or `/app` in the image), otherwise from the copies the build places next to the binary.

## HTTP routes

| route | behaviour |
|---|---|
| `POST /v1/runs` | takes `request/v1`. With `Prefer: wait=N` (default 10 s, at most 60): `200` and `result/v1` if the run finishes in time, else `202`, `run-status/v1` and a `Location` |
| `GET /v1/runs/{id}` | `202` while queued or running, `200` and `result/v1` once finished, `410` if the process that ran it ended first, `404` if unknown |
| `GET /v1/runs` | run summaries, newest first, filtered by `status` and `since`; [Run the HTTP server](server.md#routes) |
| `POST /v1/runs/{id}/cancel`, `POST /v1/halt`, `POST /v1/resume` | cancel one run, cancel every run and refuse new ones, and lift that; a cancelled run ends `failed` with `cancelled` ([driven sessions](driven.md#limits-and-the-kill-switch)) |
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

On OpenCode, every session's rules also end with `*_* * deny`, so MCP tools are refused unless a preset's `services` grant them (below). On Claude Code, chargehand translates the rules into `--tools`, `--allowedTools` and `--disallowedTools`. Claude Code's rules are deny-wins, so an allow inside an earlier deny stays denied: `*.env.example` stays unreadable there ([ADR 0020](../adr/0020-claude-code-runtime-adapter.md)).

### Services

A node kind may list `services`, an optional `preset/v1` field (ADR 0034): each entry is a `server` from the profile's `mcp_servers` and a `tools` list of names the node's workers may call. A name is exact or a glob with `*`; a lone `*` is refused, so a preset never grants a whole server. At the start of a run chargehand connects, lists the server's tools and grants the ones named; a server, secret or tool that does not resolve drops that service and is recorded on the run. No shipped preset lists services. [Memory and services](memory-and-services.md#services) has an example and what each runtime does with a grant.

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

Each schema under `schemas/` sits in `schemas/<name>/v<major>/`, next to valid and invalid examples. A published major takes additive changes only: `SchemaCompatTests` fails the build on a breaking change since the latest `v*` tag. `Chargehand.Contracts` packs the five schemas under `schemas/` with C# types and a validator. It versions by schema major (1.4.0-alpha) and is on nuget.org. `samples/ContentEngineCall` is a program caller built from it alone.

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
| `invalid_result` | a node's turns succeeded but left no valid result contract after the repair turn, or a writing node changed no files | no |
| `intake_failed` | intake returned no valid Task Spec after one retry | no |
| `invalid_request` | an unknown preset, a caller block whose sha256 does not match its text, an unknown runtime name, or both Claude Code credentials set | no |
| `sandbox_unavailable` | a writing preset ran on a machine with no `sandbox-exec` or `bwrap` and `sandbox.kind` is not `none` | no |
| `verification_failed` | a writing node's test command still failed after the last fix round; the branch is in the result's artifacts. For a driven task: chargehand's own run of the tests in a fresh container failed, or there is no test command | no |
| `container_unavailable` | a driven session needs a container engine and none answers (no Docker, or the runner is down), or a task's output volume cannot be moved | yes |
| `credential_unavailable` | a driven batch has no model credential or no push credential in the profile's secret sources | no |
| `session_failed` | a driven session made no usable branch, or chargehand refused it: nothing changed, a secret-shaped diff, a change to CI configuration | no |
| `session_stalled` | a driven session ended on no progress, a repeated tool call, too many turns or its wall clock | yes |
| `push_rejected` | the remote refused `chargehand/<run>`, or the remote is not an https or ssh URL | no |
| `pr_failed` | the branch was pushed but the draft pull request did not open; the result names the branch | yes |
| `cancelled` | a run was cancelled (`POST /v1/runs/{id}/cancel` or `POST /v1/halt`); nothing is pushed | no |
| `tasks_incomplete` | a driven batch ended with at least one task that has no draft pull request; the result names them | no |
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
| `support_check` | `true` | after each node, one call on the intake model judges whether the text each claim cites supports it ([ADR 0036](../adr/0036-support-checking-and-signed-results.md)); `false` removes the check and its call |
| `signing` | unset | `{key_file}`: a P-256 private key in PEM; every result of a run is then signed with ES256. `CHARGEHAND_SIGNING_KEY_FILE` overrides it. Chargehand never creates a key ([Signed results](support-and-signing.md#signed-results)) |
| `sandbox` | `{kind: auto, network: false}` | Where a writing preset's tests run ([ADR 0035](../adr/0035-sandboxed-writing-workers.md)). `kind`: `auto` (`sandbox-exec` on macOS, `bwrap` on Linux; a writing run is refused with `sandbox_unavailable` when neither exists), `seatbelt`, `bubblewrap`, or `none` (unconfined, an explicit opt-in). `network` allows the command network access (a build that restores packages needs it). `env` lists variable names passed in besides `PATH`, `LANG`, `LC_ALL` and `TERM`. The command may write only in the run's clone and a private temp directory, and may not read `~/.ssh`, `~/.aws`, `~/.gnupg`, `~/.config/gh`, `~/.docker`, `~/.kube`, `~/.claude`, `~/.netrc`, `~/.npmrc`, `~/.git-credentials` or `~/Library/Keychains`. |
| `default_preset` | `cheap` | `request/v1` requires `context.preset`, so each request names its own |
| `intake_model` | unset: the runtime's default model | the model intake runs on |
| `run_cap_usd` | 1.00 | hard cap per run in USD, divided evenly across a split's nodes |
| `run_log` | see [Run log](#run-log) | the JSONL run log's path |
| `telemetry` | unset | `otlp_endpoint`, `public_key_secret` and `secret_key_secret` send OTLP traces to Langfuse; `usage_on_spans` (default false) puts tokens and cost on call spans for calls no gateway records |
| `prices` | empty: cost unknown | USD per 1M tokens per `provider/model`: `input`, `output`, `cache_read`, `cache_write` (with the provider's cache-write surcharge) |
| `models` | empty | maps the presets' placeholder models to real `provider/model` ids |
| `http` | unset | `api_key_secret` (required), `port` (4300), `listen` (`127.0.0.1`), `allowed_hosts`; `serve` needs it |
| `driven` | `{enabled: false}` | Driven writing sessions ([ADR 0039](../adr/0039-driven-writing-sessions.md), [guide](driven.md#configure-it)): `enabled`, `max_parallel` (2, at most 4), `max_parallel_total` (4, at most 8), `images` (session image by digest), `network` (`allow`, `outside`, `mcp_forward`), `runner` (`url`, `api_key_secret`), `push_secret` and `task_source`. Leave `enabled` false in a shared profile |
| `mcp_servers` | unset | MCP servers by name, which `memory` entries and presets' `services` refer to; one connection per server, opened on first use and shared by both. Each has `url` (Streamable HTTP or SSE; optional `headers` and `transport`: `auto`, `streamable-http` or `sse`, default `auto`) or `command` (a stdio argv; optional `env`). `{secret:item}` is allowed in header and `env` values only and resolves through `secrets` when the connection opens; a server whose secret nothing resolves is not connected. A command secret source that runs longer than 15 s is killed and the next source tried. [Memory and services](memory-and-services.md#name-your-servers-mcp_servers) covers transports and a Keychain item stored with an account |
| `prompt_enhancer` | unset | a prompt enhancer (ADR 0040): `server` (an `mcp_servers` key whose server lists the tools `enhance` and `feedback`) and `deadline_ms` (1500; 50 to 30000). A client sends the prompt and the repository, commit, task kind and client name, shows the rewrite and sends the original unless the user accepts it, then reports the outcome with `feedback`. Late, failing or unreachable: the original is sent. An unknown server fails at load; `chargehand extensions check` lists the two tools |
| `memory` | unset | a list of providers (ADR 0034), in priority order, each: `name` and `server` (an `mcp_servers` key) and `tools` (required); `namespace` (default `name`); `max_facts` (10), `max_chars` (4000), `max_fact_chars` (600), `timeout_seconds` (10); `retain` (false) and `retain_tags` (`["chargehand"]`). `tools.recall` is required, `tools.retain` when `retain` is true, `tools.invalidate` is optional. A tool is `{ "tool": <name>, "arguments": {...} }`; in the arguments, a string that is exactly one placeholder keeps the value's type. Recall may use `{query}`, `{namespace}` and `{max_facts}`; retain `{namespace}`, `{text}`, `{context}`, `{document_id}`, `{timestamp}`, `{tags}`, `{repository}`, `{commit}` and `{locators}`; invalidate `{namespace}`, `{id}` and `{reason}`. Recall's `results` says how to read the answer: `path` (dotted, to the array; without a `results` block it is `results`, with one and no `path` it is the root), `id` (default `id`), `text` (default `text`; a field, a template over fields such as `{date}: {summary}`, or an ordered list of these, the first whose fields are all present and non-empty winning) and `format` (`json`, or `text` for one fact from the whole answer). A mapping with an unknown server or placeholder fails at load; `chargehand extensions check` connects and checks the tools. [Memory and services](memory-and-services.md) has the Hindsight and Chronicle entries. The old single object (`backend`, `url`, `namespace`, `api_key_secret`, `max_tokens`, `retain`) was removed and fails at load with a migration message ([changelog](../../CHANGELOG.md)) |

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
| `CHARGEHAND_E2E_MODEL_KEY`, `CHARGEHAND_E2E_IMAGE` | `scripts/driven-e2e.sh` | a capped Anthropic API key and the session image by digest; the script's other variables are listed at its top |
| `CHARGEHAND_E2E_GITHUB_API`, `CHARGEHAND_E2E_LOCAL_REMOTE` | the CLI, for `scripts/driven-e2e.sh` only | the draft-pull-request client's base URL, and `1` to accept a `file://` remote; never set in a deployment |

## Run log

chargehand picks the run log in this order:

1. The profile's `run_log`.
2. `runs/run-log.jsonl` when the current directory holds both `prompts/` and `presets/` (a checkout, or `/app` in the image).
3. `chargehand/run-log.jsonl` under the per-user data directory: `~/.local/share` on Linux, `~/Library/Application Support` on macOS.

The log holds one JSON object per line, tagged `start` (the request as received, the trace id, the owning process id, and a parent run for a resend with answers), `call` (tokens, cache reads and writes, cost, latency and the prompt chain of each model call), `run` (the Task Spec and the result) and `score` (hand scores). CLI runs, the server and evals append to the same file under an exclusive lock on `<log>.lock`, and readers skip a half-written last line. A `start` with no `run` is a run in progress while its process lives, and `lost` after.
