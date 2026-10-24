---
title: "Benchmark on real repositories"
description: "chargehand bench runs a fixed set of tasks on pinned public repositories through driven batches and reports pass rate, honest stops, spend and the governor's false stops."
order: 15
section: "Guides"
---

`chargehand bench` ([ADR 0052](../adr/0052-quality-benchmark-on-public-repositories.md)) answers a question the [driven](driven.md) guide's measurements cannot: out of a fixed list of real tasks on real repositories, how many end in a change that does what was asked, how many impossible ones end in an honest stop, what each costs, and how often the [session governor](driven.md#limits-and-the-kill-switch) would have stopped work that went on to succeed. Run the same set before and after a change to a prompt, a preset, a limit or the governor, and compare.

**Status: on main, not yet released. The harness is built and tested with fakes; no real run has been made, so there are no results yet.** What was checked without a model is under "What was checked".

## The set

`benchmarks/core.json` (`bench-taskset/v1`) has 13 tasks over four public repositories, each pinned by commit. Every goal and every acceptance check was written for the set; none comes from a real run.

| Repository (pin) | Ecosystem | Licence | Tasks (kind, size) |
|---|---|---|---|
| `ardalis/GuardClauses` (`f96b823e4228`) | dotnet | MIT | `gc-positive` (change, S), `gc-email` (question, M), `gc-negative-impossible` (impossible, M) |
| `tj/commander.js` (`ba6d13ddb424`) | node | MIT | `cmd-option-deprecated` (change, S), `cmd-option-range` (change, M), `cmd-help-example` (question, M), `cmd-required-unless` (change, L) |
| `more-itertools/more-itertools` (`b21ec4ee8e13`) | python | MIT | `mi-argsort` (change, S), `mi-running-mode` (change, M), `mi-first-impossible` (impossible, M) |
| `JodaOrg/joda-money` (`39d5bbf91c01`) | maven | Apache-2.0 | `jm-min-max` (change, S), `jm-allocate` (change, L), `jm-scale-impossible` (impossible, M) |

Eight tasks can be done, three cannot (the goal contradicts an existing test the session may not touch), and two need a question answered (the goal leaves out a rule the repository does not contain: the answer is in the set, and chargehand gives it in a second batch). Go is missing because the session images have no Go toolchain; the Gradle repositories looked at each broke a rule of the images (a version read from git tags, a JDK 17 toolchain pin, many build plugins). Maven stands for the JVM until one fits.

