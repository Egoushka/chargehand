# 0019. Prompt CI and the routing report

- Status: accepted
- Date: 2026-09-27

## Context

decisions.md (Prompt CI row): start with about 20 real tasks per preset and node kind, paired runs of base and change,
a quality tolerance, a cost tolerance, and an "intended trade" override declared in the pull request. Routing stays
static per preset and node kind until a cell has enough runs; suggestions are reviewed by hand, and models never
switch within a node. The repository is public. Evals need the loopback OpenCode server (ADR 0003), a gateway key and
the eval items, which are real, private tasks. GitHub-hosted runners reach none of them; a self-hosted runner would run
fork code on the owner's machine.

## Evidence

- Runners (checked 2026-09-26). promptfoo 0.123.1 is Node and DeepEval 4.2.4 is Python: either needs a sidecar, which
  ADR 0002 allows only for TypeScript and only when forced. Langfuse's experiment runner exists only in its Python and
  JS SDKs, but datasets, dataset run items and scores are plain public REST, which this repository already calls for
  prompts. The quality evaluators of Microsoft.Extensions.AI.Evaluation 10.10.0 call a chat client directly, against
  the rule that every model call goes through OpenCode. What the gate compares (evidence resolution, cited files, cost
  from the price table) is already computed in process.
- Seeds. The run log held 13 runs over 4 real requests and a smoke run; run records kept intake's goal, not the request
  text or repository. The phase 4 benchmark's three requests, their pinned commit and both arms' answers (split and
  plain session) survived in the session's working files, and the phase 3 reference question in the owner's handoff.
- Statistics. With 8 paired items a one-sided test at 0.05 survives one discordant item (sign test, 7 of 8: p = 0.035);
  below 5 items no result reaches 0.05. With per-item noise of about 0.25, 8 items detect a quality drop of 0.3 with
  power near 0.9 and a drop of 0.15 with about 0.45; about 20 items reach 0.8 for 0.15.
- Trust boundary (checked 2026-09-27, git 2.54). `git grep -O<cmd>` runs `<cmd>`, and
  `git log --format=tformat:<text> --output=<path>` writes `<text>` to `<path>`; `cheap` allows `git grep*` and
  `git log*`. A prompt from a pull request is therefore an instruction to a worker that can run programs. A grep for
  permission lines misses a rule written with quoted YAML keys, which YamlDotNet reads like any other. Since then
  no preset gives workers a shell (ADR 0006), but a preset change can restore one, so `--reviewed` stays.
- Calibration (A/A: the same prompts and presets as base and change, small model, 2026-09-27). `cheap/worker`, 13
  items: quality change -0.033 (t -1.45; 2 items differ, by 0.25 and 0.18), cost change +15% (t 1.99), so with
  C = +15% identical prompts blocked on cost. Its per-item cost ratios run from 0.73 to 1.56 (log ratio SD 0.24): a
  worker's exploration varies from run to run. Rerun with C = +30%: quality -0.076 (t -1.08), cost -15% (t -1.17),
  pass. One item carries that quality change: the phase 3 reference question runs close to `cheap`'s 400k-token node
  budget and was interrupted in one arm (0.92 against 0.00); an earlier, unfinished rerun lost its other arm to the
  gateway's token-rate limit. That noise is above T/2, the reopen condition below. `intake`, 19 items: +0.000
  (t 0.00); 2 items flip between arms and 2 fail in both. `draft/draft`, 8 items: +0.000 (t 0.00), cost +2% (t 0.41).
