# 0052. A quality benchmark on public repositories

- Status: accepted
- Date: 2026-10-13

## Context

What is measured about driven sessions so far is small. `scripts/driven-e2e.sh` ran 12 sessions on two tiny synthetic repositories with trivial goals (adherence to the `change` skill's steps, 74,206 input plus output tokens), five goals on one real TypeScript repository (101,000 tokens), and single batches on a VPS and through a gateway ([driven guide](../guide/driven.md#not-done-yet)). None of it says how often a task ends in a change that does what it was asked, how often a task that cannot be done ends in an honest stop, what a task costs by size, or how often the session governor of [ADR 0045](0045-governed-sessions.md) would have stopped work that went on to succeed. That ADR switches the governor from `observe` to `enforce` when false stops stay under 5%, and nothing counts them yet.

Every change to a prompt, a preset, a limit or the governor needs the same question answered on the same work, so the work has to be fixed: pinned repositories, written goals, a check that decides success.

## Options

1. **Grow `driven-e2e.sh`.** It already starts a server and a stub forge. It is one batch, judges only what the session reports, and a shell script is a poor place for statistics, comparison and a task set.
2. **Use an external suite** (SWE-bench style). Its tasks are issue fixes in a few ecosystems, its harness has its own container model, and its repositories and fixes are in models' training data. It would not run through chargehand's sessions, limits or governor, so it measures something else.
3. **A task set of our own on pinned public repositories, run through the server's normal API, judged by checks that chargehand runs itself.** The goals are written for the set, so they are synthetic; the repositories are public and open source; every check is written before the run and kept out of the session.

## Decision

Option 3: `chargehand bench run <taskset>` and `chargehand bench report <results.json> [<other.json>]`.

**The task set** (`bench-taskset/v1`, JSON under `benchmarks/`) lists 4 to 6 repositories, each by https URL, full commit, licence and test command, and 12 to 20 tasks. A task has a goal, a size class (S, M or L, which is the governor's budget class), a kind and an acceptance check.
- `change`: the goal can be done. The check says whether it was.
- `impossible`: the goal contradicts a test or itself. The right end is a stop with a reason. Its check is a guard that must still pass on any branch the session leaves (the protected tests are unchanged).
- `question`: the goal leaves out something the session cannot find in the repository. It should end `needs_input`; chargehand then answers from the set's `answers` in a second batch (`continue_from`, [ADR 0045](0045-governed-sessions.md)) and checks the result.

An acceptance check is a shell script (or a command) that exits 0 on the task's branch. It writes its own test file into a fresh checkout, so the session never sees it, and `{base}` stands for the pinned commit. The shipped set has 13 tasks over four repositories (.NET, Node, Python, Maven). Go has no toolchain in the session images, and the Gradle repositories checked before this change each failed one rule of the images (a version taken from git tags, a pinned JDK 17 toolchain, six build plugins), so neither is in the set yet.

**The run.** One driven batch per repository through `POST /v1/runs` with the preset `driven`, `plan: false` (the planner would resize and split the tasks being measured), the repository's test command as `context.verify`, the set's size per task, and a token or dollar cap per batch that a real run must name. The repositories are cloned at their pins into a work directory the server can read, with `origin` set to a place the server may push: a fork the owner has (`--remote`), or a local bare repository that takes `chargehand/*` branches only. Nothing is pushed to an upstream. The events of each batch give the tally, the turns, the continuations, the governor's decisions and the questions. Then each branch is fetched and its acceptance check runs in a fresh container made the way a task's own verification is: a new workspace at the pinned commit, the branch from a bundle, a network whose only way out is the allowlist proxy, no credential in the environment, one record as the verdict.

**Two modes start no model.** `--dry-run` checks the set, the pins (`--check-pins` asks each upstream) and that every acceptance command exists. `--baseline` runs each repository's test command and each acceptance check on the pinned commit in containers, and expects the checks of a change to fail and the guards to hold. A set that passes it has checks that work in the real image before any money is spent.

**What counts.** A task of kind `change` or `question` passes when the batch ended it `completed` (chargehand's draft pull request exists, so its own verification passed) and its acceptance check exits 0. An `impossible` task is an honest stop when it did not end `completed`, its guard holds, and it ended on its own account (`needs_input`, `verification_failed`, `invalid_result`), not on a limit or a fault. It is `false_done` when it was called done or its protected files changed, and `aborted` otherwise. The report gives pass rate overall, by size and by ecosystem; pass rate without a test-integrity flag ([ADR 0046](0046-integrator-and-ci-fixer.md)); honest-stop rate; how many question tasks asked and how many passed after the answer; tokens including cache and the dollar equivalent per task, per pass and in all; turns; continuations; wall time; and the governor. The false-stop rate is the share of tasks with a checkpoint or stop decision that went on to pass; the report also gives the share of passes that carried one. Every rate carries its count and a Wilson 95% interval, since a task moves a rate by 5 to 8 points. `results.json` holds facts only; the report computes every rate and outcome from them, so a later version re-renders an old run, and `bench report a.json b.json` compares two, warning when the set, a pin or completeness differ.

**Placement.** `src/Chargehand.Automation/Bench/`: a client of `Chargehand.Contracts` that reuses Driven's container pieces for the acceptance check. Core and Driven do not reference it ([ADR 0047](0047-core-and-driven-assemblies.md)). Core gains one public function, `RepositoryDetector.HostsFor`, for the package hosts of an ecosystem.

## Consequences

- A change to a prompt, preset, limit or the governor can be run on the same 13 tasks before and after, and the comparison says when the difference is inside the noise. With 13 tasks it usually will be; the set is for catching a collapse and for the governor's counts, not for ranking versions that differ by a few points. More tasks mean more cost, and the cost grows linearly.
- The pass rate is the rate of this author's checks on this author's goals. The repositories are public, so a model may have seen them; the goals and the checks are new, which limits that. It is not a leaderboard.
- Acceptance checks that are tests can be passed by an implementation that is wrong in a way the test does not see. The checks were each validated against a reference implementation written for the purpose, and fail on the pinned commit.
- A real run needs a server with driven sessions on, forks to push to (or the local remote with the E2E switches, which exist for that script and this), and the allowlist hosts the dry run names.
- The false-stop rate is only meaningful in observe mode. In enforce mode the governor stops the work it counts, and the report says so.

## Reopen if

A Go or Gradle repository fits the session images; the set grows past 20 tasks and a run no longer fits an afternoon; checks that were passed by wrong implementations turn up (then add a second, independent check per task); the false-stop count needs more than one run per task to mean anything (repeat each task and report the spread); or another suite becomes cheap to run through the driven path.
