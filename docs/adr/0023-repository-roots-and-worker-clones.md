# 0023. Repository roots and worker clones

- Status: accepted
- Date: 2026-09-27

## Context

ADR 0003 kept every worker checkout under `worker_root`, outside the OpenCode user's home: OpenCode discovers skills
by walking up from a session's directory, and a checkout under the home put 27 personal skills (8.6 KB) into every
system prompt. The orchestrator ran the worker in the caller's checkout itself, so a request could only name a
repository placed there by hand; a caller with a repository elsewhere, such as a chat's own worktree under the home,
was refused ("repository ... is not under worker_root", 2026-09-27).

Two limits are needed and they are different. The HTTP and MCP interface (ADR 0018) is callable by any process that
holds the bearer key, so the owner needs a ceiling on which repositories a request may name. The worker needs a
directory outside the home. A caller cannot widen either: the orchestrator cannot verify what a calling chat is
allowed to see, so the ceiling is the owner's profile, and a request narrows it to one repository at one commit.

## Options

1. Keep one root and ask callers to clone into it. Every caller repeats the clone, and ignored or uncommitted files
   of the caller's checkout (local `.env` files, edits) reach the worker when the checkout is used in place.
2. Let the worker run in the caller's checkout anywhere. Reintroduces the skill leak under the home, and the
   worker reads the working tree, not the pinned commit.
3. A list of roots in the profile; the orchestrator clones the named repository at the pinned commit under
   `worker_root` and runs the worker in the clone.

## Decision

Option 3.

- Profile `repository_roots` (optional, default `[worker_root]`): the directories a request's repository may sit
  under. `/` allows any repository on the machine; that is the owner's opt-in. The repository's top level, as git
  prints it (symbolic links resolved), must lie under a root with its links resolved.
- The orchestrator resolves the requested commit in the source (a short hash is enough) and runs the worker in
  `<worker_root>/.checkouts/<name>-<hash of the source path>/<commit>`: `git clone --local --no-checkout` of the
  source, then a detached checkout of the commit. The clone is built beside that path and moved into it, reused by
  later runs of the same source and commit, and never deleted (ADR 0015).
- Only tracked files at the commit reach the worker; the source's uncommitted and ignored files do not. The
  preset's read denies still refuse a clone that tracks such a file (ADR 0006).
- `worker_root` stays outside the home (ADR 0003); nodes without a checkout still run in its empty directory.

## Consequences

- A request may name any repository under a root, including a chat's worktree, without placing it by hand.
- The worker answers from the commit, never from the caller's working tree; an uncommitted change is invisible to
  it. Callers that want one answered commit first.
- `git clone --local` hardlinks the object files when source and `worker_root` share a file system, so a clone costs
  little space; across file systems it copies them. Clones accumulate, one per source and commit; clear
  `.checkouts` by hand. A failed clone leaves its temporary directory behind.
- The source repository is only read: no worktree registration or other write lands in it.
- Writing nodes (phase 6) need their changes back in the source: a clone's branch can be fetched from, or ADR 0015's
  worktrees used there. That choice is left to phase 6.

## Reopen if

Clones take noticeable time or space on the repositories callers use, or a caller needs its uncommitted state
answered.
