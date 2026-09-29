# Public benchmark, demo and listings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A stranger can rerun a benchmark of the share of claims that do not hold up, plain agent answers against chargehand's,
and find chargehand through a demo and listing drafts.

**Architecture:** Every arm's answer becomes `claims.jsonl` (plain answers through one extractor call, chargehand results directly).
One scorer resolves each citation, fetches the cited text and asks the existing `SupportJudge`, giving an outcome per claim. A runner
drives the arms with a spend cap and resume; a report rebuilds every table from the raw files with no model call.

**Tech Stack:** .NET 10, xUnit 2.9, bash, the existing `Evals/` and `Verification/` code.

**Spec:** `docs/specs/2026-09-30-public-benchmark-design.md`; decision record `docs/adr/0037-public-benchmark.md`.

## Global Constraints

- Public repository: no IP addresses, hostnames, absolute home paths, employer or project names, keys, tracker URLs, or prompts from
  real private runs. Everything the benchmark reads is a public repository.
- Done for every task: `scripts/check.sh` exits 0 and its last test line reads `Passed!  - Failed:     0`. Run dotnet outside the agent
  sandbox (`dangerouslyDisableSandbox`). Tasks 8 and 9 are prose and shell: the prose-only CI path applies, and `shellcheck`-clean scripts.
- Conventional Commits; never `--no-verify`; never force-push. PR title ends with `(CHARGEHAND-<n>)`.
- No task makes a live model call in tests or CI. Live runs are the maintainer's (Task 7), after the pilot's cost is seen.
- The judge and extractor prompts live in code, not in `prompts/`, and are not edited once Task 7's pilot starts (their hashes are in the report).
- Nothing is submitted, posted or announced by an agent. Listing files are drafts; the maintainer submits them.
- Public text says "share of claims whose cited text does not support them, as judged by a model", never "hallucination rate" or "verified true".
- No schema changes: `result/v1` and the other published majors are untouched.

## Review Focus

1. The extractor invents a citation: a locator not present in the answer text makes the claim `uncited`, never cited. Task 3.
2. Arm C looks perfect because its own check removed the failures: the report has the dropped-claims column and the A-versus-B
   comparison. Task 5.
3. One arm has fewer completed runs than another: the report refuses a headline. Task 5.
4. The question file is edited after the first run: the hash check fails the report. Tasks 1 and 5.
5. The judge fails or returns prose: claims are `unchecked`, counted apart, never `supported`. Task 2.
6. Judge or extractor prompt changed between runs: hash mismatch invalidates the run. Task 5.
7. The audit sample leaks the arm to the labeller. Task 6.
8. A demo or listing quotes a number before Task 7 published it. Tasks 8 and 9.

---

### Task 1: Question set and pinned repository

**Files:**
- Create: `benchmarks/public/pin.json` (`{ "repository": "<https url>", "commit": "<40 hex>", "license": "<spdx>" }`)
- Create: `benchmarks/public/questions.jsonl` (30 lines: `{"id":"q01","kind":"where|how|config|impact","question":"..."}`)
- Create: `benchmarks/public/questions.sha256` (SHA-256 of `questions.jsonl`)
- Create: `scripts/benchmark-fetch.sh` (clone at the pinned SHA into a directory argument, verify `git rev-parse HEAD`)
- Test: `tests/Chargehand.Tests/BenchmarkSetTests.cs`

**Interfaces:**
- Produces: `BenchmarkSet.Load(string dir)` returning `(Pin Pin, IReadOnlyList<Question> Questions)`; `record Pin(string Repository, string Commit, string License)`;
  `record Question(string Id, string Kind, string Text)`; throws `InvalidDataException` naming the first problem.

- [ ] **Step 1: Failing tests.** Thirty questions; ids unique and `q01`..`q30`; kinds only `where`, `how`, `config`, `impact` in the
  shares 8, 10, 6, 6; no empty text; the commit is 40 lowercase hex; the repository is `https://`; `questions.sha256` matches the
  file (a one-byte change fails with "questions changed since they were frozen"); no question text contains an absolute path.
