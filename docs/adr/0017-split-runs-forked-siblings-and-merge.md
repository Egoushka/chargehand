# 0017. Split runs: forked siblings and a deterministic merge

- Status: accepted
- Date: 2026-09-26

## Context

A split runs 2–4 read-only subtasks as separate nodes (ADR 0005). Every fresh session writes its ~4.5k-token
system prefix to cache (1.25× input on GPT-5.6+-style pricing) and shares nothing with its siblings, because the
session ID sits early in that prefix (ADR 0010). The node contracts then have to become one `result/v1`.

## Evidence (phase 4 spike, OpenCode 2.0.16, small model)

- A fresh session's first call: 0 read, 4,490–4,500 written. A fork of a session that has made one call: 4,490
  read, 251 written.
- **Fork before the first message** (`POST /api/session/{id}/fork` with `before` = the first user message) gives a
  session with no history that still reads the full prefix from cache: 4,479 read on its first call. It keeps the
  parent's instruction entries.
- An instruction entry set on a fork after forking is appended after the cached prefix (4,490 read, 278 written).
- `POST /api/session/{id}/compact` with `delivery: steer` on a busy session compacts after the current step; the
  turn continues and the system prefix stays cached (4,479 read after compaction). Session create takes no
  compaction settings.

## Options

1. Fresh session per node — simple; each node pays the prefix write.
2. Fork every later node from the first node's session, before its first message — nodes share the cached prefix;
   they wait for the first node's first call (one watcher poll, ≤ 5 s).
3. Merge with an extra synthesis node — a coherent prose answer; one more session base and a call per run.
4. Deterministic merge — no model call; the summary is the parts' summaries under their goals.

## Decision

- Option 2. A node forks only when its instruction entries hash to the first node's (a fork inherits them);
  otherwise it gets a fresh session. At most 2 nodes run at once. A node that does not complete stops its
  dependents; siblings finish.
- Option 4. Evidence ids are prefixed with the node id (`s1.e1`); each node's evidence resolved inside that node.
  Usage is summed; confidence is the mean over completed nodes; parts that did not complete become open questions;
  the run fails only if no part completed. `node_id` is `merge`.
- Only read-only subtasks run; a split with a writing subtask runs as one answer node until worktrees exist
  (ADR 0015).
- The run cap is divided evenly across nodes.

## Consequences

The measured fixed cost of an extra node (ADR 0010) drops from a ~6k-token cache write to a ~6k-token cache read.
Whether a split beats one session on quality per dollar is measured in the phase 4 run log, not assumed.
Measured at the phase 4 exit: blind score 0.967 against 0.950 at 1.53× the cost, 128 against 193 per dollar, so
intake should choose `split` only when one session cannot cover the parts, not merely because a question spans
several areas.

## Reopen if

The owner's blind scores rate merged answers as incoherent (then option 3), or OpenCode stops keeping the cached
prefix across a fork.
