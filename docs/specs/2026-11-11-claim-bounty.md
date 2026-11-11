# Claim bounty: a queue of claims for workers to disprove

- Status: proposed. Issue #787 (approved idea, first PR is this spec).
- Date: 2026-11-11
- Builds on: [ADR 0036](../adr/0036-support-checking-and-signed-results.md) (support check), [ADR 0019](../adr/0019-prompt-ci-and-routing-report.md) (Prompt CI, eval cells, routing report)
- Schema rule: `result/v1` and every other published major take additive changes only; this spec changes none.

## Conclusion

Keep a queue of claims taken from finished results and from docs. A worker run in a "disprove" role gets one claim and
the repository at a commit, and either finds evidence the claim is false or reports it stands. A disproof counts only
when `ClaimScorer` resolves its evidence at that commit and the support judge agrees the cited text contradicts the
claim. Each accepted disproof appends one line to a bounty ledger, and `chargehand routes` shows the tally per preset as a
suggestion. Nothing reads the ledger to change a route or an intake rank on its own.

## Problem

`SupportCheck` (`src/Chargehand/Verification/SupportCheck.cs`) judges whether the text a claim cites supports it. It does
not ask whether the claim is true: a claim whose citation is real and on topic passes while the statement is wrong. Docs
carry the same kind of claim with no check at all. A worker told to find a counterexample is a different reader from the
worker that wrote the claim, and today nothing rewards it or records that it did.

## Design

1. **Claim.** A line of `claims.jsonl`: `id` (sha256 of text and source), `text`, `source` (`result` with a run id, or
   `doc` with `path:line`), `commit`, `state` (`open`, `disproved`, `stands`, `stale`). Claims from `result/v1` answers
   are read from `ResultContract.Claims` (field `text`; `evidence` ids resolve in the same result). Doc claims come from
   lines a doc marks with an HTML comment `<!-- claim -->`. The first slice takes result claims only; doc intake is Task 3.
2. **Queue.** `ClaimQueue` in `src/Chargehand/Verification/`, a JSONL file next to the run log, the way
   `JsonlLearningStore` writes `learning.jsonl` (no new store). One open claim is handed out at a time; a handed-out
   claim returns to `open` after a lease expires. A claim whose `commit` is no longer an ancestor of the default branch is
   `stale`.
3. **Disprove run.** A worker preset `disprove` (a file in `prompts/`, same shape as the other presets) receives the
   claim text and the commit and answers with a normal `result/v1` result: one claim `"the claim is false because ..."`
   with at least one evidence item, or an empty `claims` array and an `open_questions` entry `stands`. No new schema
   field: the verdict is read from the existing `claims` and `open_questions`.
4. **Judging.** `ClaimScorer.ScoreAsync` (used by `eval score-claims`) resolves the disproof's `path:line` locators at the
   claim's commit. `SupportJudge` then judges whether the cited text contradicts the original claim. Only a resolved
   locator plus a `Supported` verdict marks the claim `disproved`; an unresolved locator or an `Unchecked` verdict leaves
   it `open` and earns nothing. A disproof of a claim the same preset wrote earns nothing (no self-farming).
5. **Bounty.** `bounty.jsonl`, one line per accepted disproof: `claim_id`, `run_id`, `preset`, `points` (1), `at`. It is
   append-only; a later `stands` run does not remove a point, a human may append a line with `points: -1` to revoke.
6. **Surface.** `chargehand routes` (`src/Chargehand.Cli/Program.cs`, the routing report "suggestions only") adds a
   column `bounty` per preset. `GitHubIntake.PriorityRankOf` ranks by issue label and is not touched.

## Why not

- A model-only verdict: rejected, a disproof must cite a locator that resolves, as `Scoring` already requires of claims.
- A priority that reorders intake automatically: the maintainer holds routing; a point is a suggestion until a
  measured gate (an `eval gate` cell) shows a preset with more points does better.
- A new store or a new `result/v1` field: the JSONL files and existing fields are enough.

## Out of scope

- Doc claim intake and the `<!-- claim -->` marker (Task 3).
- Changing a route, a preset or an intake rank from the ledger.
- Paying points across presets, decay of points, any money.
- Public wording: claims stay "citations checked" in public text until ROADMAP 0.8 ships the support check.

## Tasks

1. `ClaimQueue`, `BountyLedger` and their tests (this spec's checks 1 to 5).
2. `eval bounty` command and the `disprove` preset; the seeded run (check 6).
3. Doc claim intake; the `routes` column.

## Acceptance checks

Task 1 and 2 land only when each check below passes. Test names are the ones the tasks create.

1. `dotnet test --filter "FullyQualifiedName~ClaimBountyTests.Queue_hands_out_one_open_claim_and_expires_the_lease"` passes.
2. `dotnet test --filter "FullyQualifiedName~ClaimBountyTests.Disproof_with_unresolved_locator_earns_nothing"` passes.
3. `dotnet test --filter "FullyQualifiedName~ClaimBountyTests.Disproving_your_own_claim_earns_nothing"` passes.
4. `dotnet test --filter "FullyQualifiedName~ClaimBountyTests.Accepted_disproof_appends_one_bounty_line_for_its_preset"` passes.
5. `dotnet test --filter "FullyQualifiedName~SchemaCompatTests"` passes, and `git diff --stat origin/main -- schemas/` is empty.
6. Seeded run, with the scripted runtime from `tests/Chargehand.Tests/ScriptedRuntime.cs` and no network:
   `dotnet test --filter "FullyQualifiedName~ClaimBountyTests.Seeded_false_claim_is_disproved_and_the_bounty_recorded"`
   seeds `evals/claim-bounty-seed.jsonl` (one claim that is false against this repository at a pinned commit),
   runs the `disprove` preset, and asserts the claim's state is `disproved` and `bounty.jsonl` holds exactly one line with
   that claim id and preset.
7. `dotnet run --project src/Chargehand.Cli -- eval bounty evals/claim-bounty-seed.jsonl --repo . --commit <pinned sha>`
   exits 0 and prints `disproved: 1`; run on a true claim it prints `stands: 1` and writes no bounty line.
8. `scripts/check.sh` exits 0 and its last test line reads `Passed!  - Failed:     0`; `betterleaks git --redact -v`
   finds nothing; the files touched contain no hostname, IP or home path (the commit hooks run the denylist).
