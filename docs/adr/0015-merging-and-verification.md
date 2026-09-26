# 0015. Merging and verification

- Status: accepted
- Date: 2026-09-26

## Evidence (phase 2 spike)

- `POST /api/worktree` needs only `projectID` and returned in 60 ms, but it created a detached-HEAD worktree
  with a random name under the OpenCode server's data directory, based on the project's canonical clone rather
  than the directory passed; `branch` and `from` bodies failed. OpenCode's project id is the root-commit hash, so
  two clones of one repository are one project.
- A worktree under the data directory lies under the user's home, where skill discovery leaks (ADR 0003).
- `POST /api/session/{id}/move` into another directory works (204, 124 ms).

## Decision

- One writing node per git worktree; read-only nodes share one checkout at the pinned commit.
- The orchestrator creates worktrees itself with `git worktree add -b chargehand/<run>/<node> <worker_root>/…
  <commit>`: explicit base, branch name and location outside the home. It never deletes sessions or worktrees.
- Verification before merge: the node's contract resolves (ADR 0009), `diff` evidence matches
  `GET /api/session/{id}/diff`, and the project's own build/test command passes in the worktree. Merging is a
  human step in v0–v1.

## Reopen if

OpenCode's worktree API gains an explicit base and location.
