# Cycle B: measure, then cut

- Status: proposed; the maintainer reviews this spec before any task starts
- Date: 2026-10-28
- ADR: [0087](../adr/0087-replay-benchmark-and-calibrated-judges.md)
- Runs after: [Cycle A](2026-10-28-cycle-a-conversations-and-acp-design.md). Its files (conversations, the ACP library
  and runtime, the clients guide) are not touched here.
- Builds on: [ADR 0052](../adr/0052-quality-benchmark-on-public-repositories.md) and
  [0080](../adr/0080-bare-arm-of-the-benchmark.md) (`chargehand bench`), [0019](../adr/0019-prompt-ci-and-routing-report.md)
  and [0025](../adr/0025-prompt-ci-on-a-self-hosted-runner.md) (Prompt CI), [0036](../adr/0036-support-checking-and-signed-results.md)
  (the support judge's calibration bar), [0013](../adr/0013-middleware-policy.md) (no direct provider calls)

## Goal

Settle agent-configuration choices with numbers instead of taste, and cut the cost of chargehand's own offline model
calls only where a measurement says there is something to cut.

Done when:

1. `chargehand bench` replays 20 merged pull requests as tasks and produces one table, per configuration (runtime,
   model, MCP set, preset): pass rate, a human-fix-needed proxy, dollars per solved task, wall time and its 95th
   percentile. The table sits beside the baseline from real work.
2. A measured dollar figure per Prompt CI run says how much of it is judge calls, and, if that share is worth acting on,
   the judge calls run through the Batch API and the prompt cache with the same verdicts on a fixed set.
3. Each judge has 100 or more hand labels, a Cohen's kappa against them, and a recorded baseline. Prompt CI fails when a
   judge-model or judge-prompt change drops kappa.

## Where it stands

Evidence: `main` at 9e78f495; the maintainer's run data of 2026-09-27 to 2026-10-22; pricing page fetched 2026-10-28.

1. **Measurement stops at prompts.** Prompt CI scores a prompt or preset change against its merge base on up to 20 items
   per cell (`evals/cells.json`). Nothing compares whole configurations (runtime, model, MCP set) on the same work.
   Choices such as a code-intelligence MCP server (Serena, a Roslyn server), prompt optimisation (GEPA), model routing, ACP
   runtimes and gVisor for sandboxes cannot be settled today.
2. **The baseline is real work, not a benchmark.** 55 driven tasks produced 32 pull requests; 26 were merged; 11 of the 26
   (42%) needed a human fix after chargehand's draft. On chargehand's own repository it was 1 of 10 (reported by the
   desk; whether that counts merged-with-edits the same way is UNKNOWN until Task 4 recomputes it).
3. **A benchmark harness exists.** `chargehand bench run|report` (`src/Chargehand.Automation/Bench/`) runs a fixed task
   set through driven batches, judges each branch with an acceptance script the session never sees, and reports pass
   rate with Wilson intervals, honest stops, spend per pass and a paired comparison of two result files. It has a `bare`
   arm (one plain headless Claude Code session). Its shipped set is 13 synthetic goals on four public repositories; "no
   real run has been published" ([benchmark guide](../guide/benchmark.md)). It has no arm for another runtime, a model,
   an MCP set or a preset, no p95, and no task made from a merged pull request.
4. **Judges are calibrated once, by hand, on 30 claims.** `scripts/support-eval.sh` measures `SupportJudge` against
   `evals/support-examples.jsonl` (30 claims, one labeller) and ADR 0036 sets 80% agreement as the bar. `FactJudge` has no
   labelled set. Agreement is not chance-corrected, so a judge that says "supported" to everything scores well on a skewed set.
5. **Every offline judge call is a single synchronous call.** `FactJudge`, `SupportJudge` (used by `ClaimScorer`,
   `SupportEval` and every result's support check) and the intake generator call `IWorkerRuntime.GenerateAsync`
   (`src/Chargehand/Runtime/IWorkerRuntime.cs:41`). On Claude Code that is `claude -p` with no tools; on OpenCode it is
   one OpenCode call. ADR 0013 states that the orchestrator never calls a provider, the masking proxy or the gateway
   itself, only OpenCode. So the Batch API cannot be reached today without a decision.
6. **Prices, checked on the pricing page on 2026-10-28.** Batch processing takes 50% off input and output tokens, and
   the page says caching multipliers stack with it. A 1-hour cache write costs 2x the base input price; a cache read costs
   0.1x (0.05x on Opus 5.5 and Sonnet 5.5). Haiku 5.5 costs $0.10 / $0.50 per million input / output tokens
   ($0.05 / $0.25 in a batch). Batches finish "within 1 hour" for most and expire after 24; the docs advise the 1-hour
   cache for batches with shared context. A request inside a batch cannot pre-warm a cache (`max_tokens: 0`).
   LiteLLM's batch documentation lists OpenAI-format batches for OpenAI, Azure, Vertex, Bedrock, Mistral, vLLM and xAI,
   not Anthropic, and ties batch cost tracking to an enterprise licence. Whether the maintainer's gateway can pass
   `/v1/messages/batches` through is UNKNOWN.
7. **Harbor** (docs.harborframework.com) runs "any agent with any model on any task in any sandbox". A task is a directory
   with `instruction.md`, `task.toml`, an `environment/` (a Dockerfile) and `tests/test.sh` that writes a reward to
   `/logs/verifier/reward.txt`. It has integrations for Claude Code, Codex and OpenCode that take MCP servers and a model
   (`-m`), ACP registry agents, local Docker, Podman and Apple container runtimes, and per-trial reward, tokens, cost and
   timing. It does not describe checking out a repository at a commit in the pages read (UNKNOWN), its licence (UNKNOWN),
   a custom agent's size (not read), or Apple Silicon support for `apple-container`. It is a Python tool.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Harbor or our own | **Extend `chargehand bench`; do not adopt Harbor as the runner.** Reasons: (a) chargehand's presets, verifier, governor and driven sessions are among the things being measured, and in Harbor they are not an agent but a whole server that would have to be wrapped as a custom agent; (b) `bench` already has hidden acceptance checks, Wilson intervals, a paired comparison, caps and the `bare` arm, and Harbor would be a second report format and a Python sidecar (ADR 0019 reads ADR 0002 as allowing sidecars only when forced); (c) the dimension Harbor adds, other agents on the same task, is covered by Cycle A's runtimes. **Kept open:** the replay tasks are written so each also exports to Harbor's layout (`instruction.md`, `environment/Dockerfile`, `tests/test.sh`), and Task 2 runs a bounded Harbor cross-check on 5 tasks (Claude Code and OpenCode, same model). If the two runners agree on pass or fail for at least 4 of 5 tasks and Harbor shows a cost or reach we lack, the ADR is reopened. |
| 2 | What a replay task is | The pull request's parent commit; the goal is the pull request title and body (and the linked issue text), with test file paths removed from it; the oracle is the merged diff's test files, withheld from the session and applied after it, run in a fresh container with the repository's test command. A task is valid only if its oracle **fails at the parent and passes at the merged commit** (checked by `bench replay --baseline`, no model). The reference diff is never shown to the session. |
| 3 | Where task data lives | Real tasks are private. The task set file, the oracle files and results live in the gitignored `runs/bench/replay/` and in a Langfuse dataset of the orchestrator's own project, as `evals/` rules require. The repository holds the importer, the task-set schema extension, a **synthetic** replay set against this repository, and the selection script with criteria and counts, never repository names or pull request numbers. |
| 4 | Picking the 20 | Candidates are merged pull requests of the maintainer's repositories in the baseline window and after, filtered to: a code change with test changes in the diff; not a dependency bump, doc-only, config-only or generated change; the oracle valid by decision 2; a description that states behaviour. Choose by stratified draw with a recorded seed: at least 4 repositories and 3 ecosystems the session images support (.NET, Node, Python today); at most 5 per repository; sizes by added plus removed lines excluding generated files, about 8 small (under 50), 8 medium (50 to 300) and 4 large (over 300); both agent-written and human-written pull requests. Pull requests merged after the models' training cutoffs are preferred (Sonnet 5.5's is June 2026), so that they cannot be in training data; the count that are is reported. Candidates dropped for an invalid oracle are counted in the report. |
| 5 | Underspecified tasks | A merged pull request's tests may test a name or behaviour its description never states, and then no configuration can pass fairly. After the first full run, a task that no configuration passes is read by the maintainer, then kept, fixed (description edited, noted) or dropped. The report lists how many were dropped. |
| 6 | Configuration axes | A new file kind, `bench-arms/v1`, lists arms: `{id, runtime, model, preset, mcp, container_runtime}`. `runtime` is `claude_code` (a driven session; `bare` is the existing arm), `opencode` and `acp:<agent>` (Cycle A, when it has merged and a writing mode exists: UNKNOWN). `mcp` names servers from the profile's `mcp_servers`. `container_runtime` names the sandbox's OCI runtime (`runc`, `runsc`). The existing `--arm chargehand\|bare` stays as shorthand for two entries. The OpenCode arm may have to run through the `code` preset instead of a driven session, because driven sessions are Claude Code (ADR 0039): Task 3 decides, and an arm that cannot be built is listed as not built, not faked. |
| 7 | Output | `results.json` (`bench-results/v1`) gains additive members per task and arm: oracle result, wall seconds, the proxy below and the arm's id. The report adds per arm: pass rate (Wilson interval), the proxy rate, dollars per solved task (spend over passes, at the profile's price table, as today), median and 95th percentile wall time, and, for any two arms, an exact sign test on the tasks where exactly one passes. |
| 8 | Human-fix-needed proxy | A task needs a human fix when its branch fails the oracle, **or** passes the oracle but fails the repository's full check (build, lint, tests), **or** edits a test file that exists at the parent. Scope drift (files changed outside the directories the merged diff touched) is reported beside it, not inside it. The proxy is validated in Task 4: it is computed on the 26 merged baseline pull requests' own chargehand drafts, and its agreement with the recorded human outcome ("merged unchanged" against "merged after edits") is reported. If agreement is under 75% the proxy is labelled unreliable in every table. |
| 9 | What 20 tasks can show | With 20 paired tasks, two configurations differ at the 5% level (exact sign test, two-sided) only if the winner takes every discordant task from 6 of 6 up, or 8 of 9, or 9 of 10 (5 of 5 gives p = 0.0625 and 7 of 8 gives p = 0.07: not enough). Pass-rate intervals at 20 tasks span about 40 points. The report says "no difference shown" when the test does not clear, never "equal". Top candidates repeat 3 times to separate a real gap from run noise. The cycle's claims are limited to what this allows. |
| 10 | Cost cap | A run refuses to start without `--max-usd` and `--max-tokens`, as today. Planned: 20 tasks, 6 arms, 1 run each is 120 sessions, plus 2 repeats of the 2 leading arms (80). At the existing reference of $0.17 to about $2 a task the total is $35 to $400: UNKNOWN until Task 3 measures one full arm. The cycle's cap is set by the maintainer after that measurement; the worker stops after the first arm and reports the projection. |
| 11 | Batch and cache: measure first | Task 5 reports what share of a Prompt CI run's dollars are judge calls (stateless, one prompt, no tools) and what share are worker sessions (multi-turn agent loops, which the Batch API cannot run). The batch work (Tasks 6 and 7) starts only if the judge share is at least 20%; below that, the finding is the result and the cycle goes on with Part 3. |
| 12 | How a batch is reached | A new port in the evals code, `IBatchGenerate.GenerateManyAsync(model, prompts)`, with two implementations: the default loops over `GenerateAsync` (today's behaviour), and an Anthropic Message Batches client. The batch client takes its base URL, key item name and model id from the profile (`eval.batch`), never from code. It goes through the same gateway and masking route as other calls if the gateway can pass `/v1/messages/batches` (Task 6 finds out). It never calls `api.anthropic.com` directly unless the profile names that URL and says the items may leave unmasked. ADR 0087 amends ADR 0013 for this one case. Prompt CI waits for a batch with a deadline (10 minutes by default) and falls back to synchronous calls for what is not done, so a slow batch cannot block a pull request for 24 hours. Calibration, `bench` claim scoring and `eval support` have no deadline and use the batch whenever it is configured. |
| 13 | Cache | The 1-hour cache write (2x) pays back after two reads. A judge's static prefix is small (instructions of about 150 tokens; the minimum cacheable prompt is 286 tokens on Haiku 5.5), so the cache saves little unless the prefix grows. Task 7 measures it as is, and notes the option of adding labelled examples to a judge prompt as a separate change that Part 3's gate must pass. Whether `claude -p` and OpenCode set a 1-hour cache is UNKNOWN. |
| 14 | Judge calibration | Two judges: `FactJudge` (per fact: stated or not; per wrong statement: repeated or not), `SupportJudge` (supported, partial, unsupported). `ClaimScorer` uses `SupportJudge`; the intake classifier is out of scope. 100 labels per judge is the floor for gating (50 is reported but never gates: at 50 labels, kappa's standard error is about 0.1). Kappa is Cohen's, unweighted, with a bootstrap 95% interval, plus per-class agreement. The gate fails when kappa is below `max(0.60, baseline − 0.10)`. 0.60 is the usual "substantial" boundary, and it is a starting value the maintainer may change after seeing the first numbers. |
| 15 | Labelling flow | The labeller is the maintainer only. `chargehand judge label <judge>` is a terminal loop over the existing CLI: it shows one item (the claim and its cited text, or the answer and the numbered facts), **hides the judge's verdict**, reads one key, and appends `{item, label, at}` to the gitignored `runs/labels/<judge>.jsonl`; `--resume` skips labelled items; a label can be changed with `--redo <n>`. Items are a random sample from the offline stores (run log, bench results), with a second, separate **hard set** (items where two judge models disagree), which is reported on its own and kept out of kappa so that it does not skew it. At about 20 seconds an item, 100 labels per judge take 35 minutes: an assumption. Labels are also pushed to a Langfuse dataset by the existing `LangfuseEvals`. No web screen, no database, no inbox card: the console's inbox (ADR 0085) can show "label 20 more" later, and is not needed. |
| 16 | Where the gate runs | `scripts/prompt-ci.sh` gains a first step, `chargehand judge check`: it hashes the configured judge model reference and each judge prompt (`FactJudge.Prompt`, `SupportJudge.Prompt`) and compares them with `evals/judge-baseline.json` (hashes, label count, kappa and interval: numbers only, no ids or aliases). A match passes at once. A mismatch re-judges the labelled items with the new judge (100 to 200 calls on a small model, cents, through the batch lane if present) and fails the status with the kappa and the baseline when the threshold above is not met. A pull request edits the baseline only to record a new, passing kappa. The runner builds `main`, so a pull request that edits a judge prompt in code is gated after merge by this check on the next run and before merge only with `--trusted-build`: the ADR records this limit. |
| 17 | Not in this cycle | Running the later uses (a code-intelligence MCP A/B, GEPA, gVisor against `runc`). The arms file expresses them, and a test shows that it does. A public leaderboard; a new store; a front end; a digest; an own agent loop; a held-out test set rotated on a schedule; judging with more than one model (a panel). |

## Tasks (each one pull request)

Work in progress limit: 2 tasks open at a time. Order: 1, then 2 and 5 in parallel, then 3, then 4. Tasks 6 and 7 follow
5's finding. Task 8 can start after 1 and runs in parallel with the rest, but needs the maintainer's time.

1. **ADR 0087 and this spec**, with the roadmap line (the desk).
2. **Replay tasks**: `bench replay import|baseline` (decisions 2 to 4): the importer reads merged pull requests through the
   forge, writes the task-set and oracle files to `runs/bench/replay/`, `--baseline` checks fail-at-parent and
   pass-at-merge; the selection script; the synthetic set; the Harbor-layout export and the 5-task Harbor cross-check.
3. **Arms**: `bench-arms/v1`, `--arms <file>`, the OpenCode arm decision, and the first full run of one arm to measure its
   cost (decisions 6 and 10).
4. **The table**: the additive result members, p95, the proxy, the sign test, `bench report` for N arms (decisions 7 to 9),
   the proxy's validation on the baseline, and the first real table.
5. **Prompt CI cost, measured**: a script that reads the gateway's spend log and the run log for each of the last 10 Prompt
   CI runs and splits judge from worker dollars (decision 11). Findings are written to the pull request.
6. **The batch lane** (decision 12), if Task 5 says so: `IBatchGenerate`, the Batches client, the gateway pass-through
   check, the deadline and fallback. Amends ADR 0013 by 0087.
7. **Cache and the before/after**: 1-hour cache for the batch prefix, the A/A noise floor, and the measured dollars of a
   Prompt CI run before and after on the fixed set (decision 13).
8. **Judges calibrated**: `judge label|report|check`, the synthetic fixture, the baseline file, the Prompt CI step
   (decisions 14 to 16). The maintainer then labels; a follow-up pull request records the first baseline.

## Acceptance checks

These run in `scripts/check.sh` unless marked live. A live check runs on the maintainer's machine, and its output goes
in the pull request with ids scrubbed.

**Part 1**
- `ReplayImportTests`: on a synthetic repository with a merged pull request, the importer writes a task whose goal has no
  test paths, whose oracle files are absent from the goal, and whose parent is the merge's first parent.
- `ReplayOracleTests` (`--baseline`, no model, in containers): a valid task fails its oracle at the parent and passes at
  the merged commit; a task whose oracle already passes at the parent is rejected with a named reason.
- `ArmsSchemaTests`: `bench-arms/v1` accepts runtime, model, preset, an MCP set and `container_runtime: runsc` and rejects
  an unknown member; `--arm bare` and `--arm chargehand` still give the files they gave. `bench-results/v1` changes are
  additive and `SchemaCompatTests` stays green.
- `BenchReportTests`: on a fixture of 3 arms and 20 tasks, p95, dollars per solved task, the proxy rate and the sign
  test equal hand-computed values (7 of 8 gives p = 0.0703; 8 of 8 gives p = 0.0078).
- `SelectionTests`: with a fixed seed and a synthetic candidate list, the draw is the same on two runs and meets
  decision 4 (repositories, ecosystems, size bands, cap per repository).
- **Live, selection:** the draw on the maintainer's candidates gives 20 tasks and the counts in decision 4; every oracle is
  valid (`--baseline`). The count after the training cutoff is reported.
- **Live, cross-check:** 5 tasks on Claude Code and OpenCode in `bench` and in Harbor; the outcomes are compared (decision 1).
- **Live, the table:** at least 4 arms over the 20 tasks produce one table that includes the baseline row (55 tasks, 32 pull
  requests, 26 merged, 11 needing a fix) and the proxy's agreement with the recorded outcomes of the 26. Total spend is
  under the cap the maintainer set after Task 3.

**Part 2**
- `PromptCiCostReportTests`: a fixture spend log and run log give the judge and worker dollars of a run, matching a
  hand-computed split.
- `BatchLaneTests` (fake Batches endpoint): requests carry the profile's base URL and model, never a constant; a
  result maps back by `custom_id`; an `expired` or `errored` request is retried synchronously; the deadline falls back for
  what is unfinished; with no `eval.batch` the default loop is used and no network call to a batch URL is made.
- `ArchitectureTests` stays green: the lane is a new project and no new code references an old `src/Chargehand*` project.
- **Live, pass-through:** one batch of 3 prompts sent through the configured route; the result says whether the gateway
  passes it, and the spend log shows it priced at the batch rate.
- **Live, before and after:** (a) an A/A run of the fixed set (20 paired items, answers stored, judges re-run) twice
  without batching gives the noise floor of verdict disagreement; (b) the same set with the lane gives the gate verdict
  identical on every cell, and per-fact agreement with (a) within the noise floor, never below 95%; (c) dollars per Prompt
  CI run are reported before and after.

**Part 3**
- `KappaTests`: Cohen's kappa equals hand-computed values on three fixtures (perfect agreement 1.0; chance-level 0.0; a
  skewed set where agreement is 90% and kappa is 0.0 or near it), and the bootstrap interval is the same for a fixed seed.
- `JudgeLabelTests`: the loop never prints the judge's verdict; `--resume` skips labelled items; the file is append-only.
- `JudgeCheckTests` (fake judge): unchanged hashes pass without a model call; a changed model that drops kappa from 0.80
  to 0.65 fails (baseline − 0.10 is 0.70); a drop to 0.72 passes; a baseline with fewer than 100 labels reports and never
  fails.
- `evals/judge-labels.example.jsonl` is synthetic and `scripts/check-private-terms.sh` stays green.
- **Live:** 100 labels per judge exist; `judge report` prints kappa, its interval and per-class agreement; the baseline
  file is committed by the follow-up pull request.

## Risks

- **Twenty tasks is a small instrument.** Decision 9 limits the claims. More tasks cost linearly; the draw and the importer
  make that a re-run, not a rebuild.
- **The oracle can reject correct work or pass wrong work.** Another implementation may name things differently, so a fair
  attempt fails; and a thin test passes a wrong one. Decision 5 handles the first by reading the unpassable tasks; the
  second is the known weakness of test oracles ([ADR 0052](../adr/0052-quality-benchmark-on-public-repositories.md)), and the
  proxy's full-check clause is a partial guard.
- **Selection bias.** Tasks come from merged pull requests, so the ones nobody could finish are missing. The report says it.
- **Private data leaves the machine.** Task goals, diffs and answers go to model providers, as any run does, and to a
  batch endpoint in Part 2. Decision 12 keeps the masking route and a profile switch for the direct case. Whether the
  masking proxy sees `/v1/messages/batches` is UNKNOWN.
- **Judge share may be small.** At Haiku 5.5's price, judge calls may be a few cents of a run that is mostly worker
  sessions. That is a legitimate result of Task 5, and the batch work is then not done.
- **Hardware.** An M1 Mac with 16 GB runs about two driven sessions at once (admission by free memory), so a 120-session
  run takes hours. gVisor does not run on macOS, so a `runsc` comparison is a VPS-only measurement and its wall times are not
  comparable with the Mac's. Models are remote, so no GPU is needed.
- **Kappa on small samples.** At 100 labels the interval is about ±0.1, so the gate's 0.10 margin can still trip on noise.
  The report prints the interval; a failing gate can be re-run with more labels, and the margin is a profile value.
- **Labelling is one person's time.** 100 labels per judge is the minimum, and a single labeller's biases are in the
  baseline. The gate protects against drift from that labeller, not against a labeller's error.
- **Cycle A not merged.** The `acp:` runtimes and the Cycle A guide are inputs. Without them the arms are Claude Code and
  OpenCode only.

## Out of scope

Decision 17, and: changing any prompt, preset or judge to improve a score (this cycle measures and gates; improving comes
after, with the instrument in place); a release (releases stay held until the cycle ends).
