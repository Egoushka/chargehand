---
title: "Prompt CI"
description: "How prompt and preset changes are gated by paired evals: the cells, the eval commands, the verdict and its tolerances, and what the gate has missed."
order: 6
section: "Concepts"
---

Prompt CI runs a pull request's prompts and presets against its base on real tasks, in pairs, and blocks the change when quality falls or cost rises beyond what noise explains. You meet it when you change a file under `prompts/` or `presets/`.

## What it gates

A pull request that changes `prompts/` or `presets/` gets the commit status `prompt-ci`, and a ruleset on `main` requires it. A pull request that changes neither gets a passing `prompt-ci` at once ([ADR 0019](../adr/0019-prompt-ci-and-routing-report.md)).

Changed files map to the eval cells in [evals/cells.json](../../evals/cells.json). On main:

| cell | preset | files it gates | T | C |
|---|---|---|---|---|
| `cheap/worker` | `cheap` | `prompts/core/worker.md`, `prompts/preset/cheap.md`, `presets/cheap.yaml` | 0.10 | +30% |
| `review/worker` | `review` | `prompts/preset/review.md`, `presets/review.yaml` | 0.10 | +30% |
| `draft/draft` | `draft` | `prompts/core/draft.md`, `prompts/preset/draft.md`, `presets/draft.yaml` | 0.10 | +15% |
| `intake` | none | `prompts/intake/task-spec.md` | 0.10 | +15% |

Every cell needs at least 8 items. A changed prompt or preset file that no cell gates fails the gate unless the owner passes `--allow-uncovered`; the prompt blocks and preset files of `default`, `thorough` and `strict` have no cell. The items are real tasks in the orchestrator's Langfuse datasets. [evals/example.jsonl](../../evals/example.jsonl) shows their format with questions about this repository. The `review/worker` cell is on main, not yet released, and [docs/benchmarks.md](../benchmarks.md) records no calibration for it.

## Where it runs

[prompt-ci.yml](../../.github/workflows/prompt-ci.yml) runs on `pull_request_target`, so main's copy of the workflow runs, also for a fork. It hands the gate to a self-hosted runner labelled `chargehand-eval`. The runner builds main and takes only `prompts/` and `presets/` from the pull request, at the commit the run was approved for; a symbolic link among them stops the run.

A fork's pull request, or any change under `presets/`, first waits for the owner's approval in the `prompt-ci-review` environment, because a preset can give a worker tools. The runner holds one eval profile per runtime and a default. A `prompt-ci:<runtime>` label picks another runtime, and it counts only when someone with write access added it. A push, an edit of the pull request body or a `prompt-ci:` label reruns the gate ([ADR 0025](../adr/0025-prompt-ci-on-a-self-hosted-runner.md)).

By hand, from the main checkout:

```bash
scripts/prompt-ci.sh <pr-number> [--trusted-build] [--reviewed] [--allow-uncovered] [--no-status]
```

`--reviewed` says you read the `prompts/` and `presets/` diff, which a fork or a preset change needs. `--trusted-build` builds the runner from the pull request itself, so use it only for your own branches. `--no-status` prints the verdict without posting it. The eval profile comes from `CHARGEHAND_PROFILE`, default `profiles/local.eval.json`.

## The eval commands

| command | what it does |
|---|---|
| `eval seed <cell> <run-id>...` | proposes items (JSONL on stdout) from runs in the log, one per request and one per split subtask, for you to review; a worker item gets the seeding run's claim count as `reference_claims` |
| `eval push <cell>` | reads reviewed items (JSONL on stdin) and pushes them to the cell's Langfuse dataset |
| `eval gate <base> <change> [--cells a,b] [--changed-files f] [--pr-body f] [--name n] [--cells-file f] [--allow-uncovered]` | runs the affected cells' items under the `prompts/` and `presets/` in `<base>` and in `<change>`, prints a verdict per cell, and exits 1 when blocked |

All three need the profile's `telemetry` settings, because the items live in Langfuse. The gate reads the cells and their tolerances from the trusted checkout, never from the change under test, and evals run without memory.

## The verdict

Each item runs under base and change back to back, in alternating order. Scores run from 0 to 1 and need no model call:

- **worker**: grounding (the mean of the share of claims whose evidence resolved and the recall of the item's reference files) times completeness (claims kept over the item's `reference_claims`, at most 1). An item without `reference_claims` scores grounding alone.
- **draft**: the mean of four checks: the draft stays within bounds, its claims resolve, it cites the required inputs, it uses no banned phrase.
- **intake**: whether intake chose the expected action.
- **cost**: the `usage` in `result/v1`. Intake reports none, so the `intake` cell compares no cost, and a pair with an unpriced arm drops out of the cost change.

