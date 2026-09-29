# 0035. Sandboxed writing workers

- Status: proposed
- Date: 2026-09-30

## Context

Goal 0.7 asks for workers that write branches which build and pass their tests. ADR 0006 keeps every worker read-only
and says "No `autonomous` preset until sandboxing is designed", because rules cannot make a shell safe: OpenCode
matches a command's source text, quoting changes the text without changing the command, and `git grep -O` runs
programs. ADR 0015 sketches a worktree per writing node in the source repository; ADR 0023, written later, keeps the
source read-only and leaves the way back open. No sandbox, container or environment scrubbing exists in `src/` or
`scripts/` (design spec, "Where it stands", 2).

The dangerous step is not the edit: an edit tool writes a file inside the worker's directory. It is running code the
edit may have changed, which is what building and testing do.

## Options

1. Give the worker a shell inside a sandbox. One agent loop, fast feedback; but the sandbox then guards a process that
   also holds the model's credentials and network, and both runtimes would need per-runtime sandbox wiring.
2. Keep the worker shell-less; chargehand runs build and tests in a sandbox and feeds failures back. The sandbox guards
   one short command, the runtime adapters change only in their permission rules, and ADR 0006 stays true. Feedback is
   one turn slower than a shell.
3. A container per run (Docker). Strongest isolation, works the same on both systems; needs a daemon, images for every
   toolchain, and a place for the repository's dependencies. Not available on a laptop by default.

## Decision

Option 2, with `sandbox-exec` on macOS and `bwrap` on Linux behind one interface. A writing preset is a node kind with
`writes: true`; it runs in a per-run clone on a `chargehand/<run>/<node>` branch that chargehand commits (with hooks
disabled) and never pushes or merges. `auto` refuses to run a writing preset without a sandbox; `sandbox.kind: none` is
an explicit, recorded opt-in. Network is off by default. The full list of decisions is the design spec's table.

ADR 0015's worktree-in-source is superseded for the branch location: the run clone keeps ADR 0023's rule that the
source is only read. Its verification rule ("the project's own build/test passes in the worktree") stands, now
executed by the verifier.

## Consequences

- The worker can still edit the test or the build script so that the command passes. The verification artifact lists
  changed build and test paths; nothing blocks them. A green branch is evidence that the command passed, not that the
  change is right; the `review` preset and a person are the checks on that.
- A repository whose build needs the network (unrestored dependencies) fails in the sandbox until `sandbox.network` is
  true; the failure output says so.
- A repository's own hooks never run, so a repository that relies on a commit hook for formatting gets unformatted
  commits; the verifier catches a format check only if the build runs it.
- Clones under `.runs/` accumulate, like `.checkouts/`; clear them by hand.
- `sandbox-exec` is deprecated by Apple. The interface leaves room for a container implementation.

## Reopen if

A container sandbox is wanted (Windows, or a stronger boundary); a worker needs an interactive shell to be useful
(measured: fix rounds regularly exhausted for want of one); `sandbox-exec` stops working on a supported macOS.
