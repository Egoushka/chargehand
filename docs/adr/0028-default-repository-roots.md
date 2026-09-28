# 0028. Default repository roots

- Status: accepted
- Date: 2026-09-28

## Context

Goal 0.4 is "it runs with nothing configured". With no `repository_roots`, `Profile.Roots` is `[worker_root]`
(ADR 0023), and `worker_root` defaults to a directory outside the home (ADR 0003, ADR 0026). Repositories live under
the home, so every request that names one fails with `repository_not_allowed` until the user writes a profile.

ADR 0023 made the roots the owner's ceiling because `serve` is callable by any process holding the bearer key, and
ADR 0024 lets it listen on a private network. The CLI has no such caller: `chargehand run` is started by the user,
in a directory the user chose.

## Options

1. Keep `[worker_root]`. Safe, but the zero-config path never reaches a repository.
2. Allow any repository (`/`) when no roots are set. Right for the CLI, wrong for `serve`: a server on a private
   network would read any repository on the machine without the owner opting in.
3. Only when no profile file exists, allow the launch directory. Ties behaviour to whether a file exists, so adding
   an unrelated field (a gateway, a secret source) silently changes which repositories are allowed.
4. When the profile names no `repository_roots`, the CLI allows `worker_root` and the directory it was launched in;
   `serve` keeps `[worker_root]`.

## Decision

Option 4. `Profile.WithLaunchDirectory(dir)` adds the launch directory only when `repository_roots` is absent; the
`run` command applies it with its current directory, `serve` does not. An explicit `repository_roots` always wins.
The check itself is unchanged: the repository's top level, links resolved, must lie under a root, and the worker
still reads a clone under `worker_root` (ADR 0023), outside the home (ADR 0003).

## Consequences

- `chargehand run` from inside a repository (or a directory above it) works with no profile.
- `serve` with no `repository_roots` behaves as before: a network caller cannot name a repository the owner did not
  list.
- `run` reads `prompts/` and `presets/` from its current directory today, so until they ship with the tool
  (ADR 0027, pending) the launch directory is usually the chargehand checkout; the default pays off once a
  packaged launch starts in the user's own directory.
  2026-09-28: lifted. Outside a checkout they come from the install, and the run log from a per-user directory.
- A stdio MCP host (ADR 0027, pending) should apply the same default, or the client's declared roots.

## Reopen if

A CLI caller runs untrusted requests from a directory holding repositories the user does not want read, or `serve`
gains a per-caller identity that could carry its own roots.
