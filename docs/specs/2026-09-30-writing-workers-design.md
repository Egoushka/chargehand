# Workers write verified branches (goal 0.7)

- Status: draft for the maintainer's review. Every decision below is the proposer's; the maintainer accepts or changes them in review.
- Date: 2026-09-30

## Goal

A request run under a writing preset ends with a git branch that builds and passes the repository's tests, or with a
failed result that says why and still carries the branch. The worker edits files; chargehand runs the build and tests
itself, inside a sandbox, and never merges.

Done when: (1) a run with the `code` preset on a sample repository yields a branch whose verification artifact shows
exit code 0 from the sandbox; (2) a run whose change breaks the tests gets the failure fed back, at most 2 fix rounds,
and ends `failed` with `verification_failed` if it stays red; (3) no worker process ever holds a shell; (4) a
verification command cannot write outside its workspace or read the user's credential directories, shown by a test
that tries.

## Where it stands

Evidence: `path:line` at commit `01bc0f6` (0.6.1).

1. **Workers cannot write, by design.** Every preset ends with `edit * deny` and `shell * deny`
   (`presets/default.yaml:11-25`); ADR 0006 records why a shell cannot be made safe with rules (quoting defeats
   deny patterns, `git grep -O` runs programs) and says "No `autonomous` preset until sandboxing is designed".
2. **No sandbox exists.** No `sandbox-exec`, `bwrap`, container or env scrubbing in `src/` or `scripts/`. OpenCode
   runs as one long-lived server (`OpenCodeServerProcess.cs`); Claude Code as one `claude -p` process per turn with the
   parent's environment (`ClaudeCodeWorkerRuntime.cs`, `Env`).
3. **The worker's directory is a shared, reused, detached clone**, one per source and commit
   (`Orchestrator.cs:357-404`, ADR 0023). Writing into it would pollute later read-only runs, and the reuse check
   (`HEAD == commit`) would not notice a dirty tree.
4. **ADR 0023 keeps the source read-only** (no worktree registration in it) and leaves "how changes get back" open;
   ADR 0015 (older) proposes `git worktree add` in the source. They conflict; this design follows 0023.
5. **A writing subtask is rejected** by `SplitPlan` (`SplitPlan.cs:25-26`), so a split run cannot write either.
6. **`result/v1` needs no new shape.** `artifacts[].kind` is a free string (`schemas/result/v1/result.schema.json:46-61`);
   a branch, a diff and a verification record are artifacts. New error codes are additive enum values
   (`SchemaCompatTests` allows them).
7. **`WorkerNode` has the turns a fix loop needs** (`Turn`, repair turns, `WorkerNode.cs:60-110`) but no hook to run
   something between them.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Does the worker get a shell? | No. ADR 0006 holds. The worker has `edit`, `read`, `grep`, `glob`. Chargehand runs the repository's build and tests. |