- [ ] **Step 2: Run** `dotnet test --filter BenchmarkSetTests`; expect compile errors.
- [ ] **Step 3: Implement** the loader and the fetch script. **The maintainer names the repository (open item 1 of the spec) before the
  questions are written**; the questions are written from reading that checkout, before any run, and committed once.
- [ ] **Step 4: Run** the tests and `scripts/check.sh`. **Step 5: Commit** `feat(bench): the benchmark's question set and pinned repository`.

### Task 2: Claim scorer

**Files:**
- Create: `src/Chargehand/Evals/ClaimScorer.cs`
- Modify: `src/Chargehand.Cli/` (the `eval` command: `eval score-claims <claims.jsonl> --repo <dir> --commit <sha> [--out <verdicts.jsonl>]`)
- Test: `tests/Chargehand.Tests/ClaimScorerTests.cs`

**Interfaces:**
- Consumes: `GitEvidenceResolver`, `CitedText`, `SupportJudge` (unchanged), `IWorkerRuntime`.
- Produces:

```csharp
public sealed record BenchClaim(string Arm, string Question, int Run, string Text, IReadOnlyList<string> Locators);   // one claims.jsonl line
public enum ClaimOutcome { Uncited, Unresolved, Unsupported, Partial, Supported, Unchecked }
public sealed record ScoredClaim(BenchClaim Claim, ClaimOutcome Outcome, string Reason);
public static class ClaimScorer
{
    public static Task<IReadOnlyList<ScoredClaim>> ScoreAsync(IWorkerRuntime runtime, ModelRef? judge, IReadOnlyList<BenchClaim> claims,
        EvidenceScope scope, CancellationToken ct);
}
```

- [ ] **Step 1: Failing tests** on a real git repository (`Runs.GitRepo`) with a scripted runtime: a claim with no locators is `Uncited`
  and never reaches the judge; a locator to a missing path or a range past the end is `Unresolved`; the judge's three verdicts map to
  `Supported`, `Partial`, `Unsupported`; a judge that fails twice leaves the claim `Unchecked` (not `Supported`) and the run completes;
  claims are judged in batches per question and run, so a claim is never judged beside another run's claims; the output file has one line
  per input claim in input order; the `arm` value never appears in the judge's prompt.
- [ ] **Steps 2-5:** run, implement, run, commit `feat(bench): score claims from any source with the support judge`.

### Task 3: Plain-answer claim extractor

**Files:**
- Create: `src/Chargehand/Evals/PlainAnswerExtractor.cs`
- Test: `tests/Chargehand.Tests/PlainAnswerExtractorTests.cs`

**Interfaces:**
- Consumes: `IWorkerRuntime.GenerateAsync`.
- Produces:

```csharp
public static class PlainAnswerExtractor
{
    // One retry on invalid JSON; throws InvalidOperationException after that.
    public static Task<IReadOnlyList<ExtractedClaim>> ExtractAsync(IWorkerRuntime runtime, ModelRef? model, string answer, CancellationToken ct);
    internal static string Prompt(string answer);
    public static string PromptHash { get; }                                                    // SHA-256 of the prompt template
}
public sealed record ExtractedClaim(string Text, IReadOnlyList<string> Locators);
```

  Prompt rule: list the factual statements about code the answer makes, one per claim, with the `path:line` or `path:start-end`
  locators the answer itself gives for it; add no statement and no locator the answer does not contain; reply
  `{"claims":[{"text":"...","locators":["src/a.cs:10-20"]}]}`.

- [ ] **Step 1: Failing tests** with a fake runtime: a valid reply parses; a locator that does not appear in the answer text is
  removed and, if it was the claim's only one, the claim has no locators (`Uncited` later); a claim with no locator is kept; locators
  in `path:10`, `path:10-20` and `path#L10-L20` forms are normalised to `path:start-end`; bad JSON then good JSON succeeds and the
  second prompt names the error; two bad replies throw; an answer with no claims returns an empty list; `PromptHash` is stable.
