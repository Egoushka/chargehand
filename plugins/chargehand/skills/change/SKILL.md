---
name: change
description: One prompt to a reviewed change. chargehand researches the goal with citations checked against the current commit, you write the change on a new local branch, chargehand reviews it, you fix what it finds (at most 2 fix rounds), and a report is committed. Use when the user runs /chargehand:change with a goal or a GitHub issue reference.
argument-hint: <goal or #issue> [--budget <usd per call>]
---

Goal from the user: $ARGUMENTS

## Driven mode

If the environment variable `CHARGEHAND_DRIVEN` is `1`, no person is watching: chargehand started this session inside a container and will check the
branch itself, push it and open a draft pull request. Follow the steps below with these changes, and only these:

- Step 1: the working tree is already clean, on a branch chargehand made; do not stop for it.
- Steps 2, 7 and 8: where a step would ask the user questions, stop working, print a line `NEEDS_INPUT:` followed by the questions, one per line, and
  finish. Do not guess the answers.
- Step 3: the branch named in the environment variable `CHARGEHAND_BRANCH` already exists and is checked out; use it instead of creating `change/<slug>`.
- Step 9: do not write or commit the report file; chargehand records the outcome itself.
- Step 10: do not push and do not open a pull request. Finish with one fenced ```json block, `{"summary": ..., "claims": [...], "tests": {...}}`, as the
  session's system prompt describes it: each claim names the code lines it rests on, and what you did not verify is said, not guessed.

Follow these steps in order. In steps 1 and 2 a failure stops the run with nothing created: tell the user why and what
fixes it. From step 3 on the branch exists: any failure (git, a commit hook, a write, a chargehand error) keeps the
branch and goes to step 9, recording the step and the error with its action.

## 1. Preflight

- Run `git rev-parse --is-inside-work-tree` and `git status --porcelain`. Outside a git work tree, or with any
  uncommitted change, stop: "Commit or stash your changes first, then run the command again." Create nothing.
- Find the `orchestrate` tool of an MCP server whose name contains `chargehand` (the plugin's own server comes first).
  If none is available, stop: "chargehand's MCP server is not running. It needs the .NET 10 SDK; check `/mcp` for its
  error." Create nothing.
- Parse `--budget <usd>` out of the arguments if present (it applies to each chargehand call, not the whole run); the rest is the goal. If the goal is a GitHub issue
  reference (`#12`, or an issues URL) and `gh` or a GitHub MCP server is available, read the issue and use its title
  and body as the goal; otherwise use the text as given.
- Record the base commit: `git rev-parse HEAD`.

## 2. Research

Call `orchestrate` with `request/v1`: `contract_version` = "request/v1", `text` = "Answer without changing anything:
which files, functions and tests does this change touch, and how do they work today? The change: " followed by the
goal (the research preset is read-only; a goal sent as is reads as an edit request and fails), `context.preset` =
"default", `context.repository` = { path: the repository root, commit: the base commit (hex sha) },
`context.interactive` = true, and `context.budget_usd` when `--budget` was given.

The result is `result/v1`; its `status` is one of `completed`, `needs_input`, `failed`, `denied`.

- `needs_input` or questions: ask the user all of them in one message, then call again with the answers added to the
  text.
- `denied`: stop with the reason. Create nothing.
- `failed`: stop with `error.message` and `error.action`. Create nothing.
- `completed`: keep the claims and their citations; they are your map of the code. Claims listed under open questions
  are unverified; do not rely on them without reading the file yourself.

## 3. Branch

Make a slug: lower-case, words from the goal joined by `-`, only `a-z0-9-`, at most 40 characters. Create
`change/<slug>` from the base commit with `git switch -c`. If that branch exists, use `change/<slug>-2`, then `-3`,
and so on.

## 4. Write

Make the change the goal asks for, guided by the research claims. Keep it to what the goal needs.

## 5. Test

Find the repository's test command: CLAUDE.md or AGENTS.md first, then the README, then a standard build file
(`package.json` scripts.test, `*.sln`/`*.slnx` → `dotnet test`, `pyproject.toml` → `pytest`, `Cargo.toml` →
`cargo test`, `go.mod` → `go test ./...`). Run it if found. Keep the exit code and the last 200 lines of output. If
none is found, the test result is "no test command found". A failing test run is a result, not a failed step:
continue.

## 6. Commit

One commit with a Conventional Commit message that describes the change.

## 7. Review

Diff input: `git diff <base>..HEAD`. If it is longer than 60000 characters, keep the first 60000 characters and
append the line `[diff truncated at 60000 characters]`.

Call `orchestrate` with `request/v1`: `contract_version` = "request/v1", `text` = "Review the change against its
goal.", `context.preset` = "review", so preset "review" runs, `context.repository` = { path, commit: HEAD (hex sha) },
`context.interactive` = false, the budget as in step 2, and `inputs`:
`[{ "id": "goal", "kind": "goal", "text": <goal> }, { "id": "diff", "kind": "diff", "text": <diff input> },
{ "id": "tests", "kind": "test-output", "text": <exit code and output> }]`.

Each claim in the result is a finding. `needs_input`: record the questions as open items, keep the branch, go to
step 9. `failed` or `denied`: keep the branch, go to step 9 and record the step,
`error.message` and `error.action`.

## 8. Fix loop

If there are findings, fix the ones that hold (read the cited lines first; a finding you judge wrong goes to the report
with your reason), rerun the tests, commit with `fix: address review`, and review again as in step 7. Do at most 2 fix
rounds, 3 reviews in all. Findings left after the last review are open items, and so are tests still failing after the last round (commit as
is).

## 9. Report

Write `.chargehand/reports/<slug>.md` from `report-template.md` in this skill's directory
(`${CLAUDE_PLUGIN_ROOT}/skills/change/report-template.md`). Commit it on its own: `docs: add /change report`.
If a result has `error.code` = `cost_cap_reached`, say which step and what is unfinished.

## 10. Hand back

Tell the user: the branch name, one paragraph on what changed, the tests result, the open items, and that the report
commit can be dropped before merging (`git reset --keep HEAD~1` while it is the last commit).
