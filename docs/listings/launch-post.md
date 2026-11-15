DRAFT, not submitted.

# Launch post draft

Venue not decided (forum, social media, a blog — the maintainer's choice); this is one draft body, not tied to any
platform's format. **Not posted anywhere by an agent.** The maintainer edits and posts it by hand, wherever and
whenever they choose.

---

## chargehand: coding-agent answers you can check

Coding agents answer in prose. chargehand asks one a question about your codebase and gets back `result/v1`: claims,
each tied to a citation — a file and line range, a diff, a session message or an input — checked against a pinned
commit before you see it. Claims whose citation doesn't resolve move to open questions instead of sitting in the
answer looking certain.

It reaches you as an MCP server (stdio or HTTP), an HTTP API, or a CLI, and runs on Claude Code or OpenCode. Presets
(`default`, `cheap`, `thorough`, `strict`, `draft`, `review`) set the budget and whether a question can be split into
a few read-only subtasks that share one cached prompt prefix.

Beyond answering questions, it also writes: a `code` preset and driven batches that turn a task list into a tested
branch or a draft pull request in a sandbox, and `/chargehand:change`, a Claude Code plugin command for one prompt to
a reviewed local change.

**Try it against your own repository**, no clone of chargehand needed:

```bash
claude mcp add chargehand -e CLAUDE_CODE_OAUTH_TOKEN=<token> -- dnx Chargehand@<version> --yes -- mcp
```

(pin `<version>` to the newest on [nuget.org/packages/Chargehand](https://www.nuget.org/packages/Chargehand); the
package changes before 1.0). Full quick start in the [README](../../README.md#quick-start).

**What it doesn't do:** run its own agent loop, call model providers directly, act as a model gateway, or host
anything — it drives the coding agent you already have, over MCP, HTTP or the CLI.

TODO(after Task 7): a paragraph here reporting the public verification benchmark — the share of claims whose cited
text does not support them, for a plain agent's answers versus chargehand's, on a frozen public-repository question
set, with the human-audit agreement number — once it is run and published in `docs/benchmarks.md`
([ADR 0037](../adr/0037-public-benchmark.md), [plan Task 7](../plans/2026-09-30-public-benchmark.md)). No number is
estimated here; "no real run has been made" as of this draft.

TODO(after Task 8): a line here linking the text demo (`docs/demo.md`) once it exists — a plain answer's failed
claim caught, then the same question through chargehand, then `chargehand verify` on a signed result.

Before 1.0: pin an exact version and read the [changelog](../../CHANGELOG.md) before upgrading. Source, issues and
the roadmap: [github.com/Egoushka/chargehand](https://github.com/Egoushka/chargehand).

---

## Checklist

- [x] No number from the unpublished benchmark; both TODOs above name what unblocks them.
- [x] No feature claimed that the codebase doesn't show: quick start, presets, driven batches and the plugin command
  all match [README.md](../../README.md) and [ROADMAP.md](../../ROADMAP.md) as of this draft.
- [ ] Venue, posting account and timing — left to the maintainer.
- [ ] Not posted; this is a draft file only.
