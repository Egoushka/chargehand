# 0037. A public benchmark of claims that do not hold up

- Status: proposed
- Date: 2026-09-30

## Context

Goal 0.9 asks for a public verification benchmark. Chargehand's claim is that its answers' claims are checked against a pinned
commit; a stranger cannot weigh that without seeing plain agent answers checked the same way. The checking pieces exist: citation
resolution (ADR 0009) and a model judge of support (ADR 0036, 25 of 30 agreement on the author's set). Plain answers are prose with
few structured citations, chargehand filters its own claims, and the author writes the tool, the questions and the audit (design
spec, "Limits and bias").

## Options

1. Compare on answer correctness against a hand-written answer key. Strong for the questions asked; slow to write, hard to grade
   prose, and it measures the model more than the citations.
2. Compare the share of claims whose cited text does not support them, judged by the existing judge, for a plain agent and for
   chargehand, on 30 frozen questions over one pinned public repository, with a human audit of the judge. Measures the property
   chargehand sells; depends on a model judge and an extractor.
3. Publish chargehand's own numbers only. Cheap; no baseline, so no claim.

## Decision

Option 2. Three arms (plain, chargehand with the check off, chargehand as shipped), one shared scorer, the shipped judge prompt,
outcomes `uncited`, `unresolved`, `unsupported`, `partial`, `supported` reported separately, a blind 60-claim human audit that gates
publication (agreement at least 80 %), a bootstrap over questions for intervals, everything reproducible from committed raw files with
no model call. The result is worded as a model's check of cited text, never as a hallucination rate or a proof.

## Consequences

- New code in `Evals/` (extractor, scorer, runner, report) and a `benchmarks/public/` directory; no schema change and no change to
  the judge.
- Real spend for the live run, set by the maintainer after a pilot. Nothing runs live in CI.
- The published number can favour the plain arm or show no difference; it is published either way.
- The judge and extractor become part of what the benchmark's credibility rests on, so their prompts are hashed in every report.
- Listings and the demo quote the benchmark only after it is published.

## Reopen if

The audit agreement is below 80 %, a second model family disagrees with the judge on the headline, a reader shows the extractor
favours an arm, or a run on a second repository points the other way.
