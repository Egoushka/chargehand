# Blind tests: a second worker writes acceptance tests from the goal alone

- Status: proposed; the maintainer reviews this spec before any task starts. Every decision below is the proposer's.
- Date: 2026-11-08
- Issue: #789 (one of the nine ideas approved on 2026-10-18)
- Builds on: [ADR 0066](../adr/0066-a-check-gate-acceptance-criteria-and-a-goal-reminder.md) (the check gate and
  acceptance criteria), [ADR 0039](../adr/0039-driven-writing-sessions.md) (driven sessions, the fresh-container
  verification), [ADR 0046](../adr/0046-integrator-and-ci-fixer.md) (test integrity),
  [ADR 0052](../adr/0052-quality-benchmark-on-public-repositories.md) (hidden acceptance scripts in `bench`)

## Goal

A driven task's branch is held to tests that its own session did not write and never saw. A second worker gets only the
task's goal and the repository at the base commit, writes acceptance tests, and the implementing session's branch must
pass them before the handover pushes it.

Done when: (1) the machine-run checks below pass in `scripts/check.sh`; (2) one live batch shows a blind test catching a
seeded bug that the implementing session's own tests and the repository's checks let through.

## Where it stands

Evidence: `main` at 5240c1f.

1. **The gate exists, but its tests come from the task or the session.** `SessionChecks.RunAllAsync` runs the declared
   check, the lines of `.chargehand/checks` at the start commit and the task's `acceptance[]`, in the session's own
   container (`src/Modules/Chargehand.Modules.Sessions/Api/SessionChecks.cs:61-77`). A failure resumes the same session
   with the output tail (`SessionDriver.Run.cs:80-109`). Acceptance criteria are whatever the caller typed; nothing writes
   them for a task that has none.
2. **A session can make its own tests agree with its own mistake.** The session writes the change and the tests for it in
   one context, so a misread of the goal shows in both. `TestIntegrity` flags only a branch with no production change
   (`src/Modules/Chargehand.Modules.Handover/Api/TestIntegrity.cs:35-43`); a branch with a wrong change and matching tests
   passes every check we have.
3. **Hidden tests already work in `bench`.** A bench task's acceptance script writes a test file of its own and runs it in a
   fresh container on the task's branch, so the session never sees what decides its success (`docs/guide/benchmark.md`,
   "The set"; `ContainerAcceptanceRunner`, `src/Chargehand.Automation/Bench/AcceptanceRunner.cs:27-80`). Those scripts are
   written by a person. Blind tests are the same mechanism with a model writing the script, on every task that asks for it.
4. **A test file is recognisable by path.** `TestIntegrity.Classify` sorts a path into test, build or production
   (`TestIntegrity.cs:21-30`); the blind worker's output can be held to test paths with it.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Who asks for it | `driven.tasks[].blind_tests: true` (additive to `request/v1`), and `driven.blind_tests.default` in the profile for tasks that do not say. Off by default: it costs a second session per task. A `bare` task (ADR 0080) never gets them. |
