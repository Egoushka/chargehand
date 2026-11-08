# 0097. Earned autonomy: rights grow with a clean record and shrink on reverts

- Status: accepted
- Date: 2026-11-08

## Context

Issue #791. A repository's rights are fixed in the profile: its size classes have flat budgets (ADR 0045) and its merge mode is whatever
`driven.integrator.repositories` says (ADR 0046). The learning records already say how its work went: each task's stop reason
(`checks_failed` when its checks still failed after their retries) and, read back from the forge, whether its pull request was merged
with the head chargehand left (`merged_unchanged`), merged after edits, closed or still open (ADR 0045, the learning loop). Nothing reads
that record to widen or narrow what chargehand may do there.

## Prior art

No prior-art query was run: the design is the issue's own sentence.

- Coverage: miss (not queried)

## Options

1. **Keep the profile as the only source.** Simple, but a repository with fifty clean merges is treated like a new one, and one with a
   revert last week keeps its rights.
2. **A merge rate over a window.** Tolerates a revert among many merges, which is the case the issue wants to act on.
3. **A clean streak.** Count pull requests merged unchanged since the last revert or failed check; a loss resets it to 0.

## Decision

Option 3, per repository (the learning record's `repository` key; a preset is not recorded per task yet). `EarnedAutonomy.Assess` reads
the learning entries, pure, and returns an `AutonomyGrant`: the largest size class, whether auto-merge is earned, the streak and what
last reset it.

- **Earn.** A pull request read back as `merged_unchanged` adds 1, at the time its outcome was read.
- **Lose.** A task that ended `checks_failed` (at its end), or a pull request read back as `reverted`, resets the streak to 0.
- **Neutral.** Merged after edits, closed, open, and any other stop reason. Benchmark tasks (`bench`) never count.

The thresholds are profile keys under `driven.governor.autonomy`, with these starting values:

| Key | Starting value | Meaning |
|---|---|---|
| `large_after` | 5 | A streak this long earns the L size class (M until then). |
| `auto_merge_after` | 10 | A streak this long earns auto-merge as well. |
| `window_days` | 90 | Merges, reverts and failed checks older than this are forgotten. |

## Consequences

- The grant is computed and tested; nothing acts on it yet. Wiring it into the integrator's mode and the planner's size cap is the
  follow-up, as is reading a revert back from the forge: `PrOutcomes.Reverted` is a word the store reads, and no tracker writes it yet.
- A repository that has rights loses them all on one revert. That is deliberate for starting values; loosen it only from evidence.

### Acceptance checks

- `EarnedAutonomyTests.Merges_without_change_earn_the_large_class_and_then_auto_merge`
- `EarnedAutonomyTests.The_governor_reads_the_thresholds_from_its_settings`
- `EarnedAutonomyTests.A_revert_takes_back_every_right_and_the_streak_starts_over`
- `EarnedAutonomyTests.A_failed_check_takes_back_every_right`

## Reopen if

A revert-free repository with a long streak still needs a person's edits on most merges (the streak counts the wrong thing); repositories
lose their rights so often that none keeps them a month; presets get recorded per task, so the grant can be per preset too.