A task has `id`, `repository`, `kind` (`change`, `impossible`, `question`), `size` (`S`, `M`, `L`: the governor's budget class), `goal`, `answers` (question tasks only) and `acceptance`. A repository has `name`, `ecosystem`, `url` (public https), `commit` (all 40 characters), `license` and `test` (the test command as an argument list, sent as `context.verify`). `acceptance` is `{ "script": "acceptance/x.sh" }` (a shell script next to the set, run as `sh -c`) or `{ "argv": [...] }`, with `timeout_minutes` optional. `{base}` in either is the pinned commit. The check runs from the root of the checkout, on the task's branch, in a fresh container, and passes when it exits 0. A script usually writes a test file of its own and runs it, so the session never sees what decides its success. For an impossible task the check is a guard: it must still pass, which means the protected tests are exactly as they were.

## What you need

- A server with [driven sessions on](driven.md#configure-it), reached by `--url` or `CHARGEHAND_URL` and `CHARGEHAND_API_KEY` (or the profile's `http` block). `driven.governor.mode` should be `observe`, the default: the false-stop rate means little once the governor stops work. A `prices` table gives the report its dollar equivalent; without it the spend is tokens only.
- Hosts for the restore of each ecosystem in `driven.network.allow` (the dry run names the ones missing), and a session image per ecosystem (`driven.images`; `driven.services.images` maps `maven` and `gradle` to the JDK image).
- A work directory the server may read: `repository_roots` must cover it. The default is `runs/bench/work` under the current directory; `--workdir` changes it. When the server reaches Docker through a runner, `--accept-root` names a directory under the runner's source roots for the acceptance checks.
- A place to push the branches. Nothing is ever pushed to an upstream. Either **a fork you own of each repository**, named as in the set (`guard-clauses`, `commander-js`, `more-itertools`, `joda-money`), holding the pinned commit and reachable with `driven.push_secret`: `--remote 'https://github.com/<you>/{name}.git'`. Or **a local bare repository per repository**, made by the run (`--remote 'file:///abs/path/{name}.git'`): it takes `chargehand/*` branches and nothing else, but the pull request call still needs an answer, so start the server with `CHARGEHAND_E2E_LOCAL_REMOTE=1` and `CHARGEHAND_E2E_GITHUB_API=http://127.0.0.1:4391/`, and run `scripts/bench-forge-stub.py` (it answers the pull request endpoints and keeps nothing).
- A cap per batch. A real run refuses to start without `--max-tokens` (input plus output tokens, binding on a subscription and with a key), `--max-usd` (binding with an API key) or both. A batch is only given a task it could afford at the driven preset's per-task cap, 2,000,000 tokens, so a token cap under that starts nothing: use 4,000,000 or more.

## Run it

First two checks that call no model:

```bash
chargehand --profile profiles/local.json bench run benchmarks/core.json --dry-run --check-pins
chargehand --profile profiles/local.json bench run benchmarks/core.json --baseline
```

`--dry-run` validates the set (every rule is listed at once), asks each upstream for its pin with `--check-pins`, checks that an existing clone is still at the pin and that every acceptance command exists, and prints what the profile lacks. It starts no container and makes no request to a server. `--baseline` clones the repositories and runs, in containers made like a task's verification, each repository's test command and each acceptance check on the pinned commit. The test command must pass, the check of a change must fail (the change is not made), and the guard of an impossible task must pass. Run it before the first real run on a new machine or image: it is how a missing toolchain or a blocked host shows up for free. A command that exits 126 or 127 (not executable, not found), or that cannot start at all, is a **setup failure**: the baseline fails the check whatever it expected and names the missing tool, and a real run leaves that task out of the score (`not_run`) instead of counting it as a failed or passed check. Without this, a missing `mvn` would make every "acceptance fails on the pin" check look right.

**Maven and Gradle need their own image.** The base session image has no `mvn` and no `gradle`. `driven.images` is the base; map the ecosystem in `driven.services.images` (`"maven": "<jdk image>"`, `"gradle": ...`), or joda-money's test command fails on a missing tool. A repository whose ecosystem has no entry there silently gets the base image; `--dry-run` prints a note when that happens for maven, gradle, go or rust. The first `--baseline` (2026-10-22) hit exactly this.

Then the run:

```bash
chargehand --profile profiles/local.json bench run benchmarks/core.json \
  --remote 'https://github.com/<you>/{name}.git' --max-tokens 4000000 --max-usd 10
```

`--only gc-positive,mi-argsort` runs some tasks (a repository with no selected task is skipped). The run prints each batch's id (`chargehand watch <id>` shows it), the questions, and one line per task when its check has run. It writes `results.json` and `report.md` to `runs/bench/<set>-<time>/` (`--out`), rewriting them after each repository so an interrupted run keeps what it measured. Ctrl-C stops the run, not the batch on the server (`chargehand cancel <id>`).

What it does, per repository: clones the pinned commit with `origin` set to the remote; sends one batch through `POST /v1/runs` with the preset `driven`, `plan: false` (the planner would resize and split the tasks being measured), the test command, the set's sizes and your caps; follows its events; if question tasks ended `needs_input`, sends a second batch that continues each with `continue_from` and the set's answers; fetches each task's branch from the remote and runs its acceptance check.

## Reading the report

```bash
chargehand bench report runs/bench/core-20261014-0900/results.json
chargehand bench report before/results.json after/results.json
```

A task **passes** when the batch ended it `completed` (a draft pull request exists, so chargehand's own verification passed) and its acceptance check exits 0. The pass rate counts the tasks that can be done. An impossible task is an **honest stop** when it did not end `completed`, its guard holds and it ended on its own account (`needs_input`, `verification_failed` or `invalid_result`); it is **false done** when it was called done or its protected files changed, and **aborted** when it ended on a limit or a fault. Question tasks are counted twice: how many asked, and how many passed after the answer.

Every rate is a count, a total and a Wilson 95% interval, for example `70% (7/10, 95% CI 40-89%)`: one task moves a rate by 5 to 8 points, and the interval says how little ten tasks prove. Spend is per task, per pass and in all: input plus output tokens, cache tokens, the dollar equivalent at the profile's prices (never a bill; blank without a price table), turns, continuation sessions and wall time. The tally is the last `session_progress` the server published, which can trail a session's end by one poll (5 s), raised to the task's own result when that has more.

**The governor.** Each decision the governor logged is a `warn`, a `checkpoint` or a `stop`. The report counts the tasks with a checkpoint or a stop (the decisions that end a session when the governor enforces), and the **false-stop rate**: the share of those tasks that went on to pass. It also gives the share of passes that carried such a decision. [ADR 0045](../adr/0045-governed-sessions.md) switches to `enforce` when false stops stay under 5%. With 13 tasks that rate has at most a handful of tasks behind it, so read the count before the percentage and repeat the run before trusting either. A checkpoint continues the work in a fresh session when enforced, so a false stop costs a restart; a stop at the full budget does not continue.

**Test integrity.** Tasks flagged by chargehand (every changed file a test or build file, for a goal that did not ask for that) are counted, and the pass rate is also given without them.

**Comparing.** (A chargehand file and a bare file are put side by side as arms; see the next section.) `bench report a.json b.json` puts the rates side by side with the change in points, says whether the pass-rate intervals overlap (they will, for small differences), lists the tasks whose outcome changed, and warns when the task sets, a pin or completeness differ. `results.json` (`bench-results/v1`) holds facts only; the report computes every rate, so an old run can be rendered again by a newer report.

## The bare arm: what chargehand adds

A pass rate alone does not say what chargehand adds over the same model run bare ([ADR 0079](../adr/0079-bare-arm-of-the-benchmark.md)). `--baseline` does not either: it only checks that the acceptance scripts work, with no model. `--arm bare` runs each task as **one plain headless Claude Code session** on the same pinned commit, with the same goal text, image, network, model selection, caps and limits, and judges the branch it leaves with the same acceptance script. What is left out: the `change` skill and its system prompt, research and review calls, the governor, the repository's checks and the check retries, the goal reminder, continuation sessions, the handoff and the partial draft, chargehand's own test run before the push, and the pull request. At the end the driver commits whatever is in the working tree in one commit if the agent did not; a branch with at least one commit is pushed (the secret scan and the refusal of CI changes stay), anything else leaves no branch.

```bash
chargehand --profile profiles/local.json bench run benchmarks/core.json --arm bare --dry-run
chargehand --profile profiles/local.json bench run benchmarks/core.json --out runs/bench/core-chargehand \
  --remote 'https://github.com/<you>/{name}.git' --max-tokens 4000000 --max-usd 10
chargehand --profile profiles/local.json bench run benchmarks/core.json --arm bare --out runs/bench/core-bare \
  --remote 'https://github.com/<you>/{name}.git' --max-tokens 4000000 --max-usd 10
chargehand bench report runs/bench/core-chargehand/results.json runs/bench/core-bare/results.json
```

Run the arms on the same server profile and session image, close together in time. A full two-arm run is 13 tasks per arm. The report puts the arms side by side (pass rate and honest stops with their intervals, impossible tasks called done, tokens, dollars and turns per task), then every task with both outcomes and tokens, and the list of tasks where the arms differ. The bare arm's `results.json` has `"arm": "bare"`; the other arm's file has no such member. The session image must be built from a release that has the arm: an older image runs the full session, and the server then fails the task as `image_predates_bare_arm` after the spend. Try `--only <id> --arm bare` once after a new image.

Two differences from the other arm are in the report. An **honest stop** is an impossible task that left **no commits**; any commit counts as done (`false_done`), even a note that says the task cannot be done, because the bare output has no pull request and no report to read the claim from. And **question tasks get no second round**: the bare agent cannot ask and is not given the answers, so those tasks favour the other arm.

## Cost

The reference is what the driven guide measured: sessions on small repositories used 6,200 input plus output tokens each (74,206 over 12 sessions; 72,442 in the second set) and 10,100 each on a real TypeScript repository (101,000 over 10), with 70 to 90 times that in cache tokens; one task through a gateway cost $0.17 for 2,837 tokens ([Try it end to end](driven.md#try-it-end-to-end)). The set has 13 tasks and up to 2 answered rounds, so 13 to 15 sessions: about 90,000 to 150,000 input plus output tokens before continuations. At the gateway run's ratio ($0.17 for 2,837 tokens) that is $5 to $9. These are small repositories and trivial goals; the L tasks, a slow restore and a continuation each add to it, so plan for $5 to $25 per full run and set `--max-usd` accordingly. On a subscription there is no dollar figure, only the token cap. The ceiling a run cannot pass is the number of batches (four, and one more for each repository whose question tasks asked: six at most) times the cap you gave. `--dry-run` and `--baseline` cost no model money; `--baseline` runs 17 containers (a test command per repository and a check per task), each with its own restore, and takes about as long as the repositories' own test suites (on a development machine: `mvn -B -q test` 47 s on joda-money, `node --test` 53 s on commander.js, `python3 -m unittest` 63 s on more-itertools, `dotnet test` 45 s on GuardClauses).

## What was checked

Without a model: the four pins exist on GitHub (`--check-pins`); each acceptance check fails on the pinned commit and passes against a reference implementation written for it (run with each repository's toolchain on a development machine, not in the session image), and each guard holds on the untouched checkout (the .NET one also fails when a test file changes); each repository's test command passes on its pin. The harness's own logic (the task-set rules, the arithmetic of the report, the comparison, the batch flow, the container call of an acceptance check, the git work) has unit tests. **Not checked:** a baseline or a run in the real session image and engine, so the toolchain in the image is UNKNOWN for the .NET repository (the test project targets .NET 8 and the image has the .NET 10 SDK; the command sets `DOTNET_ROLL_FORWARD=Major`) and for Maven (the image has the distribution's Maven, a development machine had 3.10).

## Limits

Thirteen tasks measure a collapse or a big difference; they cannot rank two versions a few points apart. One run per task: the spread between runs of the same task is UNKNOWN until a set is repeated. The repositories are public and a model may have seen them; the goals and the checks are new. An acceptance check is a test written by one person, and a wrong implementation can pass it; each check was written to leave little room, and a second independent check per task is the next step if one is passed wrongly. Results are only comparable when the set, its pins, the session image, the profile's limits and prices, and the governor mode are the same; the report warns about the first two only.
