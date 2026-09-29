---
title: "What it does, and how we know"
description: "Each capability with a status of works, partial or not yet, and the test, benchmark, ADR or roadmap entry behind that status."
order: 1
---

Each row names the evidence for its status. `works` means a test in this repository or a recorded benchmark covers the capability. `partial` means part of it is missing, or nothing in the repository tests or measures it. `not yet` means it is planned and not built. Rows marked *on main, not yet released* are newer than v0.3.0.

The tests call no model. They run the orchestrator on a scripted runtime ([ScriptedRuntime.cs](../../tests/Chargehand.Tests/ScriptedRuntime.cs)) or on stand-in `opencode` and `claude` binaries. The runs against real models are the ones in [docs/benchmarks.md](../benchmarks.md) and the live checks that ADRs record.

## Status

| Capability | Status | How we know |
|---|---|---|
| Answer a read-only question with citations checked against the pinned commit | works | [EvidenceResolverTests](../../tests/Chargehand.Tests/EvidenceResolverTests.cs); [WorkerNodeTests](../../tests/Chargehand.Tests/WorkerNodeTests.cs) (one repair turn, then unresolved claims move to open questions); [CheckoutTests](../../tests/Chargehand.Tests/CheckoutTests.cs) (the worker reads a clone at the pinned commit). Cited lines that resolved: 50 of 50 at the [phase 3 exit](../benchmarks.md#phase-3-exit-one-worker-against-a-plain-session), 99 of 99 at the [phase 4 exit](../benchmarks.md#phase-4-exit-split-against-a-plain-session). |
| Split a question into 2 to 4 parallel read-only subtasks | works | [SplitTests](../../tests/Chargehand.Tests/SplitTests.cs) (dependency order, fork of the first node, at most 2 at once, deterministic merge). [Phase 4 benchmark](../benchmarks.md#phase-4-exit-split-against-a-plain-session): intake split 6 of 6 runs and 99 of 99 citations resolved, but a split cost about 1.53× a plain session ($0.00754 against $0.00492 per run) for a blind score of 0.967 against 0.950. It did not pay on those questions ([ADR 0017](../adr/0017-split-runs-forked-siblings-and-merge.md)). |
| Stop actions `deny`, `ask` and `improve`, and approval stops | works | [OrchestratorActionTests](../../tests/Chargehand.Tests/OrchestratorActionTests.cs): each stop's status and open questions, the improved request's diff artifact, `strict`'s approval thresholds, `context.approved`, and the fall back to `answer` for an action the preset does not allow. |
| OpenCode runtime, pinned to 2.0.18 | works | [OpenCodeClientTests](../../tests/Chargehand.Tests/OpenCodeClientTests.cs), [OpenCodeSpecContractTests](../../tests/Chargehand.Tests/OpenCodeSpecContractTests.cs) (against the checked-in spec), [OpenCodeServerProcessTests](../../tests/Chargehand.Tests/OpenCodeServerProcessTests.cs) (stand-in binary). The phase 3 and 4 benchmarks ran on OpenCode before the pin moved from 2.0.16 to 2.0.18; the re-pin note records no live worker session on 2.0.18 ([ADR 0004](../adr/0004-opencode-major-and-runtime-adapter.md#re-pinned-to-2018-2026-09-28)). |
| Claude Code runtime, pinned to 2.1.283 | works | [ClaudeCodeRuntimeTests](../../tests/Chargehand.Tests/ClaudeCodeRuntimeTests.cs) (stand-in CLI), [ClaudeCodeDefaultsTests](../../tests/Chargehand.Tests/ClaudeCodeDefaultsTests.cs). [ADR 0020](../adr/0020-claude-code-runtime-adapter.md) records a live run on 2.1.195 whose 6 file references all resolved, and a live two-turn session on 2.1.283; the API-key mode never ran live. Running on the CLI's own login is on main, not yet released. |
| Runtime selection: the profile's `runtime`, then `CHARGEHAND_RUNTIME`, then the one agent CLI on `PATH` (on main, not yet released) | works | [RuntimeSelectorTests](../../tests/Chargehand.Tests/RuntimeSelectorTests.cs); [changelog, Unreleased](../../CHANGELOG.md#unreleased); [ADR 0026](../adr/0026-extension-model.md). |
| Presets with per-node token and USD budgets | works | [ConfigFileTests](../../tests/Chargehand.Tests/ConfigFileTests.cs) (shipped presets validate, deny reading `*.env` files, remove the shell tool); [WorkerNodeTests](../../tests/Chargehand.Tests/WorkerNodeTests.cs) (token budget, USD cap, compaction trigger, deadline); [ReviewPresetTests](../../tests/Chargehand.Tests/ReviewPresetTests.cs). |
| Drafts for program callers (`draft` preset: no repository, caller inputs as evidence) | works | [DraftTests](../../tests/Chargehand.Tests/DraftTests.cs). [Phase 5 exit](../benchmarks.md#content-engines-call-through-the-interface-met): one draft through `chargehand serve` completed for $0.0006 with 4 claims, each citing an input the caller sent. |
| Error codes on failed results | works | [ResultErrorTests](../../tests/Chargehand.Tests/ResultErrorTests.cs), [WorkerNodeTests](../../tests/Chargehand.Tests/WorkerNodeTests.cs), [OpenCodeClientTests](../../tests/Chargehand.Tests/OpenCodeClientTests.cs); [ADR 0022](../adr/0022-error-codes-in-result-v1.md). |
| Repository roots and worker clones at the pinned commit | works | [CheckoutTests](../../tests/Chargehand.Tests/CheckoutTests.cs) (roots, `/`, clone reuse, another commit, uncommitted and ignored files kept out, a tracked denied file refused); [ProfileTests](../../tests/Chargehand.Tests/ProfileTests.cs) (the launch-directory default); [ADR 0023](../adr/0023-repository-roots-and-worker-clones.md), [ADR 0028](../adr/0028-default-repository-roots.md). |
| Run log and `show` | works | [RunLogTests](../../tests/Chargehand.Tests/RunLogTests.cs) (a start record without a run record, concurrent writers, a half-written last line); [ServerTests](../../tests/Chargehand.Tests/ServerTests.cs) checks that a run whose process ended reads as `lost`. `show` prints those records; its output format has no test of its own. |
| Reports: `cache`, `reconcile`, `routes` | works | [CacheReportTests](../../tests/Chargehand.Tests/CacheReportTests.cs), [ReconcilerTests](../../tests/Chargehand.Tests/ReconcilerTests.cs), [RoutingReportTests](../../tests/Chargehand.Tests/RoutingReportTests.cs). At the [phase 4 exit](../benchmarks.md#phase-4-exit-split-against-a-plain-session), `chargehand cache` named the instruction entry that stopped siblings from forking. [ADR 0012](../adr/0012-observability.md) records 8 of 8 calls joined to gateway spend rows in the v0 benchmark. |
| HTTP server with a bearer key | works | [ServerTests](../../tests/Chargehand.Tests/ServerTests.cs) (key on every route, Host check, refusal to listen beyond loopback without allowed hosts, status codes, events, the bound of 10 unfinished runs). [Phase 5 exit](../benchmarks.md#content-engines-call-through-the-interface-met): `samples/ContentEngineCall` sent its draft to `POST /v1/runs`. |
| MCP over Streamable HTTP (`/v1/mcp`) | works | [McpTests](../../tests/Chargehand.Tests/McpTests.cs) (the SDK's own client lists `orchestrate` with both schemas and calls it); [ADR 0018](../adr/0018-callable-interface-http-mcp-run-store.md) records a live check that ran a draft as a task. |
| MCP over stdio, `chargehand mcp` (on main, not yet released) | works | [StdioMcpTests](../../tests/Chargehand.Tests/StdioMcpTests.cs); CI packs the tool and runs [mcp-smoke.py](../../scripts/mcp-smoke.py) against it with a stand-in `claude` ([ci.yml](../../.github/workflows/ci.yml)). |
| MCP tasks, `input_required` questions, and the run id before a client timeout | works | [McpTests](../../tests/Chargehand.Tests/McpTests.cs), [StdioMcpTests](../../tests/Chargehand.Tests/StdioMcpTests.cs); [ADR 0018](../adr/0018-callable-interface-http-mcp-run-store.md), [ADR 0029](../adr/0029-mcp-run-id-before-a-client-timeout.md). The run-id paths of ADR 0029 are on main, not yet released. |
| Server container image | partial | [Dockerfile](../../Dockerfile), [release.yml](../../.github/workflows/release.yml). No test or benchmark in this repository runs the image. |
| Prompt CI on prompt and preset changes | partial | [GateTests](../../tests/Chargehand.Tests/GateTests.cs), [PromptCiTests](../../tests/Chargehand.Tests/PromptCiTests.cs), [prompt-ci.yml](../../.github/workflows/prompt-ci.yml). [Phase 5 exit](../benchmarks.md#prompt-ci-blocks-a-real-prompt-regression-met-on-a-rerun): both planted regressions passed the first gate; with a completeness score one of them blocks and the other still passes. |
| Optional long-term memory (Hindsight) | partial | [HindsightMemoryTests](../../tests/Chargehand.Tests/HindsightMemoryTests.cs) checks the adapter's recall, retain and invalidate requests against a recording HTTP handler. No test covers a run that recalls facts, and no benchmark measures one ([ADR 0008](../adr/0008-memory-provider-contract.md)). |
| Running with no profile file (on main, not yet released) | partial | [ProfileTests](../../tests/Chargehand.Tests/ProfileTests.cs) (defaults when the file is missing). The presets still name placeholder models that need a profile's `models` map ([package README](../../src/Chargehand.Cli/README.package.md)). Goal 0.4 in [ROADMAP.md](../../ROADMAP.md) is open. |
| Claude Code plugin, `/chargehand:change` (on main, not yet released) | partial | [ChangeSkillTests](../../tests/Chargehand.Tests/ChangeSkillTests.cs), [PluginManifestTests](../../tests/Chargehand.Tests/PluginManifestTests.cs), and `claude plugin validate --strict` in [ci.yml](../../.github/workflows/ci.yml). [change-e2e.sh](../../scripts/change-e2e.sh) is an end-to-end check that [change-e2e.yml](../../.github/workflows/change-e2e.yml) runs on demand; the repository records no result of it. The plugin's own server needs the unpublished package ([README](../../README.md#claude-code-plugin)). |
| `Chargehand` and `Chargehand.Contracts` on nuget.org | not yet | [README](../../README.md#from-the-package-once-published); [changelog 0.3.0](../../CHANGELOG.md#030---2026-09-28) ("dormant nuget.org publishing"); [ADR 0027](../adr/0027-dnx-package-and-mcp-registry.md) (no package on 2026-09-28); [release.yml](../../.github/workflows/release.yml). |
| Listing in the MCP Registry | not yet | Goal 0.4 in [ROADMAP.md](../../ROADMAP.md); [ADR 0027](../adr/0027-dnx-package-and-mcp-registry.md). [McpServerJsonTests](../../tests/Chargehand.Tests/McpServerJsonTests.cs) validates `.mcp/server.json`; no workflow publishes it. |
| Writing nodes in worktrees | not yet | Goal 0.7 in [ROADMAP.md](../../ROADMAP.md); [ADR 0015](../adr/0015-merging-and-verification.md); [SplitTests](../../tests/Chargehand.Tests/SplitTests.cs) (a split with a writing subtask is rejected). |
| Checking that the cited text supports each claim | not yet | Goal 0.8 in [ROADMAP.md](../../ROADMAP.md). [GitEvidenceResolver.cs](../../src/Chargehand/Verification/GitEvidenceResolver.cs) checks that a cited path and line range exist at the commit, not what the lines say. |

## Limits of the rows that work

- **Split.** On the phase 4 questions a split scored 128 per dollar against 193 for a plain session. On cost alone it needs about 1.5× the plain answer's quality to win on quality per dollar, and it reached 1.02×. [ADR 0017](../adr/0017-split-runs-forked-siblings-and-merge.md) concludes that intake should choose `split` only when one session cannot cover the parts.
- **OpenCode 2.0.18.** The contract test passes against the 2.0.18 spec, and [ADR 0030](../adr/0030-default-opencode-server.md) records a hand check that a 2.0.18 server starts and answers `/api/info`. No recorded worker session ran on 2.0.18.
- **Claude Code API-key mode.** The tests cover it with a stand-in CLI; no live run is recorded ([ADR 0020](../adr/0020-claude-code-runtime-adapter.md)).
- **USD caps need prices.** A model with no entry in the profile's `prices` has an unknown cost: `usage.usd` may be `null`, and its USD cap cannot fire. The per-node token budgets still apply. This change is on main, not yet released ([changelog, Unreleased](../../CHANGELOG.md#unreleased)).

## What is missing

### Server container image

A tag `v<Version>` builds and pushes `ghcr.io/<owner>/chargehand:<Version>` after [release.yml](../../.github/workflows/release.yml) checks the tag against `Directory.Build.props` and the changelog. Nothing in the repository starts that image and sends it a request. The image carries the Claude Code runtime only; an OpenCode profile points `opencode.url` at a server that runs next to the container ([ADR 0024](../adr/0024-server-on-a-private-network-and-release-images.md)). The [0.2.1 fix](../../CHANGELOG.md#021---2026-09-28) covers that setup: a worker clone across a container's repository mount and its work volume.

### Prompt CI

The gate runs and posts its status, and its coverage has holes:

- Its first score measured grounding, which the worker node already enforces at run time, so both planted regressions passed. A completeness factor now blocks the first (#5) on a rerun. The second (#6) kept its claim count and still passes.
- By a replay of logged runs, the score catches a thinning of #5's size in at most about 86% of reruns at 12 items ([ADR 0019](../adr/0019-prompt-ci-and-routing-report.md)).
- The prompt blocks and preset files of `default`, `thorough` and `strict` have no eval cell; a change to them fails the gate unless the owner passes `--allow-uncovered`.
- `evals/cells.json` on main defines a `review/worker` cell; `docs/benchmarks.md` records no calibration run for it.

[Prompt CI](prompt-ci.md) has the numbers.

### Optional long-term memory

With the profile's `memory` block set, a run recalls facts once and adds them to each node's prompt as context the worker is told to check in the repository and never cite. A failed recall leaves the run without them, and retain stays off by default ([ADR 0008](../adr/0008-memory-provider-contract.md)). The only backend is a self-hosted Hindsight service (`memory.backend` accepts `hindsight`). Any MCP memory server, and several at once, belong to goal 0.6 in [ROADMAP.md](../../ROADMAP.md).

### Running with no profile file

On main, `Profile.Load` returns defaults when the file is missing. The shipped presets still name placeholder models (`provider/worker-model`, `provider/small-model`, `provider/critic-model`), and with no `models` map those ids reach the runtime unresolved ([change-e2e.sh](../../scripts/change-e2e.sh)). The [README](../../README.md#quick-start) says a profile with a `models` map stays required for Claude Code until 0.4 lands the fix. Goal 0.4 ends when a clean machine with one signed-in agent CLI gets an answer with resolved evidence and no profile file ([ADR 0026](../adr/0026-extension-model.md)).

### Claude Code plugin

`/plugin install` works through the marketplace entry, but the plugin's MCP entry runs `dotnet dnx Chargehand@0.3.0 --yes -- mcp` ([.mcp.json](../../plugins/chargehand/.mcp.json)), which needs the package on nuget.org. Until then you point a `chargehand` MCP server at a checkout ([The change command](change.md)). The command is goal 0.5 in [ROADMAP.md](../../ROADMAP.md).

### Packages on nuget.org

[release.yml](../../.github/workflows/release.yml) publishes `Chargehand.Contracts` only, and only when the repository variable `NUGET_USER` is set; the 0.3.0 changelog calls that publishing dormant. No workflow step packs or pushes the `Chargehand` tool. [ROADMAP.md](../../ROADMAP.md) marks goal 0.3 done and lists `Chargehand.Contracts` on nuget.org under it, while [ADR 0027](../adr/0027-dnx-package-and-mcp-registry.md) found neither package there on 2026-09-28 and the README says the `Chargehand` package is not there yet.

### MCP Registry listing

`.mcp/server.json` describes the entry `io.github.egoushka/chargehand` for the `Chargehand` package. The registry needs the package on nuget.org before the entry ([ADR 0027](../adr/0027-dnx-package-and-mcp-registry.md)), and no workflow publishes either.

### Writing nodes

Every shipped preset denies edits. [ADR 0015](../adr/0015-merging-and-verification.md) designs one writing node per git worktree, a build and test check before merge, and merging by a person; none of it is built. Goal 0.7 reads "Workers write branches that build and pass their tests in a sandbox."

### Support checking

The resolver confirms that a citation points at something real: a path and line range at the commit, a commit, a diff hunk, a message id, a URL the node saw, an input id. It does not compare the cited text with the claim. Goal 0.8 adds that check and signed results that anyone can check offline.
