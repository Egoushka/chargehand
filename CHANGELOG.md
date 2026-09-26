# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The contract package
(`Chargehand.Contracts`) is versioned separately, by schema major.

## [Unreleased]

### Added

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

- Preset `default` 0.2.0 allows `rg` (with `--pre` denied); worker prompt 0.2.0 makes the final message the JSON
  block only.

### Security

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
