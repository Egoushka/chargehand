# Blind tests: a second worker writes acceptance tests from the issue alone

- Status: accepted 2026-11-11 (maintainer approved the proposed choices below).
- Date: 2026-11-11
- Issue: #789 (one of nine ideas approved on 2026-10-18)
- Builds on: [ADR 0039](../adr/0039-driven-writing-sessions.md) (driven sessions, handover),
  [ADR 0046](../adr/0046-integrator-and-ci-fixer.md), [ADR 0049](../adr/0049-dependent-tasks-and-planning.md) (tasks
  and `depends_on`), [dynamic fan-out](2026-10-18-dynamic-fan-out-design.md) (sibling tasks in one batch)

## Problem

The worker that writes a change also writes its tests, so its tests check what it built, not what was asked. A green
verification then proves little: `ContainerVerifier` runs whatever tests the branch holds, and `TestIntegrity` and the
mutation check (issue 788, `Handover.Mutation.cs`) only ask whether the tests are present and sharp, not whether they
came from the requirement. A second worker that has seen only the issue cannot copy the implementer's misreading.

## Design

1. **A blind task is a sibling task in the same batch.** For a task that opts in (`driven.blind_tests: true`, request
   and preset; off by default), `PlanApplier` (`BatchPlanner.cs`) adds one task `<id>.blind` with no `depends_on`. It
   gets the same repository at the same base commit and a new preset `presets/blind-tests.yaml` (a `driven` block like
   `presets/driven.yaml`, its own smaller `max_usd`).
2. **Isolation is by construction, not by instruction.** The blind task's goal is the issue title and body, nothing
   else: no plan, no implementer prompt, no diff. It has no `depends_on`, so it never starts from the implementer's
   branch (the join in ADR 0049 "Stacking" is the only path code travels between tasks). Its container is a separate
   session with its own work volume (`RunnerNames.Work(run)`), and the two tasks share no volume except the read-only
   caches of `SharedServicesPlan`. Neither session is told the other exists.
3. **Output is tests only.** At the blind task's handover the changed paths pass through `TestIntegrity.Classify`; any
   path that is not `ChangeKind.Test` fails the blind task as `blind_not_tests`. A blind task never opens a pull request:
   `Handover` takes the existing `Bare` input (push nothing, keep the bundle) and the bundle is the task's artifact.
4. **The implementer must pass them before its own handover.** `BranchVerification` (Handover.cs) gains
   `ExtraBundles`. `ContainerVerifier` applies each one on top of the branch inside `chargehand verify-branch`
   (`VerifyBranchCli.cs`, new `--tests-bundle`) and runs the repository's test command. The verification record gains
   `BlindTests` (count, failed, names). A failure is fed to the implementer as a continuation, within the existing
   `MaxContinuations` and `CheckRetries` (`DrivenLimits.cs`); it sees the failing test names and output, not the test
   source path of the bundle's other files. After the retries the task is `partial` and the pull request body says
   which blind tests still fail. The blind tests are then added to the pull request's branch as a separate commit only
   when they pass.
5. **A broken blind test is not the implementer's fault.** If the blind tests fail on the base commit with the
   feature absent for the wrong reason (build error, missing symbol the issue never named), the check runs once on the
   base commit: tests that fail to compile there are dropped and reported as `blind_uncompilable`, not charged to the
   implementer. Tests that compile and fail on base are the expected red.
6. **Cost.** The blind task counts in the batch cap and `Fits` like any task; `max_parallel` is unchanged.

## Out of scope

Property or fuzz tests, running the blind tests in CI after merge, a blind reviewer of the implementation, choosing
which tasks to opt in automatically, non-C# test runners beyond what `VerifyPlan` already detects, and any change to the
mutation check.

## Decisions (maintainer, 2026-11-11)

1. Opt-in per task (above) or default on for tasks with a stated acceptance section. Decided: opt in until a batch
   shows the cost is worth it.
2. Whether the blind tests are committed to the pull request on success. Decided: yes, as their own commit.

## Acceptance checks

Each is a command a machine runs; each test named here is written in the implementing PR.

1. `dotnet test --filter "FullyQualifiedName~BlindTaskPlanTests"` passes: `PlanApplier` adds `<id>.blind` with no
   `depends_on` for an opted-in task and nothing for a task that did not opt in.
2. `dotnet test --filter "FullyQualifiedName~BlindIsolationTests"` passes: the blind task's goal equals the issue text,
   its start commit is the base commit, and its work volume holds no path from the implementer's bundle (fake engine,
   real git, as in `BenchAcceptanceTests`).
3. `dotnet test --filter "FullyQualifiedName~BlindTestsOutputTests"` passes: a blind bundle that touches a production
   file fails with `blind_not_tests`.
4. `dotnet test --filter "FullyQualifiedName~ContainerVerifierTests"` passes with new cases: the extra bundle is applied
   and its failures appear in `VerificationRecord.BlindTests`; the verifier container still gets no model credential.
5. `dotnet test --filter "FullyQualifiedName~HandoverBlindTests"` passes: a failing blind test triggers a continuation,
   and after `MaxContinuations` the task ends `partial` with the failing test names in the pull request body.
6. `dotnet test --filter "FullyQualifiedName~BlindUncompilableTests"` passes: tests that do not compile on the base
   commit are reported `blind_uncompilable` and do not fail the implementer.
7. `dotnet test tests/Chargehand.Architecture.Tests` passes (no new reference from Core to Driven).
8. `scripts/check.sh` exits 0 and its last test line reads `Passed!  - Failed:     0`.
9. **Seeded-bug batch (the issue's "done when").** `scripts/blind-seed-batch.sh` runs one batch of a fixture
   repository under `evals/` whose issue states a rule with a boundary case (for example "a discount applies to orders of
   100 or more"). The fixture's implementer prompt is pinned to ship the off-by-one (`> 100`) and a test that matches
   it. The script exits 0 only when its JSON report shows: the implementer's own tests pass; the verification record
   `BlindTests.failed >= 1` naming the boundary test; the first verification before continuation fails; and
   `blind_tests: false` on the same batch ends with a green verification and the bug shipped. Run with the example
   profile and the eval OpenCode server (`scripts/prompt-ci.sh` conventions), once per release of the preset.
10. `betterleaks git --redact -v` reports no leak, and `.githooks` denylist passes on the PR (no real names or ids in
    the fixture).
