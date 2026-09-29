# A public benchmark: how many claims hold up (goal 0.9)

- Status: draft for the maintainer's review. Every decision below is the proposer's; the maintainer accepts or changes them in review.
- Date: 2026-09-30

## Goal

Publish one number a stranger can rerun: of the claims a coding agent makes about a public repository, the share whose cited
text does not support them, for a plain agent answer against chargehand's result. Plus a demo and listings, so people find it.

Done when: (1) the question set, the pinned repository, the scripts and the raw outputs are in the repository and one command
reproduces the tables from the raw outputs; (2) the tables are in [benchmarks](../benchmarks.md) with the models, the date, the
cost and the judge's measured agreement with a human on a sample; (3) the demo and the listing drafts exist and the maintainer has
read them. Roadmap's "five outside people have used it" is a result to observe after publishing, not something this work can do.

## Where it stands

Evidence: `path` at main after goal 0.8 (`f040d98`).

1. **The pieces exist.** `GitEvidenceResolver` checks a citation resolves; `CitedText.ForAsync` fetches the cited text (200 lines per
   citation, 12 000 characters per claim); `SupportJudge.JudgeAsync` returns `supported`, `partial` or `unsupported`
   (`src/Chargehand/Verification/`). `chargehand eval support` runs the judge over a labelled file (`scripts/support-eval.sh`).
2. **The judge's accuracy is known only roughly.** 25 of 30 on the author's own labelled set; every miss was a `partial` judged
   `unsupported` (`docs/guide/support-and-signing.md`). Thirty claims from one repository, labelled by one person.
3. **A chargehand result filters itself.** With `support_check` on, unsupported claims leave `claims` for `open_questions`. Counting
   only the claims left would make chargehand look perfect by construction.
4. **Plain answers are prose.** They have no claim list and often no citations. Nothing in the repository turns them into claims.
5. **No public number exists.** `docs/benchmarks.md` holds phase exit checks on private repositories.

## What is measured

A **claim** is one factual statement about the repository's code with the locators (`path:start-end`) it gives as support.
For each claim the scorer records one outcome, in this order:

| outcome | meaning |
|---|---|
| `uncited` | no locator at all. Only plain answers have these; a chargehand claim always cites something |
| `unresolved` | a locator that does not exist at the pinned commit (missing path, range past the end) |
| `unsupported` | the judge says the cited text does not say it, contradicts it or is about something else |
| `partial` | the judge says the text supports only part of it or a weaker statement |
| `supported` | the judge says the text says it or plainly implies it |

**Headline: the not-holding-up rate** = (`unresolved` + `unsupported`) / cited claims. `partial` and `uncited` are reported beside
it, never folded in or hidden: a reader who counts `partial` as failing, or `uncited` as failing, gets their number from the same
table. Reported per arm, pooled over claims and as the mean over questions (answers differ in length, so both are shown).

Also per arm: claims per answer, cited claims per answer, cost in USD, wall time, and for chargehand the **claims dropped by its own
check** (moved to `open_questions`), shown as its own column so the filter's work is visible.

## Arms

| arm | what runs |
|---|---|
| A. plain | One read-only agent session on the pinned checkout, same runtime and model as B and C, same tools, asked for a written answer with `path:line` citations. No chargehand. |
| B. chargehand, check off | The `default` preset with `support_check: false`. Separates what several workers and evidence resolution give from what the support check gives. |
| C. chargehand, default | The `default` preset as shipped, `support_check` on. |

