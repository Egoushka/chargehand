# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The contract package
(`Chargehand.Contracts`) is versioned separately, by schema major.

## [Unreleased]

### Security

- A failed result no longer repeats the model gateway's error text verbatim: keys, key aliases, bearer tokens and
  spend figures are redacted before a message reaches `result/v1` or the run log. A refusal over a spend budget says
  what to do in `error.action`.

### Added

- `ROADMAP.md`; `scripts/check.sh`, the one check before a push; a Claude Code hook that asks before an edit to a
  published schema major.
- OpenSSF Scorecard, dependency review on pull requests, CodeQL, and a SonarQube Cloud job that runs once the project
  is connected; badges in the README.

### Changed

- A tag `v<Version>` also creates the GitHub Release, with the version's section of this changelog as its notes, and
  publishes `Chargehand.Contracts` to nuget.org when its version is new there. Publishing waits for an approval in the
  `release` environment; a tag whose version has no section here fails before anything is published, and CI fails a
  version bump that comes without one.
- The Prompt CI runner ADR is now ADR 0025 (two ADRs had number 0022); ADRs 0024 and 0025 are accepted.
  `TRADEMARK.md` says how the name may be used, and contributions are signed off (DCO).
- The build runs the .NET analyzers at `10.0-recommended`. Parsing and formatting no longer depend on the machine's
  locale: a hand score such as `0.8` parses where the decimal separator is a comma.

## [0.2.1] - 2026-09-28

### Fixed

