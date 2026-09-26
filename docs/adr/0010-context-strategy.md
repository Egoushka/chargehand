# 0010. Context strategy

- Status: accepted
- Date: 2026-09-26

## Context

Every call resends the conversation, so each worker's context should stay small, cached and disposable. Four
levers: prefix caching, compaction, isolated sub-contexts, retrieval instead of history. Compression proxies are
out of the default path.

## Evidence (phase 2 spike, one small model unless noted)

| lever | measured |
|---|---|
| 1 Prefix caching | From the second call of a session on: weighted cache rate 94.4%, median 98.4%, 45 of 48 calls ≥ 80%. **Across sessions: 0** — the session ID sits early in OpenCode's system prompt and the shared harness before it is below the 1,024-token minimum. On a Claude model through the gateway, only system + tools were cached (the gateway injects one breakpoint at the system message); conversation turns were re-billed. |
| Instruction entries | Set before the first prompt: land at the end of the system message, cached thereafter. Changed mid-session: appended as a user message (`<system-update>`, HTML-escaped markup); prefix survives, but in 1 of 1 trial the model ignored the new value. |
| 2 Compaction | Manual compaction 2.5 s; the system prefix stays cached after it (5,580 of 5,772 read). |
| 3 Isolated sub-contexts | Node B fed only A's contract: 5,790 prompt tokens, 0 cached. Fork of A with the same question: 6,396, of which 5,969 cached — cheaper. Contracts pay only when upstream history is large relative to the ~5.6k-token session base. |
| 4 Retrieval | Not measurable before memory exists (phase 4). |
| Per-session base | ~5.6k tokens (small model) / ~7.1k (Claude) written to cache on the first call of every session (1.25× on GPT-5.6+-style pricing). |

## Decision

1. Orchestrator blocks go in as instruction entries **before** a node's first prompt and never change during the
   node; task text, facts and upstream contracts go in the prompt text.
2. Tool sets are fixed per preset and node kind; workers run without MCP servers unless a preset fixes a set.
3. One fresh session per node, but **prefer continuing or forking a session over a fresh node when the upstream
   history is smaller than the session base** (~6k tokens); pass contracts, not transcripts, beyond that.
4. Compaction: preset settings plus a hard stop at the node budget; short-lived nodes.
5. Siblings do not gain from a shared prefix (cross-session reuse is zero), so they need not be staggered.

## Consequences

The "split" hypothesis (phase 4) starts with a measured fixed cost of ~6k cached-write tokens and ~1.4 s latency
per extra node.

## Reopen if

OpenCode moves the session ID out of the system prompt, or the gateway caches the conversation tail for Claude.