| 2 | What the blind worker sees | The task's goal (with continuation answers, as the session gets it), the repository at the base commit and the repository's rules. Not the implementing session's prompt, plan, branch, messages, handoff or report, and not the task's own `acceptance[]` (so the two are independent). It starts at the same time as the implementing session, in its own container, with the same egress and image. |
| 3 | What it writes | Commits on its own branch from the base commit, every changed path a test path by `TestIntegrity.Classify` (anything else refuses its output, naming the path), and a JSON report with `checks[]`: each an `AcceptanceCheck` (`command`, `exit_code`, `output`) that runs those tests, and per check one sentence of the goal it covers. At most 10 checks. Size class S; its spend counts toward the task's caps. |
| 4 | Do the tests test anything | Before they gate anything, each check runs on the base commit in a fresh container (`ContainerAcceptanceRunner`'s shape). For a goal that asks for a change, a check that already passes at the base is dropped as `passes_at_base`. A check that exits 126 or 127 or cannot start is dropped as `setup_failure`, as `bench --baseline` does. All dropped: the task goes on without blind tests and says so; it never fails for it. |
| 5 | Where they run on the implementation | In a fresh container, after the session's own gate passed: a new workspace at the session's branch, the blind commits cherry-picked on top, the kept checks run with `sh -c`, 10 minutes each. Never in the session's container, so the session cannot read the tests from its disk. A cherry-pick conflict (the session touched the same test file) is a failed blind check named `conflict`. |
| 6 | When one fails | The session is resumed (as in ADR 0066) with the failing check's command, the goal sentence it covers, the expected and actual exit and the output tail (4000 characters, redacted). Not the test source: the session fixes the change, it does not learn the test. The rounds share `check_retries` with the gate. The session may instead end with a question that says why it thinks the test contradicts the goal; that is `needs_input`, and the answer goes to both workers' next round. |
| 7 | Still failing | As any acceptance criterion: `checks_failed`, a `[partial]` draft pull request, `verification_failed` naming the blind checks. The `checks` artifact lists them with `kind: "blind"`. |
| 8 | What the pull request shows | The blind checks that ran, passed or were dropped and why, under the cost receipt. The blind tests are not committed to the pull request's branch in this version: they are an artifact (`blind-tests`, a patch reference and its hash, like `session-log`) so a reviewer can apply them. Committing them is an open choice below. |
| 9 | Where the code lives | A new module `Chargehand.Modules.Blind` (rewrite layout, ADR 0060): the worker's prompt, the report parser, the path rule, the baseline and the gate step. `SessionChecks` gains the `blind` kind; `TaskRunner` starts the blind worker beside the session. No new code references the old `src/Chargehand*` projects. |
| 10 | Prompt | `prompts/core/blind-tests.md`, a new block, gated by a new Prompt CI cell `driven/blind-tests` scored on how many seeded bugs the tests catch on the synthetic set. |

## Tasks (each one pull request)

1. **This spec.**
2. **ADR and contract**: ADR for decisions 1 to 8; `driven.tasks[].blind_tests` in `schemas/request/v1` (additive), the
   `blind` check kind, the `blind-tests` artifact.
3. **The worker and its output**: the prompt, the session start beside the implementing one, the report parser and the
   path rule (decisions 2, 3, 10).
4. **Baseline and gate**: decisions 4 to 7, the fresh-container run, the feedback turn.
5. **Seeded-bug set and the live batch**: `benchmarks/blind.json` and its fixtures (below), the live run, its result in
   the pull request with ids scrubbed.

## Acceptance checks

These run in `scripts/check.sh` unless marked live. No model is called in them; the workers are fakes.

- `BlindPromptTests`: the blind worker's prompt holds the goal and the base commit, and holds none of the implementing
  session's prompt, branch name, session id or the task's `acceptance[]`.
- `BlindPathRuleTests`: a blind branch that changes `src/Foo.cs` is refused, naming the path; one that changes only
  `tests/FooTests.cs` and `test_foo.py` is accepted.
- `BlindReportTests`: a report with 11 checks, an empty command or no goal sentence is refused with a named reason.
- `BlindBaselineTests` (fake runner): a check that passes at the base is dropped as `passes_at_base`; exit 127 is
  `setup_failure`; when every check is dropped the task goes on and the result says `blind tests: none kept`.
- `BlindGateTests` (fake runner, fake session): a failing blind check resumes the session once with the command, the
  goal sentence and the output tail, and the feedback text does not contain the test file's contents; after
  `check_retries` rounds the task is `checks_failed` and the `checks` artifact has `kind: "blind"`; a pass lets the
  handover run.
- `BlindIsolationTests`: the implementing session's workspace never holds a blind test path, before or after the gate.
- `BlindSeededBugTests` (real git in a temp directory, no container): a synthetic repository whose base has a stub
  `clamp(x, lo, hi)`; a correct branch and a seeded branch (`hi` exclusive, off by one) each with the session's own
  passing test that does not probe `x == hi`; a fixture blind test for `x == hi` fails at the base, fails on the seeded
  branch and passes on the correct one. This is the pipeline the live check repeats with a model.
- `SchemaCompatTests` stays green (`request/v1` change is additive); `ArchitectureTests` stays green.
- **Live, the done-when:** one batch on `benchmarks/blind.json` (the bench repositories, three change tasks whose goals
  name an edge case). After each session finishes, `bench` applies the task's recorded seed patch (a one-line bug in the
  edge case) on the session's branch and reruns the session's own tests and the repository's checks (they must pass,
  or the seed is not one the session missed) and the blind checks. At least one task shows the blind checks failing on
  the seeded branch and passing on the unseeded one. The output and spend go in Task 5's pull request.

## Risks

- **The blind tests can be wrong.** A test that misreads the goal fails a correct change. Decision 6 lets the session
  say so with a question instead of bending the change; decision 4 drops tests that test nothing. How often a correct
  change is failed is measured in Task 5 and reported, not assumed.
- **Cost.** One more session per task, size S. Off by default; the live batch reports the share.
- **Leaking the tests.** The failing output can quote an assertion. That is accepted: the output is what a person would
  see in CI too. The source never goes to the session.

## Out of scope

Blind tests for the OpenCode writer (ADR 0035); writing blind tests for read-only presets; more than one blind worker
(a panel); using the blind tests in the integrator's merge decision.

## Open choices

- Commit the blind tests to the pull request's branch after they pass, as a separate commit with the blind worker's
  trailers (more coverage in the repository, but a reviewer then merges tests no person asked for), or keep them an
  artifact (decision 8, the proposal).
- Whether the profile default should be on for repositories the integrator may merge without a person.