- A worker clone of a repository on another mount (a container's repository mount and its work volume) failed with
  `checkout_invalid`: `git clone --local` cannot hard-link across mount points. The clone now copies when linking
  fails.

## [0.2.0] - 2026-09-28

### The server runs on a private network

`chargehand serve` can bind a private-network address: `http.listen` sets it and `http.allowed_hosts` names the host
clients use; without allowed hosts it refuses to start beyond loopback. A `Dockerfile` builds the server with the
Claude Code runtime, and a tag `v<Version>` publishes `ghcr.io/<owner>/chargehand:<Version>`. See ADR 0024.

### Prompt CI runs on its own

A pull request that changes `prompts/` or `presets/` is gated on a self-hosted runner, not by a manual run of
`scripts/prompt-ci.sh`. A fork's pull request or a preset change waits for an approval in the `prompt-ci-review`
environment. The runner holds one eval profile per runtime and a default; a `prompt-ci:<runtime>` label picks another
when someone with write access adds it. The manual run still works. See ADR 0025.

### Added

- `result/v1` has an optional `error` on failed results (ADR 0022): a fixed `code`, the `message`, `retryable`, and
  an `action` when there is something to do, such as the command that starts the OpenCode server. Clients branch on
  the code instead of parsing `summary`. The schema stays v1; `Chargehand.Contracts` is 1.2.0-alpha with the
  `ResultError` record and the `ErrorCode` enum.

### Changed

- Preset blocks `default` 0.5.0, `cheap`, `thorough` and `strict` 0.4.0 say "read the files that answer the task"
  instead of "read every file the task needs". Worker prompt 0.3.0 keeps the completeness rules. In the phase 3
  benchmark the old wording cost 28% more than a plain session through extra reading; the new one costs the same
  and stays more complete (docs/benchmarks.md).
- Worker prompt 0.3.0 asks for the whole task, one claim per item and a citation for every file relied on, and
  no longer caps the summary at 120 words. Preset blocks `default` 0.4.0, `cheap`, `thorough` and `strict` 0.3.0
  say "read every file the task needs" instead of "prefer the smallest set of files". The phase 3 blind verdict
  failed on completeness in 3 of 3 pairs at equal exploration: the worker read files it then left out, and merged
  several services into one claim.
- A worker interrupted at its token budget (`budget.max_input_tokens`) gets one turn to answer from what it has
  read, with room for three calls at the last call's context on top of what it spent; the result lists the stop in
  `open_questions`. It used to fail with nothing. The USD cap still ends a node without that turn (ADR 0010).
- A request's repository may sit anywhere under the profile's new `repository_roots` (default: `worker_root`; `/`
  allows any). The worker reads a clone of it at the pinned commit under `worker_root`, reused per source and commit,
  so the source's uncommitted and ignored files never reach it and the worker stays outside the home. A short commit
  hash is enough. A checkout that tracks a file the preset denies reading is still refused (ADR 0023).
- `chargehand run` prints a failed `result/v1` with `error` when it cannot connect to the runtime (server not
  running, binary missing, another version), instead of ending with an unhandled exception.
- The eval gate retries an arm whose result has code `rate_limited`, instead of matching "rate limit" in its summary.

### Prompt CI calibration

`cheap/worker`'s items pinned a checkout that tracks encrypted env files the `cheap` preset denies reading, so since
workers refuse such a checkout (0.1.0) every arm of its A/A failed at $0. The items now pin a commit of that checkout
without those files; every reference file is unchanged. The A/A there, the first since workers lost the shell, 12
items: quality -0.046 (t -0.63), cost +13% (t 1.82), pass, $0.11; 5 items differ, by 0.75, 0.27, 0.25, 0.20 and 0.02.
Claims per item: 6.25 against 5.97 in the runs that seeded `reference_claims`, 20 of 24 arms within their range, so
`reference_claims` stays.

## [0.1.0] - 2026-09-27

First usable release. chargehand turns a request into a Task Spec, runs it on one or more coding-agent sessions, and
returns `result/v1` with evidence behind every claim. You call it from the CLI, over HTTP, or as an MCP tool. It
covers roadmap phases 3 to 5 (v0, v1, v2). Benchmark and exit-check numbers live in
[docs/benchmarks.md](docs/benchmarks.md).

### Added

#### Runs and actions

- `chargehand run` reads `request/v1`, runs intake to get a Task Spec, and returns `result/v1`.
- Intake picks one action. `answer` runs one worker session. `split` runs 2–4 read-only subtasks as a task graph.
  `deny` returns status `denied` with a reason and an unblock condition. `ask` returns `needs_input` with questions.
  `improve` returns `needs_input` with an improved request and an inline diff artifact. When the preset does not allow
  the chosen action, the run falls back to `answer`; the run log records both.
- Split runs (ADR 0017) execute at most 2 nodes at once and pass upstream contracts to dependents; a failed node stops
  only its dependents. Later nodes fork the first node's session before its first message, so they read its system
  prefix from cache. Node contracts merge deterministically, with evidence ids prefixed by the node id.
- The worker node fixes its instruction entries before the first prompt. A watcher rejects permission requests and
  interrupts above the run cap. Each node gets a 15-minute deadline and one repair turn each for the result schema
  and for evidence.
- Per-node budgets: the watcher interrupts a node above `budget.max_input_tokens` and compacts it mid-turn above
  `compaction.trigger_tokens` (new optional `preset/v1` field).
- The evidence resolver checks claims against git at the pinned commit, session messages, caller inputs, URLs seen,
  and the session diff. Claims that do not resolve move to `open_questions`.
- `request/v1` gains optional `context.approved` to run past a preset's approval thresholds.

#### Presets

- `default`, plus `cheap` 0.1.0, `thorough` 0.1.0 and `strict` 0.1.0, all read-only. `strict` asks for approval above
  risk `low` or an estimate above $0.50.
- `draft` 0.1.0 (node kind `draft`) writes a draft for a program caller from the caller's own inputs, with no
  repository and no tools, and returns it as an inline artifact. New optional `preset/v1` field `checkout: false`.

#### Callable interface (ADR 0018)

- `chargehand serve` hosts HTTP and MCP on 127.0.0.1. Every route requires a bearer key from the secret store
  (profile `http`), and bodies cap at 1 MB.
- `POST /v1/runs` takes `request/v1`. If the run finishes within `Prefer: wait=N` (default 10 s, at most 60) you get
  `200` with `result/v1`, otherwise `202` with `run-status/v1`.
- `GET /v1/runs/{id}` answers `202` while running, `200` when finished, `410` when the owning process died, `404`
  when unknown. `GET /v1/runs/{id}/events` streams `accepted`, `started`, `intake`, `node_started`, `node_finished`
  and `run_finished`.
- Runs outlive the request and execute one at a time. The server holds at most 10 unfinished runs, then answers 429.
- MCP at `/v1/mcp` (Streamable HTTP, MCP 2026-07-28 with hybrid sessions, C# SDK 2.2.0) exposes tool `orchestrate`,
  with `inputSchema` `request/v1` and `outputSchema` `result/v1`. Long runs become tasks through the tasks extension.
  An `ask` becomes `input_required`, and the answers resend the request as a child run.
- The run log's new `start` record (request, trace id, owning pid, parent run) makes it the store the CLI and the
  server share. `show` prints runs that are still running or were lost. Concurrent appends take a lock file, and
  readers skip a half-written last line.
- `Chargehand.Contracts` 1.1.0-alpha adds schema `run-status/v1` and `RunStatus`; `PromptBlock.Create` hashes a caller
  block by request/v1's rule.
- `samples/ContentEngineCall` builds the content engine's call (ADR 0014) from `Chargehand.Contracts` alone.

#### Worker runtimes

- OpenCode V2 adapter pinned to 2.0.16: a hand-written client and `IWorkerRuntime` implementation, with a retry for
  the `Model unavailable` race while a location boots. `scripts/opencode-serve.sh` starts the orchestrator's own
  server; `profiles/opencode.example.json` configures it.
- Claude Code adapter (ADR 0020, `Chargehand.ClaudeCode`) runs `claude -p` with stream-json, one process per turn,
  `--bare` and `dontAsk`, and translates preset rules to `--tools`, `--allowedTools` and `--disallowedTools`. Profile
  `claude_code` selects it instead of `opencode`, which becomes optional. It takes `version`, `binary`, and exactly
  one of `api_key_secret` (per-token API billing) or `oauth_token_secret` (a `claude setup-token` subscription
  token). Optional `base_url` routes workers through an Anthropic-compatible gateway.

#### Prompts, evals and routing

- Prompt registry in `prompts/` with SemVer front matter and normalised sha256. Every call records its
  `prompt_chain`; `chargehand prompts sync` mirrors blocks into Langfuse prompt management.
- Prompt CI (ADR 0019): `chargehand eval seed|push|gate` with cells `cheap/worker`, `draft/draft` and `intake` in
  `evals/cells.json`, items in Langfuse datasets, and deterministic scores. It runs base and change in pairs and gates
  on quality tolerance T (0.10) and cost tolerance C (+15%, or +30% for `cheap/worker`), with a declared-trade
  override. Results land as Langfuse dataset runs and scores.
- `scripts/prompt-ci.sh <pr>` runs Prompt CI on the owner's machine and posts commit status `prompt-ci`.
  `.github/workflows/prompt-ci.yml` marks pull requests that change no prompt or preset.
- `chargehand routes` prints a routing report per preset, node kind and model (runs, score, tokens, cache rate, cost,
  latency) and only suggests changes. `chargehand score` records a hand score for a run.

#### Memory

- `IMemoryProvider` (recall, retain, invalidate; scope is backend plus namespace) with an adapter for a self-hosted
  Hindsight service (HTTP API 0.10.0). With profile `memory` set, a run recalls facts once and appends them to each
  node's prompt as unverified context (chain block `memory/recall`, source `runtime`). A failed recall leaves the run
  without them. `retain` stores completed runs and stays off by default.

#### Observability

- JSONL run log with tokens, cache rate, own-table cost and latency per call. `chargehand show` summarises a run;
  `chargehand reconcile` joins calls to exported gateway spend rows by model, token counts and time.
- `chargehand cache <run>` reports cache reads, writes and hit rate per call. Per node, it names the first instruction
  entry, prompt block or as-sent field that broke a prefix that should have been shared, plus a checklist for breakers
  outside the prompt chain. Call records carry the fork parent and hashed instruction entries.
- OTLP traces (run → intake → node → call) carry `chargehand.prompt_chain` and the OpenCode session id.
- `telemetry.usage_on_spans` (ADR 0021) puts Langfuse usage and cost on call spans that no gateway records, such as
  Claude Code on a subscription. Off by default; with a LiteLLM gateway, ADR 0012 still applies.

### Changed

- Intake prompt 0.2.0 says when to split. 0.3.0 asks only when a required fact is missing and the caller's inputs do
  not supply it; 0.2.0 asked a program caller for facts it had already sent (1 of 2 A/A runs).
- Presets `default` 0.4.0, `cheap` 0.2.0 and `thorough` 0.2.0 drop approval thresholds. Intake's estimate is
  uncalibrated (ADR 0005): in the phase 4 benchmark it guessed up to $0.35 for runs that cost about $0.01 and stopped
  one with `needs_input`. `strict` keeps its thresholds.
- Preset `default` 0.2.0 allows `rg` (with `--pre` denied). Worker prompt 0.2.0 makes the final message the JSON block
  only.
- Prompt CI scores a worker as grounding times completeness. Completeness is claims kept over the item's
  `reference_claims` (new optional field in an eval item's `expected`), capped at 1; `eval seed` proposes the count
  from the seeding run. Grounding alone passed #5, which cited the same code in fewer, wider claims.
- `cheap/worker` drops the phase 3 reference question (now 12 items). At `cheap`'s 400k-token node budget it failed
  in about half its runs, and one flip moved an A/A's mean by up to 0.08.
- The orchestrator computes an inline artifact's sha256 itself and caps its content at 64 KiB.

### Fixed

- A run that throws (bad checkout, unknown preset, no valid Task Spec) ends with a failed `result/v1` and a run record
  instead of an exception.
- Caller inputs appear in the task text as `- id "<id>" (<kind>): <text>`. The old `[id]` form led workers to cite
  `[id]`, which matched no input. A failed `input` reference now lists the ids that exist, and intake sees each
  input's id and kind.
- OpenCode's stateless generate (intake) retries one 503, as ADR 0011 prescribes for the masking proxy.
- Prompt CI retries an arm that hits a rate limit (after 15, 30 and 60 s) and stops the gate if the limit persists,
  instead of scoring the item 0.
- Prompt CI stops the gate (status `error`) when a worker or draft arm fails with zero usage, meaning a refusal before
  any model call. Such arms used to score 0 on both sides and pass as no change. Intake arms report no usage and stay
  exempt.

### Security

- `chargehand serve` requires its bearer key on loopback too, since any local process can reach the port. It accepts
  only a loopback Host header (against DNS rebinding) and sends no CORS headers.
- Prompt CI never builds or runs a pull request's code. The runner uses the trusted checkout's build, and the pull
  request contributes only `prompts/` and `presets/`. Those still steer a worker, and a preset can grant tools, so a
  fork's pull request or any preset change runs only after the owner reads the diff (`--reviewed`). A symbolic link
  among them stops the run. GitHub holds no model, gateway, tracing or tailnet key; evals use their own OpenCode
  server and a spend-capped gateway key limited to the small model.
- Presets `default` 0.5.0, `cheap` 0.3.0, `thorough` 0.3.0 and `strict` 0.2.0 remove the shell tool from workers, and
  their prompt blocks point to the `grep` and `glob` tools instead. OpenCode matches shell rules against each
  command's source text, so the `git grep*`, `git log*` and `git show*` allows let a worker execute programs
  (`git grep -O`) and write files (`--output`), and quoting or naming a file slipped past the `rg` denies.
- A run fails before any worker session starts when its checkout holds a file the preset denies reading (`*.env`,
  `*.env.*`), ignored files included. OpenCode's `grep` tool checks permission against its pattern rather than the
  path it searches, and it reads ignored files named by path or `include` glob, so `read` rules alone could not keep
  those files out.
- Preset `default` 0.3.0 denies reading `*.env` and `*.env.*`, and denies `rg --no-ignore` and `rg -u`. The session
  ruleset's leading allow had overridden OpenCode's own ask-before-reading-`.env` rule, so a worker could read secret
  files without asking.

## [0.1.0-alpha] - 2026-09-26 (not tagged)

### Added

- Repository bootstrap: license, contribution guide, security policy, CI with secret scanning,
  Conventional Commits check, ADR template.
- JSON Schemas `task-spec/v1`, `result/v1` and `preset/v1` with fixtures and schema tests.
- Architecture decision records 0000–0016.
- Generated OpenCode API map and a contract test against the checked-in spec.
- Solution skeleton: CLI entry point, intake, `IWorkerRuntime` port, OpenCode adapter interface,
  contract validator, evidence resolver, prompt registry, telemetry and run log interfaces.

[Unreleased]: https://github.com/Egoushka/chargehand/compare/v0.2.1...HEAD
[0.2.1]: https://github.com/Egoushka/chargehand/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/Egoushka/chargehand/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Egoushka/chargehand/releases/tag/v0.1.0
