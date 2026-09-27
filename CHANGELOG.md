# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The contract package
(`Chargehand.Contracts`) is versioned separately, by schema major.

## [Unreleased]

### Added

- v2 (roadmap phase 5), callable interface (ADR 0018): `chargehand serve` hosts HTTP and MCP on 127.0.0.1, every route
  behind a bearer key from the secret store (profile `http`), a loopback Host header, bodies up to 1 MB.
  `POST /v1/runs` takes `request/v1` and answers `result/v1` if the run finishes within `Prefer: wait=N` (default
  10 s, at most 60), else `202` with `run-status/v1`; `GET /v1/runs/{id}` (202, 200, 410 when the owning process
  died, 404); `GET /v1/runs/{id}/events` streams `accepted`, `started`, `intake`, `node_started`, `node_finished`,
  `run_finished`. Runs outlive the request and execute one at a time; at most 10 unfinished, then 429.
- MCP at `/v1/mcp` (Streamable HTTP, MCP 2026-07-28 with hybrid sessions, C# SDK 2.2.0): tool `orchestrate` with
  `inputSchema` `request/v1` and `outputSchema` `result/v1`; long runs as tasks (tasks extension); an interactive
  `ask` becomes `input_required` (elicitation inside a task, an input-required result outside one), and the answers
  resend the request as a child run.
- Run store: the run log's new `start` record (request, trace id, owning pid, parent run) makes it the store the CLI
  and the server share; `show` prints a run that is still running or was lost. Appends from several processes hold
  a lock file; readers skip a half-written last line.
- `Chargehand.Contracts` 1.1.0-alpha: schema `run-status/v1` and `RunStatus`; `PromptBlock.Create` hashes a caller
  block by request/v1's rule.
- Preset `draft` 0.1.0 (node kind `draft`, prompt blocks `core/draft` 0.1.0 and `preset/draft` 0.1.0): a program
  caller's draft from its own inputs, with no repository and no tools; the draft returns as an inline artifact. New
  optional `preset/v1` field `checkout: false`.
- `samples/ContentEngineCall`: the content engine's call (ADR 0014) built from `Chargehand.Contracts` alone, standing in
  until the content engine exists.
- Prompt CI (ADR 0019): `chargehand eval seed|push|gate`, cells in `evals/cells.json` (`cheap/worker`, `draft/draft`,
  `intake`) with items in Langfuse datasets, deterministic scores, paired runs of base and change, a gate with
  tolerances T (0.10) and C (+15%; +30% for `cheap/worker`) and a declared-trade override, results as Langfuse
  dataset runs and scores.
  `scripts/prompt-ci.sh <pr>` runs it on the owner's machine and posts the commit status `prompt-ci`;
  `.github/workflows/prompt-ci.yml` marks pull requests that change no prompt or preset.
- `chargehand routes`: routing report per preset, node kind and model (runs, score, tokens, cache rate, cost,
  latency), suggestions only. `chargehand score` records a hand score for a run.

- v1 (roadmap phase 4): `split` runs 2–4 read-only subtasks as a task graph (`SplitPlan`, `GraphRunner`): at most
  2 nodes at once, upstream contracts passed to dependents, a failed node stops its dependents only. Later nodes
  fork the first node's session before its first message and read its system prefix from cache (ADR 0017). Node
  contracts merge deterministically, evidence ids prefixed with the node id.
- Actions `deny` (status `denied`, reason, unblock condition), `ask` (`needs_input` with questions) and `improve`
  (`needs_input`, improved request plus an inline diff artifact). An action the preset does not allow runs as
  `answer`; the run log records intake's action and the executed one.
- Presets `cheap` 0.1.0, `thorough` 0.1.0 and `strict` 0.1.0 (read-only; `strict` asks for approval above risk
  `low` or an estimate above $0.50) with prompt blocks `preset/cheap`, `preset/thorough`, `preset/strict` 0.1.0.
- Per-node budgets: the watcher interrupts a node above `budget.max_input_tokens` and compacts (steered, mid-turn)
  above the new optional `compaction.trigger_tokens` in `preset/v1`.
- `request/v1`: optional `context.approved` to run past a preset's approval thresholds.
- Intake prompt 0.2.0 says when to split.
- `chargehand cache <run>`: cache report with reads, writes and hit rate per call; per node, the first instruction
  entry, prompt block or as-sent field that differs where a prefix should have been shared (the previous call, or
  the node it forked or could have forked); a checklist for breakers outside the prompt chain. Call records now
  carry the fork parent and the hashed instruction entries.
- Memory provider `IMemoryProvider` (recall, retain, invalidate; scope = backend + namespace) with one adapter for a
  self-hosted Hindsight service (HTTP API 0.10.0). Optional profile `memory`: an executed run recalls facts for the
  request once and appends them to each node's prompt text as unverified context (chain block `memory/recall`,
  source `runtime`); a failed recall leaves the run without them. `retain` (off by default) stores completed runs.

