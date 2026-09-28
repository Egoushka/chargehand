# `/change`: one prompt to a reviewed change (goal 0.5)

- Status: draft for the maintainer's review
- Date: 2026-09-29

## Goal

A Claude Code plugin, shipped from this repository, whose command takes one prompt (a goal) and ends with a local
branch holding a reviewed change. chargehand does the reading and the reviewing, each with citations checked against a
pinned commit; the Claude Code session the user is already in writes the code. The user never leaves the chat.

## Decisions (maintainer, 2026-09-29)

| # | Question | Decision |
|---|---|---|
| 1 | Audience | Anyone who installs the plugin. Issue trackers, memory and a model gateway are optional extras, never required |
| 2 | Where a run ends | A local branch with commits. The user pushes and opens the pull request |
| 3 | Review findings | Fix and review again, at most 2 fix rounds (3 reviews in all); what remains open goes into the report |
| 4 | Transport | The plugin declares an MCP server that starts `chargehand mcp` over stdio |
| 5 | Research | Intake decides; trivial edits get a short answer instead of a full research run |
| 6 | Intake questions | Asked in the chat as one batch, then the run continues |
| 7 | Cost | The preset's USD budget caps chargehand's calls; `--budget <usd>` overrides it; at the cap the run stops and reports |
| 8 | Input | Plain text, or a GitHub issue reference (`#12`, an issue URL) read through `gh` or a GitHub MCP server when one is present, else taken as text |
| 9 | Tests | The session runs the repository's test command when it finds one (CLAUDE.md, README, a standard build file); the result goes to the review |
| 10 | Report | A Markdown file committed on the branch in its own commit, so it is easy to drop before merging |
| 11 | Branch | `change/<short-slug>` from the current commit; refuse to start on uncommitted changes |
| 12 | Scope of 0.5 | The plugin and its marketplace entry, the command, a read-only `review` preset, and chargehand reviewing its own pull requests on the maintainer's runner. Memory and services in runs (0.6), other issue trackers and opening pull requests come later |
| 13 | Done when | The maintainer runs `/change` on 10 real tasks in two weeks and at least 7 end in a merged change without leaving the chat, plus a scripted end-to-end check on a sample repository |
| 14 | Name | `change`: the command is `/chargehand:change <goal>`, named for what it returns. It was `/ch` in earlier plans, which the plugin prefix would make `/chargehand:ch` |

## Components

| Unit | Where | What it does |
|---|---|---|
| Marketplace | `.claude-plugin/marketplace.json` (repository root) | Lists the `chargehand` plugin so `/plugin marketplace add Egoushka/chargehand` then `/plugin install chargehand@chargehand` works |
| Plugin manifest | `plugins/chargehand/.claude-plugin/plugin.json` | Name `chargehand`, version from `Directory.Build.props` (stamped at release like `.mcp/server.json`) |
| MCP server entry | `plugins/chargehand/.mcp.json` | `chargehand` server: `dnx Chargehand@<version> --yes -- mcp`. Needs the package on nuget.org (the release follow-up of ADR 0027); until then a development override runs `dotnet run --project src/Chargehand.Cli -- mcp` from a checkout |
| The command | `plugins/chargehand/skills/change/SKILL.md` | The flow below, as instructions to the session. Invoked as `/chargehand:change <goal>` |
| `review` preset | `presets/review.yaml` | Read-only; its intake takes the diff and test output as caller inputs and returns findings as claims, each citing the diff (`input` evidence) or a file at the new commit |
| Report writer | inside the skill | Writes `.chargehand/reports/<slug>.md` from the run results |

No new endpoint: both chargehand calls go through the existing `orchestrate` tool with `request/v1`.

## Flow

1. **Preflight.** Inside a git work tree, with no uncommitted changes, and the `chargehand` MCP server answering.
   Otherwise stop with the reason and the fix. If the input is a GitHub issue reference and `gh` or a GitHub MCP
   server is present, the issue's title and body become the goal.
2. **Research.** `orchestrate` with the goal, `context.repository` = (work tree, HEAD), the default preset, the
   budget. Intake picks `answer`, `split`, `ask` or `improve` as today. `ask`: the questions go to the user in one
   message (MCP elicitation where the client supports it, else `needs_input` relayed by the session), then the call
   is repeated with the answers. `improve`: the improved request is used. The result's claims, each with checked
   citations, are the session's context for the change.
3. **Branch.** `git switch -c change/<slug>` from the base commit.
4. **Write.** The session makes the change, guided by the research claims.
5. **Test.** Run the repository's test command if one is found; keep its exit code and the tail of its output.
6. **Commit.** One Conventional Commit for the change.
7. **Review.** `orchestrate` with preset `review`: the goal, the diff (base..HEAD) and the test result as caller
   inputs, `context.repository` = (work tree, HEAD). Findings are claims; unresolved citations become open questions
   as everywhere else.
8. **Fix loop.** If findings remain, the session fixes them, re-runs tests, commits (`fix: address review`), and
   reviews again. At most 2 fix rounds; then stop.
9. **Report.** Write `.chargehand/reports/<slug>.md`: goal, research summary with citations, what changed, test
   results, each review round's findings and whether it was fixed, open items, chargehand run ids and cost. Commit it
   separately (`docs: add /change report`).
10. **Hand back.** Show the branch name, the summary and the open items in the chat.

## Errors

- chargehand unreachable, runtime missing, or any `result/v1` error: stop before creating the branch when it happens
  in step 2; after that, keep the branch and put the error with its `action` in the report.
- Budget reached: stop where it happened; the report says which step and what is unfinished.
- Tests still failing after the last round: committed as is, listed as open in the report.
- Intake `deny`: stop with the reason; no branch.

## Testing

- `claude plugin validate --strict` over the plugin in CI.
- Unit tests for the `review` preset: schema validity, read-only permissions (like `ConfigFileTests`), and a
  scripted-runtime run that turns a diff input into findings with resolved `input` citations.
- End-to-end: a script runs `/chargehand:change` non-interactively (`claude -p`) on a small sample repository and checks
  the branch, the commits and the report. It needs a model, so it runs on the maintainer's self-hosted runner by label
  or by hand, like Prompt CI, not on every push.
- Dogfood: the done bar in decision 13.

## Out of scope for 0.5

Opening pull requests; issue trackers other than GitHub; memory and services in runs (0.6); chargehand writing code
itself (0.7); running without the Claude Code session (after 1.0).

## Open items

- Whether Claude Code also accepts plain `/change` when no other command has that name; if not, the documented command
  is `/chargehand:change`.
- The first `dnx` install on macOS prints a notice on stdout before the first MCP message (ADR 0027); check that
  Claude Code tolerates it, or pre-install in preflight.
- The `review` preset's intake prompt and its finding format need Prompt CI cells before they are relied on.
