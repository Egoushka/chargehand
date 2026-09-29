using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Budget;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Intake;
using Chargehand.Memory;
using Chargehand.Nodes;
using Chargehand.Plans;
using Chargehand.Prompts;
using Chargehand.Results;
using Chargehand.RunLog;
using Chargehand.Runtime;
using Chargehand.Sandbox;
using Chargehand.Verification;
using Chargehand.Workspace;

namespace Chargehand;

/// <summary>Request → intake → action: stop (ask, improve, deny, approval) or run one node (answer) or a task graph (split) → result/v1.</summary>
/// <param name="promptVersions">Langfuse prompt versions by block sha256, for linking spans; may be empty.</param>
/// <param name="memory">Long-term memory from the profile, one source or several, or null (ADR 0008, ADR 0026).</param>
/// <param name="services">Resolves a preset's <c>services</c> into the grants workers get; null: a preset's services are ignored (ADR 0034).</param>
/// <param name="sandboxFor">Picks the sandbox a writing preset's tests run in (ADR 0035); the platform's, per the profile, if null.</param>
public sealed class Orchestrator(
    Profile profile,
    IWorkerRuntime runtime,
    string opencodeVersion,
    string rootDirectory,
    IRunLog runLog,
    IReadOnlyDictionary<string, int> promptVersions,
    MemoryStack? memory = null,
    IServiceResolver? services = null,
    Func<SandboxSettings?, ISandbox>? sandboxFor = null)
{
    public const string NodeKindName = "worker";

    public static string NewRunId() => $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

    /// <param name="runId">The id to run under (the HTTP interface hands it out before the run starts); a new one if null.</param>
    /// <param name="progress">Receives run-status/v1 events: started, intake, node started and finished, run finished.</param>
    /// <param name="parentRunId">The run this one resends with answers, if any; recorded in the start record.</param>
    public async Task<ResultContract> RunAsync(RunRequest request, CancellationToken ct, string? runId = null, Action<RunStatus>? progress = null,
        string? parentRunId = null)
    {
        var started = DateTimeOffset.UtcNow;
        runId ??= NewRunId();
        using var run = Telemetry.Source.StartActivity("chargehand.run");
        var traceId = run?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
        run?.SetTag("langfuse.trace.name", "chargehand.run");
        run?.SetTag("chargehand.run_id", runId);
        var extensions = new ExtensionsCollector();
        await runLog.AppendAsync(new StartRecord(runId, started, traceId, request, Environment.ProcessId, parentRunId), ct);
        progress?.Invoke(RunStatus.Of(runId, RunState.Running, RunEventKind.Started));

        var registry = new PromptRegistry(Path.Combine(rootDirectory, "prompts"));
        var presetName = request.Context.Preset;
        var date = started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var intakeBlock = registry.Get("intake/task-spec");
        // Unset (ADR 0026): the runtime picks its own default; "auto" stands in for the chain metadata and run log,
        // which need a concrete label even though no specific model was pinned.
        var intakeModel = profile.IntakeModel ?? "auto";
        var intakeChain = PromptChains.Build([(intakeBlock, BlockSource.Registry)], new AsSent(opencodeVersion, "generate", intakeModel, date));

        ResultContract Stop(ResultStatus status, string summary, IReadOnlyList<string> questions, IReadOnlyList<Artifact>? artifacts = null) =>
            new("result/v1", runId, "intake", traceId, intakeChain, status, summary, [], [], artifacts ?? [], questions, 0, new Usage(0, 0, 0, 0, 0));

        ResultContract result;
        IntakeOutcome? intake = null;
        TaskAction? executed = null;
        try
        {
            var preset = Preset.Load(Path.Combine(rootDirectory, "presets"), presetName);
            var (kindName, kind) = AnswerKind(preset);
            // A writing preset without a sandbox is refused before anything is spent (ADR 0035).
            var sandbox = kind.Writes ? (sandboxFor ?? SandboxSelector.Select)(profile.Sandbox) : null;
            // Unset when the profile maps no real model to the preset's placeholder: the runtime uses its own default.
            var workerModel = profile.ResolveModel(kind.Model);
            var callerBlocks = (request.CallerBlocks ?? []).Select(PromptChains.VerifyCallerBlock).ToList();

            // Intake.
            using (var span = Telemetry.Source.StartActivity("chargehand.intake"))
            {
                TagChain(span, intakeChain);
                span?.SetTag("gen_ai.request.model", intakeModel);
                intake = await new GenerateIntake(runtime, ParseModel(profile.IntakeModel), intakeBlock, runId, kind.Checkout).RunAsync(request, ct);
                span?.SetTag("chargehand.intake.action", intake.Spec?.Action.ToString().ToLowerInvariant() ?? "needs_input");
                foreach (var c in intake.Calls)
                    await runLog.AppendAsync(new CallRecord(runId, "intake", "intake", null, null, intakeModel, c.Started, c.LatencyMs, null, null, intakeChain), ct);
            }

            var blocks = new List<(PromptBlock, BlockSource)> { (registry.Get($"core/{kindName}"), BlockSource.Registry), (registry.Get($"preset/{presetName}"), BlockSource.Registry) };
            blocks.AddRange(callerBlocks.Select(b => (b, BlockSource.Caller)));
            var instructions = new List<(string Key, string Value)> { ("chargehand-core", blocks[0].Item1.Text), ("chargehand-preset", blocks[1].Item1.Text) };
            var chain = PromptChains.Build(blocks, new AsSent(opencodeVersion, kind.OpencodeAgent, workerModel ?? "auto", date,
                BasePromptVariant: kind.OpencodeAgent,
                InstructionFilesSha256: PromptChains.InstructionsSha256(instructions),
                ToolsSha256: PromptChains.ToolsSha256(opencodeVersion, kind.OpencodeAgent, kind.Rules)));

            if (intake.Spec is not { } spec)
            {
                progress?.Invoke(RunStatus.Of(runId, RunState.Running, RunEventKind.Intake));
                result = Stop(ResultStatus.NeedsInput, intake.NeedsInput!, [intake.NeedsInput!]);
            }
            else
            {
                // An action the preset does not allow, or a split that fails validation, runs as one answer node.
                executed = preset.AllowedActions.Contains(Name(spec.Action)) ? spec.Action : TaskAction.Answer;
                IReadOnlyList<PlanNode> plan = [new PlanNode(kindName, null, [])];
                if (executed == TaskAction.Split)
                {
                    var (nodes, reason) = SplitPlan.From(spec);
                    plan = nodes ?? plan;
                    executed = nodes is null ? TaskAction.Answer : TaskAction.Split;
                    run?.SetTag("chargehand.split.rejected", reason);
                }
                var stops = executed is TaskAction.Deny or TaskAction.Ask or TaskAction.Improve;
                progress?.Invoke(RunStatus.Of(runId, RunState.Running, RunEventKind.Intake) with
                {
                    Action = Name(spec.Action),
                    ExecutedAction = Name(executed.Value),
                    Nodes = stops ? 0 : plan.Count,
                });
                var detail = spec.ActionDetail ?? default;
                result = executed switch
                {
                    TaskAction.Deny => Stop(ResultStatus.Denied, detail.GetProperty("reason").GetString()!, [$"Unblock: {detail.GetProperty("unblock_condition").GetString()}"]),
                    TaskAction.Ask => Stop(ResultStatus.NeedsInput, "Answer these questions and resend the request.", [.. detail.GetProperty("questions").EnumerateArray().Select(q => q.GetString()!)]),
                    TaskAction.Improve => Stop(ResultStatus.NeedsInput, detail.GetProperty("improved_request").GetString()!,
                        ["Intake proposes the improved request in summary (diff in artifacts). Resend it, or resend the original."],
                        [Inline("improved_request", "text/x-diff", detail.GetProperty("diff").GetString()!)]),
                    _ when preset.Approval?.Requires(spec) == true && request.Context.Approved != true => Stop(ResultStatus.NeedsInput,
                        $"Preset {presetName} asks for approval: risk {Name(spec.Risk)}, estimate up to ${spec.Estimate.UsdHigh}.", ["Resend with context.approved = true to run it."]),
                    _ => await Execute(request, spec, plan, kindName, kind, workerModel, instructions, chain, callerBlocks, runId, traceId, run, extensions, progress, sandbox, ct),
                };
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A run that throws (a bad checkout, an unknown preset, no valid Task Spec) still ends with a result and a run record.
            var error = ChargehandException.ErrorOf(e);
            result = new ResultContract("result/v1", runId, "run", traceId, intakeChain, ResultStatus.Failed, error.Message, [], [], [], [error.Message], 0, new Usage(0, 0, 0, 0, 0),
                error);
        }

        run?.SetTag("chargehand.contract.status", result.Status.ToString().ToLowerInvariant());
        run?.SetTag("chargehand.intake.action", intake?.Spec is null ? null : Name(intake.Spec.Action));
        run?.SetTag("chargehand.action", executed is null ? null : Name(executed.Value));
        await runLog.AppendAsync(new RunRecord(runId, started, DateTimeOffset.UtcNow, presetName, intake?.Spec is null ? null : Name(intake.Spec.Action), intake?.Spec, result,
            executed is null ? null : Name(executed.Value), extensions.ToReport()), ct);
        progress?.Invoke(RunStatus.Of(runId, RunStatus.StateOf(result.Status), RunEventKind.RunFinished) with { Result = result });
        return result;
    }

    /// <summary>The node kind that answers: "worker", or a preset's only kind (draft).</summary>
    internal static (string Name, NodeKind Kind) AnswerKind(Preset preset) =>
        preset.NodeKinds.TryGetValue(NodeKindName, out var worker) ? (NodeKindName, worker) : (preset.NodeKinds.Keys.Single(), preset.NodeKinds.Values.Single());

    /// <summary>Runs the plan's nodes (one for answer) and returns the node's contract, or the merged one for a split.</summary>
    private async Task<ResultContract> Execute(RunRequest request, TaskSpec spec, IReadOnlyList<PlanNode> plan, string kindName, NodeKind kind, string? workerModel,
        IReadOnlyList<(string Key, string Value)> instructions, PromptChain chain, IReadOnlyList<PromptBlock> callerBlocks, string runId, string traceId,
        Activity? run, ExtensionsCollector extensions, Action<RunStatus>? progress, ISandbox? sandbox, CancellationToken ct)
    {
        var (directory, commit, repository) = kind.Checkout
            ? await Checkout(request.Context.Repository!, kind, ct)
            : (EmptyDirectory(), "", "");
        // A writing node works in its own clone on its own branch; the shared checkout stays clean for other runs (ADR 0035).
        var workspace = kind.Writes ? await RunWorkspace.CreateAsync(directory, commit, profile.WorkerRoot, runId, plan[0].Id, ct) : null;
        if (workspace is not null)
            directory = workspace.Directory;
        var verifier = sandbox is null ? null : new Verifier(sandbox, profile.Sandbox?.Network ?? false, profile.Sandbox?.Env ?? []);
        var verify = kind.Verify ?? new VerifySettings();
        VerifyOutcome? lastCheck = null;
        VerifyPlan? verifyPlan = null;
        var attempts = 0;
        // ponytail: the run cap is split evenly across nodes up front; share the remainder dynamically if nodes vary a lot.
        var cap = new[] { profile.RunCapUsd / plan.Count, kind.Budget.MaxUsd, (request.Context.BudgetUsd ?? decimal.MaxValue) / plan.Count }.Min();
        // The id is quoted: rendered as "[id]", a model cited "[id]" as the locator, which no input id matches.
        var inputText = string.Join("\n", (request.Inputs ?? []).Select(i => $"- id \"{i.Id}\" ({i.Kind}): {i.Text}{(i.SourceUrl is null ? "" : $" <{i.SourceUrl}>")}"));
        var prices = new PriceTable(profile.Prices ?? new Dictionary<string, ModelPrice>());
        // Unset (ADR 0026): labelled "auto" in the run log, as for intake, when the runtime reports no model for a call.
        var modelLabel = workerModel ?? "auto";
        // Recalled facts go in the prompt text, after the task (ADR 0007 order; ADR 0010 keeps instructions fixed, so
        // siblings still fork). The chain records them as one runtime block per source that contributed.
        var recall = await Recall(request.Text, run, extensions, ct);
        var facts = recall.Prompt;
        chain = chain with { Blocks = [.. chain.Blocks, .. recall.Blocks] };
        // Granted once per run and shared by every node; the tools hash covers the grant, and is today's when there is none.
        var granted = await Grant(kind.Services, run, extensions, ct);
        if (granted.Count > 0)
            chain = chain with { AsSent = chain.AsSent with { ToolsSha256 = PromptChains.ToolsSha256(opencodeVersion, kind.OpencodeAgent, kind.Rules, granted) } };

        async Task<NodeResult> RunNode(PlanNode node, IReadOnlyList<ResultContract> upstream, ForkPoint? fork, TaskCompletionSource<ForkPoint?>? primed, CancellationToken token)
        {
            var nodeRequest = new NodeRequest(runId, node.Id, traceId,
                new NodeSpec(directory, kind.OpencodeAgent, ParseModel(workerModel), kind.Rules, new Dictionary<string, string> { [IRunCleanup.RunMetadataKey] = runId, ["chargehand.node"] = node.Id }, granted),
                instructions, TaskText(request, spec, commit, inputText, callerBlocks, node, plan.Count, upstream) + facts + ServiceCitationNote(granted), chain, directory, commit,
                (request.Inputs ?? []).Select(i => i.Id).ToHashSet(), inputText, cap, TimeSpan.FromMinutes(15),
                kind.Budget.MaxInputTokens, kind.Compaction?.TriggerTokens, fork,
                workspace is null ? null : async (_, token) =>
                {
                    // Run on a copy of what would be committed: what the tests write stays out of the branch. Resolved on each
                    // check, since the worker may have added the build file it needs.
                    using var tree = await workspace.ExportAsync(token);
                    verifyPlan = VerifyCommand.Resolve(request.Context.Verify, tree.Directory);
                    if (verifyPlan is null)
                        return null;
                    lastCheck = await verifier!.RunAsync(verifyPlan, tree.Directory, TimeSpan.FromSeconds(verify.TimeoutSeconds), token);
                    attempts++;
                    return lastCheck.Passed ? null : ChangeRun.Feedback(lastCheck, attempts, verifier.Network);
                },
                verify.MaxFixRounds);

            var instructionRefs = nodeRequest.Instructions.Select(i => new InstructionRef(i.Key, PromptBlock.Hash(i.Value))).ToList();
            using var span = Telemetry.Source.StartActivity("chargehand.node");
            TagChain(span, chain);
            span?.SetTag("chargehand.node", node.Id);
            progress?.Invoke(RunStatus.Of(runId, RunState.Running, RunEventKind.NodeStarted) with { NodeId = node.Id });
            var nodeResult = await new WorkerNode(runtime, prices, new GitEvidenceResolver()).RunAsync(nodeRequest, token, primed);
            if (workspace is not null && nodeResult.Contract.Status == ResultStatus.Completed)
            {
                var branch = await workspace.CommitAsync(ChangeRun.CommitMessage(nodeResult.Contract.Summary), token);
                var changed = branch is null ? [] : await workspace.ChangedPathsAsync(token);
                var diff = branch is null ? "" : await workspace.DiffAsync(token);
                nodeResult = nodeResult with
                {
                    Contract = ChangeRun.Finish(nodeResult.Contract, branch, diff, lastCheck, attempts, verifier!.SandboxKind, verifier.Network, changed, verifyPlan),
                };
            }
            span?.SetTag("langfuse.session.id", nodeResult.SessionId);
            run?.SetTag("langfuse.session.id", nodeResult.SessionId);
            span?.SetTag("chargehand.contract.status", nodeResult.Contract.Status.ToString().ToLowerInvariant());
            span?.SetTag("chargehand.forked_from", nodeResult.ForkedFrom);
            if (runtime is IServiceHealth health)
                foreach (var (server, status) in health.UnavailableServices(nodeResult.SessionId))
                {
                    extensions.Unavailable(server, $"not_connected: {status}");
                    span?.SetTag($"chargehand.service.{server}.not_connected", status);
                    run?.SetTag($"chargehand.service.{server}.not_connected", status);
                }

            foreach (var m in nodeResult.Calls)
            {
                var usd = prices.PriceUsd(m.Model ?? modelLabel, m.Tokens!);
                var latency = ((m.Completed ?? m.Created) - m.Created).TotalMilliseconds;
                await runLog.AppendAsync(new CallRecord(runId, node.Id, kindName, nodeResult.SessionId, m.Id, m.Model ?? modelLabel, m.Created, latency, m.Tokens, usd, chain,
                    nodeResult.ForkedFrom, instructionRefs), token);
                using var call = Telemetry.Source.StartActivity("chargehand.call", ActivityKind.Client, span?.Context ?? default, startTime: m.Created);
                TagChain(call, chain);
                call?.SetTag("langfuse.session.id", nodeResult.SessionId);
                call?.SetTag("chargehand.message_id", m.Id);
                call?.SetTag("gen_ai.request.model", m.Model ?? modelLabel);
                if (profile.Telemetry?.UsageOnSpans == true)
                {
                    // ADR 0021: no gateway records these calls, so the span is the generation's only usage and cost.
                    call?.SetTag("langfuse.observation.usage_details", JsonSerializer.Serialize(new Dictionary<string, long>
                    {
                        ["input"] = m.Tokens!.Input,
                        ["output"] = m.Tokens.Output + m.Tokens.Reasoning,
                        ["cache_read_input_tokens"] = m.Tokens.CacheRead,
                        ["cache_creation_input_tokens"] = m.Tokens.CacheWrite,
                    }));
                    // Unknown cost (ADR 0026) reports no cost_details rather than a $0 that would read as measured.
                    if (usd is { } known)
                        call?.SetTag("langfuse.observation.cost_details", JsonSerializer.Serialize(new Dictionary<string, decimal> { ["total"] = known }));
                }
                call?.SetEndTime((m.Completed ?? m.Created).UtcDateTime);
            }
            progress?.Invoke(RunStatus.Of(runId, RunState.Running, RunEventKind.NodeFinished) with
            {
                NodeId = node.Id,
                NodeStatus = nodeResult.Contract.Status,
                Usd = nodeResult.Contract.Usage.Usd,
            });
            return nodeResult;
        }

        ResultContract Failed(PlanNode node, string reason) =>
            new("result/v1", runId, node.Id, traceId, chain, ResultStatus.Failed, reason, [], [], [], [reason], 0, new Usage(0, 0, 0, 0, 0));

        IReadOnlyList<NodeOutcome> outcomes;
        try
        {
            outcomes = await new GraphRunner().RunAsync(plan, RunNode, Failed, ct);
        }
        finally
        {
            // Whatever became of the nodes, and even when the caller cancelled: a server registered for the run must not outlive it.
            await EndRun(runId, granted.Count > 0, run);
        }
        var result = plan.Count == 1 ? outcomes[0].Contract : ResultMerger.Merge(runId, traceId, chain, outcomes);
        await Retain(result, repository, commit, runId, run, extensions, ct);
        return result;
    }

    /// <summary>
    /// Keeps the claims whose citations resolved, with where they were checked (ADR 0034). A run with no commit has nothing
    /// checked, and a claim resting on the request or on the session anchors nothing in the repository; neither is retained.
    /// Memory fails open (ADR 0008): the stack reports a failed retain instead of throwing it, so a completed run stays completed.
    /// </summary>
    private async Task Retain(ResultContract result, string repository, string commit, string runId, Activity? run, ExtensionsCollector extensions, CancellationToken ct)
    {
        if (memory is null || result.Status != ResultStatus.Completed)
            return;
        var selection = commit.Length == 0 ? new RetainSelection([], []) : RetainableClaims.From(result);
        if (selection.Skipped.Count > 0)
            run?.SetTag("chargehand.retain.claims_left_out", selection.Skipped.Count);
        if (selection.Claims.Count == 0)
        {
            var why = commit.Length == 0 ? "no commit" : "no claim qualified";
            extensions.Retained(memory.Sources.Where(s => s.Retain).Select(s => new RetainReport(s.Name, 0, why)));
            return;
        }
        var reports = await memory.RetainAsync(RetainItems.Build(selection, repository, commit, runId, DateTimeOffset.UtcNow), ct);
        extensions.Retained(reports);
        foreach (var r in reports.Where(r => r.SkippedReason is not null))
            run?.SetTag($"chargehand.memory.{r.Source}.error", r.SkippedReason);
    }

    /// <summary>Memory is optional context: a source that fails leaves the run without its facts rather than failing it.</summary>
    private async Task<RecallOutcome> Recall(string query, Activity? run, ExtensionsCollector extensions, CancellationToken ct)
    {
        if (memory is null)
            return new RecallOutcome([], "", []);
        var outcome = await memory.RecallAsync(query, ct);
        extensions.Recalled(outcome.Sources);
        foreach (var s in outcome.Sources)
            if (s.SkippedReason is { } reason)
                run?.SetTag($"chargehand.memory.{s.Source}.error", reason);
            else
                run?.SetTag($"chargehand.memory.{s.Source}.recalled", s.Items.Count);
        return outcome;
    }

    /// <summary>A runtime that keeps a run's services with a server other runs share (OpenCode) lets them go when the run's nodes are done.
    /// Best effort: a failure here is a tag on the run, never a failed run, and the caller's cancellation does not cut it short.</summary>
    private async Task EndRun(string runId, bool hasServices, Activity? run)
    {
        if (!hasServices || runtime is not IRunCleanup cleanup)
            return;
        try
        {
            await cleanup.EndRunAsync(runId, CancellationToken.None);
        }
        catch (Exception e)
        {
            run?.SetTag("chargehand.service.end_run_error", ChargehandException.Scrub(string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message));
        }
    }

    /// <summary>Services are optional tools: one that does not resolve leaves the workers without it, reported in the run log, and never fails the run.</summary>
    private async Task<IReadOnlyList<ServiceGrant>> Grant(IReadOnlyList<ServiceUse>? uses, Activity? run, ExtensionsCollector extensions, CancellationToken ct)
    {
        if (uses is not { Count: > 0 } || services is null)
            return [];
        ResolvedServices resolved;
        try
        {
            resolved = await services.ResolveAsync(uses, ct);
        }
        catch (Exception e)
        {
            // Whatever the resolver threw, it is a service failure; only the caller's own cancellation is not.
            ct.ThrowIfCancellationRequested();
            var reason = string.Join(' ', ChargehandException.Scrub(string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            resolved = new ResolvedServices([], [.. uses.Select(u => u.Server).Distinct().Select(server => new ServiceReport(server, [], [$"unreachable: {reason}"]))]);
        }
        extensions.Serviced(resolved.Report);
        foreach (var r in resolved.Report)
        {
            run?.SetTag($"chargehand.service.{r.Server}.granted", r.Tools.Count);
            if (r.Issues.Count > 0)
                run?.SetTag($"chargehand.service.{r.Server}.issues", string.Join("; ", r.Issues));
        }
        return resolved.Grants;
    }

    private static string Name<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static Artifact Inline(string kind, string mediaType, string content) =>
        new(kind, mediaType, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), Content: content);

    /// <summary>
    /// The worker reads a clone of the caller's repository at the requested commit, made under worker_root (ADR 0023):
    /// the source may sit anywhere under the profile's repository roots, the worker's directory stays outside the
    /// OpenCode user's home (ADR 0003), and the source's uncommitted and ignored files never reach the worker. A clone
    /// per source and commit is reused and never deleted (ADR 0015). The clone may hold no file the node kind may not
    /// read (ADR 0006): OpenCode's grep searches any path or include glob it is given, and its permission resource is
    /// the search pattern, so a read deny cannot keep it out of a file.
    /// </summary>
    private async Task<(string Directory, string Commit, string Repository)> Checkout(RepositoryRef repo, NodeKind kind, CancellationToken ct)
    {
        // git prints the top level with symbolic links resolved, so a link under a root cannot point the worker elsewhere.
        var source = await Git(Path.GetFullPath(repo.Path), ct, "rev-parse", "--show-toplevel")
            ?? throw new ChargehandException(ErrorCode.CheckoutInvalid, $"{repo.Path} is not a git checkout",
                "Point context.repository.path at a git working tree.");
        if (!profile.Roots.Any(root => Under(source, RealPath(root))))
            throw new ChargehandException(ErrorCode.RepositoryNotAllowed,
                $"repository {source} is outside the profile's repository_roots ({string.Join(", ", profile.Roots)}); workers read only repositories under them",
                $"Clone the repository under one of those roots, or add a directory that contains {source} to repository_roots (\"/\" allows any).");
        var commit = await Git(source, ct, "rev-parse", "--verify", "--end-of-options", repo.Commit + "^{commit}")
            ?? throw new ChargehandException(ErrorCode.CheckoutInvalid, $"commit {repo.Commit} is not in {source}",
                "Fetch the commit into that checkout, or pin one it has.");
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..8];
        var dir = Path.Combine(Path.GetFullPath(profile.WorkerRoot), ".checkouts", $"{Path.GetFileName(source)}-{id}", commit[..12]);
        OutsideHome(dir + Path.DirectorySeparatorChar);
        if (!Directory.Exists(Path.Combine(dir, ".git")) || await Git(dir, ct, "rev-parse", "HEAD") != commit)
        {
            // Built beside the final path and moved into it, so an interrupted clone is never reused.
            // ponytail: a failed clone leaves its temporary directory behind; sweep .checkouts by hand if that piles up.
            var temp = $"{dir}.tmp-{Guid.NewGuid():N}";
            var parent = Path.GetDirectoryName(dir)!;
            Directory.CreateDirectory(parent);
            // --local hard-links the objects, which fails across mount points (a container's repository mount and its
            // work volume); git removes the failed clone, and the second attempt copies instead.
            if ((await Git(parent, ct, "clone", "-q", "--local", "--no-checkout", "--", source, temp) is null
                 && await Git(parent, ct, "clone", "-q", "--no-hardlinks", "--no-checkout", "--", source, temp) is null)
                || await Git(temp, ct, "checkout", "-q", "--detach", commit) is null)
                throw new ChargehandException(ErrorCode.CheckoutInvalid, $"could not clone {source} at {commit[..12]} into {Path.GetDirectoryName(dir)}",
                    "Check that worker_root is writable and has free space.");
            try
            {
                Directory.Move(temp, dir);
            }
            catch (IOException)
            {
                // Another run made the same clone first; anything else at that path is not ours to replace.
                if (await Git(dir, ct, "rev-parse", "HEAD") != commit)
                    throw;
            }
        }
        var denied = await DeniedFiles(dir, kind.Permissions, ct);
        return denied.Count == 0
            ? (dir, commit, RepositoryLabel.From(await Git(source, ct, "config", "--get", "remote.origin.url"), Path.GetFileName(source)))
            : throw new ChargehandException(ErrorCode.CheckoutHasSecrets, $"the repository tracks files the preset denies reading, which grep would still reach: " +
                $"{string.Join(", ", denied.Take(5))}{(denied.Count > 5 ? ", ..." : "")}",
                "Pin a commit that does not track them, or remove them from the repository.");
    }

    private static bool Under(string path, string root) =>
        path == root || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>The path with every symbolic link in it resolved, as git prints a top level (macOS: /var is /private/var).</summary>
    private static string RealPath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetDirectoryName(full) is not { } parent)
            return full;
        var here = Path.Combine(RealPath(parent), Path.GetFileName(full));
        return Path.Exists(here) && File.ResolveLinkTarget(here, returnFinalTarget: true) is { } target ? RealPath(target.FullName) : here;
    }

    /// <summary>git's trimmed output, or null when it fails.</summary>
    private static async Task<string?> Git(string directory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git", ["-C", directory, .. args])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 ? output.Trim() : null;
    }

    /// <summary>Tracked and untracked (ignored included) files whose last matching read rule is a deny. In a fresh clone only
    /// tracked files exist.</summary>
    private static async Task<IReadOnlyList<string>> DeniedFiles(string repo, IReadOnlyList<RuleEntry> rules, CancellationToken ct)
    {
        var read = rules.Where(r => r.Action is "read" or "*").ToList();
        var patterns = read.Where(r => r.Effect == "deny" && r.Resource != "*").Select(r => r.Resource).ToList();
        if (patterns.Count == 0)
            return [];
        var psi = new ProcessStartInfo("git", ["-C", repo, "ls-files", "-z", "--cached", "--others", "--", .. patterns])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var files = (await p.StandardOutput.ReadToEndAsync(ct)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new ChargehandException(ErrorCode.CheckoutInvalid, $"git ls-files failed in {repo}",
                "Delete the checkout under worker_root/.checkouts and retry.");
        return files.Where(f => read.LastOrDefault(r => Wildcard(f, r.Resource))?.Effect == "deny").ToList();
    }

    /// <summary>OpenCode's rule match: * is any run of characters, ? one character, the rest literal.</summary>
    private static bool Wildcard(string text, string pattern) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.Singleline);

    /// <summary>Where a node without a checkout runs: an empty directory under worker_root, outside the home (ADR 0003).</summary>
    private string EmptyDirectory()
    {
        var dir = Path.Combine(Path.GetFullPath(profile.WorkerRoot), ".chargehand-empty");
        OutsideHome(dir + Path.DirectorySeparatorChar);
        return Directory.CreateDirectory(dir).FullName;
    }

    /// <summary>OpenCode discovers skills by walking up from a session's directory, so none may sit under the home (ADR 0003).</summary>
    private static void OutsideHome(string fullPath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + Path.DirectorySeparatorChar;
        if (fullPath.StartsWith(home, StringComparison.Ordinal))
            throw new ChargehandException(ErrorCode.RepositoryNotAllowed, "worker checkouts must live outside the home directory (ADR 0003)",
                "Set the profile's worker_root to a directory outside your home directory.");
    }

    /// <summary>Volatile content goes in the prompt text, after the fixed instruction entries (ADR 0010).</summary>
    /// <summary>
    /// Workers do not see message ids, so a service tool's reply is cited by quoting it (GitEvidenceResolver). Told in the task
    /// text, only when services are granted: the shipped prompts and the tools hash stay as they are without them.
    /// </summary>
    internal static string ServiceCitationNote(IReadOnlyList<ServiceGrant> granted) => granted.Count == 0
        ? ""
        : $"\n\nTo cite what a service tool returned, add evidence of kind session_message whose locator quotes at least {GitEvidenceResolver.MinQuoteLength} characters of the tool's reply exactly as returned (copy it, do not paraphrase or summarize). Never use the reply text as a file or url locator.";

    private static string TaskText(RunRequest request, TaskSpec spec, string commit, string inputText, IReadOnlyList<PromptBlock> callerBlocks,
        PlanNode node, int nodes, IReadOnlyList<ResultContract> upstream)
    {
        var sb = new StringBuilder();
        sb.AppendLine(commit.Length > 0 ? $"Task (repository commit {commit}):" : "Task:").AppendLine(request.Text).AppendLine();
        if (node.Goal is not null)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Your part ({node.Id}, one of {nodes} parts, each answered by a separate worker): {node.Goal}")
              .AppendLine("Answer only your part; do not research the other parts.").AppendLine();
        if (spec.Constraints.Count > 0)
            sb.AppendLine("Constraints:").AppendLine(string.Join("\n", spec.Constraints.Select(c => $"- {c}"))).AppendLine();
        // Acceptance criteria describe the whole answer, so a part does not get them.
        if (node.Goal is null && spec.AcceptanceCriteria.Count > 0)
            sb.AppendLine("A good answer:").AppendLine(string.Join("\n", spec.AcceptanceCriteria.Select(c => $"- {c}"))).AppendLine();
        foreach (var u in upstream)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Result of part {u.NodeId} (you may rely on it; cite files yourself):")
              .AppendLine(JsonSerializer.Serialize(new { u.Summary, u.Claims, u.Evidence }, ContractJson.Options)).AppendLine();
        if (inputText.Length > 0)
            sb.AppendLine("Inputs (cite each with kind \"input\" and its id, without the quotes, as the locator):").AppendLine(inputText).AppendLine();
        foreach (var b in callerBlocks)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Caller instructions ({b.Name} {b.Version}):").AppendLine(b.Text).AppendLine();
        return sb.ToString();
    }

    private void TagChain(Activity? span, PromptChain chain)
    {
        if (span is null)
            return;
        span.SetTag("chargehand.prompt_chain", JsonSerializer.Serialize(chain, ContractJson.Options));
        var first = chain.Blocks.Count > 0 ? chain.Blocks[0] : null;
        if (first is not null && promptVersions.TryGetValue(first.Sha256, out var version))
        {
            span.SetTag("langfuse.observation.prompt.name", first.Name);
            span.SetTag("langfuse.observation.prompt.version", version);
        }
    }

    /// <summary>Null: unset, the runtime uses its own default model (ADR 0026).</summary>
    public static ModelRef? ParseModel(string? model)
    {
        if (model is null)
            return null;
        var slash = model.IndexOf('/');
        var hash = model.IndexOf('#');
        return new ModelRef(model[..slash], hash < 0 ? model[(slash + 1)..] : model[(slash + 1)..hash], hash < 0 ? null : model[(hash + 1)..]);
    }
}
