# 0040. The prompt-enhancer extension

- Status: accepted
- Date: 2026-10-04

## Context

The planned client (docs/specs/2026-10-02-a-client-of-your-own-design.md, decision 7) improves a user's prompt before it is
sent. The user's prompts belong to the user, so the improving is a separate tool, whetstone, in its own repository; chargehand
only defines the seam. whetstone's contract is published as `enhance/v1` and `feedback/v1`: `enhance` takes the prompt, an
optional context (repository, commit, task kind, client) and `deadline_ms`, and answers with a prompt, `changed`, a template id
and version, a reason and a `request_id`; `feedback` takes that `request_id` and the outcome. That differs from the spec's
sketch, which keys feedback by template id and version; the published contract wins, because a request id joins the outcome to
the one answer it follows.

## Options

1. A fixed tool mapping in the profile, like `memory` (ADR 0034): any server whose tools can be mapped. Flexible; the mapping
   has nothing to map, because the contract is fixed and published.
2. **Chosen.** `prompt_enhancer: { server, deadline_ms }` names an `mcp_servers` entry that lists `enhance` and `feedback`.
   A server with another shape needs an adapter in front of it.

## Decision

- Category `prompt-enhancer`, default none: with no `prompt_enhancer` in the profile every prompt is sent as written.
- `IPromptEnhancer` (`EnhanceAsync`, `FeedbackAsync`) in the core; `McpPromptEnhancer` calls the two tools over the shared
  connection pool; `PromptEnhancers.From(profile, pool)` builds it, as `MemoryStacks.From` does for memory.
- **Never worse off for asking.** `GuardedPromptEnhancer` wraps every enhancer. A late, failing or unreachable answer, an empty
  rewrite, and a rewrite equal to the original all become the original prompt with `Rewrite` null and a reason. The call runs off
  the caller's thread, so an enhancer that blocks or ignores its token still meets the deadline (default 1500 ms, profile
  `deadline_ms` 50 to 30000, also sent as `deadline_ms`). Feedback is best effort. Only the caller's own cancellation escapes.
- **What is sent.** `EnhanceContext` has exactly four fields (repository, commit, task kind, client), so nothing else can be sent
  by mistake. Prompts and server error texts are never put in a reason or a log line.
- **The rewrite is text for the user.** A client shows it beside the original and sends the original unless the user accepts. The
  contract has no field for tool calls, system prompts or model choice, and chargehand reads none.
- `chargehand extensions check` connects the server and reports whether it lists `enhance` and `feedback`.
- Not here: the client that calls it, the diff view, and recording the outcome (the client task). This ADR is the seam.

## Consequences

- A user adds an enhancer in the profile without forking, which makes the category's extension point real.
- The profile schema gains one optional property; `profile/v1` stays compatible.
- A server that changes the contract's major breaks the call; the guard turns that into the original prompt, and the check names it.

## Reopen if

A second enhancer with another tool shape needs to be supported without an adapter, or the client needs more than a prompt and
four context fields.
