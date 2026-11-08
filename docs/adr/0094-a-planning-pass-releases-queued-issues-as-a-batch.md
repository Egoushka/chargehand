# 0094. A planning pass releases queued issues as a batch

- Status: proposed
- Date: 2026-11-08

## Context

Intake starts a run for each labelled issue as soon as the governor (ADR 0084) lets it. The governor looks at counts and state (review backlog,
breakers, memory), never at what the issues say. Evidence from the first bulk run (2026-11-07, issue 756; replay by `scripts/planner-replay.py`
over the 8 pull requests that run opened, read-only, no model calls, cost 0):

- **Same code, parallel runs.** Issues 429, 447, 448 and 506 all changed `src/Chargehand/Orchestrator.cs`; 447, 429 and 448 are the intake and result
  cluster the owner already knew about. 737 (the overlap hold itself) was never held by anything.
- **Failed checks nobody needed to run to see.** All 8 pull requests failed the `title` check: the pull request title is the issue title, which is not
  a Conventional Commit (issue 528, open). One also failed `ci-gate` and `web`.
- **Wasted shape.** Pull request 768 came out `[partial]` with two scratch files (`PublicApiShapeBefore.cs`, `PublicApiShapeAfter.cs`). The issue was
  broad; the scratch files are only visible after the run.
- **Invisible to `gh`.** The exit-126 chronicle run and two stalled runs left no pull request, so the replay cannot score them. A planner would need the
  run log to be tested on them.

Replay result (heuristics over the issue text and the repository file list, scored per run on five outcomes: title check, other failed check,
partial, scratch files, conflict with another run): **33 of 40 outcomes right (82%)**; without the title check, which is predictable from the title
alone and so inflates the number, **25 of 32 (78%)**. Caught 11, false alarms 2, missed 5. Clusters 429/447/448 were found from shared words and
named files (all three really overlap). Missed: 737 (names no file), 506 (hub file), the `ci-gate` failure of 447, 768's partial and scratch files.
Caveats: n = 8, not the 20 the issue asked for (only 8 runs left a pull request); the scratch pattern was written after seeing 768, so that hit is
fitted, not predicted; every run ran on one day against one tree.

## Prior art

Not searched. The question is about this repository's own queue (ADR 0084), and the comparable tools (merge queues, Renovate's grouping) order
pull requests that already exist, not issues before work.

## Options

1. **Planning pass over a quiet-window queue (pick).** The label adds the issue to the existing `Waiting` list with reason `planning`. When the queue has
   been quiet for a window (default 3 minutes) or holds N issues (default 8), one read-only pass reads the queue, the open pull requests and recent
   runs, and writes `plan/v1`: per issue a verdict (`ready`, `needs-spec`, `owner-only`, `epic`, `duplicate`, `delivered`), size, likely files and a
   cluster; per plan the order and the clusters. A cluster runs in sequence in one batch; `ready` issues go to the governor; the rest get one comment.
   Up to 3 `triage` suggestions per pass, never a `chargehand` label. A `chargehand-now` label skips planning. Pros: uses the queue that exists,
   catches overlaps and no-spec issues before money is spent. Cons: adds a delay and a pass that costs money.
2. **Deterministic planner only (no model).** The heuristics of the replay script, run in intake. Pros: free, instant, testable. Cons: 78% on the
   replay, blind to issues that name no file, cannot judge a spec or size. Good as the first stage of option 1, not as the whole.
3. **Fix the causes, plan nothing.** Pull request titles follow the repository convention (528), pre-push guard against scratch files (430), overlap
   hold plus `git merge-tree` before push (737, running as pull request 765). Pros: each is a bounded change and removes a failure for every run, not
   only batched ones. Cons: none of them stops two runs from building the same thing or picks an order; the title and scratch causes are not planner
   work at all and should be done either way.
4. **Unconventional: plan by dry run.** Start each issue as a cheap, read-only, time-boxed run that must return the files it would touch and a patch
   outline; overlap is computed from real file lists, not guessed from text, and the plan is the set of outlines. Pros: ground truth for overlap and
   size, no heuristic ceiling. Cons: one model run per issue before the real run (the planning cost is at least the dry-run cost times the queue size,
   against the 10% budget below), so likely over budget for small issues.

Ranking: 1 (with 2 as its cheap first stage), then 3 (do it anyway; it is not exclusive), then 4, then 2 alone.

## Decision

Proposed: option 1, built in two stages. Stage A is the deterministic screen of option 2 (title convention, named files and words, duplicate of an
open pull request, closed issue, size words), which also gives the planner a prior. Stage B is the model pass, run only on what stage A leaves
ambiguous. Options 3's three fixes are separate work that this ADR does not wait for.

**What it changes.**

- Intake: a labelled issue is added to `Waiting` with reason `planning` (a new reason after `max_concurrent`, ADR 0084's order is kept, `planning` is
  last). A planning pass is a read-only driven run on the cheap preset; its result is `plan/v1` in the run log, not a new store.
- Governor: no new check. The pass only decides which waiting items are released and in what order; each release still goes through backlog, breakers
  and admission. Clusters are released as one batch, so the overlap hold of 737 becomes a fallback for what the plan missed.
- Console: the plan is visible per issue (verdict and cluster), so a waiting issue shows why it waits.

## Hypothesis and check

Fewer failed, partial or colliding runs per opened pull request. **Check:** on the next 20 issues that run, count wasted runs (any failed required
check other than one a human caused, `[partial]`, or a merge conflict) divided by runs, with planning on against the replay baseline: 8 of 8 here
(all failed the title check; 5 of 8 for causes other than the title: conflict 5, partial 1, other check 1). **Stop** the idea if (a) stage A plus B
verdict accuracy on the 20 issues that ran is under 80%, (b) the planning pass costs more than 10% of the batch it plans, or (c) the wasted-run
rate does not fall by half once options 3's fixes are in (then planning is not what moves it).

## Cost

Stage A: none. Stage B: one read-only run per pass on the cheap preset over the queue text; the estimate to confirm on the first pass is a few
tenths of a dollar for 8 issues, as a starting value, not measured. Delay: the quiet window, 3 minutes by default; `chargehand-now` skips it.
Not built here.

## Consequences

- A new `plan/v1` schema (additive) and a `planning` reason on `/v1/intake`.
- Issues that name no file (like 737) still get through unclustered; the model pass has to read the code to catch them.
- The scratch-file and title failures are not fixed by planning; their own issues remain.

## Reopen if

The replay over the next 20 runs disagrees with this one by more than 10 points; owner waits on the quiet window more than it saves; or run-log
data on the exit-126 and stalled runs shows planning would not have caught them either.
