# Claim bounty: a queue of claims for workers to disprove

- Status: draft for the maintainer's review. The idea (claims from answers and docs go to a queue; a worker that
  disproves one with evidence earns its preset a priority point) is one of nine the maintainer approved on 2026-10-18,
  issue #787. Every decision below is the proposer's.
- Date: 2026-11-08
- ADR: none yet; Task 1 writes one from the Decisions below
- Builds on: [ADR 0009](../adr/0009-result-contract.md) (the result contract),
  [0036](../adr/0036-support-checking-and-signed-results.md) (support check), [0045](../adr/0045-governed-sessions.md)
  (learning records), [0087](../adr/0087-replay-benchmark-and-calibrated-judges.md) (calibrated judges)

## Goal

The support check (ADR 0036) asks one question of a claim: does the text it cites say it? It never asks whether
something else in the repository says the opposite. A claim can cite a real line, pass, and still be false, and a doc
can drift from the code it describes. The bounty turns that second question into work: claims wait in a queue, a
worker tries to disprove one with a citation, the disproof is checked the same way any claim is, and a disproof that
holds is recorded as a point for the preset that found it.

Done when: one run takes a seeded false claim from the queue, disproves it with a citation that resolves at the pinned
commit, and the bounty is recorded (acceptance checks below).

## Where it stands

Read at `5240c1f` (main), 2026-11-08.

1. **Claims and evidence.** A `result/v1` claim has `text`, at least one evidence id and a `confidence`, and an optional
   `support` (`schemas/result/v1/result.schema.json:18-46`). Evidence is `{id, kind, locator}` with an optional
   `commit` and `sha256`.
2. **Checks that exist.** `GitEvidenceResolver` confirms a `file` locator names real lines at the commit
   (`src/Chargehand/Verification/GitEvidenceResolver.cs`); `SupportJudge` asks a model whether the cited text supports
   the claim (`src/Chargehand/Verification/SupportJudge.cs`). `chargehand eval score-claims` runs both over a
   `claims.jsonl` file (`src/Chargehand/Evals/ClaimScorer.cs`). Nothing looks for text that contradicts a claim.
3. **Priority.** Intake ranks candidates by completion probability × `priority` ÷ predicted cost, `priority` being a
   weight of 1 or more the caller sends (`src/Modules/Chargehand.Modules.Oversight/Api/CandidateRanking.cs`,
   `docs/guide/intake.md`). Presets carry budgets and models, no score (`presets/*.yaml`).
4. **No queue.** Nothing in the tree holds claims after their run ends, and no module is named for a bounty.
   `Chargehand.Modules.Evaluation` exists as an empty project ("Bench and evals").

## Terms