- v0: one request, one worker, traced (roadmap phase 3). `chargehand run` reads `request/v1`, runs intake
  (Task Spec via OpenCode's stateless generate; only `answer` executes, the chosen action is logged), runs one
  worker session on the orchestrator's own OpenCode server and returns `result/v1`.
- OpenCode V2 adapter: hand-written client and `IWorkerRuntime` implementation, pinned to 2.0.16, with a retry for
  the `Model unavailable` race while a location boots.
- Worker node: instruction entries fixed before the first prompt, a watcher that rejects permission requests and
  interrupts above the run cap, a 15-minute deadline, one repair turn each for the result schema and for evidence.
- Evidence resolver (git at the pinned commit, session messages, caller inputs, URLs seen, session diff); claims
  whose evidence does not resolve move to `open_questions`.
- Prompt registry (`prompts/`, SemVer front matter, normalised sha256), `prompt_chain` on every call, and
  `chargehand prompts sync` to mirror blocks into Langfuse prompt management.
- JSONL run log with tokens, cache rate, own-table cost and latency per call; `chargehand show` and
  `chargehand reconcile` (joins calls to exported gateway spend rows by model, token counts and time).
- OTLP traces (run → intake → node → call) carrying `chargehand.prompt_chain` and the OpenCode session id.
- `scripts/opencode-serve.sh` launcher and `profiles/opencode.example.json`.

### Phase 5 exit

1. **Met: the content engine's call through the interface.** `samples/ContentEngineCall`, built on
   `Chargehand.Contracts` alone and standing in for the content engine (which has no repository yet), sent one draft
   request to `chargehand serve` (`POST /v1/runs`, 2026-09-26). The run completed for $0.0006: 4 claims, each citing
   an input the caller sent, the draft's sha256 verified, and the caller's generator block in `prompt_chain`.
2. **Not met: Prompt CI blocking a real prompt regression.** The mechanics hold: with a ruleset requiring `prompt-ci`
   on `main`, a pull request that changes a prompt stays blocked until the owner's run posts the status. But both
   deliberate regressions passed the gate:

| pull request | change | cell (items) | mean quality, base → change | quality change (t) | cost change (t) | T | C | verdict |
|---|---|---|---|---|---|---|---|---|
| #5 | `preset/cheap` 0.2.0: stop at the first file that answers, at most 3 claims | `cheap/worker` (13) | 0.87 → 0.97 | +0.097 (1.51) | -27% (-2.87) | 0.10 | +30% | pass |
| #6 | `preset/cheap` 0.2.0: answer from what the worker knows, open at most one file | `cheap/worker` (13) | 0.91 → 0.85 | -0.064 (-1.42) | -2% (-0.59) | 0.10 | +30% | pass |
| #4 | intake prompt 0.2.0 → 0.3.0 (the v2 pull request itself) | `intake` (18) | 0.89 → 0.89 | +0.000 (0.00) | not priced | 0.10 | +15% | pass |

The score measures grounding: the share of claims whose evidence resolves, and file-level recall of reference files.
The worker node already enforces grounding at run time (the resolver and an evidence repair turn), so the worker kept
reading and citing files under both regressions, and under two local probes that told it to cite paths without lines
or to answer from memory (4 of 4 runs at quality 1.00). What #5 changed, fewer claims (13 to 9, 5 to 3), file-level
recall cannot see. Next: score recall of the reference answers' line ranges, then rerun #5's change.

### Prompt CI calibration (phase 5)

A/A runs: the same prompts and presets as base and change, small model, 2026-09-27, each item under both arms back
to back with the order alternating. Quality is 0 to 1; intake calls report no usage, so its cost is not compared.

| cell | items | quality change (t) | cost change (t) | items that differ | verdict |
|---|---|---|---|---|---|
| `cheap/worker`, C +15% | 13 | -0.033 (-1.45) | +15% (1.99) | 2, by 0.25 and 0.18 | block, on cost |
| `cheap/worker`, C +30% | 13 | -0.076 (-1.08) | -15% (-1.17) | 3, by 0.92, 0.12 and 0.05 | pass |
| `intake` | 19 | +0.000 (0.00) | not priced | 2 flip; 2 fail in both | pass |
| `draft/draft` | 8 | +0.000 (0.00) | +2% (0.41) | none | pass |

A worker's exploration varies from run to run: `cheap/worker`'s per-item cost ratios ran from 0.73 to 1.56 (log
ratio SD 0.24), so identical prompts exceeded C = +15% by chance and blocked. Its C is now +30%. In the rerun, the
phase 3 reference question stopped at `cheap`'s 400k-token node budget in one arm (0.92 against 0.00) and carries most
of the quality change. Cost per run: `cheap/worker` $0.0010–0.0213 (mean $0.0054, about $0.14 per A/A), `draft/draft`
$0.0004–0.0007.

### Benchmark (phase 4 exit, cost half)

Three breadth-first read-only questions (each spans 3 independent areas) on a private repository at a pinned
commit; v1 (`cheap` preset, intake chose `split` in 6 of 6 runs, 3 nodes each) against a plain single OpenCode
session with the same model, build agent and read-only ruleset; 2 repetitions, alternating order, same day and
OpenCode build. Small model for both arms. Cost priced from OpenCode's token counts with the profile's table; intake
(stateless generate, no usage reported) is excluded from v1.

| | v1 split | plain session |
|---|---|---|
| cost per run (mean of 6) | $0.00754 | $0.00492 |
| cost ratio per task (t1, t2, t3) | 1.62×, 1.51×, 1.63× | 1× |
| wall time (mean) | 123 s | 73 s |
| cited file:line that resolve | 99/99 | 112/112 |

Forked siblings read the first node's prefix from cache (4,878 of ~5.2k tokens on their first call), so the extra
cost is the nodes' own exploration, not the session base. On cost alone a split needs about 1.5× the plain
answer's quality to win on quality per dollar; the owner's blind scores decide it.

Cache report check: with the subtask brief moved into a per-node instruction entry (a local, uncommitted change),
siblings could not fork and wrote their prefix again (0 read); `chargehand cache` named the entry.

### Benchmark (phase 3 exit, cost half)

Reference read-only question on a private repository at a pinned commit; v0 against a plain single OpenCode
session, same day, model, OpenCode build and commit; 3 pairs, alternating order. Cost from the gateway's spend log.

| | v0 | plain session |
|---|---|---|
| cost per run (mean of 3) | $0.199 (+ ~$0.0004 intake) | $0.205 |
| wall time (mean) | 155 s | 122 s |
| cited file:line that resolve | 50/50 | 75/75 |

Answer quality is judged blind by the owner (pending at the time of this entry).

### Changed

- Intake prompt 0.3.0: `ask` only when a required fact is missing and the caller's inputs do not supply it. 0.2.0
  asked a program caller for facts it had sent as inputs (the content engine's draft request, in 1 of 2 A/A runs).
- A run that throws (a bad checkout, an unknown preset, no valid Task Spec) now ends with a failed `result/v1` and a
  run record instead of an exception.
- The task text lists caller inputs as `- id "<id>" (<kind>): <text>`: listed as `[id]`, a worker cited `[id]`, which
  matched no input. A failed `input` reference names the ids that exist, and intake sees each input's id and kind.
- The orchestrator computes an inline artifact's sha256 itself and bounds its content at 64 KiB in bytes.
- OpenCode's stateless generate (intake) retries one 503, as ADR 0011 prescribes for the masking proxy.

- Presets `default` 0.4.0, `cheap` 0.2.0 and `thorough` 0.2.0 set no approval thresholds. Intake's estimate is
  uncalibrated (ADR 0005): in the phase 4 benchmark it estimated up to $0.35 for runs that cost about $0.01 and
  stopped one with `needs_input`. Only `strict`, whose definition is to ask, keeps them.

- Preset `default` 0.2.0 allows `rg` (with `--pre` denied); worker prompt 0.2.0 makes the final message the JSON
  block only.

### Security

- `chargehand serve` requires its bearer key on loopback too (any local process can reach the port), accepts only a
  loopback Host header (DNS rebinding from a browser) and sends no CORS headers.
- Prompt CI never builds or runs a pull request's code: the runner is the trusted checkout's build and the pull
  request contributes only `prompts/` and `presets/`. Those still steer a worker whose allowed commands can run
  programs (`git grep -O`), so a fork's pull request or any preset change runs only after the owner has read the
  diff (`--reviewed`), and a symbolic link among them stops the run. GitHub holds no model, gateway, tracing or
  tailnet key; evals use their own OpenCode server and a spend-capped gateway key limited to the small model.

- Preset `default` 0.3.0 denies reading `*.env` / `*.env.*` and `rg --no-ignore` / `rg -u`. The session ruleset's
  leading allow had overridden the OpenCode agent's own ask-before-reading-`.env` rules, so a worker could read
  secret files without asking.

## [0.1.0-alpha] - unreleased

### Added

- Repository bootstrap: license, contribution guide, security policy, CI with secret scanning,
  Conventional Commits check, ADR template.
- JSON Schemas `task-spec/v1`, `result/v1` and `preset/v1` with fixtures and schema tests.
- Architecture decision records 0000–0016.
- Generated OpenCode API map and a contract test against the checked-in spec.
- Solution skeleton: CLI entry point, intake, `IWorkerRuntime` port, OpenCode adapter interface,
  contract validator, evidence resolver, prompt registry, telemetry and run log interfaces.
