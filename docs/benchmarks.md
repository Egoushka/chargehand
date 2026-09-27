# Benchmarks and exit checks

Measurements taken at each roadmap phase exit. The [changelog](../CHANGELOG.md) says what changed; this file says how
it performed. Repositories behind the benchmarks are private, so each entry names the setup, not the code.

## Phase 5 exit (0.1.0)

### Content engine's call through the interface: met

`samples/ContentEngineCall` stands in for the content engine, which has no repository yet. Built on
`Chargehand.Contracts` alone, it sent one draft request to `chargehand serve` (`POST /v1/runs`, 2026-09-26). The run
completed for $0.0006 with 4 claims, each citing an input the caller sent. The orchestrator verified the draft's
sha256, and the caller's generator block appeared in `prompt_chain`.

### Prompt CI blocks a real prompt regression: met on a rerun

With a ruleset requiring `prompt-ci` on `main`, a pull request that changes a prompt stays blocked until the owner's
run posts the status. Both deliberate regressions passed the first gate. #5 blocks on a rerun with a worker score that
also measures completeness.

| pull request | change | cell (items) | mean quality, base → change | quality change (t) | cost change (t) | T | C | verdict |
|---|---|---|---|---|---|---|---|---|
| #5 | `preset/cheap` 0.2.0: stop at the first file that answers, at most 3 claims | `cheap/worker` (13) | 0.87 → 0.97 | +0.097 (1.51) | -27% (-2.87) | 0.10 | +30% | pass |
| #5, rerun | the same, scored with completeness (ADR 0019) | `cheap/worker` (13) | 0.88 → 0.68 | -0.199 (-2.81) | -14% (-1.51) | 0.10 | +30% | block |
| #6 | `preset/cheap` 0.2.0: answer from what the worker knows, open at most one file | `cheap/worker` (13) | 0.91 → 0.85 | -0.064 (-1.42) | -2% (-0.59) | 0.10 | +30% | pass |
| #4 | intake prompt 0.2.0 → 0.3.0 (the v2 pull request itself) | `intake` (18) | 0.89 → 0.89 | +0.000 (0.00) | not priced | 0.10 | +15% | pass |

**Why the first score missed #5.** The first score measured grounding: the share of claims whose evidence resolves,
plus file-level recall of reference files. The worker node already enforces grounding at run time through the
resolver and an evidence repair turn. So the worker kept reading and citing files under both regressions, and under
two local probes that told it to cite paths without lines or to answer from memory (4 of 4 runs at quality 1.00).

#5 cut the claim count (13 to 9, 5 to 3), and file-level recall cannot see that. Recall of the reference answers' line
ranges, the planned next step, misses it too: replayed on the logged runs, #5's answers cite the same code in fewer,
wider ranges. The worker score now multiplies grounding by completeness, the claims kept over the item's reference
claim count (ADR 0019). Scored both ways, the rerun gives grounding alone -0.028 (t -1.38) and with completeness
-0.199 (t -2.81).

#6 kept its claim count, so the new score does not catch it either.

## Prompt CI calibration (phase 5)

A/A runs use the same prompts and presets as base and change, on the small model, 2026-09-27. Each item ran under both
arms back to back, alternating order. Quality runs from 0 to 1. Intake calls report no usage, so the gate compares
no cost for intake.

| cell | items | quality change (t) | cost change (t) | items that differ | verdict |
|---|---|---|---|---|---|
| `cheap/worker`, C +15% | 13 | -0.033 (-1.45) | +15% (1.99) | 2, by 0.25 and 0.18 | block, on cost |
| `cheap/worker`, C +30% | 13 | -0.076 (-1.08) | -15% (-1.17) | 3, by 0.92, 0.12 and 0.05 | pass |
| `cheap/worker`, completeness score | 13 | -0.042 (-0.50) | -23% (-1.43) | 5, by 1.00, 0.33, 0.17, 0.11 and 0.06 | pass |
| the same runs without the phase 3 reference question | 12 | +0.038 (1.16) | -16% (-1.03) | 4, by 0.33, 0.17, 0.11 and 0.06 | pass |
| `intake` | 19 | +0.000 (0.00) | not priced | 2 flip; 2 fail in both | pass |
| `draft/draft` | 8 | +0.000 (0.00) | +2% (0.41) | none | pass |

**Cost noise sets C.** A worker explores differently from run to run. `cheap/worker`'s per-item cost ratios ran from
0.73 to 1.56 (log ratio SD 0.24), so identical prompts crossed C = +15% by chance and blocked. Its C is now +30%.

**One item carried the quality noise.** In the rerun, the phase 3 reference question hit `cheap`'s 400k-token node
budget in one arm (0.92 against 0.00) and accounts for most of the quality change. Under the completeness score it
failed in one arm again (1.00 against 0.00, -0.077 on the mean by itself). An item whose reference holds 3 claims
moves by 0.33 per claim. The question has since left `cheap/worker`.

**Cost per run.** `cheap/worker` $0.0010–0.0213 (mean $0.0054, about $0.14 per A/A); `draft/draft` $0.0004–0.0007.

## Phase 4 exit: split against a plain session

Three breadth-first read-only questions, each spanning 3 independent areas, on a private repository at a pinned
commit. v1 ran the `cheap` preset; intake chose `split` in 6 of 6 runs, 3 nodes each. The baseline was a plain single
OpenCode session with the same model, build agent and read-only ruleset. 2 repetitions, alternating order, same day
and OpenCode build, small model for both arms. Cost comes from OpenCode's token counts priced with the profile's
table. Intake (stateless generate, no usage reported) is excluded from v1.

