# 0060. Feature modules and Native AOT hosts: the skeleton the rewrite ports into

- Status: accepted
- Date: 2026-10-13

## Context

chargehand grew to nine projects and about 30k lines of C#. A measured baseline (churn, complexity, coupling) found the
code healthy in the median (median cyclomatic complexity 2, 0.5% duplication) and bad in about a dozen places: the
driven-batch engine (`BatchScheduler` CCN 107, `DrivenRun` 91, `SessionDriver` 61, `TaskRunner` 1,000 lines),
`Orchestrator`, and the settings wiring (`Config/Profile.cs`, 690 lines, read by 17 feature areas). Three namespace
cycles cross the core. 95% of types are public. A feature map of the 219 source files found 30 candidate feature
modules and 14 pieces of generic plumbing with no chargehand types.

Nothing in the code is source-generated for JSON (167 reflection-based serializer calls), so no host can publish with
Native AOT today. Measured on this skeleton (macOS arm64, empty hosts): the server and runner hosts publish to an 8.8 MB
binary, answer `/healthz` about 145 ms after start and hold about 24 MB resident; the CLI and session hosts are 1.7 MB.
These are floors, not a comparison: the hosts are empty.

The maintainer decided to rewrite chargehand into a feature-based structure, ported feature by feature with the existing
tests as the specification, on .NET 10 and later .NET 11, with Native AOT hosts. Styles compared: Clean Architecture
with four projects per module, hexagonal, vertical slices, a modular monolith with one project per module, a functional
core with state machines, actors, in-process events.

## Options

1. Refactor the nine projects in place. Least work; boundaries stay enforced by tests over namespaces only.
2. Four projects per module (Domain, Application, Infrastructure, Contracts). Strongest walls; 80+ projects and the most
   ceremony per feature.
3. One project per feature module, internal by default, with a pure state-machine core inside and ports only at real
   I/O. The compiler enforces the layering; tests enforce the public surface.
4. One project per candidate module (about 30). Many would be 300-500 lines: boundaries that hide little.

## Decision

Option 3, built beside the current projects and filled by porting:

- `src/Shared/Chargehand.Kernel`: types every module shares (`ChargehandException` among them); stays a few hundred lines.
  `src/Chargehand.Contracts` stays the published wire contract, layer 0; the Kernel sits on it, layer 1.
- `src/Modules/Chargehand.Modules.*`: 17 feature modules (Ledger, Accounts, Memory, Journal; Runners, Sessions,
  Gateway; Tasks, Handover, Oversight; Batches, Research; Intake, Console, Evaluation, Onboarding, Watch).
  `src/Runtimes/Chargehand.Runtimes.*`: the Claude Code and OpenCode adapters.
- `src/Hosts/Chargehand.Hosts.*`: Cli, Server, Runner and Session (the driver inside each session container), each
  published with Native AOT.
- Rules, checked by `tests/Chargehand.Architecture.Tests`: every project has a layer and references only lower layers
  (`Skeleton.Layers`); nothing in the skeleton references the old `src/Chargehand*` projects, so the port runs old to
  new only; a module's public types live only in its `.Api` namespace; no source file over 400 lines. CA1502 fails the
  build on a method with cyclomatic complexity over 15 (`src/CodeMetricsConfig.txt`). Libraries set
  `IsAotCompatible`, so trim and AOT warnings fail the build; CI job `aot` publishes and runs every host
  (`scripts/aot-smoke.sh`).
- Inside a module: `Core/` holds pure decision logic (`Decide(state, input)`, no I/O), one folder per use case, and
  `Adapters/` for I/O behind the module's ports. Modules call each other through their `Api` facades; the only
  asynchronous hand-off is a step recorded in the journal. Each module owns its settings.
- Generic code with no chargehand types moves to a separate MIT library repository, published on nuget.org, so this
  repository keeps building from public packages only.

Port order: Ledger, Accounts, Memory; Journal, Runners, Sessions (the restart bugs first); Tasks, Handover, Oversight;
Batches as state machines; Research, Gateway; the edges; then the old projects are deleted and .NET 11 follows.

## Consequences

- Two structures live side by side until the port ends; old projects may reference new modules, never the reverse.
- More projects: 24 in the skeleton against 9 today, plus a test project per module as its code lands.
- Every JSON type needs a source-generated context; presets need YamlDotNet's static generator; MCP tools are registered
  explicitly. A dependency that blocks AOT keeps its host on the JIT until fixed.
- The `Profile` record goes away: each module binds and validates its own options.

## Reopen if

Porting Tasks, Sessions and Batches needs more than two upward ports (the module cut is wrong); the port leaves both
structures alive for more than one release cycle; or a host cannot publish with AOT for a reason the dependency's
maintainers will not fix.

## Amendment, 2026-10-14: an 18th module, Forge

`driven.forge` (which code host the remotes are on and how to reach it) is read by Handover, which pushes and opens the
pull request, and by Oversight, whose pull-request outcome tracker reads the request's state. Both sit on layer 4 and
neither may reference the other. The owner chose a small module below both, `Chargehand.Modules.Forge` on layer 2,
which holds the forge settings and their validation; not the Kernel, which stays types every module shares, and not an
upward port. The forge ports (`IPullRequests`, `IIntegrationHost`) stay with the code that implements them until
Handover is ported. The skeleton now has 18 feature modules and 25 projects.

## Amendment, 2026-10-14: Oversight below Sessions, Handover below Tasks

