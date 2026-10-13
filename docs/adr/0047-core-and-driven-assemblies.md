# 0047. The orchestrator core ships without the driven infrastructure

- Status: accepted
- Date: 2026-10-13

## Context

`src/Chargehand` held two products in one assembly: the orchestrator (intake, task graph, results, citation checks, the support check, signing, budget, run log, memory, prompts, the shared parts of the runtime adapters) and the driven infrastructure of [ADR 0039](0039-driven-writing-sessions.md) onward (containers, the egress proxy, the runner client, sessions, batches, the governor, the handover, the integrator of [ADR 0046](0046-integrator-and-ci-fixer.md)). About 13.9k lines, of which `Driven/`, `Containers/`, `Runner/` and `Egress/` were roughly a third.

The two have different users. A read-only run needs a model runtime and a repository; it needs no container engine, no push credential and no GitHub App. Anything that grows around driven sessions (intake from triggers, a scheduler) would pile into the same assembly, and a reader could not tell what a read-only host depends on. The seam was already narrow: outside the four directories, only `RunService`, `McpTaskSource`, `Profile` (the `driven` section), the onboarding checks and two `Orchestrator` members touched driven types.

## Options

1. Keep one assembly and enforce folders by convention. No cost now; the seam erodes with the next feature.
2. Separate assemblies in one repository, separate deployables. One version, one CI, one release; the dependency direction is checked by a test.
3. Separate repositories. Clean boundary, but every change across the seam becomes two pull requests and a version bump, the contract between them needs its own compatibility tests and release train, and a skewed pair (Core N, Driven N-1) becomes a supported state. The seam is small and moves together with Core; this cost buys nothing today.

## Decision

Option 2.

**Layout.** `Chargehand` (Core) cannot reference `Chargehand.Driven`. `Chargehand.Driven` references Core. `Chargehand.Server` and `Chargehand.Cli` reference both and build the driven services only when `driven.enabled` is set (`DrivenRunFor` returns a run with no services otherwise); the adapters `Chargehand.Mcp`, `Chargehand.OpenCode` and `Chargehand.ClaudeCode` stay on Core. So a host with Core, Contracts and Mcp can answer read-only runs. A later `Chargehand.Automation` (intake triggers, a scheduler) references Driven. `ArchitectureTests` reflects over the assembly references and fails if Core, Contracts or an adapter references Driven.

**What moved.** `Driven/`, `Containers/`, `Runner/` and `Egress/` with `git mv`, namespaces unchanged. The CLI keeps the `Chargehand` tool package and carries `Chargehand.Driven.dll` in it, and both Dockerfiles publish the CLI, so the session image still has `chargehand session`.

**Profile.** The settings records of the `driven` section (`DrivenSettings`, `DrivenNetwork`, `DrivenRunner`, `DrivenGovernor`, `DrivenLimits`, `DrivenAdmission`, `DrivenTaskSource`, `DrivenIntegrator`) stay in Core as plain data in `Chargehand.Config`. The other choice, an opaque section in Core bound in Driven, would change every reader of `profile.Driven` (init, doctor, the server, the CLI, the profile tests). Keeping the records changes none. To make that possible, five small things crossed to Core: `GovernorMode` with its JSON converter (`Config`), `DrivenCheckout` (`Workspace`), `ITaskSource` with `TaskSourceException` (root namespace), and `AllowlistMatcher` (`Egress`, used by init and doctor to validate `network.allow`). `DrivenGovernor.ToSession` builds a Driven type, so it became an extension method in Driven with the same call syntax.

**Internals.** Core gives `InternalsVisibleTo` to Driven for three internals the driver uses: `ChangeRun`, `ProcessRunner` and `Orchestrator.AnswerKind`. Widening them to public would add API; the grant is to a sibling assembly of the same version. Tests keep their `InternalsVisibleTo` and stay in one project.

**Public, stable extension points.** At most four, versioned with the semantics of `Chargehand.Contracts` (additive within a major):

| Point | Interface | State |
|---|---|---|
| Agent runtime | `IWorkerRuntime` (Core) | exists; OpenCode and Claude Code implement it |
| Task source | `ITaskSource` (Core) | exists; `McpTaskSource` implements it |
| Forge (pull and merge requests) | `IPullRequests` (Driven) for opening a draft | exists, GitHub-shaped; the merge side is `IIntegrationHost` and the two should become one forge interface before it is called stable |
| Notifier | none | does not exist yet |

Everything else is `internal` or documented as unstable. Many types are still `public` in the code; narrowing them is not part of a change that must not alter behavior, and no type outside the table carries a stability promise.

**The proof pipeline is not pluggable.** Verification and citation checking (Core, `Verification/`), the support check, the secret scan of a task's diff and history (`DiffScan`, Driven) and the governor (Driven) have no extension interface. An extension can add work before or after them, never skip, replace or weaken them: the handover and the result path call them directly.

## Consequences

- A change that makes Core depend on Driven fails `ArchitectureTests` before review.
- Driven keeps reaching three Core internals, so a rename there is a compile error in Driven, which is the intended signal.
- One version, one release workflow, one image: nothing changes for users. No behavior changes.
- `AllowlistMatcher` lives in Core only because profile validation needs the grammar; the proxy that enforces it stays in Driven.

## Reopen if

A second consumer needs Driven without Core's release cadence; the extension points need to ship as their own package (then publish an abstractions assembly, not a new repository); or the `InternalsVisibleTo` list to Driven grows past a handful (then promote those types to a public Core API).