| 2 | How is a writing run chosen? | By preset. A node kind may set `writes: true` (additive, optional). The new preset `code` has one node kind, `writer`, `allowed_actions: [answer, ask, deny]`. No new task action, so the intake prompt is unchanged. |
| 3 | Where does the writer work? | A per-run clone under `<worker_root>/.runs/<run>/<node>`, cloned from the cached checkout of the pinned commit (`git clone --local`), on branch `chargehand/<run>/<node>`. The source repository and the shared clone are never written (ADR 0023). |
| 4 | How does the branch get back? | It stays in the run clone. The result names it; the caller runs `git fetch <clone> <branch>`. Chargehand never pushes and never merges (ADR 0015: merging is a human step). |
| 5 | What runs the tests? | The verifier: the request's `context.verify` argv, else a detected command (`dotnet test`, `npm test`, `pytest`, `cargo test`, `go test ./...`, chosen by the files present). Run by chargehand in the sandbox, working directory the run clone. Timeout: preset `verify.timeout_seconds`, default 600. |
| 6 | What is the sandbox? | macOS: `sandbox-exec` with a generated profile. Linux: `bwrap` (bubblewrap). Selected by `sandbox.kind` in the profile: `auto` (default) picks the platform's, and **refuses to run a writing preset when none is available**; `none` is an explicit opt-in that the run records. |
| 7 | What may a sandboxed command do? | Read the filesystem except credential locations (`~/.ssh`, `~/.aws`, `~/.gnupg`, `~/.config/gh`, `~/.netrc`, `~/.docker`, `~/.kube`, `~/.claude`, keychains); write only the run clone and a private temp directory; no network unless the profile sets `sandbox.network: true`; environment cut to an allowlist (`PATH`, `LANG`, `LC_ALL`, `TERM`, `TMPDIR`, `HOME` pointing at the private directory, plus names listed in `sandbox.env`). |
| 8 | Fix loop | After the worker's answer, verify. On a non-zero exit, send the tail of the output (last 8 KiB) as the next prompt in the same session; at most 2 fix rounds (`verify.max_fix_rounds`, default 2). Same budget cap as the node: the loop cannot exceed the preset's `max_usd`. |
| 9 | Commit | Chargehand commits, not the worker: `git add -A` then `git -c core.hooksPath=/dev/null commit` as `chargehand <chargehand@localhost>`. No repository hook runs outside the sandbox. Nothing to commit means no branch artifact and a `failed` result. |
| 10 | Result | `completed` only when the verifier exits 0, or when no command exists (then with the open question "no test command found" and confidence at most 0.5). After the last fix round still red: `failed`, `error.code = verification_failed`, `retryable: false`, an `action`, and the artifacts still attached. |
| 11 | Artifacts | `branch` (`application/vnd.chargehand.branch+json`, inline: clone path, branch, commit, base), `diff` (`text/x-diff`, `git diff base..HEAD`, at most 64 KiB, truncated with a marker), `verification` (`application/json`: argv, source `request`/`detected`, sandbox kind, network, exit code, attempts, duration, output tail, and the changed paths that look like build or test files). |
| 12 | Can the worker cheat the verifier? | Yes, by editing the test or the build script. Not prevented. The `verification` artifact lists changed paths matching test and build files so a reader (and the `review` preset) sees it. ADR 0035 says so plainly. |
| 13 | Runtimes | Both (OpenCode, Claude Code). Only the permission rules differ from a read-only preset (`edit allow`, denies for `.git/**`, `*.env`, `*.env.*`). |
| 14 | Splits | A writing preset runs one node. `allowed_actions` has no `split`. |

## Components

| Unit | Responsibility |
|---|---|
| `Sandbox/ISandbox`, `SandboxSpec`, `SandboxResult` | Run an argv in a directory under limits; return exit code, output tail, timeout flag. |
| `Sandbox/SeatbeltSandbox`, `BubblewrapSandbox`, `NoSandbox`, `SandboxSelector` | The platform implementations; the selector applies decision 6. |
| `Verification/VerifyCommand`, `Verification/Verifier` | Decision 5 and the output tail. |
| `Workspace/RunWorkspace` | Decisions 3, 4, 9: create the clone and branch, commit, read the diff. |
| `Nodes/WorkerNode` `AfterAnswer` hook | Lets the orchestrator run the verifier between turns (decision 8). |
| `Orchestrator.ExecuteChange` | Wires the above for a node kind with `writes: true`, builds the artifacts and the error (decisions 10, 11). |
| `presets/code.yaml`, `prompts/core/writer.md`, `prompts/preset/code.md` | The preset and its prompts; one Prompt CI cell. |

## Errors

New `ErrorCode` values (additive): `sandbox_unavailable` (no platform sandbox and `sandbox.kind` is not `none`; action:
install bubblewrap or set `sandbox.kind: none` knowingly), `verification_failed` (decision 10).

## Testing

- Unit: selector, seatbelt profile text, bubblewrap argv, detection, tail truncation, workspace on a real git repo,
  the hook in `WorkerNode` with the existing fake runtime.
- Sandbox behaviour on macOS: a real `sandbox-exec` test that writes outside the workspace and reads a fake credential
  directory and must fail; skipped elsewhere. On Linux CI the bubblewrap test runs when `bwrap` is present.
- Orchestrator: a scripted runtime whose "worker" edits a file in `NodeSpec.Directory`; a green case, a red-then-fixed
  case, a red-forever case, a no-command case, a no-sandbox case.
- End to end (`scripts/write-e2e.sh`, model needed, run by hand or on the self-hosted runner): the sample repository of
  `change-e2e.sh` with a failing test to fix.

## Out of scope for 0.7

Pushing, opening pull requests, merging; several writers in one run; a container sandbox (Docker); windows; making the
verifier tamper-proof; sandboxing the worker process itself (it has no shell, and edits go through the runtime's file tools).

## Open items

- Whether `dotnet test` and `npm test` work in the sandbox without network. Assumption: a restored repository builds
  offline, an unrestored one needs `sandbox.network: true`. Checked in the e2e, recorded in the guide.
- `sandbox-exec` is deprecated by Apple but present and used by other tools; if it disappears, macOS falls back to
  requiring `sandbox.kind: none` (or a container, later).
