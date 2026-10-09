---
name: run
description: Hand a prompt to chargehand, which does the whole job in a container (a driven session writes the change, chargehand re-runs the tests itself, pushes a branch and opens a draft pull request), and watch it from here. Use when the user runs /chargehand:run with a prompt.
argument-hint: <prompt>
allowed-tools: mcp__chargehand__orchestrate Monitor Bash(chargehand watch:*) Bash(chargehand show:*) Bash(chargehand cancel:*) Bash(git rev-parse:*) Bash(git status:*)
---

Prompt from the user: $ARGUMENTS

You hand this prompt to chargehand and observe. Do not do the task yourself: do not read the code to answer it, edit files, run its tests or open a pull
request, and if chargehand fails, report the failure instead of taking over. chargehand never merges; a person reviews the draft pull request.

## 1. Preflight

- Run `git rev-parse --show-toplevel` and `git rev-parse HEAD`. Outside a git work tree, stop: "Run this inside the repository." Create nothing.
- Run `git status --porcelain`. A session works from the committed `HEAD`, not from the working tree: if anything is uncommitted, say so in the hand-over line
  (step 2), and go on.
- Find the `orchestrate` tool of an MCP server whose name contains `chargehand` and that runs on a machine which can read this repository (an HTTP server).
  The plugin's own stdio server has no driven setup. If there is none, stop: "No chargehand server that can run driven sessions is connected; check `/mcp`."

## 2. Hand over

Call `orchestrate` with `request/v1`: `contract_version` = "request/v1", `text` = the prompt, `context.preset` = "driven", `context.interactive` = false,
`context.repository` = { path: the repository root, commit: HEAD (hex sha) }, and `driven` = { tasks: [{ id: "t1", goal: the prompt }], max_parallel: 1,
max_tokens_total: 3000000, max_usd_total: 5 }. A server on a subscription needs the token cap and one on an API key or gateway key needs the dollar cap, and the
skill cannot tell which, so it sends both; use the user's numbers instead if they named a cap, and say the caps in the hand-over line.

The call returns after about 10 seconds. Normally it is a tool error whose first block says the run "is still running" and names it, and whose last block is
`run-status/v1`: its `run_id` is the batch's id, and the run goes on. That error is expected, not a failure. Do not call `orchestrate` again for the same prompt:
it would start a second batch.

If the result is `result/v1` instead, the batch ended or was refused before it started:

- `failed` with `error.code` = `invalid_request` and a message that driven sessions are off: tell the user that driven sessions are off on that server and that
  turning them on is the owner's decision (`driven.enabled` in the server's profile). Stop; never edit a profile, and never try another server to get around it.
- Any other `failed` or `denied`: stop with `error.message` and `error.action`.

Tell the user in one line: the run id, the repository and commit, and any uncommitted changes that are not included.

## 3. Observe

Start `Monitor` with the command `chargehand watch <run-id>`, `timeout_ms` = 1800000 and a description such as "chargehand run <run-id>". Each line it prints is one
event of the batch. Pass on the ones that change what the user knows, in a short sentence each: the container started, the session's stage when it changes,
tests passed or failed, branch pushed, pull request opened (with its URL). Do not echo every line.

Events replay from the start on every `chargehand watch`. If the Monitor expires while the run is still going, start it again and pass on only what is new.

When the process ends, its exit code is the run's:

- exit 0, the last line is `run_finished: completed: ...`: give the user the pull request URL from the `pr_opened` line, the branch, the tests result and the summary.
  Say it is a draft that needs their review.
- exit 1: the run failed, was denied or was lost. Say which step it stopped at (the last lines say), and the error's next step if one is printed.
- exit 3: the session needs input. Give the user the questions from the last lines. Do not answer them yourself.
- exit 2: `watch` could not reach the server or was refused (no server or key, a wrong key, an unknown run). Print the message. The run is not affected. The server
  comes from `CHARGEHAND_URL` and the key from `CHARGEHAND_API_KEY` in the shell Claude Code runs in; tell the user which is missing and never ask them to
  paste a key.

`chargehand show <run-id>` prints a finished run from the run log of the machine it runs on, which only has the run when the server is on that machine.

## 4. Cancel

If the user asks to stop the run, run `chargehand cancel <run-id>`, then `chargehand watch <run-id>` under `Monitor` again to see it end. Stopping the Monitor or
ending this session does not cancel the run.
