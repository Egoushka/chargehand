---
title: "Writing a branch"
description: "The code preset takes a request to a git branch that passed the repository's tests in a sandbox: how to run it and what the result carries."
order: 11
section: "Guides"
---

Since 0.8.0. The `code` preset takes a request to a git branch that passed the repository's tests in a
sandbox. [ADR 0035](../adr/0035-sandboxed-writing-workers.md) has the decisions and their reasons.

## Run it

```sh
echo '{"contract_version":"request/v1","text":"Make the failing test pass","context":{"interactive":false,
  "preset":"code","repository":{"path":"/path/to/repo","commit":"<sha>"},
  "verify":["python3","-m","unittest","-q"]}}' | chargehand run
```

`verify` is optional: without it chargehand finds the repository's test command (`dotnet test`, `npm test` with a
`test` script, `pytest` or `unittest`, `cargo test`, `go test ./...`). With neither, the run completes with the open
question "No test command found" and a confidence of at most 0.5.

## What happens

1. A sandbox is chosen before anything is spent. Without one the run fails `sandbox_unavailable` and says what to do.
2. The worker gets its own clone under `<worker_root>/.runs/<run>/writer`, on branch `chargehand/<run>/writer`. It has
   no shell: it edits files with its edit tools.
3. After its answer, chargehand exports what a commit would hold to a temporary directory and runs the test command
   there, in the sandbox. What the tests write (caches, build output) never reaches the branch. A failure goes back to
   the worker with the last 8 KiB of output, at most twice (`verify.max_fix_rounds` in the preset).
4. Chargehand commits with the repository's hooks and signing off. Nothing is pushed or merged.
5. With local delivery (below), the branch is then fetched into your repository.

The result carries three artifacts: `branch` (the clone's path, the branch, its commit and the base), `diff`
(`base..HEAD`, at most 64 KiB) and `verification` (command, sandbox, network, exit code, attempts, output tail, and the
changed paths that look like tests or build files). Take the branch with:

```sh
git fetch <clone path> chargehand/<run>/writer
```

A run that is still red after the last round has status `failed`, `error.code` `verification_failed` and the same
artifacts, so the attempt is not lost.

## Local delivery

For code that must stay on the machine, set `context.delivery` to `"local"`, or put the repository's directory under `delivery.local_only` in the profile ([ADR 0069](../adr/0069-local-delivery.md)). The profile wins: a repository under a `local_only` root is always local, and a request that says `push` for it fails `local_only`.

A local `code` run fetches its branch into your repository as `chargehand/<run>/writer` and the `branch` artifact names your repository (`"delivery": "local"`). The fetch adds objects and one new branch; it does not touch your working tree, index, `HEAD` or current branch, and never moves an existing branch. If it cannot, the run fails `delivery_failed` and the branch stays in the run clone.

Local also means: no push, no pull request, no forge call, no driven batch (refused `local_only`), no intake of that repository (the profile does not load), no prompt enhancer, and recall and retain only for `memory` entries with `"local_ok": true`. To keep verified claims of private code, mark a store that may hold them (a separate bank) `local_ok` and leave the shared one unmarked; mark none to keep nothing. Telemetry is not changed by the mode: omit the profile's `telemetry` block for a server that runs private repositories.

A server that does not hold the repository cannot refuse it: keep private checkouts out of a shared server's `repository_roots`, and run them on the machine that has them (`chargehand serve` there). `/chargehand:change --local` sends `delivery: "local"` on every call and uses only the MCP server named `chargehand-local`.

## The sandbox

| | |
|---|---|
| macOS | `sandbox-exec` with a generated profile. Apple marks it deprecated. |
| Linux | `bwrap` (bubblewrap). Not exercised on a Linux machine yet: its arguments are tested as text only. |
| `sandbox.kind: none` | Runs the tests unconfined. An explicit opt-in in the profile, recorded in the artifact. |

A command may write only in its directory and a private temp directory, may not read `~/.ssh`, `~/.aws`, `~/.gnupg`,
`~/.config/gh`, `~/.docker`, `~/.kube`, `~/.claude`, `~/.netrc`, `~/.npmrc`, `~/.git-credentials` or
`~/Library/Keychains`, has no network unless the profile sets `sandbox.network: true`, and sees only `PATH`, `LANG`,
`LC_ALL`, `TERM`, a private `HOME` and `TMPDIR`, and the variables named in `sandbox.env` ([profile fields](reference.md#profile-fields)).

A build that restores packages needs the network (or `sandbox.env` naming a package cache such as `NUGET_PACKAGES`
that points at a restored one); without either it fails in the sandbox, and the output tells the worker so.

## What it does not do

- **It does not stop the worker from editing the tests.** A green branch means the command passed, not that the
  change is right. The `verification` artifact lists changed test and build paths, and the `review` preset and a
  person are the checks on the rest.
- **It does not push, open a pull request or merge.** Local delivery only adds a branch to your repository.
- **It runs one writer per run.** The preset has no `split`.
- **It cannot use ignored files.** The tests run on tracked and new, non-ignored files only; a repository that needs
  `node_modules` or a restored `obj` from the workspace must restore inside the command.
- **Evidence of a real run:** on 2026-09-29, `scripts/write-e2e.sh` on macOS with the signed-in Claude Code fixed a failing
  Python test in one attempt, verified under `sandbox-exec`, in a one-hunk diff. That is one small case, not a benchmark.
