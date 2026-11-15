DRAFT, not submitted.

# MCP Registry blurb

Nothing to submit here: the registry entry already exists. `.mcp/server.json` is packed into the published tool
package and the release workflow lists each new version ([ADR 0027](../adr/0027-dnx-package-and-mcp-registry.md),
[`docs/guide/capabilities.md`](../guide/capabilities.md#listing-in-the-mcp-registry)). The entry has been listed since
0.4.1 as `io.github.Egoushka/chargehand`.

This file exists only so the blurb the registry shows has one place to be read next to the other listing drafts, per
decision 13 of the [design spec](../specs/2026-09-30-public-benchmark-design.md) ("the MCP Registry blurb reuses
`.mcp/server.json`'s description and changes nothing else").

## Current blurb (verbatim from `.mcp/server.json`, not changed by this file)

```json
{
  "name": "io.github.Egoushka/chargehand",
  "title": "chargehand",
  "description": "Runs coding-agent workers on a codebase question; every claim in the answer carries evidence."
}
```

Source: [`.mcp/server.json`](../../.mcp/server.json).

## Checklist

- [x] Registry entry exists and is listed — [ADR 0027](../adr/0027-dnx-package-and-mcp-registry.md),
  [CHANGELOG](../../CHANGELOG.md) (0.4.1).
- [x] Description here matches `.mcp/server.json` exactly; this file does not restate or rephrase it.
- [ ] Nothing to submit; no action for the maintainer unless `.mcp/server.json`'s own description changes, in which
  case the release workflow republishes the registry entry on the next version — no separate submission step exists
  to track here.
- [ ] TODO(after Task 7): decision 13 allows a listing to quote the benchmark only once it is published. The registry
  schema's `description` field is short and used elsewhere (the package README, this file); if the benchmark is ever
  added to it, change `.mcp/server.json` itself (one place) rather than this file.
