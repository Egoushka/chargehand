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
  permission lines misses a rule written with quoted YAML keys, which YamlDotNet reads like any other.
- Calibration (A/A: the same prompts and presets as base and change, small model, 2026-09-27). `cheap/worker`, 13
  items: quality change -0.033 (t -1.45; 2 items differ, by 0.25 and 0.18), cost change +15% (t 1.99), so with
  C = +15% identical prompts blocked on cost. Its per-item cost ratios run from 0.73 to 1.56 (log ratio SD 0.24): a
  worker's exploration varies from run to run. Rerun with C = +30%: quality -0.076 (t -1.08), cost -15% (t -1.17),
  pass. One item carries that quality change: the phase 3 reference question runs close to `cheap`'s 400k-token node
  budget and was interrupted in one arm (0.92 against 0.00); an earlier, unfinished rerun lost its other arm to the
  gateway's token-rate limit. That noise is above T/2, the reopen condition below. `intake`, 19 items: +0.000
  (t 0.00); 2 items flip between arms and 2 fail in both. `draft/draft`, 8 items: +0.000 (t 0.00), cost +2% (t 0.41).

## Options

1. Runner: promptfoo, DeepEval, Langfuse's experiment runner, Microsoft.Extensions.AI.Evaluation, or a small in-repo
   runner.
2. Trigger: manual dispatch or a protected environment on GitHub (the runner cannot reach the loopback server and would
   need the gateway, the tailnet and the private items as secrets), a self-hosted runner (runs untrusted code on the
   owner's machine; the brief forbids it), or a local run that posts a commit status.
3. Score: an LLM judge (reliability on these tasks not researched), or deterministic checks.

## Decision

- **Runner:** in repository, `chargehand eval` (`src/Chargehand/Evals`). Cells are public in `evals/cells.json`: name,
  Langfuse dataset, kind (`worker`, `draft`, `intake`), preset, the files a change to which the cell gates, T, C, and a
  minimum of 8 items. Items live only in the orchestrator's Langfuse datasets; `evals/example.jsonl` shows their format
  against this repository. `eval seed` proposes items from runs in the log (one per request, one per split subtask);
  the owner reviews them; `eval push` uploads them.
- **Cells now.** `cheap/worker`, 13 items: the 3 phase 4 questions, their 9 subtasks, the phase 3 reference question.
  `intake`, 19: those requests labelled with the action intake should choose, 5 synthetic stop cases, the content
  engine's draft. `draft/draft`, 8: the content engine's call and 7 variants built from this repository's public
  changelog, one of them baiting a fact the inputs lack. `core/worker.md` is evaluated on `cheap` only; the
  `default`, `thorough` and `strict` blocks have no cell, and a change to them fails unless the owner passes
  `--allow-uncovered`.
- **Scores**, 0 to 1, no model call. Worker: the mean of the share of claims whose evidence resolved and the recall of
  reference files (files cited by at least 3 of the 4 benchmark answers). Draft: the mean of a draft within bounds,
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
  parallel-request limit).
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
owner's machine; a fork's pull request always waits for it. 13 items catch large regressions only, and `cheap/worker`
lets a cost rise below +30% through; start records keep every request since ADR 0018, so the sets grow from real runs
toward 20, and C can tighten with them. The phase 3 reference question flips between about 0.9 and 0 at `cheap`'s node
budget and dominates `cheap/worker`'s quality noise until its budget or its place in the set changes.

The exit test (2026-09-27, changelog: phase 5 exit) showed the score's blind spot: two deliberate `preset/cheap`
regressions passed, because the score measures grounding, which the worker node already enforces at run time, and
file-level recall does not see an answer with fewer claims. Until the worker score measures completeness (recall of
the reference answers' line ranges is the next step), Prompt CI catches regressions that break grounding or cost, not
thinner answers.

## Reopen if

A cell reaches 20 items (revisit T and the minimum); an A/A run's noise exceeds T/2; an LLM judge proves reliable on
these tasks; evals move to a server, where a protected environment with a required reviewer can hold the key; worker
presets stop allowing commands that run programs or write files, so a fork's prompt-only change could run unreviewed.
