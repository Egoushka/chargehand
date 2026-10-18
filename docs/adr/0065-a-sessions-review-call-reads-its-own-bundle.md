# 0065. A session's review call reads its own bundle

- Status: accepted
- Date: 2026-10-18

## Context

A driven session (ADR 0039) calls `orchestrate` back into chargehand, through `CHARGEHAND_MCP_URL`, for research and review (the
`/chargehand:change` skill's steps 2 and 7). Those calls carry a `RunTokenClaims` token that `RunScope.Apply` (`RunToken.cs`) pins to
the task's own repository path and base commit (`context.repository` must equal the claims exactly) and to the `default` or `review`
preset — on purpose: a session's own calls must never read or touch anything outside its task.

That pinning collides with the review step's purpose. Evidence from run `run-20261014-135204-0f21e9`
(2026-10-14): a review call naming the branch's HEAD commit was refused ("a session's calls are limited to the commit of its task"),
which is correct — `RunScope` does its job. A call pinned at the task's base commit, as the pinning requires, was admitted but came
back `needs_input`: the review worker's own file reads see only the base checkout (`Orchestrator.Checkout`, a clone of
`context.repository.path` at `context.repository.commit`), never the branch's commits, because those commits exist only inside the
session's container until it stops (`SessionDriver.Run.cs`'s `FinishAsync` is the only place that wrote `chargehand.bundle`, at the end).
Supplying the diff as a caller input did not help: the reviewer cross-checks every claim against its own file reads
(`GitEvidenceResolver`), not against text it was handed. The result: every driven task whose preset requires review ends `needs_input`
even when write and the tests are green.

## Options

1. Weaken `RunScope.Apply`'s commit pin for the review preset (accept the branch's HEAD as a second allowed commit). Rejected: that is
   exactly the rule the pinning exists to enforce, and relaxing it for one preset relaxes it for every session, including ones with no
   bundle-based alternative yet.
2. Teach the review worker to read the diff input instead of its checkout. Rejected: `GitEvidenceResolver` resolving evidence from a
   caller-supplied diff string rather than a real git history is a bigger, riskier change to a shared, trusted claims path, for a result
   worth less (a diff's context is thinner than a real checkout's).
3. Make the session's own `chargehand.bundle` live while the session runs, and point the session's own review call's checkout at that
   bundle instead of the task's base commit, leaving everything about scoping (`RunToken.cs`, `RunScope.Apply`) untouched. The signal
   that a call is a session's own call already exists: `OrchestrateTool.Run()` sets `chargeTo` to the token's `scope.RunId` only for a
   call that arrived on a valid `RunTokenClaims` through `RunScope.Apply`, and to null for every other call path. A non-session call
   (no token, or a token on a different preset) is unaffected.

## Decision

Option 3.

- `SessionDriver.Run.cs`'s `WatchAsync` now refreshes `chargehand.bundle` while the session is still running, not only once in
  `FinishAsync`: on each poll (`BundleCheckEvery`, 2 seconds) it checks the task branch's head with a cheap `rev-parse`, and bundles
  again only when the head moved since the last bundle. The combined measure-and-bundle task is awaited the same way the diff measure
  already was, so `SessionDriver.RunAsync`'s shape does not change.
- `ISessionVolumes` gains `FetchBundleAsync(runId, path, ct)`, with a default body that returns false (the same pattern as
  `ReadTranscriptTailAsync`): a soft capability, not a required one. `EngineSessionVolumes` implements it for real;
  `JournaledSessionVolumes` passes it through unjournaled, like the other reads (ADR 0061: the journal records writes, not reads).
  `UnavailableSessionVolumes` relies on the default (false) rather than throwing: a session whose deployment cannot serve a live bundle
  is not an error here, it is "no bundle yet," and the caller falls back.
- `RunService.Execute` computes `sessionBundleRunId = chargeTo is not null && request.Context.Preset == "review" ? chargeTo : null` and
  passes it as a new, optional trailing argument to `Orchestrator.RunAsync`. This is the only place the `chargeTo` signal is read for
  this purpose; `RunToken.cs` is not touched.
- `Orchestrator` gets a mutable `SessionVolumes` property (set once, by the driven runner, after it builds its own
  `ISessionVolumes`; null when driven sessions are off, so nothing changes for a profile without them) and a new private
  `CheckoutSessionBundle`: it fetches the named session's live bundle to a temp file, discovers its one ref with
  `git bundle list-heads` (no need to know the task's branch name), and builds a fresh `git init` + `git fetch <bundle> <ref>` +
  `git checkout --detach FETCH_HEAD` checkout under `.checkouts/<repo>-<hash>/session-<runId>`, rebuilt every call since the branch
  moves while the session runs. It runs the same root check and the same `DeniedFiles` secret-path check as `Checkout`. When the
  volume has no bundle yet (false from `FetchBundleAsync`), it falls back to `Checkout` — an early review call gets the same base
  checkout and the same `needs_input` it got before this fix, not a new error.
- `Orchestrator.Execute` only takes this path when both `sessionBundleRunId` is set *and* `SessionVolumes` is set; every other call
  (no token, a token on `default`, a non-session HTTP or CLI call pinned to the same repository and commit) calls `Checkout` exactly as
  before. `RunScope.Apply`'s refusals (wrong repository, wrong commit, wrong preset, spend or token cap) are unchanged and untested by
  this ADR's change — they are `RunTokenTests`' job, and that file is not touched.

### Limitation accepted

ADR 0058 lets a profile name several runner sites, each with its own `ISessionVolumes`. `Orchestrator.SessionVolumes` is one property,
wired from the default (first) site only. A session that ran on a named, non-default site has no live-bundle checkout for its review
call: it falls back to the base checkout and the pre-fix behaviour. Revisit if named runners and this fix are used together in
practice.

## Consequences

- A driven task's review step can now complete instead of ending `needs_input` for every task, once the fix is deployed.
- No change to what a session's call may name as its repository or commit, or which preset it may use: `RunToken.cs` is byte-for-byte
  the same file.
- One more git clone per review call (under `.checkouts/.../session-<runId>`), rebuilt each time; cheap for the sizes a session's own
  branch reaches, not swept automatically (same as the existing `.checkouts` cache noted in `Orchestrator.Checkout`'s doc comment).
- A session now does one extra `rev-parse` every 2 seconds and a `git bundle create` whenever the branch's head moves, on top of the
  existing diff measure; both run in the session's own container against its own working tree, so this adds no load outside it.

## Reopen if

- A named-runner profile (ADR 0058) needs this fix too: thread `SessionVolumes` per site instead of one property.
- The 2-second poll proves too chatty or too slow in practice; it is a single named constant (`BundleCheckEvery`) to retune.