| | v1 split | plain session |
|---|---|---|
| cost per run (mean of 6) | $0.00754 | $0.00492 |
| cost ratio per task (t1, t2, t3) | 1.62×, 1.51×, 1.63× | 1× |
| wall time (mean) | 123 s | 73 s |
| cited file:line that resolve | 99/99 | 112/112 |
| blind score, (correct + complete) / 10 (mean of 6) | 0.967 | 0.950 |
| blind score per task (t1, t2, t3) | 1.00, 0.90, 1.00 | 1.00, 0.95, 0.90 |
| quality per dollar (score / cost) | 128 | 193 |

Forked siblings read the first node's prefix from cache (4,878 of ~5.2k tokens on their first call). The extra cost
comes from the nodes' own exploration, not the session base. On cost alone, a split needs about 1.5× the plain
answer's quality to win on quality per dollar. At the owner's request a model judge scored each answer 1–5 on correct
and complete, checking claims against the repository, and saw the arms only after every pair was scored. The split came out at 1.02× the plain answer's quality, so it does not pay
on these questions (ADR 0017).

**Cache report check.** A local, uncommitted change moved the subtask brief into a per-node instruction entry.
Siblings could no longer fork and wrote their prefix again (0 read), and `chargehand cache` named the entry.

## Phase 3 exit: one worker against a plain session

A reference read-only question on a private repository at a pinned commit. v0 against a plain single OpenCode
session: same day, model, OpenCode build and commit, 3 pairs in alternating order. Cost from the gateway's spend log.

| | v0 | plain session |
|---|---|---|
| cost per run (mean of 3) | $0.199 (+ ~$0.0004 intake) | $0.205 |
| wall time (mean) | 155 s | 122 s |
| cited file:line that resolve | 50/50 | 75/75 |
| blind A/B: correct, complete, cites resolve (3 pairs) | tie 3, lost 3, tie 3 | tie 3, won 3, tie 3 |
| blind score (win 1, tie 0.5, loss 0; mean of 3) | 0.333 | 0.667 |

At the owner's request a model judge compared the answers blind, checking claims against the repository, and saw the
arms only after every pair was scored. The quality half fails: v0 won or tied on both correct and complete in 0 of 3
pairs, against a bar of 2 of 3.

### Rerun on worker prompt 0.3.0 (2026-09-27)

Diagnosis of the failure: both arms explored alike (calls, cache reads and output tokens within about 10%), intake
kept the question whole, and v0's open questions were real unknowns. v0 read files it then left out of its answer and
merged several stacks into one claim; its prompts capped the summary at 120 words and asked for "the smallest set of
files that answers the task". Worker prompt 0.3.0 and the preset blocks (#19) ask for the whole task, one claim per
item, and every file the task needs.

The same question on the same repository at a commit that drops only its encrypted env files (workers now refuse a
checkout that tracks them), same model, 3 pairs in alternating order. Cost from the profile's price table for both
arms (the same prices); intake reports no usage.

| | v0 (0.1.0 + #19) | plain session |
|---|---|---|
| cost per run (mean of 3) | $0.257 | $0.201 |
| cost ratio per pair | 1.48×, 1.19×, 1.19× | 1× |
| worker calls per run | 13, 11, 11 | 9, 10, 7 |
| wall time (mean) | 150 s | 118 s |
| claims per run | 16, 17, 17 (6–9 before) | |
| blind A/B: correct, complete, cites resolve (3 pairs) | tie 3, won 2 lost 1, tie 3 | tie 3, won 1 lost 2, tie 3 |
| blind score (win 1, tie 0.5, loss 0; mean of 3) | 0.556 | 0.444 |
| blind 1–5, correct / complete (mean of 3) | 5.0 / 4.7 | 5.0 / 4.0 |

Same model judge and procedure. No answer had a false claim and every sampled citation resolved; completeness
decided each pair, v0 missing stacks in one and the plain session in two. The quality half now passes (2 of 3 against
a bar of 2 of 3), and the cost half fails: v0 costs 28% more because it reads more (pair 1: 351k cached input tokens
against 222k), with no repair turns. Quality per dollar is about even (0.556 / $0.257 against 0.444 / $0.201). The
phase 3 exit still does not hold.

### Ablation: the preset's read scope (2026-09-27)

#19 changed two things: the worker prompt's completeness rules (one claim per item, cite every file relied on) and
the preset's read scope ("read every file the task needs"). The completeness failure was about files read and then
left out, so the rules alone should fix it; the read scope looked like the source of the extra cost. Here the preset
says "read the files that answer the task" and the worker prompt is unchanged. Same question, commit, model and
procedure, with a fresh plain arm.

| | v0 (preset "read the files that answer the task") | plain session |
|---|---|---|
| cost per run (mean of 3) | $0.223 | $0.222 |
| cost ratio per pair | 1.19×, 0.85×, 1.02× | 1× |
| worker calls per run | 8, 7, 10 | 7, 8, 7 |
| claims per run | 17, 21, 17 | |
| blind A/B: correct, complete, cites resolve (3 pairs) | tie 3, won 3, tie 2 lost 1 | tie 3, lost 3, tie 2 won 1 |
| blind score (win 1, tie 0.5, loss 0; mean of 3) | 0.611 | 0.389 |
| blind 1–5, correct / complete (mean of 3) | 5.0 / 4.3 | 5.0 / 3.3 |

No false claim on either side; v0 listed more of the direct callers in every pair, and the plain session had the
tighter citation in one. The quality half passes (3 of 3). Cost is the plain session's within noise: the mean is
0.6% higher ($0.0013), and the plain arm alone ranged from $0.189 to $0.254 across the three pairs. Against the
exit's letter ("costs no more") that is a miss by $0.0013 on 3 pairs.
