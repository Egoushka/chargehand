---
title: "The change command"
description: "/chargehand:change takes one goal to a reviewed change on a local branch: installing the plugin today, the steps it runs, and what --budget bounds."
order: 5
section: "Guides"
---

`/chargehand:change <goal>` takes one prompt to a reviewed change on a local branch `change/<slug>`. chargehand researches the goal with citations checked against the current commit, your Claude Code session writes the change and runs the tests, chargehand reviews the diff with the `review` preset, the session fixes what holds (at most 2 fix rounds), and a report lands in `.chargehand/reports/<slug>.md` as its own commit. Nothing is pushed.

The plugin, the command and the `review` preset are on main, not yet released. The command is goal 0.5 in [ROADMAP.md](../../ROADMAP.md).

## Install the plugin

```text
/plugin marketplace add Egoushka/chargehand
/plugin install chargehand@chargehand
```

The plugin starts chargehand through `dnx`: its [MCP entry](../../plugins/chargehand/.mcp.json) runs `dotnet dnx Chargehand@0.3.0 --yes -- mcp`. That needs the .NET 10 SDK and a published `Chargehand` package, and the package is not on nuget.org yet. After `/plugin install`, the plugin's own server cannot start.

## Point it at a checkout for now

Until the package is published, configure an MCP server named `chargehand` that runs a checkout:

```json
{"mcpServers": {"chargehand": {"command": "dotnet", "args": ["run", "--project", "<checkout>/src/Chargehand.Cli", "--", "mcp"], "env": {"CHARGEHAND_PROFILE": "<checkout>/profiles/local.json"}}}}
```

The profile needs a `models` map from the presets' placeholder ids to real models; `scripts/change-e2e.sh` writes a minimal working one, shown in the [Quickstart](quickstart.md#write-a-profile). The command uses the `orchestrate` tool of an MCP server whose name contains `chargehand`, preferring the plugin's own. [Use it from an MCP client](mcp.md#over-stdio-from-a-checkout) explains what the server's working directory decides.

## What the command does

The command is a skill, [SKILL.md](../../plugins/chargehand/skills/change/SKILL.md), that your Claude Code session follows step by step. In steps 1 and 2 a failure stops the run and creates nothing. From step 3 on the branch exists: a failure keeps the branch and jumps to the report, which records the step, the error and its action.

1. **Preflight.** The session checks that you are inside a git work tree with no uncommitted change. Otherwise it stops: "Commit or stash your changes first, then run the command again." It finds the `orchestrate` tool, or stops and says the server is not running. It reads `--budget <usd>` from the arguments. A GitHub issue reference (`#12` or an issues URL) becomes the goal through `gh` or a GitHub MCP server when one is available. It records the base commit.
2. **Research.** One `orchestrate` call with preset `default`, the repository at the base commit, `interactive: true`, and a text that asks which files, functions and tests the change touches and how they work today, without changing anything. On `needs_input` the session asks you all the questions in one message and calls again with the answers. `denied` or `failed` stops the run. On `completed`, the claims and their citations become the session's map of the code. Claims that landed in open questions did not resolve, so the session reads those files itself before it relies on them.
3. **Branch.** `git switch -c change/<slug>` from the base commit. The slug is the goal's words in lower case joined by `-`, only `a-z0-9-`, at most 40 characters. If the branch exists, the session tries `-2`, `-3` and so on.
4. **Write.** The session makes the change the goal asks for, guided by the research claims.
5. **Test.** The session looks for the test command in CLAUDE.md or AGENTS.md, then the README, then a standard build file: `package.json` scripts.test, a `*.sln` or `*.slnx` for `dotnet test`, `pyproject.toml` for `pytest`, `Cargo.toml` for `cargo test`, `go.mod` for `go test ./...`. It keeps the exit code and the last 200 lines of output. A failing test run counts as a result, and the command goes on.
6. **Commit.** One Conventional Commit for the change.
7. **Review.** One `orchestrate` call with preset `review`, the repository at `HEAD`, `interactive: false`, and three inputs: `goal`, `diff` (`git diff <base>..HEAD`, cut at 60000 characters) and `tests`. Each claim in the result is a finding. On `needs_input` the questions become open items; on `failed` or `denied` the report records the step and the error. Either way the branch stays and the session goes to the report.
8. **Fix loop.** The session fixes the findings that hold, after reading the cited lines; a finding it judges wrong goes into the report with the reason. It reruns the tests, commits `fix: address review`, and reviews again. At most 2 fix rounds, so 3 reviews in all. Findings left after the last review, and tests still failing, become open items.
9. **Report.** `.chargehand/reports/<slug>.md` from [report-template.md](../../plugins/chargehand/skills/change/report-template.md), committed on its own as `docs: add /change report`. Its sections: Goal, Research, Change, Tests, Review rounds, Open items, Runs. If a result carries `error.code` `cost_cap_reached`, the report says which step stopped and what is unfinished.
10. **Hand back.** The session tells you the branch name, what changed, the test result and the open items. It also tells you that you can drop the report commit before merging with `git reset --keep HEAD~1` while it is the last commit.

## Budget

`--budget <usd>` sets `context.budget_usd` on each chargehand call, not on the whole run, and a run makes up to four calls: one research and up to three reviews. Without `--budget`, each call is bounded by the preset's per-node `max_usd` ($1.00 for `default` and `review`) and the profile's `run_cap_usd` (default $1.00).

USD limits need prices. For a model with no entry in the profile's `prices`, chargehand cannot price a call and no USD limit fires; the preset's token budget, 400,000 input tokens per node for `default` and `review`, still bounds each call (on main, not yet released). The minimal profile from `scripts/change-e2e.sh` has no prices.

## How it is checked

- [ChangeSkillTests](../../tests/Chargehand.Tests/ChangeSkillTests.cs) keeps the names and limits in SKILL.md (the branch and report paths, the report commit, preset `review`, at most 2 fix rounds, 60000 characters) and the report template's sections.
- [PluginManifestTests](../../tests/Chargehand.Tests/PluginManifestTests.cs) checks the plugin version against `Directory.Build.props` and the MCP entry against the package id. CI runs `claude plugin validate --strict` on the plugin and on the marketplace ([ci.yml](../../.github/workflows/ci.yml)).
- [change-e2e.sh](../../scripts/change-e2e.sh) runs the command in a sample repository against this checkout's build and checks four cases: a reviewed change, a dirty tree, no server, and an existing branch. It calls a real model, so it runs by hand or through [change-e2e.yml](../../.github/workflows/change-e2e.yml) on the maintainer's runner. The repository records no result of it.
