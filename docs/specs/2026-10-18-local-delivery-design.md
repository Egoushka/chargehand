# Local delivery: a writing run that stays on the machine

- Status: draft for the maintainer's review. Every decision below is the proposer's.
- Date: 2026-10-18

## Goal

A person can run chargehand on a repository whose code must never leave their machine: a writing run commits to a
local branch in the caller's checkout and nothing else happens. No push, no pull request, no forge call, no memory
write to a store the profile did not clear. The person picks the mode per request; the profile can force it for whole
directory trees, and then no request can undo it.

Done when: (1) a `code` run with `context.delivery: "local"` ends with a branch `chargehand/<run>/<node>` in the
caller's repository, and the caller's working tree, index, `HEAD` and current branch are unchanged; (2) a repository
under a profile `delivery.local_only` root runs in that mode whatever the request says, and a request that says `push`
is refused; (3) each path in the table under "Paths that push" refuses a local-only run, shown by a test per path;
(4) a local run recalls from and retains into only the memory sources the profile marks `local_ok`.

## Where it stands

Evidence: `path:line` at commit `b919c23` (0.10.1).

1. **The `code` preset never pushes.** The branch stays in the run clone (`RunWorkspace.cs`, ADR 0035 decision 4);
   the result's `branch` artifact names the clone and the caller runs `git fetch`. Nothing lands in the caller's repository.
2. **Driven sessions push by design.** `Handover.RunAsync` fetches the session's bundle into a scratch clone, scans,
   verifies, pushes (`Handover.cs:87-105`, `Handover.Git.cs:12`) and opens a draft pull request. `driven.push_secret`
   is required (`DrivenRun.cs:360`). The integrator merges and updates branches through the forge
   (`Integrator.cs`, `GitHubIntegrationHost.cs`); the CI fixer pushes fix commits (`CiFixRequest`, `CiFixResult.Pushed`).
3. **Intake reads a forge.** GitHub intake and Plane intake watch `automation.*.repositories`, each with a server
   checkout `path`; both start batches (ADR 0051, 0067).
4. **Memory is per profile source.** `memory[]` entries recall and, with `retain`, write claims (ADR 0034, 0008);
   the prompt enhancer sends the request text to an MCP server (ADR 0040). A run has no way to say "not this one".
5. **The repository ceiling is `repository_roots`** (ADR 0023): where a request may point. It says nothing about what
   may leave.
6. **`/chargehand:change` is already local** (`docs/guide/change.md`): the caller's own session writes on `change/<slug>`
   and nothing is pushed. Its research and review calls go to whichever chargehand server the client picks.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Request field | `context.delivery`, optional, `"local"` or `"push"`. Additive to `request/v1`. Absent means the profile decides, else `push`. |
