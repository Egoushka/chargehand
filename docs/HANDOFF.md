# Handoff: chargehand 0.6.1 to 1.0

Written 2026-09-30 at the end of stage 0.7. Rewritten at the end of each stage. "Continue" means: read this, check it
against `git log origin/main` and `gh pr list -R Egoushka/chargehand`, carry on from Next.

## Goal

Take chargehand from 0.6.1 to 1.0 per `ROADMAP.md`: 0.7 writing workers, 0.8 support-checked claims and signed results,
0.9 public benchmark and listings, 1.0 stability declaration. One task = one branch = one PR, squash-merge; Plane
project CHARGEHAND for items; plans in `docs/plans/`. Yehor decides releases, tags, registry publishes, signing keys,
announcements and the 1.0 declaration.

## Stage 0.7: state

Code complete, one gate open: PR #125 waits for Yehor.

| Item | Evidence |
|---|---|
| PR 28 (fact-checklist scoring) | Conflicted with main. Rebased, build fixed (nullable model, CA1305), carried as [#118](https://github.com/Egoushka/chargehand/pull/118) (merged, 802 tests, CI green); #28 closed as superseded. No force-push. |
| Design | Spec `docs/specs/2026-09-30-writing-workers-design.md`, ADR 0035 (status: proposed), plan `docs/plans/2026-09-30-writing-workers.md`: [#119](https://github.com/Egoushka/chargehand/pull/119) merged. |
| Contracts (`writes`, `verify`, 2 error codes, additive) | [#120](https://github.com/Egoushka/chargehand/pull/120) merged. |
| Sandbox (`sandbox-exec`, `bwrap`, selector) | [#121](https://github.com/Egoushka/chargehand/pull/121) merged. Real macOS tests: outside write blocked, credential read blocked, network blocked unless allowed. |
| Run workspace | [#122](https://github.com/Egoushka/chargehand/pull/122) merged. |
| Node verify hook | [#123](https://github.com/Egoushka/chargehand/pull/123) merged. |
| Verifier | [#124](https://github.com/Egoushka/chargehand/pull/124) merged. |
| `code` preset, orchestration, e2e script, docs | [#125](https://github.com/Egoushka/chargehand/pull/125): all CI green (`ci-gate`, build, package, plugin, 857 tests); `prompt-ci` pending "owner review needed: fork or preset change"; auto-merge is on. |

Real run (2026-09-29, macOS, signed-in Claude Code, `scripts/write-e2e.sh`): `ok green`, `ok refused`. Green: a failing
Python test fixed in one attempt, `sandbox: seatbelt`, `network: false`, one-hunk diff, fetched branch passes its tests.
The first run found a real bug (verification output such as `__pycache__` was committed into the branch); fixed by
verifying an exported copy of what a commit would hold, with two tests. One small case, not a benchmark; cost unknown
(`usd` null: no Claude prices in the throwaway profile). The Linux `bwrap` path is tested as arguments only, never run.

## Needs Yehor

1. **Approve `prompt-ci` for #125** in the `prompt-ci-review` environment (read the diff of `presets/code.yaml`,
   `prompts/core/writer.md`, `prompts/preset/code.md`). It gives a worker edit rights (shell still denied). Plane item CHARGEHAND-100.
2. **Read ADR 0035** and accept or change its 14 decisions (Plane CHARGEHAND-91 is done; the ADR is "proposed").
3. **Seed the `chargehand-code-writer` Langfuse dataset** (CHARGEHAND-99). The `code/writer` cell names it.
4. **0.7's own exit bar is yours**: Plane goal 5 says done when 5 real issues go through the writer, at least 4 pass
   verification, you merge at least 3 without rewriting, mean cost is no more than a plain session, and no run writes outside
   its clone. Not done, not faked. 0.5's usage bar (your own `/chargehand:change` use) is also still yours.
5. **Release 0.7.0** (version bump, tag, GitHub Release, `Chargehand.Contracts` to nuget.org because schemas changed,
   MCP Registry) when you decide. Not started.

## Findings recorded (Plane Intake, CHARGEHAND-90)

Dogfood of `/chargehand:change`'s research step on this repo failed twice: `provider_unavailable`, "OpenCode 503
ServiceUnavailableError: Detection service unavailable", `retryable: true`, no `error.action` (run ids
run-20260929-221935-91256e, run-20260929-221942-e6fab5). So the 0.5 command was not used on real tasks here; only the
`code` preset e2e was run live. Work-item state is in Plane; the cycle is `v0.7.0`.

## Working notes for the next session

- Run dotnet with the agent sandbox off (`dangerouslyDisableSandbox`): inside it `dotnet restore` hangs about 5 minutes and exits 1;
  outside, `scripts/check.sh` takes under a minute.
- The agent sandbox also blocks writing `.mcp.json` and `.claude/settings.json` into a fresh checkout; use `git sparse-checkout`
  to skip them, or work in the session worktree.
- Parallel PRs conflict on the `[Unreleased]` changelog. Merge main into the branch (no force-push), resolve, push.
- `gh` must be a standalone call. `gh pr checks --watch` never ends while `prompt-ci` is pending.
- After a squash-merge, merge origin/main into stacked branches and take your side for files both touched.
- This file was meant for the main checkout; a hook blocks writes there from a worktree session, so it lives in the
  session worktree (`.claude/worktrees/adoring-blackwell-582457/docs/HANDOFF.md`, untracked). Copy it to the main checkout
  by hand or commit it.

## Next

Stage 0.8: every claim checked for support against its cited text, and results signed so anyone can verify them offline.
Plan and Plane items first. Signing-key creation or storage waits for Yehor; the design uses a key the user supplies and
tests use in-memory keys.
