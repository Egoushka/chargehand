# 0102. Agents land changes through a service, not git push

- Status: proposed
- Date: 2026-11-23

## Context

Agents commit and push with whatever credential their shell holds. They leave worktrees dirty or orphaned, and conflict
with each other on `main`. The owner's 30-day scan of 2026-11-07 counted 181 conflicts on `main`, 40 push rejections,
sandbox bypass in 33.0% of Bash calls and about 110 h of foreground waits. ADR 0046, ADR 0084 and the #737 merge
check fixed part of this. The write path itself is still the agent's.

Two studies agree that declared paths are a weak signal. MergeGym (arXiv 2610.04779) found that 47.3% of textual
conflicts were between pull requests with disjoint authored files. AgenticFlict (arXiv 2604.03551) found that 27.67% of
107K+ agent pull requests conflicted. Anthropic's Scion post
(https://www.anthropic.com/research/redesigning-source-control, 2026-10-13) moves commits and merges to the server, and
asks others to apply those ideas to git rather than adopt Scion.

The full design and checks are in the [spec](../specs/2026-11-23-agent-git-landing.md).

## Prior art

- Query: "How do tools let many coding agents commit and merge into one repository without conflicts, with server-side
  commits, merge queues and undo of agent actions?"
- Coverage: hit (by count). Only three facts bear on the question: jj's pilot verdict, and worktree isolation in
  claude-squad and emdash. None of them lands changes for an agent.
- Facts: 01a187ec-a2eb-7988-a2b2-f7768bd1eb78 (jj), 01a187fe-3a14-758d-9c1c-ec20a45af1fa (claude-squad),
  01a187fe-3a14-78a6-89e8-3284e3377c27 (emdash).
- Read directly for this ADR:
  - the Scion post;
  - MergeGym;
  - AgenticFlict;
  - GitHub's changelog on stateless installation tokens (2026-10-02);
  - a report that Apps on personal accounts cannot open pull requests
    (https://savas.me/2026/04/27/my-coding-agent-needed-its-own-github-identity/);
  - Simon Willison's guide to git with agents.

## Options

1. **Keep glue around `git push`** (D-107/D-108 as they are). Cheapest. The agent still holds the write path, so the
   guards stay string matches, and uncommitted work still dies with its worktree.
2. **A landing service in chargehand over plain git and GitHub.** Agents call `land`. The service snapshots, commits
   with a one-hour App token for one repository, predicts conflicts with `merge-tree` against `main` and every queued
   landing, merges one at a time per repository, and reverts on red. Intent records and an operation log build on it.
   It reuses the integrator, `GitHubInstallationTokens`, the handover's merge check and the event log.
3. **ghgate as the token broker.** Agents keep `git push` through the proxy with a better token. The write path stays
   the agent's.
4. **jj or a new version control system.** jj broke `ws ship` and worktrees in the owner's pilot (D-63). A new VCS is
   what Scion's authors say not to copy.
5. **Agent races** (N attempts per task, a selector picks one). Declined by the maintainer on 2026-11-23.

## Decision

Option 2, in stages, each gated by its own check: 0 preconditions tested against GitHub, 1 landing service, 2
intent-first review, 3 operation log. It lives in chargehand as `Chargehand.Modules.Landing`, not in a new repository.
ghgate keeps its read path, and its write path and personal access token end. wheelhouse shows leases and the queue
through chargehand's API.

Stage 1 must beat the 2026-11-07 baseline after 30 days, or landing returns behind a profile switch and the later
stages do not start. The targets are starting values: conflicts ≤ 60, rejections ≤ 5, bypass ≤ 15%, waits ≤ 40 h.

## Consequences

- No agent has a GitHub credential. Every write goes through `land`, so personal access tokens can leave the
  deployment.
- Branches become transport (`land/<id>`), created and deleted by the service only.
- `DiffScan`'s token pattern misses stateless `ghs_` tokens today. Stage 0 fixes that before any token flows through
  the service.
- Whether an App on a personal account can open and merge pull requests is UNKNOWN. Stage 0 tests it. A failure stops
  stage 1 and comes back to this ADR with the fallbacks the spec lists.
- Every landed commit carries trailers and a `refs/notes/intent` note. The transcript is referenced by id and hash,
  never stored in a public repository.
- Migrations never auto-merge. They need the `efcore-migration-safety` review, the `migration-reviewed` label and the
  maintainer.

## Reopen if

- Stage 0 shows the App cannot open pull requests on a personal account.
- Stage 1 misses its 30-day check.
- A reap ever loses work.
- GitHub ships server-side commit or merge-queue APIs for user-owned repositories that make the queue redundant.
