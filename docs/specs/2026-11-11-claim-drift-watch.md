# Claim-drift watch: flag docs whose claims no longer match the code

- Status: accepted 2026-11-11 (maintainer approved the recommendations below). Issue #785.
- Date: 2026-11-11
- Builds on: [ADR 0068](../adr/0068-scheduled-work-within-a-monthly-cap.md) (schedules), [ADR 0074](../adr/0074-provenance-trailers-on-commits.md)
  (provenance trailers, already shipped), the `doc-drift` template in `ScheduleTemplates`
- Obeys: public repository rules (CLAUDE.md), no reflection-based JSON, files under 400 lines

## Problem

The weekly `doc-drift` schedule (`src/Modules/Chargehand.Modules.Intake/Api/ScheduleTemplates.cs`, started by
`src/Chargehand.Automation/Schedules/Scheduler.cs`) asks a read-only model run to find statements in README.md and `docs/`
that the code contradicts. It has three gaps:

1. It re-reads everything each week and costs a model run, though most claims did not move.
2. Its result is free text posted to `notify_url` (`ScheduleResults.Format`); nobody is told which line to fix.
3. A claim has no stable link to the code it rests on, so a rename in code cannot be tied to the doc line it breaks.

The guide pages in `docs/guide` hold hundreds of such claims (options, defaults, paths, test names). Today none says
where it comes from.

## Design

A **claim** is a line in `docs/guide/*.md` that carries a citation marker, an HTML comment at the end of the line:

```
`at` accepts `daily HH:MM`, `weekly <mon..sun> HH:MM`. <!-- cites: src/Modules/Chargehand.Modules.Intake/Api/ScheduleAt.cs#ScheduleAt -->
Days stop at 28. <!-- cites: tests/Chargehand.Tests/ScheduleAtTests.cs#Monthly_day_29_is_rejected -->
Default cap is 4 runs. <!-- cites-text: src/Modules/Chargehand.Modules.Intake/Api/AutomationSchedules.cs "DefaultMaxPerMonth = 4" -->
```

Three forms, all checkable without a model:

- `cites: <path>#<Symbol>`: the file exists and still declares the symbol (a type, member or test method name as a whole
  word). If the cited file is a test, the test must also exist in the compiled test list (`dotnet test --list-tests`).
- `cites-text: <path> "<literal>"`: the file still contains the literal.
- `cites: <path>`: the file exists.

**Parser.** `ClaimMarkers.Parse(markdown)` in `src/Chargehand.Automation/Schedules/` (pure, no I/O) returns
`Claim(DocFile, Line, Kind, Path, Symbol?, Literal?)`. A marker inside a fenced code block is ignored. A malformed marker
is itself a finding (`malformed`), so a typo does not silently drop a claim.

**Checker.** `ClaimDrift.Check(claims, repositoryRoot)` reads the cited files and returns
`DriftFinding(DocFile, DocLine, Reason, CitedPath)`, where Reason is `file-missing`, `symbol-missing`, `text-missing`,
`test-missing` or `malformed`. It is deterministic and runs in under a second on this repository, so it needs no budget
class and no model.

**Where it runs.**

1. *CLI:* `chargehand claims check [--root <dir>] [--changed-since <ref>] [--json]` in `Chargehand.Cli`; exit 1 on any
   finding, one `docs/guide/x.md:42: symbol-missing src/...#Name` line each. With `--changed-since` it checks only
   claims whose cited file changed in `<ref>..HEAD` (the on-merge mode).
2. *CI on merge:* a job in `.github/workflows` runs `claims check --changed-since` on pushes to `main` and fails with the
   lines above. A pull request that renames a cited symbol therefore shows the doc line it breaks.
3. *Weekly schedule:* a new template `claim-drift` with no model call (the `ScheduleKind.Report` path in
   `Scheduler.StartAsync`: it posts directly). It runs the full check and posts findings to `notify_url`. The existing
   `doc-drift` template stays: it finds claims nobody marked.

**Reporting.** A finding is one issue per doc line, titled `docs: claim at <file>:<line> no longer matches <path>`, and
carries the `docs-drift` label. The issue body ends with the cited path and the commit that last touched it
(`git log -1 --format=%H -- <path>`); where that commit has `Chargehand-Run` trailers (ADR 0074) the body names the run
via `chargehand why <path>:<line>`, so the reader sees which task changed the code. The scheduler keeps the keys of
findings already reported in its state (like `ScheduleState.Taken` for alerts) and opens each once; a fixed claim closes
its issue on the next run.

## Out of scope

- Judging whether prose is *true*: only that a cited thing still exists. Support checking is ADR 0036 and ROADMAP 0.8;
  until it ships, public text says "citations checked", not "claims verified".
- Adding markers to every guide page: the PR that implements this marks `docs/guide/schedules.md` only, as the first
  consumer; other pages follow one at a time.
- Claims in `docs/adr` and `docs/specs`: they record decisions at a date and are meant to age.
- Auto-fixing docs, or a model that proposes the fix.

## Acceptance checks

Each is run by a machine; the implementing PR adds the tests, this PR adds none.

1. **Seeded stale claim is reported** (the done condition of #785):
   `dotnet test --filter "FullyQualifiedName~ClaimDriftTests.Seeded_stale_claim_is_reported"` passes. The test writes a
   temp repository with `docs/guide/a.md` citing `src/A.cs#Gone`, where `A.cs` has no `Gone`, and asserts one finding:
   `a.md`, the claim's line, `symbol-missing`.
2. **A live claim is not reported:** `ClaimDriftTests.Live_claims_of_every_form_pass` (file, symbol, text forms, all present) yields zero findings.
3. **Each reason is reachable:** `ClaimDriftTests.Each_reason_has_a_case` covers `file-missing`, `symbol-missing`,
   `text-missing`, `test-missing` and `malformed`.
4. **Fenced markers are ignored:** `ClaimDriftTests.Marker_in_a_code_fence_is_not_a_claim`.
5. **CLI exit code:** `ClaimsCliTests.Check_exits_1_with_one_line_per_finding_and_0_when_clean` runs the command against the
   seeded temp repository, then against a clean one.
6. **Changed-since narrows:** `ClaimsCliTests.Changed_since_checks_only_claims_whose_file_changed` (temp git repository with two commits).
7. **Schedule posts without a model:** `ClaimDriftScheduleTests.Claim_drift_posts_findings_and_starts_no_run` asserts the notifier
   got the finding text and `IBatchStarter` was not called.
8. **Reported once:** `ClaimDriftScheduleTests.Claim_drift_reports_a_finding_once_across_two_occurrences`.
9. **Template is valid config:** `ScheduleSettingsTests.Claim_drift_template_validates`, and
   `ScheduleTemplates.Names` contains `claim-drift`.
10. **This repository is clean:** `dotnet run --project src/Chargehand.Cli -- claims check --root .` exits 0 (guards the markers
    added to `docs/guide/schedules.md`).
11. **Whole gate:** `scripts/check.sh` exits 0 and its last test line reads `Passed!  - Failed:     0`.
12. **No private text:** the commit-msg and pre-commit denylist hooks pass; no marker names a host, address or home path.

## Decisions (maintainer, 2026-11-11)

- Opening issues needs Issues write permission on the integrator App, which `driven.integrator` does not request today
  ([GitHub setup](../guide/github-start.md)). Decided: ship the notify-only version first (checks 1 to 10 hold
  without it) and add issue opening as a second PR once the permission is granted.
- Marker syntax: an HTML comment keeps rendered pages clean but is invisible to a reader. The alternative is a footnote-style
  link, which shows on the page. Decided: the comment.
