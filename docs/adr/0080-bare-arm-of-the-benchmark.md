# 0080. A bare control arm for the benchmark

- Status: accepted
- Date: 2026-10-24

## Context

The benchmark ([ADR 0052](0052-quality-benchmark-on-public-repositories.md)) measures chargehand and nothing else. A pass rate of 70% does not say what chargehand adds: the same model on the same tasks, run bare, might pass 65% or 85%. `--baseline` does not help; it runs the acceptance scripts on the untouched pin, with no model, to show that the scripts work. Without a control arm, a change to a prompt or a limit can be compared with the last run, but chargehand's whole design (the skill's research and review, the governor of [ADR 0045](0045-governed-sessions.md), the check gate, continuations, the handover) cannot be compared with doing nothing.

A control is only worth having when everything but chargehand is held equal: the repository and commit, the goal text, the model, the caps, the image and network, and the acceptance script that judges the branch.

## Options

1. **A bare runner in the harness.** `chargehand bench run` would start a session container itself and run `claude -p`. The model credential, the egress proxy, the per-task token exchange and the output volume are the server's ([ADR 0039](0039-driven-writing-sessions.md)); the harness would copy them and would have to hold a real credential on the machine that runs it. Rejected: a second, different way to reach the model is a second thing that can differ between the arms.
2. **The Claude Code worker runtime directly.** The read-only workers of `orchestrate` run Claude Code without a shell or an edit tool and in no container. The bare agent has to write files and run the repository's tests. Rejected.
3. **The same session container and the same server path, with the driver's features off.** The batch asks for `arm: bare`; the server starts the usual session container with a task file that says `bare`, and the driver in the container runs `claude -p <goal>` and nothing else. Cost, credential, network, limits, telemetry and the results file are the ones the other arm already has. Chosen.

## Decision

Option 3. `chargehand bench run <set> --arm bare` (default `--arm chargehand`) sends the same batches as the other arm with `driven.arm = "bare"`, which needs `driven.bench = true` (the arm pushes a branch nobody has tested, so it is for the benchmark only; the server refuses it otherwise and refuses an unknown arm).

**What a bare task is.** One headless Claude Code session in the session image, on the checkout at the pinned commit, with the branch `chargehand/<run>` checked out, and:

- the same repository, commit, goal text (`-p <goal>` as written, with no `/chargehand:change` in front), image, network allowlist, model selection, per-task token and dollar caps, and turn, wall-clock and no-progress limits as the other arm (the limits bound cost; they are not chargehand's judgement of the work);
- the same tools as the other arm minus chargehand's own: `Bash,Read,Edit,Write,Glob,Grep`, permission mode `dontAsk`, an empty MCP config;
- none of: the plugin and the `change` skill, the appended system prompt and the environment note, research and review calls, the governor, the repository's check rules and the task's acceptance criteria (no check gate, no check retries), the goal reminder, planning (`plan: false` in both arms), continuation sessions, the stop snapshot's handoff and progress files, the partial draft pull request, the integrator and the CI fixer;
- one mechanical step at the end, which a person running the agent would do by hand: if the working tree has changes, the driver commits them in one commit ("Changes left by the agent") on the branch. The agent is not told to commit or to use the branch.

**What the server does with the branch.** If the session ended `completed` with at least one commit, the handover pushes the branch as the other arm's does, with two things removed: chargehand's own test run in a fresh container, and the pull request. The secret scan of the diff and history and the refusal of a change to CI configuration stay: they protect the push, not the measure. Any other end (a limit, a rate limit, a fault, no commits) leaves no branch: no salvage, no second session. The harness then runs the same acceptance script on the pushed branch, in the same kind of container, as for the other arm. Results use `bench-results/v1`; the bare arm's file has `"arm": "bare"` and the other arm's file is unchanged (the member is left out).

**Impossible tasks.** In the other arm an impossible task is an honest stop when it ends without a pull request, the protected files are unchanged and it did not call itself done. The bare arm has no pull request and no report. The nearest reading the output allows: a task is an honest stop when the agent **left no commits** (so nothing in the protected files changed, and nothing was handed over as work done); any commit counts as done and the task is `false_done` even when the guard holds. That is stricter than the other arm in one case: an agent that commits a note saying the task cannot be done is counted as having called it done. A session that is stopped on a limit or a fault is `aborted`, as in the other arm. The report states this difference.

**Question tasks.** The bare arm has no way to ask and gets no second round: the goal is as written, no answers are given, and the task passes if the branch passes the acceptance script. `AskedQuestion` is false for every bare task. This favours the other arm's rate on those tasks (it gets the answer), and the report says so.

**The report.** `bench report <chargehand results> <bare results>` (either order) puts the arms side by side: the pass rate and honest stops with their Wilson intervals, impossible tasks called done, tokens, dollar equivalent per task and per pass and turns, then every task with both outcomes and tokens, and the list of tasks where the arms differ. A single bare file renders as a report of its own, without the governor and test-integrity sections that only the other arm has. Two files of the same arm are compared as before.

**A session image from before this change** ignores the flag and would run the full session while the results call it the control. The driver writes `bare: true` into `session-outcome.json` and the host fails a bare task whose outcome does not carry it (`image_predates_bare_arm`). The spend of that session is not recovered, so run `--dry-run` and one task first (`--only <id> --arm bare`) after a new image.

## Consequences

- The control costs about what the other arm does, per task or less (no research, review or check rounds); UNKNOWN until measured. A full two-arm run is 13 tasks per arm, 26 sessions, plus the other arm's answered rounds. Run both on the same server profile, image and set, close together in time: the provider's model can change between days.
- Same caps does not mean same spend: the bare arm may stop sooner or run to the cap.
- The bare arm still pushes through the server's push credential, so the remote, the fork and the stub of the pull-request call are set up as for the other arm. It opens no pull request, so the stub is not asked.
- Results are comparable only when the set, its pins, the image, the profile's limits and the model are the same; the report checks the first two.
- Not checked: a real run. The arm is tested with the harness's fakes, a fake Claude Code process in the driver, the task runner on its fakes, and a real git handover; no container, no model and no real server were used.

## Reopen if

- The arms differ by one or two tasks on a repeat of the same arm: the spread between runs is larger than the difference this arm is meant to show, and the control needs repeats before it means anything.
- A bare agent leaves a branch that only says the task cannot be done and the stricter reading of `false_done` hides a real honest stop: read the final message of the stream instead of the commits.
- The harness gets a second control (a different model, Claude Code with only the plugin on), which would make the arm a list.
