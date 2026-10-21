# 0067. Intake from Plane work items

- Status: accepted
- Date: 2026-10-18

## Context

[ADR 0051](0051-intake-from-github.md) starts a driven batch from a GitHub issue. The maintainer plans work in Plane, a self-hosted tracker, and wants a work item moved to a chosen state to start the same batch, with the pull request link and the state change coming back on the item.

The server listens on a private network ([ADR 0024](0024-server-on-a-private-network-and-release-images.md)); Plane cannot call it. Plane's query filter (PQL) is not on every edition, and Plane renamed issues to work items, so older instances still serve `/issues/`.

## Options

1. **A Plane webhook.** Needs an endpoint Plane can reach and a signature check; the same reach problem [ADR 0051](0051-intake-from-github.md) rejected.
2. **Plane's MCP server through the pool** ([ADR 0034](0034-memory-and-services-over-mcp.md)). A tool call returns text for a model; intake needs typed fields (state ids, cursors, update times) and runs without a session. It also ties intake to the tool names of a server version.
3. **Poll Plane's REST API** with an API key, as intake polls GitHub.

## Decision

Option 3, beside GitHub intake in `Chargehand.Automation`, off unless the profile has `automation.plane`.

- **Poll, not webhook.** Same reasons as 0051; delay is up to `poll_seconds`.
- **REST, not the MCP pool.** `PlaneHost` implements `IPlaneHost` over `HttpClient` against `api/v1` with `X-API-Key`; the key is a secret-store item read at each call and redacted from every message. Work items are asked for as `work-items` and, if the instance answers 404, as `issues`; the answer is kept for the process.
- **State names, not ids.** The profile names `trigger_state`, `started_state`, `review_state` and `failed_state`; each poll resolves them per project (case-insensitive), because ids differ per project and instance. A project missing one is skipped with a warning.
- **Filter client-side.** The project's items are paged through (100 per page, at most 20 pages) and kept when their state is the trigger state.
- **Dedupe by item id and update time.** `intake/plane.json` records `<item id>@<updated_at>` before the comment about it. Moving an item out and back is a new trigger; an item whose batch still runs is never started twice.
- **Caps** as GitHub intake: `max_concurrent` waits (one comment), `max_starts_per_day` refuses (one comment), token and dollar totals per batch.
- **Trust.** There is no author list: whoever can move an item can start a batch, and the API key's member limits which projects are read. The goal is not fenced as untrusted text. Use a key of a member who can reach only the projects listed.
- **Outcome.** A pull request: comment with the link, item to `review_state`. Failure, a lost run or questions: comment with the reason, item to `failed_state` if set, else it stays in `started_state`. Replies are not read; the comment says to put the answer in the description and move the item to the trigger state again.

## Consequences

Plane intake does not continue a task from a reply, does not run review rounds and does not read comments. The row reader of the batch result is a copy of the one in `GitHubIntake`. Not run against a real Plane: tests use a fake host and a stub HTTP handler (UNKNOWN: the exact list response of every edition).

## Reopen if

Plane webhooks become reachable, a Plane edition changes the cursor paging, or the maintainer wants answers read from comments.