- **Bounty item:** one claim in the queue: its text, its source (a run's result or a doc), the commit it is about, and
  the evidence it cited.
- **Disproof:** a `result/v1` from a bounty run whose one claim says the item is false, citing at least one `file`
  locator at the item's commit.
- **Bounty:** the record that a disproof held: the item, the disproving run, its preset and one point.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Where items come from | Two sources. (a) Completed results: each claim marked `supported` or `partial`, with its evidence, enqueued after the run, off by default (`bounty.enqueue_results`). (b) Docs: `chargehand bounty seed <file.jsonl>` adds hand-written items; `evals/bounty-seeds.jsonl` holds synthetic seeds against this repository, each a known-false claim with the line that disproves it. Items from private runs stay in the server's state, never in the repository. |
| 2 | The store | Append-only JSONL beside the learning records (`bounty.jsonl`), lines tagged `item`, `attempt` and `bounty`, read by taking the latest line per id. No new database (the events spec, D-39). |
| 3 | What a bounty run is | A read-only run on preset `bounty` (a copy of `default` with its own prompt block): the item's text and evidence go in as `input`s, the repository at the item's commit, and the worker answers with one claim, either "false" with a citation, or "not disproved". It cannot write and it starts nothing. |
| 4 | When a disproof holds | All three: (1) its `file` locators resolve at the item's commit (`GitEvidenceResolver`, unchanged); (2) `SupportJudge` judges the disproof `supported` by its cited text; (3) a second judge call, with the original claim and the disproof's cited text, answers `contradicts`. A failure of any check records an `attempt` and no point. Seeds also carry the expected disproving line, so a seed's bounty can be checked without a model. |
| 5 | The point | One point per held disproof to the preset of the bounty run, recorded on the `bounty` line, never on the preset file. The first held disproof of an item closes it; later ones record an `attempt` only. A point is a count for a report (`chargehand bounty report`: points per preset, attempts, holds), not an input to intake's ranking until a later ADR says how, after 50 held disproofs. |
| 6 | Cost | Each bounty run has the preset's budget; `bounty.max_usd_per_day` (default $1) stops the queue for the day and says so in the report. A run is picked oldest item first. |
| 7 | What happens to a disproved claim | Nothing automatic. The report lists it with the source run or doc, so a person fixes the doc or the prompt. A disproved claim from memory (ADR 0034) is a candidate for the provider's `invalidate` tool in a later task, not this one. |

## Tasks (each one pull request)

1. **This spec**, the synthetic seeds and the check that keeps them honest (`BountySeedTests`). Then the ADR.
2. **Store and seed command** (decisions 1b and 2) in `Chargehand.Modules.Evaluation`, behind its `.Api` namespace.
3. **The bounty run** (decisions 3 and 4) with the `bounty` preset and prompt block, a fake runtime in tests.
4. **Points and the report** (decision 5), the daily cap (decision 6).
5. **Enqueue from results** (decision 1a), off by default.

## Acceptance checks

Machine-run in `dotnet test` unless marked live.

- `BountySeedTests` (Task 1): every line of `evals/bounty-seeds.jsonl` has an id, a claim and a disproof; the
  disproof's `path:line` exists in this checkout and contains its `contains` text, so the seeded claim is false at
  `HEAD`. A change that moves the line fails the test until the seed's locator is moved with it.
- `BountyStoreTests` (Task 2): seeding the file twice adds each item once; an `attempt` then a `bounty` line for one
  item reads as closed; a reader skips a torn last line.
- `BountyRunTests` (Task 3, scripted runtime and scripted judge): for each seed, a worker reply citing the seed's
  disproving line records a `bounty` with one point for preset `bounty`; a reply citing a line that does not exist,
  or one the judge calls `unsupported` or not `contradicts`, records an `attempt` and no point; a reply "not
  disproved" records an `attempt` only.
- `BountyReportTests` (Task 4): points per preset match a fixture; the daily cap stops the queue and the report says
  so.
- `SchemaCompatTests` stay green: no published schema changes; any new contract is a new schema name.
- `ArchitectureTests` stay green: the bounty code lives in `src/Modules` and references no old `src/Chargehand*` project.
- **Live** (after Task 3): `chargehand bounty seed evals/bounty-seeds.jsonl`, then one bounty run on this repository
  disproves at least one seed, and `chargehand bounty report` shows its point. This is the issue's "done when".

## Risks

- **A disproof is a model's opinion.** Decision 4 needs a resolved citation and two judge verdicts; the report says
  "disproof held", never "claim false". The second judge is uncalibrated until it has labels (ADR 0087's bar).
- **Points reward volume.** A preset could farm cheap items. Mitigation: one point per item, ever, and points do not
  change ranking until a later ADR.
- **Seeds drift.** A refactor moves a line and a seed's disproof no longer holds. `BountySeedTests` fails on it.
- **Private text in the queue.** Items from real runs hold real claims; they stay in the server's state like learning
  records and never enter `evals/`.

## Out of scope

Rewriting or deleting a disproved claim's source, ranking intake by points, and claims from outside this server's runs
and the repository's own docs.
