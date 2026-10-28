# 0087. A replay benchmark, calibrated judges, and a batch lane only if measured

- Status: proposed
- Date: 2026-10-28
- Spec: [Cycle B](../specs/2026-10-28-cycle-b-measure-then-cut-design.md)
- Amends: [0013](0013-middleware-policy.md) for one case (decision 3)

## Context

Prompt CI (ADR 0019) scores a change to a prompt or preset. Nothing scores a whole agent configuration (runtime, model,
MCP set, preset) on work like the maintainer's own, so choices that depend on that, such as a code-intelligence MCP server,
prompt optimisation, model routing, ACP runtimes and sandbox runtimes, are settled by taste.

What exists: `chargehand bench` (ADR 0052, 0080) runs 13 synthetic tasks on four public repositories through driven batches
with hidden acceptance checks, reports Wilson intervals, and compares two result files. What real work shows: 55 driven
tasks from 2026-09-27 to 2026-10-22 became 32 pull requests, 26 merged, and 11 of the 26 (42%) needed a human fix.

The judges that decide Prompt CI and every support check are checked by agreement on 30 claims from one labeller (ADR 0036).
Agreement is not corrected for chance, and `FactJudge` has no labels.

Offline model calls are single synchronous calls through `IWorkerRuntime.GenerateAsync`. Anthropic's pricing page
(2026-10-28) gives the Batch API 50% off, stacking with caching, and a cache read costs 0.1x the input price. Haiku 5.5
already costs $0.10 / $0.50 per million tokens, and workers in Prompt CI are multi-turn agent sessions that batches cannot run,
so the saving is bounded by the judges' share of the cost, which has not been measured. ADR 0013 says the orchestrator
never calls a provider directly. Harbor (a Terminal-Bench harness) is a candidate runner.

## Options

1. **Adopt Harbor as the runner.** Pre-integrated Claude Code, Codex and OpenCode, many sandboxes, a task format that
   other people know. A Python tool beside a .NET one, a second report, and chargehand's own presets and verifier would
   have to be wrapped as an agent. Its handling of a repository at a commit is UNKNOWN from its docs.
2. **Extend `chargehand bench`** with replay tasks from merged pull requests and a file of arms. Reuses hidden checks,
   caps, intervals, the paired comparison and driven sessions. Gains no other agents for free, which Cycle A's runtimes supply.
3. **A new harness.** Duplicates option 2.

For judges: agreement only (cheap, skews on imbalanced labels), or Cohen's kappa against 100 or more labels with a floor and
a margin from a recorded baseline.

For batches: always batch offline calls; never; or measure the judges' share of cost first and build only above a stated
threshold.

## Decision

1. **Option 2.** Task data for replays is private and stays in gitignored `runs/` and a Langfuse dataset. The
   repository holds code, a synthetic set and the selection criteria. Each replay task also exports to Harbor's layout, and
   a 5-task cross-check against Harbor decides whether to reopen this.
2. **Judges are gated by kappa.** At least 100 hand labels per judge, an unweighted Cohen's kappa with a bootstrap
   interval, and a check in Prompt CI that fails when the judge model or prompt changed and kappa falls below
   `max(0.60, baseline − 0.10)`. The baseline file holds hashes and numbers only. Because the runner builds `main`, a judge
   prompt changed in a pull request is gated after merge, or before it with `--trusted-build`.
3. **Batch only if measured.** Task 5 reports the judges' share of a Prompt CI run's dollars; the batch lane is built only
   when that share is at least 20%. The lane is an interface with a default that loops over today's calls, and a Batches
   client whose URL, key item and model come from the profile. It goes through the same masking and gateway route as other
   calls when the gateway can pass `/v1/messages/batches`; a direct call to the provider needs the profile to say so and say
   the items may leave unmasked. This amends ADR 0013 only for offline, stateless judge calls, never for workers. Prompt CI
   waits for a batch up to a deadline, then calls synchronously for what is not done.
4. **Claims stay within the instrument.** Twenty paired tasks support a "differs" only on an exact sign test; the report
   says "no difference shown" otherwise.

## Consequences

- The first table is a baseline for later comparisons. The comparisons that motivated it (a code-intelligence MCP server,
  GEPA, model routing, gVisor) are cheap to run afterwards, because they are rows in an arms file.
- A judge-model change costs about 100 to 200 small calls and can fail the build, which is the point.
- If the gateway cannot pass batches, the lane waits for a gateway change or is dropped; the cache work is then all there is
  to do for Part 2.
- Hand labels are one person's judgement. The gate detects drift from it, not error in it.
- Replay tasks come from pull requests that were merged, so work nobody finished is not in the set.

## Reopen if

- The Harbor cross-check disagrees with `bench` on fewer than 4 of 5 tasks and Harbor shows a clear gain.
- The judge share of a Prompt CI run is under 20% (drop the lane), or the gateway passes batches natively (simplify it).
- A labeller other than the maintainer exists, or kappa between two labellers on 50 shared items is under 0.6 (the labels
  are not a stable reference).
- 20 tasks prove too few: a comparison the maintainer cares about is never decided by the sign test.