| 2 | What the two values mean | `local`: the run's output stays on the machine, as in the goal. `push`: today's behaviour (the `code` branch stays in the run clone; driven sessions push and open draft pull requests). Read-only presets honour `local` too: no forge, no uncleared memory, no enhancer. |
| 3 | Profile setting | `delivery.local_only`: a list of directories. A repository whose top level (symbolic links resolved, as ADR 0023 resolves it) lies under one is local-only. `/` makes the whole server local-only. A leading `~/` expands to the home directory. |
| 4 | Precedence | The profile wins. Effective mode is `local` if the repository is under a `local_only` root or the request says `local`. A request that says `push` for a local-only repository fails with `local_only` (additive), so a client can tell "not allowed here" from "malformed". |
| 5 | How the branch lands | After the commit, chargehand runs `git -C <source> fetch --no-tags --no-write-fetch-head <run clone> refs/heads/<branch>:refs/heads/<branch>` (no `+`, never forced). That writes objects and one new ref under `refs/heads/chargehand/`; it touches no working-tree file, the index, `HEAD` or any existing ref. The branch name carries the run id, so it cannot collide. This is the one write ADR 0023 forbade ("the source is only read"); ADR 0069 carves it out for `local` delivery only. A bundle was considered and rejected: it adds a file to move and unbundle for the same result. |
| 6 | When landing fails | The run is `failed` with `delivery_failed` (additive), the branch still in the run clone, and `action` saying the fetch command to run by hand. |
| 7 | What the result shows | The `branch` artifact gains `delivery` (`local` or `push`); for `local`, `repository` is the caller's checkout, where the branch now is. The run's span carries `chargehand.delivery`. Read-only results carry nothing new: they have no branch. |
| 8 | What the person sees before the run | The MCP `orchestrate` tool lists `context.delivery` in its description and input schema. `/chargehand:change` takes `--local`: it sends `delivery: "local"` on every call and uses the server named `chargehand-local`, stopping with a message if that server is absent. The profile still forces local on a server that has the root. |
| 9 | Driven sessions | Refused for a local run with `local_only`. Sessions run in containers, hand over a bundle and exist to push; a local variant (fetch the bundle into the caller's repository instead of pushing) is a follow-up, not in this change. Reopen when someone needs it. |
| 10 | Memory and enhancer | A source marked `local_ok: true` serves a local run; every other source is skipped for it, both recall and retain. The prompt enhancer is skipped for a local run. So a profile that retains verified claims to a shared store can retain them to a separate work store by marking only that source `local_ok`, or retain nothing by marking none. |
| 11 | Where it runs | On the machine that holds the code: the Mac's `chargehand-local` server. The server has no way to know a path belongs to someone else's machine, so keeping work code off a shared server rests on that server's own `repository_roots` not containing it. Local-only roots protect a server that does see the code from pushing it; they do not protect one that never sees it. |
| 12 | Telemetry | Out of scope here. OTLP spans carry prompts; a profile for local-only work omits `telemetry`. The guide says so. |

## Paths that push, and the guard for each

| Path | Where | Guard | Test |
|---|---|---|---|
| `code` preset, request says push on a local-only repository | `Orchestrator.RunAsync` | `DeliveryPolicy.IsLocal` throws `local_only` before intake and the clone | refusal; landing happens for `local`; working tree unchanged |
| Driven batch (driven `push_secret`, scratch clone, PR) | `DrivenRun.SetupOf`, before the push credential is read | refuse if effective mode is local | the credential is never resolved |
| Hand-over (`git push`, draft pull request) | `Handover.RunAsync` | `HandoverInput.LocalOnly` refuses before any git or forge call | fake pull requests never called, no git started |
| Integrator (ready, merge, update branch) | `Integrator.IntegrateAsync` | `IntegrationTarget.LocalOnly` returns without a host call | fake host records no call |
| CI fixer (fix push) | `Integrator.FollowAsync` | same flag, before the fixer session | fixer never invoked |
| GitHub and Plane intake (forge reads, batch start) | `Profile.Load` | an `automation.*.repositories[*].path` under a `local_only` root fails the profile | profile load throws naming the key |
| Memory and enhancer | `Orchestrator` | local runs use `MemoryStack.LocalOnly()` | non-`local_ok` source never asked or written |

The driven guard is the first line, and the hand-over, integrator and CI-fixer guards are the second: a bug in the
first must not push.

## Components

| Unit | Change |
|---|---|
| `Chargehand.Kernel.DeliveryPolicy` | Pure: `IsLocal(requested, repositoryTopLevel)` and `Forced(path)`, roots resolved once. Shared by every guard. |
| `Chargehand.Kernel.PathScope` | `Under` and `RealPath`, moved out of `Orchestrator` so the policy and the checkout check share them. |
| `Contracts.RequestContext.Delivery`, `ErrorCode.LocalOnly`, `ErrorCode.DeliveryFailed` | Additive; schema and `SchemaCompatTests` baseline updated. |
| `Profile.Delivery` (`DeliverySettings`) | `local_only` list, validated at load. |
| `RunWorkspace.LandAsync` | Decision 5. |
| `ChangeRun.Finish` | `delivery` in the branch artifact. |
| `MemoryStack.LocalOnly()`, `MemoryProviderSettings.LocalOk` | Decision 10. |
| `plugins/chargehand/skills/change/SKILL.md` | `--local`. |

## Testing

Unit and integration on real git repositories in a temp directory: landing leaves `HEAD`, index and tracked files
byte-identical; the profile forces local and refuses `push`; each guard in the table with a fake that fails if called.
No model is called.

## Out of scope

Local driven sessions; a local integrator (nothing to integrate without a pull request); making telemetry local-aware;
detecting that a repository belongs to another machine's server.

## Open items

- The VPS profile needs no change for this: it has no `delivery` block and no work roots. Confirming that its
  `repository_roots` exclude any work checkout is an operator check, not code.
