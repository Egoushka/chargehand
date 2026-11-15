DRAFT, not submitted.

# Entry draft: an "awesome MCP servers" list

Several lists of this name exist on GitHub; the maintainer picks the specific one before submitting, and re-reads its
`CONTRIBUTING.md` / `README.md` contribution section on the day, since this draft could not load that page from this
environment (network here reaches only package registries, not `github.com`). The format below is the shape these
lists have shared for a long time: one line per server in a category section, alphabetical within the category,
submitted as a pull request that adds exactly one line (plus, for some lists, a short entry in a "new" or "recently
added" section that gets pruned later).

## Proposed line

```
- [chargehand](https://github.com/Egoushka/chargehand) - Runs coding-agent workers on a codebase question; every claim in the answer carries evidence.
```

The description is `.mcp/server.json`'s `description` field, unchanged, so the MCP Registry entry
([`mcp-registry.md`](mcp-registry.md)) and this line read the same.

## Category

Most of these lists sort servers by what they connect to or what they do (e.g. "Developer Tools", "Code Execution",
"Knowledge & Memory", "Aggregators"). chargehand runs coding-agent sessions and returns checked claims about a
codebase — closest to a "Developer Tools" or "Code / Dev" category depending on the list's own headings; it is not a
data-source connector, so it does not belong under a "Databases" or "Search" style category some lists use.

## Requirements checklist (typical of this family of lists; confirm against the picked list's actual page)

- [x] The server is real, installable and documented: `dnx Chargehand@<version> --yes -- mcp` from the
  [`Chargehand` NuGet package](https://www.nuget.org/packages/Chargehand), quick start in the
  [README](../../README.md#quick-start).
- [x] One-line description, no emoji, no superlatives ("best", "ultimate"), matches what the server actually does.
- [x] Link points at the repository, not a landing page.
- [x] License is stated in the repository (`LICENSE`, `LICENSE-APACHE-2.0`, [`NOTICE`](../../NOTICE)):
  Functional Source License 1.1 (ALv2 future licence) — some lists filter to OSI licences only; the maintainer should
  check the picked list's own rule before opening the pull request, since this may exclude chargehand from a
  strict-OSI list.
- [ ] Alphabetical placement within the chosen category — depends on the final list and category, left for the
  maintainer to place.
- [ ] CI / list-specific lint (some of these lists run a link checker or a format check on the PR) — not run here;
  runs in that list's own CI once the PR is opened.
- [ ] TODO(after Task 7): once the public verification benchmark is published, consider whether a benchmark number
  belongs in the one-line description. Decision 13 says a listing may quote the benchmark only after it is
  published, and only if the target list's own format allows more than a plain description (most do not; if so,
  leave the line as the description alone and let the benchmark speak from the README instead).