Each question runs 3 times per arm (agents vary), so N questions give 3N answers per arm.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Repository | One public repository under a permissive licence, pinned by full commit SHA, of a size where a question needs several files (order of 100 to 1 000 source files), written in a language the runtime handles well, with a commit recent enough that answers cannot come from memory alone. Not chargehand itself: it would grade its own home. The maintainer picks it (open item 1). |
| 2 | Questions | 30, written before any run and frozen with a hash. Four kinds in fixed shares: where something is defined or done (8), how a behaviour works across files (10), what a setting or flag does (6), what would break if X changed (6). Written from reading the repository, with no answer key: the judge checks claims against their own citations, so no key is needed. The whole set is published, including questions chargehand does badly on. A question may be dropped only if it is ambiguous, and the drop is listed with its reason. |
| 3 | Judge | The existing `SupportJudge` and `CitedText`, unchanged, for every arm, through one scorer (`eval score-claims`). The judge model is not the one that wrote the answers where a second model is available (open item 2), so no arm is graded by its own family alone. Its prompt is the shipped one, hashed in the report. |
| 4 | Plain answers to claims | One extractor call (a model, a fixed prompt in the trusted build, one retry) turns an answer into `{claim, locators[]}`. Rules: extract statements the answer makes, add none; a locator counts only if it appears in the answer text (checked by string match, else the claim becomes `uncited`); a compound sentence is split only at "and" between independent statements. |
| 5 | Chargehand results to claims | Read directly from `claims[]` and their `file` and `diff` evidence. Claims whose evidence is only a commit, URL or session message are dropped from the scored set and counted in a column, because the judge cannot read them. |
| 6 | Same scorer for all arms | Claims from every arm are written as `claims.jsonl` (`arm, question, run, claim, locators`) and scored by one command, so no arm gets a different path to a verdict. Arm labels are removed from what the judge and the human auditor see. |
| 7 | Judge audit | A blind sample of 60 scored claims (20 per outcome class the judge produced, drawn by a fixed seed, arm hidden) is labelled by the maintainer. Reported: agreement, the confusion table, and the headline recomputed on the human labels for that sample. If agreement is below 80 % the benchmark is not published as a headline; the misses are published instead. |
| 8 | Uncertainty | Claims cluster inside answers and answers inside questions, so intervals come from a bootstrap over questions (10 000 resamples, fixed seed). Differences between arms are reported with their interval; "no difference" is a possible result. |
| 9 | Cost | Measured in a 3-question pilot first, then multiplied; the script has `--max-usd` and stops, resumable, when reached. Expected order: 3N answers per arm across three arms plus one extractor call per plain answer plus judge calls, so 270 sessions and about 300 small calls. The pilot replaces this estimate; nothing runs live before the maintainer has seen the pilot's cost. |
| 10 | Reproducibility | `scripts/benchmark.sh` takes the pinned repository SHA and chargehand commit from `benchmarks/public/pin.json`, runs the arms, writes raw answers, results, extracted claims and verdicts under `benchmarks/public/runs/<date>/`, and `chargehand eval report` rebuilds every table from those files with no model call. The report records model ids, chargehand commit, prompt hashes, dates. |
| 11 | What is published | Tables in `docs/benchmarks.md`; the raw files in the repository; the misses (every claim the human audit disagreed with) listed. Wording: "share of claims whose cited text does not support them, as judged by a model, agreement with a human X %", never "hallucination rate" or "verified". |
| 12 | Demo | A text demo (`scripts/demo.sh` and a transcript in `docs/demo.md` shown in the README): one question on the pinned repository, the plain answer with one claim that fails, the chargehand result with the same claim in `open_questions`, then `chargehand verify` on a signed result. No recording tool; the transcript is real output with the command that produced it. |
| 13 | Listings | Drafts only, in `docs/listings/`, one file per target (awesome-mcp-servers, awesome-claude-code, MCP Registry description). Each says where and how it must be submitted and by whom. Nothing is submitted by an agent: awesome-claude-code takes human submissions only, from 2026-10-10. Drafts quote the benchmark only after it is published. |

## Limits and bias, stated up front

- **The judge is a model.** It agreed with the author on 25 of 30 labelled claims and confused `partial` with `unsupported`. Using the
  same judge on every arm removes a difference in how arms are graded, not the judge's errors; if the errors correlate with answer
  style (long citations, terse claims) they can favour one arm. The human audit measures this on a sample; it does not remove it.
