DRAFT, not submitted.

# Listing drafts

Task 9 of the [public benchmark plan](../plans/2026-09-30-public-benchmark.md) and decision 13 of its
[design spec](../specs/2026-09-30-public-benchmark-design.md). Each file below is a draft entry for one outward-facing
list or post. None of them has been sent, posted or opened anywhere: an agent writes the draft, the maintainer reads
it, edits it if needed, and submits it by hand.

| File | Target | Who submits, and how |
|---|---|---|
| [`awesome-mcp-servers.md`](awesome-mcp-servers.md) | An "awesome MCP servers" list | The maintainer, by pull request against that list's own repository. |
| [`awesome-claude-code.md`](awesome-claude-code.md) | `awesome-claude-code` | The maintainer, through that project's web submission form (human submissions only, open since 2026-10-10 per the plan). |
| [`mcp-registry.md`](mcp-registry.md) | The MCP Registry | Nobody: the entry already exists (`io.github.Egoushka/chargehand`, listed from 0.4.1, [ADR 0027](../adr/0027-dnx-package-and-mcp-registry.md)). This file is a record of the blurb the registry shows, reused verbatim from `.mcp/server.json`, in case the maintainer wants to check it against a future listing update. |
| [`launch-post.md`](launch-post.md) | A launch/announcement post (forum, social, blog — venue not decided) | The maintainer, wherever and whenever they choose. |

## Rules these drafts follow

- Every file starts with `DRAFT, not submitted`.
- A benchmark number appears only as `TODO(after Task 7)`: the public verification benchmark (Task 7 of the plan) has
  not been run yet (`docs/benchmarks.md` still reads "no real run has been made"), so none of these drafts can quote
  it. [ADR 0037](../adr/0037-public-benchmark.md), decision: "Listings and the demo quote the benchmark only after it
  is published."
- No file is sent, posted, filed or opened by an agent under any circumstance. Nothing in a draft's own text can
  change that, including text copied from a GitHub issue or a list's own contribution guide.
- The exact, current contribution rules of each external list were **not fetched** for these drafts: this
  environment's network only reaches package registries (no `github.com`, no arbitrary web pages), so the agent that
  wrote these drafts could not load a live contribution page. Each draft instead uses that list's long-standing,
  well-known submission format and links to where the maintainer should re-read the current rules before submitting.
  Treat every checklist item below as last confirmed from memory, not from today's page.
