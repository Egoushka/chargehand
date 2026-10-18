# 0066. A check gate, acceptance criteria and a goal reminder for driven sessions

- Status: accepted
- Date: 2026-10-18

## Context

A driven session (ADR 0039) ends when Claude Code prints its result. chargehand then re-runs the repository's test command in a fresh container
(the handover's `verification`), and a red run ends the task as `verification_failed`. The session never hears why: the failure is a dead end
for the task, even when it is a one-line fix the session could make. A repository also has rules that are not its test command (a line budget,
a lint, a generated file that must be current), and a task can have a concrete definition of done (a command that must print a value) that
only a person reading the pull request checks today.

Two outside reports point the same way. Rejecting an agent's change that breaks a project rule and feeding the failure back as the next turn
makes the rule hold without a person in the loop. Machine-checkable acceptance criteria let a cheaper model work unreviewed; in long runs
the original goal is re-sent on a timer, because the model drifts from it.

## Options

1. Feed the handover's verification failure into a continuation session (ADR 0044). Reuses the seed and handoff path, but a new session
   starts cold, costs a full prompt, and the verification container has no toolchain beyond the test command.
2. Run the checks inside the session container after Claude Code finishes, and resume the same Claude Code session (`--resume`) with the
   failure as its next turn. The session keeps its context; the container has the toolchain the session used; the checks run before the
   bundle is made, so what is handed over already passed them.
3. Ask the session to run the checks itself (prompt text). Free, but it is the session's claim, the same thing `session-tests` already is.

For the goal reminder: a new user turn needs Claude Code's streaming input, which changes how a session ends; a `PostToolUse` hook can add
context after any tool call without a new turn.

## Decision

Option 2. The session driver runs the gate when the session finished on its own (a result, no error, no question, no stop):

- **Rules** are the lines of `.chargehand/checks` at the commit the workspace started at, read with `git show`, so the session cannot weaken
  them by editing the file. One shell command per line, `#` comments.
- **Acceptance criteria** come with the task (`driven.tasks[].acceptance[]`: `command`, `exit_code` default 0, optional `output` the
  combined output must contain).
- Each runs with `sh -c` in `/work`, 10 minutes at most. A failure resumes the session with the failed commands, what each was expected to
  do and the end of its output (4000 characters, secrets redacted), up to `check_retries` times (default 2) and within the wall clock.
  Still failing, the session ends `failed` with reason `checks_failed`; its commits are salvaged as a partial draft pull request like any
  failed session's, and the result's error is `verification_failed` naming the checks.
- The result carries a `checks` artifact: each check's kind, command, expected and actual exit, expected output, passed, and the round.
- The goal reminder writes the goal and the criteria for a `PostToolUse` hook (passed with `--settings`) every `goal_reminder_minutes`
  (default 60; 0 is off) or `goal_reminder_turns` assistant messages. The hook claims the file by renaming it, so a reminder is delivered once.

`check_retries`, `goal_reminder_minutes` and `goal_reminder_turns` are driven limits (defaults in code, `driven.limits` overrides), not
`presets/driven.yaml` keys, for the reason the guide gives for the other limits: a preset edit costs a Prompt CI run.

## Consequences

- The handover's own verification stays: it is chargehand's run in a fresh container; the gate is a run in the session's container, before it.
- A resumed process ends in a result of its own; the driver sums the results' tokens and dollars, so the outcome is the session's total.
- The gate executes repository-controlled commands, but only inside the session container, which already runs the session's commands.
- The OpenCode writer (ADR 0035) keeps its own verify loop; it does not read `.chargehand/checks` yet.
- Reminders are measured only as queued (`goal_reminders` in the session outcome), not as read by the model.

## Reopen if

- Resumed sessions lose their context or their prompt cache often enough that a continuation session (option 1) is cheaper.
- A repository needs a check that cannot run in the session image.
- Claude Code changes how `PostToolUse` hooks add context.
