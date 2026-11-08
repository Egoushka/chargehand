# Claim-drift watch: flag docs whose claims no longer match the code

- Status: proposed. First PR: this spec, the checker (`ClaimDrift` in `Chargehand.Modules.Watch`) and its acceptance tests.
  Running it on merge and opening issues are later tasks, below.
- Date: 2026-11-08
- Builds on: [ADR 0068](../adr/0068-scheduled-work-within-a-monthly-cap.md) (the weekly `doc-drift` schedule),
  [ADR 0074](../adr/0074-provenance-trailers-on-commits.md) (provenance trailers), the [events and watches spec](2026-11-02-events-and-watches-design.md)

## Goal

Each claim in `docs/guide` that cites code or a test is re-checked on every merge, and a claim whose citation is broken
opens an issue naming the doc line. The weekly `doc-drift` schedule stays: a model reads the docs for statements the code
contradicts. That costs a run and happens once a week. The claim-drift watch is the cheap part that runs on every merge:
it checks citations without a model.

Done when: a test with a seeded stale claim makes the watch report it, with the doc file and line.

## What counts as a claim

A Markdown link in a file under `docs/guide` whose target resolves, relative to the doc, to a path under `src/`,
`tests/` or `scripts/`. Example: `[EvidenceResolverTests](../../tests/Chargehand.Tests/EvidenceResolverTests.cs)`.
A `#L10` or `#L10-L12` anchor cites lines. Links in fenced code blocks, images, absolute URLs and links to other docs or
ADRs are not claims.

## When a claim is broken

1. The cited file does not exist.
2. A line anchor runs past the end of the file.
3. The link text is a symbol (an identifier, maybe dotted: `ServerTests`, `ContainerTemplate`) that the file does not
   contain. A label that is the file's own name (`mcp-smoke.py`, `Dockerfile`) is not a symbol.

The check is lexical and runs without a model. It catches a renamed or moved file, a deleted test class and a cited range
that shrank. It does not catch a claim whose file and symbol still exist but whose behaviour changed. The weekly
`doc-drift` run and the support check (ADR 0036) cover that.

## Acceptance checks (machine-run)

All checks are in `tests/Chargehand.Modules.Watch.Tests/ClaimDriftTests.cs` and run in `dotnet test`:

| Check | Test |
|---|---|
| Claims that match the code report nothing | `Claims_that_match_the_code_report_nothing` |
| A seeded stale claim (a deleted test file) is reported with its doc file and line | `A_seeded_stale_claim_is_reported_with_its_doc_line` |
| A symbol the cited file no longer contains is reported | `A_symbol_the_file_no_longer_contains_is_reported` |
| A line anchor past the end of the file is reported | `A_line_anchor_past_the_end_of_the_file_is_reported` |
| Links to docs, URLs and code-fence examples are not claims | `Links_outside_code_and_tests_and_inside_code_fences_are_not_claims` |
| The issue text names the doc line, the cited file and the reason | `The_issue_for_a_broken_claim_names_the_line` |
| This repository's own `docs/guide` has no stale claims | `The_guide_of_this_repository_has_no_stale_claims` |

The last check runs in `scripts/check.sh`, so a pull request that breaks a guide citation fails before merge. Its first run
found one stale claim: `capabilities.md` cited `tests/Chargehand.Tests/RetainableClaimsTests.cs`, but the file had moved to
`tests/Chargehand.Modules.Memory.Tests/`. This PR fixes that citation.

## Later tasks

1. **On merge.** The scheduler runs `ClaimDrift.Scan` on the commit `origin/HEAD` points at after each fetch, for the
   repositories that have a `doc-drift` entry. It runs with no model call and no charge against the monthly cap.
   Once the event log of ADR 0090 carries forge push events, a watch that wakes on a push to the default branch replaces the fetch.
2. **Open an issue.** For each finding not already open, `ClaimDrift.Issue` gives the title and body. The forge
   credential the schedule already reads with opens the issue. The title (`docs: stale claim at <doc>:<line>`) is the
   key that stops the same issue from opening twice. This is an external action, so it is listed in the action registry
   (events and watches spec, decision on reversibility).
3. **Wider scope.** Run the check on `README.md` and the rest of `docs/` once the guide stays clean.