- Completeness (replayed 2026-09-27 on the logged `cheap/worker` runs: per item up to 8 runs under the base prompts,
  from both arms of the A/A runs and the base arms of #5 and #6, and one run under each regression). Under #5 the worker
  kept citing the same code: 1.53 ranges per claim against 1.48, 38% wider on average (19.6 lines against 14.2), while
  it made 26% fewer claims (4.4 against 5.9 per item, the phase 3 reference question left out); on 5 of 12 items it made
  fewer claims than any run under the base prompts. Against the mean of those runs, with the phase 3 reference question
  left out, recall of the reference answers' line ranges (lines cited by more than half of them; a range counts when a
  cited range overlaps it) moved +0.001, the same recall with one claim per range -0.086, and the claims kept over their
  median claim count -0.179. Resampling each item's base arm from its logged runs, with the change arm fixed at its one
  run (so the rates below are upper estimates for #5 and #6), the gate blocks #5 in 0% of reruns with range recall as a
  third part of the score, in 1% with the claim ratio as a third part (the mean falls by a third of the part's drop,
  below T), and in 78% with grounding times the claim ratio; A/A pairs block by chance in 0.3%, 0.1% and 2.2%. Under
  that last score the logged #5 pairing itself still passes (-0.039, t -0.57): its base arm lost the phase 3 reference
  question, which fails in 4 of its 8 runs under the base prompts. #6 kept its claim count (5.75 against 5.94) and
  blocks in at most 7% under any of these scores.
- Rerun of #5 with grounding times the claim ratio (2026-09-27, 13 items): quality 0.88 → 0.68, -0.199 (t -2.81),
  cost -14% (t -1.51): blocked. The 8 items whose answers fell below their reference claim count lost 0.20 to 0.55;
  scored by grounding alone, the same runs give -0.028 (t -1.38). A/A under the same score, 13 items: -0.042
  (t -0.50), cost -23% (t -1.43), pass, below T/2, so T stays 0.10. The phase 3 reference question failed in one arm
  again (1.00 against 0.00), -0.077 on the mean by itself; an item whose reference is 3 claims moves by 0.33 per
  claim. Scored by grounding alone, the same A/A runs give -0.072 (t -0.93). An earlier A/A, cut off after 8 items:
  -0.027 (t -0.32).
- Set (2026-09-27). The phase 3 reference question failed in 4 of its 8 runs under the base prompts, and one flip
  moved an A/A's mean by up to 0.08 on its own, so it left `cheap/worker`. The same runs without it, 12 items: #5's
  rerun -0.187 (t -2.47), block; the A/A +0.038 (t 1.16), cost -16% (t -1.03), pass. The replay without it blocks #5
  in 86% of reruns and A/A pairs in 1.5%.
- #6 (2026-09-27, its logged change runs against the 86 base-prompt runs, 12 items). The worker did not follow "open
  at most one file": model calls +1%, prompt tokens +3%, claims unchanged. 96% of its cited ranges were lines it had
  read or found with a search in its session (base runs: 100%, one #6 run 50%). It cited 17% fewer files, and file
  recall fell 0.074, below T even if file recall were the whole score. No deterministic part was added for it; a
  check that cited lines were seen in the session is the next step if a worker does answer from memory.
- Re-pin (2026-09-27). Since workers must not run in a checkout holding files the preset denies reading (ADR 0006),
  the A/A on the old pin failed all 24 arms at $0: the items' repository tracks encrypted env files in every stack, not
  only under an archive directory, so no commit of it passes. The items now pin a local eval commit of that checkout
  that deletes those files and nothing else; every reference file is byte-identical, so line ranges hold. The A/A
  there, also the first without a worker shell, 12 items: quality -0.046 (t -0.63), cost +13% (t 1.82), pass, $0.11;
  5 items differ, by 0.75, 0.27, 0.25, 0.20 and 0.02. Claims per item 6.25 against 5.97 in the runs that seeded
  `reference_claims`; 20 of 24 arms fall within those runs' range, 3 above it and 1 below (a single claim with an open
  question, the 0.75). `reference_claims` stays; the item with both arms above its range is re-seeded if the next A/A
  repeats it.

## Options

1. Runner: promptfoo, DeepEval, Langfuse's experiment runner, Microsoft.Extensions.AI.Evaluation, or a small in-repo
   runner.
2. Trigger: manual dispatch or a protected environment on GitHub (the runner cannot reach the loopback server and would
   need the gateway, the tailnet and the private items as secrets), a self-hosted runner (runs untrusted code on the
   owner's machine; the brief forbids it), or a local run that posts a commit status.
3. Score: an LLM judge (reliability on these tasks not researched), or deterministic checks.
4. A worker's completeness: recall of the reference answers' line ranges (any overlap, or one claim per range), or
   the claim count against theirs, as a third part of the score or scaling grounding.

## Decision

- **Runner:** in repository, `chargehand eval` (`src/Chargehand/Evals`). Cells are public in `evals/cells.json`: name,
  Langfuse dataset, kind (`worker`, `draft`, `intake`), preset, the files a change to which the cell gates, T, C, and a
  minimum of 8 items. Items live only in the orchestrator's Langfuse datasets; `evals/example.jsonl` shows their format
  against this repository. `eval seed` proposes items from runs in the log (one per request, one per split subtask);
  the owner reviews them; `eval push` uploads them.
- **Cells now.** `cheap/worker`, 12 items: the 3 phase 4 questions and their 9 subtasks; the phase 3 reference
  question left it on 2026-09-27 and stays archived in the dataset (Evidence). Its items pin a checkout without the
  files the preset denies reading (Evidence). `intake`, 19: those requests labelled with the action intake should
  choose, 5 synthetic stop cases, the content engine's draft. `draft/draft`, 8: the content engine's call and 7
  variants built from this repository's public changelog, one of them baiting a fact the inputs lack. `core/worker.md`
  is evaluated on `cheap` only; the `default`, `thorough` and `strict` blocks have no cell, and a change to them fails
  unless the owner passes `--allow-uncovered`.
- **Scores**, 0 to 1, no model call. Worker: grounding, the mean of the share of claims whose evidence resolved and
  the recall of reference files (files cited by at least 3 of the 4 benchmark answers), times completeness, the
  claims kept over the item's `reference_claims` (the median over its runs under the base prompts; `eval seed`
  proposes the seeding run's count), at most 1; an item without `reference_claims` scores grounding alone. Grounding
  saturates, because the worker node enforces it at run time, and neither it nor the ranges an answer cites show a
  thinner answer (Evidence). Draft: the mean of a draft within bounds,
  resolved claims, required inputs cited, and no banned phrase. Intake: the expected action. Cost: `result/v1` usage
  (intake reports none).
- **Gate** per cell: each item runs under base and change back to back, the order alternating. A change is blocked
  when mean quality falls by more than T and a one-sided paired t-test is significant at 0.05, or when cost rises by
  more than C with the same test on log cost ratios. T = 0.10; C = +15%, except `cheap/worker` at +30%, because its
  A/A cost noise alone reached +15% (Evidence). A line
  `prompt-ci: trade quality>=-0.15 cost<=-25%` in the pull request body replaces T and C with its bounds, which the
  measured means must meet. A cell new in the change records its first baseline and passes; an intake item whose
  preset is new runs under the change alone and is not paired. An arm that fails on a gateway or provider rate limit
  runs again after 15, 30 and 60 s; one still limited after that stops the gate (status `error`), because a 0 for a
  run that never reached the model would move the verdict (PR #9's run lost one item to the gateway's
  parallel-request limit). A worker or draft arm that fails with no usage at all (no tokens, $0) was refused before
  any model call and stops the gate the same way: on 2026-09-27 an A/A of `cheap/worker` refused every arm on the
  checkout, scored 0 against 0 and passed.
- **Where:** `scripts/prompt-ci.sh <pr>` on the owner's machine. The runner is the trusted checkout's build; the pull
  request contributes only `prompts/` and `presets/`, taken with `git archive`, and a symbolic link among them stops
  the run. A fork's pull request, or any change under `presets/` (permissions, agent, model, budget), runs only after
  the owner has read that diff and passes `--reviewed`. The verdict becomes the commit status `prompt-ci`, whose text
  carries numbers only. A ruleset on `main` requires `prompt-ci`; `.github/workflows/prompt-ci.yml` holds no secret
  and sets it only for same-repository pull requests that change no prompt or preset.
- **Key** (extends ADR 0016): a gateway virtual key for evals alone, with its own spend cap, limited to the small model,
  two parallel requests and a token rate below the organisation's, read by a second OpenCode server (its own port,
  state directory and run log) that runs only for evals. GitHub holds no model, gateway, tracing or tailnet key.
- **When:** before merging a pull request that touches `prompts/` or `presets/`; an A/A calibration when a set changes
  or the model or OpenCode pin moves.
- **Routing:** `chargehand routes` prints, per preset, node kind and model: runs, completions, score, prompt tokens,
  cache rate, cost and latency. It names a model a candidate only when it and the default each have 20 scored runs,
  it scores within 0.10 of the default and costs at least 15% less. Nothing routes automatically. `chargehand score`
  records hand scores, such as blind ones.

## Consequences

Each gated pull request costs one local eval (about $0.14 for `cheap/worker`, $0.02 for the others) and needs the
owner's machine; a fork's pull request always waits for it. 12 items catch large regressions only, and `cheap/worker`
lets a cost rise below +30% through; start records keep every request since ADR 0018, so the sets grow from real runs
toward 20, and C can tighten with them. The phase 3 reference question, which flipped between about 0.9 and 0 at
`cheap`'s node budget and dominated `cheap/worker`'s quality noise, can return to the set when its budget changes.

The exit test (2026-09-27, changelog: phase 5 exit) showed the score's blind spot: two deliberate `preset/cheap`
regressions passed, because the score measured grounding, which the worker node already enforces at run time. Recall of
the reference answers' line ranges, the planned fix, would have passed #5 too: its thinner answers cite the same code in
fewer, wider ranges. Scaled by the claim count, the score blocked #5 on its rerun; by the replay it catches a thinning
of that size in at most about 86% of reruns at the 12 items, not every time. It does not see #6, which kept its claim
count. A prompt that merges facts into fewer claims on purpose
reads as a thinner answer and declares a trade.

## Reopen if

A cell reaches 20 items (revisit T and the minimum); an A/A run's noise exceeds T/2; an LLM judge proves reliable on
these tasks; evals move to a server, where a protected environment with a required reviewer can hold the key; worker
presets stop allowing commands that run programs or write files, so a fork's prompt-only change could run unreviewed.
