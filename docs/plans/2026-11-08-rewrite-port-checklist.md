# Rewrite port checklist: what is left in the old projects

> A tracking list for ADR 0060 and ADR 0086, not a decision. Where a feature has no module named in either ADR, the
> target is a proposal and says so; the owner settles it when that port starts.

**Goal:** every feature still in `src/Chargehand*` has a target project in `src/Shared`, `src/Modules`, `src/Runtimes` or
`src/Hosts`. Once every box below is ticked, the old projects can be deleted (ADR 0060, port order: "then the old projects
are deleted and .NET 11 follows").

**State as of `5240c1f`.** Old projects: `src/Chargehand` (about 6,700 lines), `Chargehand.Automation` (6,000),
`Chargehand.Server` (6,700), `Chargehand.Driven` (1,200), `Chargehand.Cli` (1,000), `Chargehand.Mcp` (1,100),
`Chargehand.OpenCode` (1,000), `Chargehand.ClaudeCode` (900), `Chargehand.Acp` (600). `src/Chargehand.Contracts` is not on
the list: it stays the published wire contract, layer 0. Already ported, at least in part: Ledger, Accounts, Memory,
Journal, Forge, Oversight, Gateway, Runners, Sessions, Handover, Tasks, Batches (the lifecycle and scheduler), Research
(the task result and evidence port), Intake (settings, schedules, notify and task source types), Conversations,
Economics, and the Acp, ClaudeCode and OpenCode runtimes (settings only for OpenCode). Console, Evaluation, Onboarding and
Watch are empty projects; the four hosts are stubs.

## How the order is built

- Layers are `Skeleton.Layers` in `tests/Chargehand.Architecture.Tests/Skeleton.cs`. A project references only lower
  layers, and nothing in the skeleton references an old project, so a feature can move only after everything it uses
  has moved. The sections below go bottom to top; within a section the items have no order between them unless one
  names another.
- The old projects' own references give the rest: `Chargehand.Driven` uses `src/Chargehand`; `Chargehand.Automation`
  uses Driven; `Chargehand.Server` uses Automation and Driven; the runtimes and `Chargehand.Mcp` use `src/Chargehand`;
  `Chargehand.Cli` uses all of them.
- The only upward ports allowed stay the two in `UpwardPortTests`: `ITaskResults` and `IConversationRuntime`. A target
  below that would need a third is wrong and goes back to the owner (ADR 0060, "Reopen if").

## 1. Settings out of `Profile` (every layer, first)

ADR 0060: "The `Profile` record goes away: each module binds and validates its own options." Most modules already own
a settings type with its JSON context; what is left is in `src/Chargehand/Config`.

- [x] `Config/Profile.cs`: the sections it declared go to the module that reads each, with that module's settings JSON
  context: `SecretSource` (and the resolver behind `Profile.Secret`, `SecretSources`), `TelemetrySettings` and
  `DeliverySettings` to Kernel, `DrivenNetwork` to Gateway, `PromptEnhancerSettings` to Research, `InboxSettings` to Console.
- [ ] `Config/Profile.cs`, `Config/ProfileJsonContext.cs`: what is left is composition: the root record, `HttpSettings`
  (it composes Accounts', Events' and Conversations' sections, so it goes to Hosts.Server with the root, not to a module),
  `McpServerSettings` (now in Memory.Api) and `DrivenSettings`. The hosts compose the root. Plan: `2026-11-10-profile-move.md`.
- [x] `Config/DrivenRunners.cs` → Runners (`DrivenRunnerSet`; the `DrivenSettings` members stay as thin delegates until the root goes).
- [x] `Config/InstallPaths.cs` → Kernel (prompts, presets and run log locations; used by the CLI only today).
- [ ] `Config/Preset.cs` (preset/v1 loader) → Research, beside intake, which is its main reader. Proposal. Waits for the
  worker runtime port (section 3): `NodeKind.Rules` builds `PermissionRule` of `src/Chargehand/Runtime`; and the loader
  (YamlDotNet into reflection JSON) needs a source-generated path before a host publishes it.

## 2. Layers 2 to 8: leftovers of modules already ported

- [ ] Memory (2): `Chargehand.Mcp/McpMemoryProvider.cs`, `MemoryStacks.cs` (done: `McpMemoryException.cs`, `RecallResults.cs`, now in Memory `Api`).
  Left in place: `McpMemoryProvider` takes the `McpConnectionPool` and `MemoryStacks` reads the old `Profile`, both still in `Chargehand.Mcp`/Core; they follow the pool.
  The MCP client they sit on (`McpConnectionPool.cs`, `SecretTemplate.cs`, `ToolArguments.cs`,
  `McpUnavailableException.cs`) has no chargehand types beyond settings: ADR 0060 sends such code to the separate MIT
  library; until then, Memory's `Adapters/`. Proposal.
- [x] Ledger (2): `src/Chargehand/RunLog/ExtensionsCollector.cs` (what memory and services did, for the run record).
- [x] Accounts (3): `Chargehand.Server/AccountsCli.cs`, `AccountsHttp.cs`, `AccountStanding.cs`, `AccessSignIn.cs`
  (`ProxySignIn` and `CloudflareAccess` are there already); `RunToken.cs` (a driven session's capped token);
  `CredentialProbe.cs`, `CredentialAlert.cs` (beside `CredentialHealth`); `src/Chargehand/PublicFreeGate.cs` and
  `Chargehand.Server/RunService.PublicFree.cs` (beside `PublicFreePool`); `RunApprovalGate.cs` and
  `RunService.Approval.cs` (the owner's gate on priced spend, ADR 0092).
  Moved: `AccountsCli`, `AccountStanding`, `RunToken` (git half stays as `Server/RunScopeGit`), `RunApprovalGate`. Left behind (they need the
  server, ASP.NET, `Profile` or a layer above): `AccountsHttp`, `AccessSignIn`, `CredentialProbe`, `CredentialAlert`, `PublicFreeGate`,
  `RunService.PublicFree.cs`, `RunService.Approval.cs`.
- [x] Oversight (3): `Chargehand.Server/PrOutcomeRefresher.cs` (the timer around `PrOutcomeTracker`).
- [ ] Gateway (4): `Chargehand.Driven/Driven/DrivenModelRoute.cs`, `BatchFallback.cs` (the model route and the API-key
  fallback inside the egress gateway, ADR 0088).
- [ ] Runners (5): `Chargehand.Driven/Driven/RunnerSite.cs`; the runner's own server `Chargehand.Server/Runner/RunnerServer.cs`
  (its process entry `RunnerCli.cs` goes to Hosts.Runner, section 5).
  Left in place. `RunnerSite` holds an `ISessionVolumes` (Sessions, a layer above Runners), so it moves with or after Sessions
  (or the volumes port moves down). `RunnerServer` needs `Microsoft.AspNetCore.App`, which no module references, and serialises
  anonymous replies (`new { error }`, `new { count }`) through the reflection-based `RunnerJson.Options`, which Native AOT forbids;
  it goes to Hosts.Runner with `RunnerCli.cs`, after typed replies join `RunnerJsonContext`.
- [x] Handover (7): `Chargehand.Server/HeldPrReconciler.cs` (updates a pull request once its base turns green).

## 3. Layer 9: Research (the one-shot run) and Batches

Research is the module the answer pipeline lands in; ADR 0060 names it, and it already holds `IEvidenceResolver` and
`TaskResultBuilder`. Its parts depend on each other in this order; each box needs the ones above it.

- [ ] Sandbox: `src/Chargehand/Sandbox/*` (`ISandbox`, `BubblewrapSandbox`, `SeatbeltSandbox`, `NoSandbox`,
  `SandboxSelector`, `ProcessRunner`, `CredentialPaths`). Needed by verification and by `Chargehand.Acp`.
- [ ] Worker runtime port: `src/Chargehand/Runtime/*` (`IWorkerRuntime`, `RuntimeSelector`, `UnavailableRuntime`,
  `WorkerKill`, `IRunCleanup`, `IServiceHealth`, `IServiceResolver`). Layer 9 keeps the runtimes (layer 10) pointing
  down. ADR 0086's amendment makes this the step that lets `src/Chargehand.Acp` fold into `Runtimes.Acp`.
- [ ] Prompts: `src/Chargehand/Prompts/*`, `LangfusePrompts.cs`.
- [ ] Prompt enhancer: `src/Chargehand/Enhancement/*`, `Chargehand.Mcp/McpPromptEnhancer.cs`, `PromptEnhancers.cs`.
- [ ] Services per preset: `Chargehand.Mcp/ServiceResolver.cs` (needs the MCP client, section 2).
- [ ] Intake (request to Task Spec): `src/Chargehand/Intake/*` (`IIntake`, `GenerateIntake`, `StructuredSpec`).
- [ ] Workspace: `src/Chargehand/Workspace/*` (`DrivenCheckout`, `RepositoryNames`, `ReviewedChange`, `RunWorkspace`).
- [ ] Verification and support check: `src/Chargehand/Verification/*` (`Verifier`, `CitedText`, `GitEvidenceResolver`,
  `SupportCheck`, `SupportJudge`).
- [ ] Signing: `src/Chargehand/Signing/ResultSigner.cs` (P4 signs a task's result in Research). `VerifyCli.cs` goes to
  Hosts.Cli (section 5).
- [ ] Worker node, results, plans: `src/Chargehand/Nodes/WorkerNode.cs`, `Results/*` (`ResultAssembler`, `ResultMerger`,
  `ReviewReuse`), `Plans/*` (`GraphRunner`, `SplitPlan`), `ChangeRun.cs`.
- [ ] Orchestrator: `src/Chargehand/Orchestrator.cs` (844 lines; splits under the 400-line and CCN 15 budgets as it moves).
- [ ] `Chargehand.Server/RunService.cs` (the run admission and lifecycle the server and MCP tools share). Proposal: the
  service to Research, its routes to Hosts.Server.

Batches, same layer, independent of the Research boxes:

- [ ] `Chargehand.Driven/Driven/DrivenRun.cs`, `SiteBatches.cs`: the profile-dependent batch composition the CA1502
  amendment leaves in Driven until it is deleted. Needs sections 1 and 2 (Gateway, Runners) first.
- [ ] `Chargehand.Server/BatchResumer.cs`, `BatchEventStream.cs` (the journal's events as SSE).

## 4. Layer 10: runtimes and edge modules

Each needs the Research worker runtime port (section 3) unless it says otherwise.

- [ ] Runtimes.OpenCode: `Chargehand.OpenCode/*` (`IOpenCodeClient`, `OpenCodeClient`, `OpenCodeServerProcess`,
  `OpenCodeServices`, `OpenCodeWorkerRuntime`).
- [ ] Runtimes.ClaudeCode: `Chargehand.ClaudeCode/ClaudeCodeWorkerRuntime.cs` (895 lines; splits as it moves),
  `AccountRuntimes.cs`.
- [ ] Runtimes.Acp: `Chargehand.Acp/*` (`AcpWorkerRuntime`, `AcpConnection`, `AcpNode`, `AcpTranscript`, `AcpGit`,
  `AcpSandboxLauncher`), folded in as ADR 0086's amendment says; needs the sandbox too.
- [ ] Intake: GitHub intake `Chargehand.Automation/GitHubIntake.cs` (904 lines; splits), `GitHubIntakeHost.cs`,
  `IntakeHost.cs`, `IntakeGoal.cs`, `IntakeState.cs`, `IntakeSources.cs`, `IntakeBreakers.cs`, `ReviewBacklog.cs`,
  `ScopeOverlap.cs`, `IssueClaim.cs`, `TriggeredStart.cs`, `PullReviews.cs`, `CheckoutSources.cs`, `ManagedClones.cs`,
  `GitRepositoryHeads.cs`; schedules `Automation/Schedules/*`; phone answers `Automation/Notify/*`; the tracker source
  `src/Chargehand/ITaskSource.cs`, `Chargehand.Mcp/McpTaskSource.cs`; the hosted services
  `Chargehand.Server/IntakeService.cs`, `ScheduleService.cs`, `AnswerService.cs`. Needs Batches (section 3).
- [x] Intake, alerts: `Intake/Api/IncidentCli.cs` (an alert into a `request/v1`). Proposal.
- [ ] Console: `Chargehand.Server/Console/*` (29 files: inbox, fleet, batches, sources, routes, system and the rest),
  `WebConsole.cs`, `DependencyHealth.cs`. Needs Intake for its intake and sources screens, so it moves after it.
- [ ] Evaluation: `src/Chargehand/Evals/*` (`EvalRunner`, `EvalItem`, `Gate`, `Scoring`, `ClaimScorer`, `FactJudge`,
  `SupportEval`, `LangfuseEvals`); the benchmark `Chargehand.Automation/Bench/*` (15 files, `bench` and `bench replay`;
  needs Batches); `src/Chargehand/ReleaseNotes/ReleaseNotesCli.cs` (proposal: it reads verification types, as evals do).
- [ ] Onboarding: `src/Chargehand/Onboarding/*` (`AdoptCli`, `AdoptProbes`, `Doctor`, `DoctorCli`, `InitCli`,
  `StarterProfile`, `SystemDoctorEnvironment`); `Chargehand.Mcp/ExtensionsCheck.cs` (`extensions check`).
  Partly done: `StarterProfile` is in `Modules.Onboarding.Api`; the rest waits on `RepositoryDetector` and the doctor probes.
- [x] Watch: `src/Chargehand/Watch/WatchCli.cs` (`watch` and `cancel`; needs only Contracts, so it can move any time).

## 5. Layer 11: hosts

- [ ] Hosts.Server: `Chargehand.Server/ChargehandServer.cs`, the MCP tools `OrchestrateTool.cs`, `ResultTool.cs`,
  `RepositoriesTool.cs`, the conversation edge `Conversations/ConversationRoutes.cs` and `ConversationHost.cs`.
- [ ] Hosts.Runner: `Chargehand.Server/Runner/RunnerCli.cs`.
- [ ] Hosts.Session: `Chargehand.Driven/Driven/VerifyBranchCli.cs` (what a verification container runs).
- [ ] Hosts.Cli: `Chargehand.Cli/Program.cs` (980 lines of verb dispatch; each verb's code moves with its module above),
  `src/Chargehand/Signing/VerifyCli.cs`.
- [ ] Every host publishes with Native AOT (`scripts/aot-smoke.sh`), which needs a source-generated JSON context for
  every type above and the MCP tools registered explicitly (ADR 0060, consequences).

## 6. Delete

- [ ] Delete `src/Chargehand`, `Chargehand.Driven`, `Chargehand.Automation`, `Chargehand.Server`, `Chargehand.Cli`,
  `Chargehand.Mcp`, `Chargehand.OpenCode`, `Chargehand.ClaudeCode`, `Chargehand.Acp`, and their tests' references; the
  `Chargehand` dnx tool packs from Hosts.Cli.
- [ ] `tests/Chargehand.Tests/ArchitectureTests.cs` (ADR 0047: Core never references Driven) goes with the projects it checks.
- [ ] .NET 11 (ADR 0060, port order).

## Open questions for the owner

1. Research at layer 9 as the home of the sandbox and `IWorkerRuntime`: both are used by the runtimes (layer 10) only,
   so it fits the layers, but the sandbox is also generic enough for Kernel. Which one?
2. The MCP client (`McpConnectionPool` and its helpers): the separate MIT library now, or Memory's adapters first?
3. `IncidentCli` and `ReleaseNotesCli` have no module in ADR 0060; the targets above are proposals.