- [ ] **Steps 2-5:** run, implement, run, commit `feat(bench): turn a plain agent answer into claims with their citations`.

### Task 4: Runner

**Files:**
- Create: `src/Chargehand/Evals/BenchmarkRunner.cs`
- Modify: `src/Chargehand.Cli/` (`eval bench run --arm plain|check-off|default [--questions <dir>] [--reps 3] [--max-usd <n>] [--out <dir>]`)
- Test: `tests/Chargehand.Tests/BenchmarkRunnerTests.cs`

**Interfaces:**
- Consumes: `BenchmarkSet` (Task 1), `PlainAnswerExtractor` (Task 3), `Orchestrator` for arms B and C, `IWorkerRuntime` for arm A.
- Produces: under `<out>/`: `answers/<arm>/<question>-<run>.md` (arm A) or `results/<arm>/<question>-<run>.json` (B, C), `claims.jsonl`,
  `runs.jsonl` (`arm, question, run, status, usd, seconds, error`), `manifest.json` (chargehand commit, pinned repository commit, model ids,
  prompt hashes, dates, questions hash).

- [ ] **Step 1: Failing tests** with a scripted runtime and a two-question fixture repository: arm A writes one answer per question and
  repetition and its claims come from the extractor; arm B runs the default preset with `support_check` false and arm C with it on;
  chargehand claims with only commit, URL or session-message evidence are left out of `claims.jsonl` and counted in `runs.jsonl`;
  a second invocation skips finished (arm, question, run) triples; `--max-usd` stops before the next run with "cap reached" and exit 1,
  leaving finished runs intact; a failed run is recorded `failed` with its reason and the loop continues; the plain prompt asks for
  `path:line` citations and gives the same tools as the chargehand workers; the manifest records the hashes.
- [ ] **Steps 2-5:** run, implement, run, commit `feat(bench): run the arms with a spend cap and resume`.

### Task 5: Report

**Files:**
- Create: `src/Chargehand/Evals/BenchmarkReport.cs`
- Modify: `src/Chargehand.Cli/` (`eval report <run dir> [--audit <labels.jsonl>]`)
- Test: `tests/Chargehand.Tests/BenchmarkReportTests.cs`

**Interfaces:**
- Consumes: the run directory (Task 4) and `verdicts.jsonl` (Task 2).
- Produces: markdown with, per arm: claims, cited claims, counts and shares of each outcome, the headline (`unresolved` plus
  `unsupported` over cited claims) pooled and as a mean over questions, a 95 % bootstrap interval over questions (10 000 resamples, seed
  in the manifest), claims per answer, cost, seconds, failed runs, and for B and C the claims dropped by the check; the A-versus-B and
  A-versus-C differences with intervals; the audit section when `--audit` is given (Task 6). No model call.

- [ ] **Step 1: Failing tests** on fixture run directories with hand-counted numbers: rates and the mean over questions match; the
  interval is reproducible for the seed; arm C's dropped claims are shown; arms with unequal completed runs make the command exit 1 with
  "arms completed different numbers of runs" and print no headline; a changed `questions.jsonl` (hash differs from the manifest), or
  differing judge or extractor prompt hashes between arms, exit 1 naming the mismatch; an arm with no cited claims prints "n/a", never 0 %;
  `unchecked` is its own column.
- [ ] **Steps 2-5:** run, implement, run, commit `feat(bench): rebuild the benchmark's tables from the raw files`.

### Task 6: Human audit of the judge

**Files:**
- Modify: `src/Chargehand/Evals/BenchmarkReport.cs`
- Modify: `src/Chargehand.Cli/` (`eval audit export <run dir> --sample 60 --seed <n> --out audit.jsonl`, `eval audit import` folded into `eval report --audit`)
- Test: `tests/Chargehand.Tests/BenchmarkAuditTests.cs`

**Interfaces:**
- Produces: `audit.jsonl` lines `{ "id", "claim", "cited_text", "judge": "supported|partial|unsupported", "label": null }` (no arm, question id or run in
  the file; a separate `audit-key.jsonl` maps `id` to them and stays with the maintainer); the report's audit section: agreement, the
  confusion table, the headline recomputed on the labelled sample, and a refusal to print a headline when agreement is below 80 %.