Porting Tasks, Handover and Oversight on the layers above needs five upward ports, more than the two the reopen
condition allows: Tasks to Handover and Tasks to Oversight (same layer, so forbidden), Tasks to the Batches task state,
Tasks to the Research proof, and Sessions to Oversight (the governor runs inside the session). The owner chose to
re-layer the same 18 modules instead of adding ports or merging modules. Bottom to top:

| Layer | Projects |
|---|---|
| 0 | Contracts |
| 1 | Kernel |
| 2 | Ledger, Memory, Journal, Forge |
| 3 | Accounts, Oversight |
| 4 | Runners, Sessions, Gateway |
| 5 | Handover |
| 6 | Tasks |
| 7 | Batches, Research |
| 8 | Intake, Console, Evaluation, Onboarding, Watch; Runtimes ClaudeCode, OpenCode |
| 9 | Hosts Cli, Server, Runner, Session |

Oversight needs only Forge, and Handover names nothing in Tasks, Oversight or Sessions once its verify command sits in
Sessions or the Kernel, so three of the five edges point down. Two upward ports remain, both declared in `Tasks.Api`,
implemented by the higher module and wired by the hosts:

- P3, Tasks to Batches: `ITaskState`, the task's journal entry, updates, finish and records, which live in the batch
  state today. It goes when the batch state is retired (ADR 0061).
- P4, Tasks to Research: `ITaskResults`, building, checking and signing the task's result with the evidence resolver
  Research owns.

`UpwardPortTests` in `tests/Chargehand.Architecture.Tests` lists the allowed ports by type name and fails on any other
interface or abstract type a higher skeleton project implements for a lower one. The list stays empty until the Tasks
port adds P3 and P4. On the layer order, this supersedes the R7 target-architecture diagram, which put Tasks, Handover
and Oversight together on one layer above Sessions; the governor now sits below Sessions, where it runs.

## Amendment, 2026-10-14: Gateway, Runners and Sessions in order

Layer 4 held Runners, Sessions and Gateway side by side, and a project may not reference one on its own layer. The code
they hold is a chain, so the Sessions and Runners port (C3 slice S6) could not move it with references pointing down
(paths as of `7631425`):

- The container engine uses the gateway's egress rules. `ContainerTemplate.EgressArgs` parses forwards with
  `PortForward.Parse` and checks the model host with `ModelGateway.RefuseHost`
  (`src/Chargehand.Driven/Containers/ContainerTemplate.cs:147-159`). It writes the egress env file with
  `EgressCli.CredentialVariable` and `KeyVariable` (`:192`). `ModelGatewaySpec` defaults to `EgressCli.DefaultModelHost`
  (`ContainerSpec.cs:55`).
- The runner client is an engine. `RunnerClient` implements `IContainerEngine`, `IWorkspaceEngine` and the other engine
  interfaces, and caps downloads at `DockerCliEngine.OutFileCap` (`src/Chargehand.Driven/Runner/RunnerClient.cs:14-15`).
  `RunnerPolicy` checks cache names with `CacheVolumes` and output files with `ContainerTemplate`
  (`Runner/RunnerPolicy.cs:35-36,95`).
- Sessions run on the engine. `SessionDriver` writes the files `ContainerTemplate` names (`GovernorFile`, `UsageFile`,
  `HandoffFile`, `ProgressFile`, `SeedFile`; `src/Chargehand.Driven/Driven/SessionDriver.cs:86,293,378-389`).
  `Transcript` caps at `ContainerTemplate.MaxTailBytes` (`Transcript.cs:24`), and `SessionEnvironment` names the cache
  mounts (`SessionEnvironment.cs:141`). `EngineSessionVolumes` and `JournaledSteps` move a session's files through
  `IOutVolumeEngine`, `IOutTailEngine` and `IWorkspaceEngine`.

The owner's direction for C3 is option B: keep the same 18 modules and change only the layer order. Layer 4 becomes three
layers, in the order of the chain, and everything above moves up by two:

| Layer | Projects |
|---|---|
| 0 | Contracts |
| 1 | Kernel |
| 2 | Ledger, Memory, Journal, Forge |
| 3 | Accounts, Oversight |
| 4 | Gateway |
| 5 | Runners (with the container engine, `Containers/` today) |
| 6 | Sessions |
| 7 | Handover |
| 8 | Tasks |
| 9 | Batches, Research |
| 10 | Intake, Console, Evaluation, Onboarding, Watch; Runtimes ClaudeCode, OpenCode |
| 11 | Hosts Cli, Server, Runner, Session |

No port is added. P3 (`ITaskState`, Tasks to Batches) and P4 (`ITaskResults`, Tasks to Research) are still the only
allowed upward ports, and `UpwardPortTests`' list stays empty until the Tasks port adds them. Handover keeps its place
below Tasks and above all three, so it may now reference Runners and Sessions, which `ContainerVerifier` needs.

## Amendment, 2026-10-14: CA1502 on the wiring left in Driven

C4's exit ("no Batches function over CCN 15") could pass while the batch's wiring stayed in `src/Chargehand.Driven` at
CCN 98. The owner chose to hold that wiring to the skeleton's budget until C6 deletes the project: `.editorconfig`
makes CA1502 an error under `src/Chargehand.Driven/`, and the project reads the same threshold file
(`src/CodeMetricsConfig.txt`). The batch lifecycle itself (start record, steps, result, run record) is
`BatchLifecycle` in Batches; Driven keeps the profile-dependent composition (`DrivenRun.RunBatchAsync`, `SiteBatches`).
