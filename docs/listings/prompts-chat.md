DRAFT, not submitted.

# Entry draft: prompts.chat

[prompts.chat](https://prompts.chat) is a community library of prompts, Agent Skills and bots. Prompt and skill text on
it is CC0 1.0 (its [LICENSE](https://github.com/f/prompts.chat/blob/main/LICENSE), read 2026-11-16). **No agent
submits this: the maintainer creates the entry by hand**, either through the web form at
[prompts.chat/skills](https://prompts.chat/skills) or through the `save_skill` MCP tool with the maintainer's own `PROMPTS_API_KEY`
([API docs](https://prompts.chat/docs/api), read 2026-11-16).

## Which entry type

| Type | Fits? | Why |
|---|---|---|
| Skill | Yes | `save_skill` takes a title, a description and files that must include a `SKILL.md`. The plugin's `change` skill is a `SKILL.md`. |
| Bot / bot team | No | `save_bot` takes `platforms` from a fixed list (`GROK_BOT`, `OPENCLAW`, `HERMES`, `AUTOGPT`, `DOTS`, `DUET`, `MUSE`). Claude Code and OpenCode are not on it, and a team is built from bots. Revisit if that list gains Claude Code. |

## Licence check before submitting

prompts.chat dedicates all user-submitted prompt text to the public domain (CC0). chargehand is FSL-1.1-ALv2, and the
skill files are part of this repository. Submitting `SKILL.md` verbatim would put that copy under CC0. Choose one
before submitting:

1. Submit a short wrapper `SKILL.md` (below) that points to the plugin and contains no FSL code. **Recommended.**
2. Submit `plugins/chargehand/skills/change/SKILL.md` verbatim, after deciding to release that file under CC0.

Never call chargehand open source in the entry: FSL is not an open-source licence.

## Proposed fields

- **Title:** chargehand: one prompt to a reviewed change, with checked citations
- **Description (≤ 500 chars):** Turns one goal or GitHub issue into a reviewed change inside Claude Code. chargehand
  researches the goal with citations checked against the current commit, you write the change on a new branch,
  chargehand reviews it, you fix what it finds (at most 2 rounds), and a report is committed. Needs the .NET 10 SDK and
  the chargehand Claude Code plugin. FSL-1.1-ALv2, becomes Apache 2.0 two years after each release.
- **Category:** `coding` (guessed from the `--category coding` example in the prompts.chat Claude Code plugin docs;
  check the form's list).
- **Tags:** `claude-code`, `mcp`, `code-review`, `citations`, `git`, `dotnet`
- **Files:** one `SKILL.md`:

```markdown
---
name: chargehand-change
description: One prompt to a reviewed change with chargehand. Use when the user wants a goal or GitHub issue turned into a branch that was researched with checked citations, reviewed, and fixed before a person looks at it.
---

# chargehand change

This skill needs the chargehand Claude Code plugin, which runs the chargehand MCP server through `dnx`
(needs the .NET 10 SDK):

    /plugin marketplace add Egoushka/chargehand
    /plugin install chargehand@chargehand

Then run `/chargehand:change <goal or #issue>`. The plugin's own skill does the steps:
research with citations checked against the current commit, the change on a new local branch,
a chargehand review, up to 2 fix rounds, and a committed report in `.chargehand/reports/`.

Guide: https://github.com/Egoushka/chargehand/blob/main/docs/guide/change.md
Licence: FSL-1.1-ALv2 (https://github.com/Egoushka/chargehand/blob/main/LICENSE)
```

## Before submitting

- Re-read the form's current fields; they may differ from the API docs above.
- Every claim above comes from [the change guide](../guide/change.md) and
  [`plugins/chargehand/skills/change/SKILL.md`](../../plugins/chargehand/skills/change/SKILL.md). No benchmark number
  appears until the public benchmark is published ([ADR 0037](../adr/0037-public-benchmark.md)).