- [ ] **Step 1: Failing tests:** the export draws an equal number from each judge outcome that has enough claims, deterministically for a seed;
  the exported file has no arm, question or run anywhere (search the raw text); unlabelled lines are refused on import with their ids; agreement and the
  confusion table are right on a fixture; agreement 0.79 suppresses the headline and prints why; agreement 0.80 does not.
- [ ] **Steps 2-5:** run, implement, run, commit `feat(bench): a blind human audit of the judge that gates the headline`.

### Task 7: Pilot, live run and publication (maintainer's spend)

**Files:**
- Create: `scripts/benchmark.sh` (fetch the pinned repository, run the three arms, score, print the report; takes `--pilot` for 3 questions, `--max-usd`)
- Create: `benchmarks/public/runs/<date>/` (raw files from the live run)
- Modify: `docs/benchmarks.md` (a "Public verification benchmark" section: the tables, models, date, cost, audit agreement, the misses, the limits from the spec)
- Modify: `ROADMAP.md`, `CHANGELOG.md`

**Interfaces:** consumes Tasks 1 to 6.

- [ ] **Step 1:** the maintainer runs `scripts/benchmark.sh --pilot --max-usd <cap>` and reads the cost and one answer per arm. If the pilot's
  cost times ten is above what the maintainer will spend, they lower repetitions or stop.
- [ ] **Step 2:** freeze the prompts and the question hash (a tag or a commit noted in `pin.json`), then the full run; label the audit sample.
- [ ] **Step 3:** if audit agreement is below 80 %, publish the misses and the finding, not a headline (spec, decision 7).
- [ ] **Step 4:** write the section in `docs/benchmarks.md` with the spec's wording rules; tick ROADMAP.md only for what is done.
- [ ] **Step 5: Commit** `docs(bench): the public verification benchmark`. This task is labelled `you`: it spends the maintainer's money and staking a public claim.

### Task 8: Text demo

**Files:**
- Create: `scripts/demo.sh` (runs the demo on the pinned repository: a plain answer's failed claim, the same question through chargehand, then `chargehand verify` on a result signed with a throwaway key made by `openssl` in a temp directory)
- Create: `docs/demo.md` (the transcript, with the command that produced it and its date)
- Modify: `README.md` (a short demo block near the top linking `docs/demo.md`)

- [ ] **Step 1:** write `scripts/demo.sh` with `set -euo pipefail`, no key kept after it exits, `shellcheck` clean, and a `--dry` flag that prints the commands without calling a model.
- [ ] **Step 2:** the maintainer (or an agent with their approval of the spend) runs it once; paste its real output into `docs/demo.md`.
  Numbers in the demo are the one question's, not the benchmark's, and say so.
- [ ] **Step 3:** run `scripts/check.sh`; **Step 4: Commit** `docs: a text demo of a failed claim caught and a result verified`.

### Task 9: Listing drafts

**Files:**
- Create: `docs/listings/README.md` (what each file is, that they are drafts, who submits, when)
- Create: `docs/listings/awesome-mcp-servers.md`, `docs/listings/awesome-claude-code.md`, `docs/listings/mcp-registry.md`, `docs/listings/launch-post.md`

- [ ] **Step 1:** for each target, read its current contribution rules on the day (link them in the file, with the date read), then draft the
  entry in that list's own format: one line of description, category, link, and the checklist of its requirements with what is met.
  awesome-claude-code accepts human submissions through its web form from 2026-10-10: the draft says so and is not sent.
- [ ] **Step 2:** the MCP Registry blurb reuses `.mcp/server.json`'s description and changes nothing else; note that the registry entry already exists.
- [ ] **Step 3:** benchmark numbers appear only as `TODO(after Task 7)` placeholders, never estimated. Every file starts with `DRAFT, not submitted`.
- [ ] **Step 4:** `scripts/check.sh`; **Step 5: Commit** `docs: draft listing entries and a launch post, not submitted`.