- **The extractor is a model too.** It can merge, split or miss claims in plain answers. Rule 4 in decision 4 limits invention, and
  the extractor's claim list for every audited answer goes into the audit sheet. Chargehand claims skip this step, an asymmetry that
  cannot be removed: they come already structured. The benchmark says so.
- **Cited-text only.** The judge reads what a claim cites. A claim right because of code outside its citation counts as
  `unsupported`. This penalises terse citations equally in every arm; it does penalise them.
- **Self-filtering.** Arm C removes claims its own judge rejects. Comparing A with C mostly shows the filter working, as designed;
  A with B shows what the workers and resolver add. Both comparisons are published, with the dropped-claims column.
- **One repository, one language, one model family, 30 questions.** A result generalises to nothing beyond that. Say so in the table caption.
- **The author wrote the tool, the questions and the audit.** Freezing the questions before the runs, publishing every output and
  reporting a loss the same way as a win are the only controls. An outside reviewer of the questions and a second labeller for the
  audit sample would strengthen it and are welcome before publishing (open item 3).

## What would make the result meaningless

1. Questions written or changed after seeing any answer. The frozen hash is checked by the report.
2. A strawman plain arm: a different or weaker model, no repository tools, no request for citations, or a shorter budget than the chargehand arms.
   Arm A gets the same runtime, model, tools and the same read-only checkout; its prompt asks for `path:line` citations.
3. The judge or extractor prompt edited between arms or runs. Their hashes are in the report; a mismatch invalidates the run.
4. Judge agreement with the human sample below 80 %, or a sample labelled by someone who saw the arm.
5. A repository the models have effectively memorised, where uncited correct answers are common; or one so small that every answer
   cites the same three files.
6. Reporting only the tables that favour chargehand, or rounding a wide interval into a claim.
7. Runs stopped by the spend cap in one arm and not another: the report refuses to compare arms with unequal completed runs.

## Components

| Unit | Responsibility |
|---|---|
| `benchmarks/public/` | `questions.jsonl`, `pin.json` (repository URL and SHA, chargehand commit), `runs/` |
| `Evals/PlainAnswerExtractor` | Decision 4 |
| `Evals/ClaimScorer`, CLI `eval score-claims` | Decisions 3 and 6: resolve, cite, judge, outcome |
| `Evals/BenchmarkRunner`, CLI `eval bench` | Arms, repetitions, budget cap, resume |
| `Evals/BenchmarkReport`, CLI `eval report` | Decisions 7 to 11: tables, intervals, audit sheet in and out |
| `scripts/benchmark.sh`, `scripts/demo.sh` | Decisions 10 and 12 |
| `docs/listings/` | Decision 13 |

## Errors

No new error codes. A run that cannot finish a question records it as `failed` with the reason; the report counts failed runs per
arm and refuses a headline when arms differ in completed runs (meaningless-result 7). A judge that fails leaves a claim
`unchecked`, counted in its own column, never as supported.

## Testing

Scorer and extractor: scripted runtimes returning canned text; the extractor rejects a locator that is not in the answer, keeps
uncited claims, retries once on bad JSON; the scorer maps a missing path to `unresolved` and a failed judge to `unchecked`.
Runner: a scripted runtime and a two-question fixture repository; resume skips finished runs; the cap stops the run with a clear
message. Report: fixtures with known counts give known rates and intervals; an unequal number of runs is refused; a changed question
file fails the hash check. Live: the pilot, then the full run, by the maintainer.

## Out of scope for 0.9

Other repositories and languages, other models as arms, comparisons with named commercial products, a leaderboard, continuous runs,
scoring answers for correctness beyond their citations, and the launch post's publication.

## Open items

1. The repository, from the maintainer's shortlist; needs a permissive licence and a SHA.
2. Whether a second model family is available to judge, or the audit alone covers judge bias.
3. Who reviews the question set and who second-labels the audit sample.
4. Budget: the cap the maintainer sets after the pilot.