The gate blocks a cell when fewer items ran than the cell's minimum, when mean quality falls by more than T and a one-sided paired t-test is significant at 0.05, or when cost rises by more than C with the same test on log cost ratios. Noise alone does not block, and a real change inside the tolerances passes.

A line in the pull request body such as `prompt-ci: trade quality>=-0.15 cost<=-25%` declares a trade. Its bounds replace T and C, and the measured means must meet them.

An arm that fails on a rate limit runs again after 15, 30 and 60 seconds; if the limit persists, the gate stops with status `error`. A worker or draft arm that fails with no usage at all was refused before any model call, and it stops the gate the same way. A new runtime or model gets an A/A run per cell on the runner before its verdicts count; until then they are advisory.

## Why C is +30% for cheap/worker

An A/A run uses the same prompts and presets as base and change, so any block it produces is noise. The calibration on the small model, 2026-09-27 ([benchmarks](../benchmarks.md#prompt-ci-calibration-phase-5)):

| cell | items | quality change (t) | cost change (t) | verdict |
|---|---|---|---|---|
| `cheap/worker`, C +15% | 13 | -0.033 (-1.45) | +15% (1.99) | block, on cost |
| `cheap/worker`, C +30% | 13 | -0.076 (-1.08) | -15% (-1.17) | pass |
| `cheap/worker`, completeness score | 13 | -0.042 (-0.50) | -23% (-1.43) | pass |
| the same runs without the phase 3 reference question | 12 | +0.038 (1.16) | -16% (-1.03) | pass |
| `intake` | 19 | +0.000 (0.00) | not priced | pass |
| `draft/draft` | 8 | +0.000 (0.00) | +2% (0.41) | pass |

A worker explores differently from run to run. `cheap/worker`'s per-item cost ratios ran from 0.73 to 1.56 (log ratio SD 0.24), so identical prompts crossed C = +15% by chance and blocked. Its C is now +30%. The next A/A, after its items moved to a commit without the files the preset denies reading and the first since workers lost the shell, gave 12 items: quality -0.046 (t -0.63), cost +13% (t 1.82), pass ([changelog 0.2.0](../../CHANGELOG.md#020---2026-09-28)).

The wider tolerance has a price: `cheap/worker` lets a cost rise below +30% through, and 12 items catch large regressions only ([ADR 0019](../adr/0019-prompt-ci-and-routing-report.md)).

## What it has missed

At the phase 5 exit, two deliberate regressions of `preset/cheap` went through the gate on the `cheap/worker` cell with 13 items ([benchmarks](../benchmarks.md#prompt-ci-blocks-a-real-prompt-regression-met-on-a-rerun)):

| pull request | change | mean quality, base → change | quality change (t) | cost change (t) | verdict |
|---|---|---|---|---|---|
| #5 | stop at the first file that answers, at most 3 claims | 0.87 → 0.97 | +0.097 (1.51) | -27% (-2.87) | pass |
| #5, rerun | the same, scored with completeness | 0.88 → 0.68 | -0.199 (-2.81) | -14% (-1.51) | block |
| #6 | answer from what the worker knows, open at most one file | 0.91 → 0.85 | -0.064 (-1.42) | -2% (-0.59) | pass |

Both passed the first gate. The first score measured grounding, which the worker node already enforces at run time through the resolver and an evidence repair turn, so the worker kept reading and citing files under both regressions. #5 cut the claim count, which file-level recall cannot see. The score now multiplies grounding by completeness, and #5 blocks on the rerun. #6 kept its claim count, so the new score does not catch it either. By a replay of logged runs, the gate catches a thinning of #5's size in at most about 86% of reruns at 12 items ([ADR 0019](../adr/0019-prompt-ci-and-routing-report.md)).

## When you change a prompt or preset

- Expect a paired eval for every cell your change touches. If you trade quality for cost on purpose, declare the trade in the pull request body.
- A change to the prompt block or preset file of `default`, `thorough` or `strict` has no cell; the gate fails unless the owner passes `--allow-uncovered`.
- A change under `presets/`, or a pull request from a fork, waits for the owner's approval before any eval runs.
- A prompt that merges facts into fewer claims on purpose reads as a thinner answer; declare it as a trade.
- A pass means the gate saw no drop beyond its tolerances on its items. It did not see #6.
