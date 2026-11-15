DRAFT, not submitted.

# Entry draft: awesome-claude-code

`awesome-claude-code` takes human submissions only, through its web submission form, since 2026-10-10 (per the plan
task this draft implements — `docs/plans/2026-09-30-public-benchmark.md`, Task 9). **No agent submits this: the
maintainer fills in the form by hand**, using the fields below as a starting point. This draft could not load the
form's current field list or review rules from this environment (network here reaches only package registries, not
`github.com` or the form's own site); the maintainer should re-read the current form before filling it in, since the
fields it asks for may have changed since 2026-10-10.

## Proposed fields

- **Name:** chargehand
- **Category:** closest published category is something like "Workflows & Tooling" or "MCP servers" — the exact
  category list lives on the form itself and could not be read here; pick the one the form currently offers that best
  matches "runs Claude Code sessions as workers behind an MCP server and a CLI".
- **Link:** `https://github.com/Egoushka/chargehand`
- **One-line description:** Runs coding-agent workers on a codebase question; every claim in the answer carries
  evidence. (same text as `.mcp/server.json`'s `description`, see [`mcp-registry.md`](mcp-registry.md))
- **Longer description, if the form asks for one:**
  > chargehand asks a question about a codebase, runs a read-only Claude Code or OpenCode session against a pinned
  > commit, and returns a typed result where every claim cites a file, a diff, a session message or an input — each
  > citation checked before the answer leaves the worker. It also drives headless Claude Code sessions end to end: a
  > `/chargehand:change` Claude Code plugin command, and driven batches that write a tested branch or a draft pull
  > request from a task list. Before 1.0; FSL-1.1-ALv2 licensed, becoming Apache 2.0 on its own schedule (see
  > [`LICENSE`](../../LICENSE)).
- **Why it's relevant to Claude Code, specifically:** Claude Code is one of its two worker runtimes (OpenCode is the
  other), and the `/chargehand:change` command is a Claude Code plugin ([guide](../guide/change.md)).

## Requirements checklist (from the plan; confirm against the form's current page)

- [x] Public repository, documented, installable — [README quick start](../../README.md#quick-start).
- [x] Description matches the registry/package description word for word.
- [ ] Submitted through the form by the maintainer — not done; this is a draft only.
- [ ] Whatever proof-of-use or screenshot field the form currently asks for — not prepared here; needs the
  maintainer to check the live form.
- [ ] TODO(after Task 7): the public verification benchmark is not published yet
  (`docs/benchmarks.md` still reads "no real run has been made"). Per decision 13 of the design spec, this draft
  quotes no benchmark number; once Task 7 publishes one, the longer description above may gain a sentence like "X% of
  its claims' citations are checked to support them, Y% agreement with a human audit" — filled in from
  `docs/benchmarks.md` at that time, not estimated now.
