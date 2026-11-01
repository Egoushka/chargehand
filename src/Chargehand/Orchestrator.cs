using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Enhancement;
using Chargehand.Intake;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Memory.Api;
using Chargehand.Modules.PeerClones.Api;
using Chargehand.Modules.Research.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Nodes;
using Chargehand.Plans;
using Chargehand.Prompts;
using Chargehand.Results;
using Chargehand.RunLog;
using Chargehand.Runtime;
using Chargehand.Sandbox;
using Chargehand.Signing;
using Chargehand.Verification;
using Chargehand.Workspace;

namespace Chargehand;

/// <summary>Request → intake → action: stop (ask, improve, deny, approval) or run one node (answer) or a task graph (split) → result/v1.</summary>
/// <param name="promptVersions">Langfuse prompt versions by block sha256, for linking spans; may be empty.</param>
/// <param name="memory">Long-term memory from the profile, one source or several, or null (ADR 0008, ADR 0026).</param>
/// <param name="services">Resolves a preset's <c>services</c> into the grants workers get; null: a preset's services are ignored (ADR 0034).</param>
/// <param name="sandboxFor">Picks the sandbox a writing preset's tests run in (ADR 0035); the platform's, per the profile, if null.</param>
/// <param name="enhancer">The profile's prompt enhancer, already behind its guard, or null (ADR 0040, ADR 0041). It sees each request's text and reports the outcome; it never changes what is sent.</param>
/// <param name="runtimeFor">The runtime a run of the given account uses (ADR 0053), which throws for an account that may use no model credential; null: <paramref name="runtime"/> for every run.</param>
/// <param name="peerClones">Resolves <c>gh:owner/name</c> into a shallow clone for an account with <c>public_clone</c>; null: such a path is refused like any other repository outside the roots.</param>
public sealed class Orchestrator(
    Profile profile,
    IWorkerRuntime runtime,
    string opencodeVersion,
    string rootDirectory,
    IRunLog runLog,
    IReadOnlyDictionary<string, int> promptVersions,
    MemoryStack? memory = null,
    IServiceResolver? services = null,
    Func<SandboxSettings?, ISandbox>? sandboxFor = null,
    IPromptEnhancer? enhancer = null,
    Func<string?, IWorkerRuntime>? runtimeFor = null,
    IPeerCloneManager? peerClones = null)
{
    public const string NodeKindName = "worker";

    /// <param name="runId">The id to run under (the HTTP interface hands it out before the run starts); a new one if null.</param>
    /// <param name="progress">Receives run-status/v1 events: started, intake, node started and finished, run finished.</param>
    /// <param name="parentRunId">The run this one resends with answers, if any; recorded in the start record.</param>
    /// <param name="cancelledByCaller">True once the caller cancelled this run on purpose (not a shutdown): the run then ends as a failed result with
    /// <c>cancelled</c> and is recorded, instead of the cancellation escaping.</param>
    /// <param name="actor">The account that started the run (ADR 0050); on the start record, the run record and the result.</param>
    /// <param name="budgetIsCap">Server-internal, never read from a request (ADR 0085): the owner set this budget, so it replaces the preset's and the profile's per-run cap, and the input-token budget grows by the same ratio.</param>
    public async Task<ResultContract> RunAsync(RunRequest request, CancellationToken ct, string? runId = null, Action<RunStatus>? progress = null,
        string? parentRunId = null, Func<bool>? cancelledByCaller = null, string? actor = null, bool budgetIsCap = false)
    {
        var started = DateTimeOffset.UtcNow;
        runId ??= RunIds.New();
        // A run is its own trace: under serve the ambient activity is the request's, which the SDK does not record, and a
        // parent-based sampler would drop every span below it. The change to Activity.Current stays inside this method.
        if (Activity.Current is { Recorded: false })
            Activity.Current = null;
        using var run = Telemetry.Source.StartActivity("chargehand.run");
        var traceId = run?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
        run?.SetTag("langfuse.trace.name", "chargehand.run");
        run?.SetTag("chargehand.run_id", runId);
        var extensions = new ExtensionsCollector();
        await runLog.AppendAsync(new StartRecord(runId, started, traceId, request, Environment.ProcessId, parentRunId, ProcessBoot.Id, actor), ct);
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
        System.Security.Cryptography.ECDsa? signingKey = null;
        IntakeOutcome? intake = null;
        TaskAction? executed = null;
        Enhanced? enhanced = null;
        string? credential = null;
        List<IAsyncDisposable> leases = [];
        try
        {
            if (request.Driven is not null)
                throw new ChargehandException(ErrorCode.InvalidRequest, "a request with a driven block runs only through chargehand serve", "Send it to the server's POST /v1/runs or its MCP tool.");
            // gh:owner/name resolves to a shallow clone before intake, so a refusal (public_clone off, too large, too little free space) costs nothing.
            if (request.Context.Repository is { } repo && repo.Path.StartsWith("gh:", StringComparison.Ordinal))
            {
                var lease = await AcquirePeerCloneAsync(repo.Path["gh:".Length..], repo.Commit, actor, ct);
                leases.Add(lease);
                request = request with { Context = request.Context with { Repository = repo with { Path = lease.Directory } } };
            }
            // The account's own credential, or a refusal, before anything is spent (ADR 0053).
            var worker = runtimeFor?.Invoke(actor) ?? runtime;
            credential = worker.Credential;
            var preset = Preset.Load(Path.Combine(rootDirectory, "presets"), presetName);
            var (kindName, kind) = AnswerKind(preset);
            // An ACP agent runs research presets only, and a dollar cap holds only on counted tokens (ADR 0086, decisions 8 and 10).
            if (kind.Writes && worker.ResearchOnly)
                throw new ChargehandException(ErrorCode.InvalidRequest, $"preset {presetName} writes, and this runtime runs research presets only",
                    "Use claude_code or opencode for a writing preset, or a research preset with this runtime.");
            if (!worker.ReportsUsage && profile.Acp?.AllowUncapped != true)
                throw new ChargehandException(ErrorCode.RuntimeUncapped, "this agent reports no token usage, so the run's dollar cap cannot hold on it",
                    "Use a runtime that reports usage, or set acp.allow_uncapped in the profile to run it with its spend unrecorded.");
            // A writing preset without a sandbox is refused before anything is spent (ADR 0035).
            var sandbox = kind.Writes ? (sandboxFor ?? SandboxSelector.Select)(profile.Sandbox) : null;
            // A key that cannot be used stops the run before anything is spent (ADR 0036).
            signingKey = ResultSigner.Load(profile.Signing, Environment.GetEnvironmentVariable);
            // Unset when the profile maps no real model to the preset's placeholder: the runtime uses its own default.
            var workerModel = profile.ResolveModel(kind.Model);
            var callerBlocks = (request.CallerBlocks ?? []).Select(PromptChains.VerifyCallerBlock).ToList();
            var local = await IsLocalAsync(request, ct);
            run?.SetTag("chargehand.delivery", local ? DeliveryMode.Local : DeliveryMode.Push);
            // The enhancer sends the request text to an MCP server; a local run keeps it on the machine (ADR 0069).
            enhanced = local ? null : await Enhance(request, run, ct);

            // Intake.
            using (var span = Telemetry.Source.StartActivity("chargehand.intake"))
            {
                TagChain(span, intakeChain);
                span?.SetTag("gen_ai.request.model", intakeModel);
                intake = await new GenerateIntake(worker, ParseModel(profile.IntakeModel), intakeBlock, runId, kind.Checkout).RunAsync(request, ct);
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
                // An action the preset does not allow (Preset.Executes), or a split that fails validation, runs as one answer node.
                executed = preset.Executes(spec.Action);
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
                    _ => await Execute(request, spec, plan, kindName, kind, workerModel, instructions, chain, callerBlocks, runId, traceId, run, extensions, progress, sandbox, worker, local, ct, budgetIsCap),
                };
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || cancelledByCaller?.Invoke() == true)
        {
            // A run that throws (a bad checkout, an unknown preset, no valid Task Spec) still ends with a result and a run record.
            var error = e is OperationCanceledException ? Cancelled() : ChargehandException.ErrorOf(e);
            result = new ResultContract("result/v1", runId, "run", traceId, intakeChain, ResultStatus.Failed, error.Message, [], [], [], [error.Message], 0, new Usage(0, 0, 0, 0, 0),
                error);
        }
        finally
        {
            // A peer clone is deleted when the run ends, whatever the outcome (completed, failed or cancelled).
            foreach (var lease in leases)
                await lease.DisposeAsync();
        }

        result = result with { Actor = actor };
        if (signingKey is not null)
        {
            result = ResultSignature.Sign(result, signingKey);
            signingKey.Dispose();
        }
        run?.SetTag("chargehand.contract.status", result.Status.ToString().ToLowerInvariant());
        run?.SetTag("chargehand.intake.action", intake?.Spec is null ? null : Name(intake.Spec.Action));
        run?.SetTag("chargehand.action", executed is null ? null : Name(executed.Value));
        // A caller's cancel must not stop the record of it from being written.
        await runLog.AppendAsync(new RunRecord(runId, started, DateTimeOffset.UtcNow, presetName, intake?.Spec is null ? null : Name(intake.Spec.Action), intake?.Spec, result,
            executed is null ? null : Name(executed.Value), extensions.ToReport(), actor, credential), cancelledByCaller?.Invoke() == true ? CancellationToken.None : ct);
        await ReportOutcome(enhanced, result, executed);
        progress?.Invoke(RunStatus.Of(runId, RunStatus.StateOf(result.Status), RunEventKind.RunFinished) with { Result = result });
        return result;
    }

    /// <summary>
    /// Asks the prompt enhancer about the request's text (ADR 0041). The answer is only recorded: the original is what intake and the
    /// workers get, because no one is there to accept a rewrite. The guard makes this fail open; only the caller's cancellation escapes.
    /// </summary>
    private async Task<Enhanced?> Enhance(RunRequest request, Activity? run, CancellationToken ct)
    {
        if (enhancer is null || request.Context.Enhance == false || string.IsNullOrWhiteSpace(request.Text))
            return null;
        var answer = await enhancer.EnhanceAsync(request.Text, EnhanceContextOf(request), ct);
        run?.SetTag("chargehand.enhancer.rewrite_offered", answer.Changed);
        return answer;
    }

    /// <summary>Exactly the five fields ADR 0040 and 0070 allow. The repository is the checkout's folder name, never its path; a commit that is not a hex id is left out, and so is a parent request id outside 1 to 128 characters.</summary>
    internal static EnhanceContext EnhanceContextOf(RunRequest request)
    {
        var repo = request.Context.Repository;
        var name = repo is null ? null : Path.GetFileName(repo.Path.TrimEnd('/', '\\'));
        return new EnhanceContext(string.IsNullOrEmpty(name) ? null : name,
            repo is not null && Regex.IsMatch(repo.Commit, "^[0-9a-f]{7,40}$") ? repo.Commit : null, request.Context.Preset,
            ParentRequestId: request.Context.ParentRequestId is { Length: >= 1 and <= 128 } parent ? parent : null);
    }

    /// <summary>
    /// Tells the enhancer how the run ended, once, if it gave a request id. A rewrite that was offered was not accepted (ADR 0041);
    /// with none there was nothing to accept or reject. No score: a run is not scored. Cost only when a worker ran, since a run
    /// that stopped at intake prices nothing. Best effort and bounded by the guard's deadline.
    /// </summary>
    private async Task ReportOutcome(Enhanced? enhanced, ResultContract result, TaskAction? executed)
    {
        if (enhancer is null || enhanced?.RequestId is not { } requestId)
            return;
        var model = result.PromptChain.AsSent.Model;
        var ran = executed is TaskAction.Answer or TaskAction.Split;
        await enhancer.FeedbackAsync(requestId, new EnhanceOutcome(
            RewriteAccepted: enhanced.Changed ? false : null, CostUsd: ran ? result.Usage.Usd : null, Model: model == "auto" ? null : model), CancellationToken.None);
    }

    /// <summary>One call to the intake model, the cheapest a run makes: whether the runtime, its credential and any gateway answer at all.</summary>
    /// <param name="actor">The account whose credential to try (ADR 0053); null: the server's.</param>
    public async Task<ModelProbe> ProbeAsync(TimeSpan timeout, CancellationToken ct, string? actor = null)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await (actor is null ? runtime : runtimeFor?.Invoke(actor) ?? runtime).GenerateAsync(ParseModel(profile.IntakeModel), "Reply with the single word ok.", bounded.Token);
            return new ModelProbe(true, profile.IntakeModel ?? "auto", sw.ElapsedMilliseconds, null);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ModelProbe(false, profile.IntakeModel ?? "auto", sw.ElapsedMilliseconds, e is OperationCanceledException ? $"no answer within {timeout.TotalSeconds:0} s" : e.Message);
        }
    }

    /// <summary>The support check a driven task's claims go through, or null when the profile has it off.</summary>
    /// <param name="actor">The account whose batch it is: the check's model calls go on that account's credential (ADR 0053).</param>
    public Func<ResultContract, EvidenceScope, CancellationToken, Task<ResultContract>>? DrivenSupportCheck(string? actor) =>
        profile.SupportCheck ? (contract, scope, ct) => SupportCheck.RunAsync(runtimeFor?.Invoke(actor) ?? runtime, ParseModel(profile.IntakeModel), contract, scope, ct) : null;

    /// <summary>The clone of the request's commit that driven sessions start from (the same checkout as a worker's), with the source's remote and default branch.
    /// A tracked file the preset denies reading refuses the session: it has a shell, and git reads a file whether or not it is in the working tree.</summary>
    public async Task<DrivenCheckout> CheckoutForDrivenAsync(RepositoryRef repo, string presetName, CancellationToken ct)
    {
        var (_, kind) = AnswerKind(Preset.Load(Path.Combine(rootDirectory, "presets"), presetName));
        var (directory, commit, _, _) = await Checkout(repo, kind, refuseDenied: true, ct);
        var source = Path.GetFullPath(repo.Path);
        var remote = await Git(source, ct, "config", "--get", "remote.origin.url")
            ?? throw new ChargehandException(ErrorCode.CheckoutInvalid, $"{repo.Path} has no remote.origin.url to push to", "Add an origin remote to the repository.");
        var head = await Git(source, ct, "symbolic-ref", "--short", "refs/remotes/origin/HEAD");
        return new DrivenCheckout(directory, commit, remote, head is not null && head.StartsWith("origin/", StringComparison.Ordinal) ? head["origin/".Length..] : "main");
    }

    /// <summary>The clone of the request's commit that a conversation's agent reads (the same checkout as a worker's). A tracked file the preset denies reading refuses the conversation, as
    /// it does a driven session: the agent has a shell. A preset that writes is refused: a conversation reads (ADR 0086).</summary>
    /// <param name="presetName">The preset whose read rules apply; null: <c>default</c>.</param>
    public async Task<string> CheckoutForConversationAsync(RepositoryRef repo, string? presetName, CancellationToken ct)
    {
        var (_, kind) = AnswerKind(Preset.Load(Path.Combine(rootDirectory, "presets"), presetName ?? "default"));
        if (kind.Writes)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"preset '{presetName}' writes, and a conversation only reads", "Name a research preset or leave the preset out.");
        return (await Checkout(repo, kind, refuseDenied: true, ct)).Directory;
    }

    private static ResultError Cancelled() => new(ErrorCode.Cancelled, "the run was cancelled", false, "Start the task again if it is still wanted; nothing was pushed.");

    /// <summary>Records a run that was cancelled before it started (it was queued): a start record and a failed result with <c>cancelled</c>.</summary>
    /// <summary>The checkouts this server reads (<see cref="RepositoryNames"/>) with the commit each has checked out, for the repositories tool.</summary>
    public Task<IReadOnlyList<RepositoryHead>> RepositoriesAsync(CancellationToken ct) => RepositoryNames.HeadsAsync(RepositoryNames.List(profile), ct);

    public async Task<ResultContract> RecordCancelledAsync(RunRequest request, string runId, string? parentRunId, Action<RunStatus>? progress = null, string? actor = null)
    {
        var now = DateTimeOffset.UtcNow;
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        var chain = new PromptChain([], new AsSent(opencodeVersion, "generate", profile.IntakeModel ?? "auto", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        var error = Cancelled();
        var result = new ResultContract("result/v1", runId, "run", traceId, chain, ResultStatus.Failed, "The run was cancelled before it started.", [], [], [],
            [error.Message], 0, new Usage(0, 0, 0, 0, 0), error, Actor: actor);
        await runLog.AppendAsync(new StartRecord(runId, now, traceId, request, Environment.ProcessId, parentRunId, ProcessBoot.Id, actor), CancellationToken.None);
        await runLog.AppendAsync(new RunRecord(runId, now, now, request.Context.Preset, null, null, result, Actor: actor), CancellationToken.None);
        progress?.Invoke(RunStatus.Of(runId, RunState.Failed, RunEventKind.RunFinished) with { Result = result });
        return result;
    }

    /// <summary>The node kind that answers: "worker", or a preset's only kind (draft).</summary>
    internal static (string Name, NodeKind Kind) AnswerKind(Preset preset) =>
        preset.NodeKinds.TryGetValue(NodeKindName, out var worker) ? (NodeKindName, worker) : (preset.NodeKinds.Keys.Single(), preset.NodeKinds.Values.Single());

    /// <summary>Runs the plan's nodes (one for answer) and returns the node's contract, or the merged one for a split.</summary>
    private async Task<ResultContract> Execute(RunRequest request, TaskSpec spec, IReadOnlyList<PlanNode> plan, string kindName, NodeKind kind, string? workerModel,
        IReadOnlyList<(string Key, string Value)> instructions, PromptChain chain, IReadOnlyList<PromptBlock> callerBlocks, string runId, string traceId,
        Activity? run, ExtensionsCollector extensions, Action<RunStatus>? progress, ISandbox? sandbox, IWorkerRuntime worker, bool local, CancellationToken ct, bool budgetIsCap = false)
    {
        var (directory, commit, repository, excluded) = kind.Checkout
            ? await Checkout(request.Context.Repository!, kind, refuseDenied: false, ct)
            : (EmptyDirectory(), "", "", []);
        string? changeNote = null;
        if (kind.Checkout && !kind.Writes && (request.Inputs ?? []).FirstOrDefault(i => i.Kind == "diff") is { } change)
            (directory, commit, changeNote, excluded) = await WithChange(directory, commit, excluded, change.Text, kind, ct);
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
        var presetCap = Math.Min(profile.RunCapUsd / plan.Count, kind.Budget.MaxUsd);
        var cap = budgetIsCap && request.Context.BudgetUsd is { } owned ? owned / plan.Count : Math.Min(presetCap, (request.Context.BudgetUsd ?? decimal.MaxValue) / plan.Count);
        var maxInputTokens = cap > presetCap && presetCap > 0 ? (long)Math.Min(long.MaxValue / 2, kind.Budget.MaxInputTokens * (double)(cap / presetCap)) : kind.Budget.MaxInputTokens;
        // The id is quoted: rendered as "[id]", a model cited "[id]" as the locator, which no input id matches.
        var inputText = string.Join("\n", (request.Inputs ?? []).Select(i => $"- id \"{i.Id}\" ({i.Kind}): {i.Text}{(i.SourceUrl is null ? "" : $" <{i.SourceUrl}>")}"));
        var prices = new PriceTable(profile.Prices ?? new Dictionary<string, ModelPrice>());
        // Unset (ADR 0026): labelled "auto" in the run log, as for intake, when the runtime reports no model for a call.
        var modelLabel = workerModel ?? "auto";
        // Recalled facts go in the prompt text, after the task (ADR 0007 order; ADR 0010 keeps instructions fixed, so
        // siblings still fork). The chain records them as one runtime block per source that contributed.
        var stack = local ? memory?.LocalOnly() : memory;
        var recall = await Recall(stack, request.Text, run, extensions, ct);
        var facts = recall.Prompt;
        chain = chain with { Blocks = [.. chain.Blocks, .. recall.Blocks] };
        // Granted once per run and shared by every node; the tools hash covers the grant, and is today's when there is none.
        var granted = await Grant(profile.ServicesFor(request.Context.Preset, kind.Services), run, extensions, ct);
        if (granted.Count > 0)
            chain = chain with { AsSent = chain.AsSent with { ToolsSha256 = PromptChains.ToolsSha256(opencodeVersion, kind.OpencodeAgent, kind.Rules, granted) } };

        async Task<NodeResult> RunNode(PlanNode node, IReadOnlyList<ResultContract> upstream, ForkPoint? fork, TaskCompletionSource<ForkPoint?>? primed, CancellationToken token)
        {
            var nodeRequest = new NodeRequest(runId, node.Id, traceId,
                new NodeSpec(directory, kind.OpencodeAgent, ParseModel(workerModel), kind.Rules, new Dictionary<string, string> { [IRunCleanup.RunMetadataKey] = runId, ["chargehand.node"] = node.Id }, granted),
                instructions, TaskText(request, spec, commit, changeNote, inputText, callerBlocks, node, plan.Count, upstream) + facts + ServiceCitationNote(granted) + ExcludedNote(excluded), chain, directory, commit,
                (request.Inputs ?? []).Select(i => i.Id).ToHashSet(), inputText, cap, TimeSpan.FromMinutes(15),
                maxInputTokens, kind.Compaction?.TriggerTokens, fork,
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
            var nodeResult = await new WorkerNode(worker, prices, new GitEvidenceResolver(),
                supportCheck: profile.SupportCheck ? (contract, scope, ct2) => SupportCheck.RunAsync(worker, ParseModel(profile.IntakeModel), contract, scope, ct2) : null).RunAsync(nodeRequest, token, primed);
            if (workspace is not null && nodeResult.Contract.Status == ResultStatus.Completed)
            {
                var provenance = ChangeRun.ProvenanceOf(runId, $"opencode {opencodeVersion}", modelLabel, nodeResult.Calls, prices, lastCheck, attempts);
                var branch = await workspace.CommitAsync(Provenance.Apply(ChangeRun.CommitMessage(nodeResult.Contract.Summary), provenance), token);
                string? landFailure = null;
                if (local && branch is not null)
                    (branch, landFailure) = await LandAsync(workspace, branch, request.Context.Repository!.Path, token);
                var changed = branch is null ? [] : await workspace.ChangedPathsAsync(token);
                var diff = branch is null ? "" : await workspace.DiffAsync(token);
                nodeResult = nodeResult with
                {
                    Contract = ChangeRun.Finish(nodeResult.Contract, branch, diff, lastCheck, attempts, verifier!.SandboxKind, verifier.Network, changed, verifyPlan, local ? DeliveryMode.Local : DeliveryMode.Push, landFailure),
                };
            }
            span?.SetTag("langfuse.session.id", nodeResult.SessionId);
            run?.SetTag("langfuse.session.id", nodeResult.SessionId);
            span?.SetTag("chargehand.contract.status", nodeResult.Contract.Status.ToString().ToLowerInvariant());
            span?.SetTag("chargehand.forked_from", nodeResult.ForkedFrom);
            if (worker is IServiceHealth health)
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
                    nodeResult.ForkedFrom, instructionRefs, worker.Credential), token);
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
            outcomes = await new GraphRunner(Math.Clamp(profile.MaxParallelNodes, 1, 2)).RunAsync(plan, RunNode, Failed, ct);
        }
        finally
        {
            // Whatever became of the nodes, and even when the caller cancelled: a server registered for the run must not outlive it.
            await EndRun(worker, runId, granted.Count > 0, run);
        }
        var result = plan.Count == 1 ? outcomes[0].Contract : ResultMerger.Merge(runId, traceId, chain, outcomes);
        await Retain(stack, result, repository, commit, runId, run, extensions, ct);
        return result;
    }

    /// <summary>
    /// Keeps the claims whose citations resolved, with where they were checked (ADR 0034). A run with no commit has nothing
    /// checked, and a claim resting on the request or on the session anchors nothing in the repository; neither is retained.
    /// Memory fails open (ADR 0008): the stack reports a failed retain instead of throwing it, so a completed run stays completed.
    /// </summary>
    private static async Task Retain(MemoryStack? memory, ResultContract result, string repository, string commit, string runId, Activity? run, ExtensionsCollector extensions, CancellationToken ct)
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
    private static async Task<RecallOutcome> Recall(MemoryStack? memory, string query, Activity? run, ExtensionsCollector extensions, CancellationToken ct)
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

    /// <summary>A runtime that keeps a run's services with a server other runs share (OpenCode), or a process per node (ACP), lets them go when the run's nodes are done.
    /// Best effort: a failure here is a tag on the run, never a failed run, and the caller's cancellation does not cut it short.</summary>
    private static async Task EndRun(IWorkerRuntime worker, string runId, bool hasServices, Activity? run)
    {
        if (worker is not IRunCleanup cleanup || !(hasServices || cleanup.EndsEveryRun))
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
    /// Resolves <c>gh:owner/name</c> to a shallow clone, refusing for $0 before intake runs: the account needs
    /// <c>public_clone</c>, and the profile needs a <c>peer_clone</c> to clone into.
    /// </summary>
    private async Task<IPeerCloneLease> AcquirePeerCloneAsync(string slug, string commit, string? actor, CancellationToken ct)
    {
        var account = actor is null ? null : profile.Http?.Accounts?.FirstOrDefault(a => a.Name == actor);
        if (account?.PublicClone != true)
            throw new ChargehandException(ErrorCode.RepositoryNotAllowed,
                $"gh:{slug} is outside the profile's repository_roots, and {(actor is null ? "a run with no account" : $"account {actor}")} may not read a public repository by clone",
                "Set public_clone: true on the account, or use a checkout under repository_roots.");
        if (peerClones is null)
            throw new ChargehandException(ErrorCode.RepositoryNotAllowed, "this server has no peer_clone configured", "Add peer_clone to the profile to read gh: repositories.");
        return await peerClones.AcquireAsync(slug, commit, ct);
    }

    /// <summary>
    /// The worker reads a clone of the caller's repository at the requested commit, made under worker_root (ADR 0023):
    /// the source may sit anywhere under the profile's repository roots, the worker's directory stays outside the
    /// OpenCode user's home (ADR 0003), and the source's uncommitted and ignored files never reach the worker. A clone
    /// per source and commit is reused and never deleted (ADR 0015). The clone's working tree holds no file the node kind may
    /// not read (ADR 0006): OpenCode's grep searches any path or include glob it is given, and its permission resource is
    /// the search pattern, so a read deny cannot keep it out of a file. Such files are left out of the working tree, or,
    /// with <paramref name="refuseDenied"/>, refuse the run. Returns the files left out.
    /// </summary>
    private async Task<(string Directory, string Commit, string Repository, IReadOnlyList<string> Excluded)> Checkout(RepositoryRef repo, NodeKind kind, bool refuseDenied, CancellationToken ct)
    {
        // git prints the top level with symbolic links resolved, so a link under a root cannot point the worker elsewhere.
        var source = await Git(Path.GetFullPath(repo.Path), ct, "rev-parse", "--show-toplevel") ?? await ByName(repo.Path, ct);
        // A peer clone (ADR: gh:owner/name) sits under peer_clone.root, deliberately kept out of repository_roots so it never shows up in RepositoryNames.List.
        if (!profile.Roots.Any(root => PathScope.Under(source, PathScope.RealPath(root)))
            && !(profile.PeerClone is { } peerClone && PathScope.Under(source, PathScope.RealPath(peerClone.Root))))
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
        if (denied.Count > 0 && refuseDenied)
            throw new ChargehandException(ErrorCode.CheckoutHasSecrets, $"the repository tracks files the preset denies reading, which a session's shell would still reach: " +
                $"{string.Join(", ", denied.Take(5))}{(denied.Count > 5 ? ", ..." : "")}",
                "Pin a commit that does not track them, or remove them from the repository.");
        if (denied.Count > 0)
            await Exclude(dir, denied, ct);
        return (dir, commit, RepositoryLabel.From(await Git(source, ct, "config", "--get", "remote.origin.url"), Path.GetFileName(source)), denied);
    }

    /// <summary>A review gets the change it reviews on disk (<see cref="ReviewedChange"/>). Files the preset denies reading, a change's new ones too, are left out of the clone as in <see cref="Checkout"/>.</summary>
    private static async Task<(string Directory, string Commit, string? Note, IReadOnlyList<string> Excluded)> WithChange(string directory, string commit, IReadOnlyList<string> excluded, string diff,
        NodeKind kind, CancellationToken ct)
    {
        var change = await ReviewedChange.ApplyAsync(directory, commit, diff, ct);
        if (change.Directory == directory)
            return (directory, commit, change.Note, excluded);
        var denied = await DeniedFiles(change.Directory, kind.Permissions, ct);
        await Exclude(change.Directory, denied, ct);
        return (change.Directory, change.Commit, change.Note, denied);
    }

    /// <summary>
    /// Leaves tracked files out of a clone's working tree with a sparse checkout, so grep and read cannot reach them. They stay in
    /// the index (skip-worktree): a commit in a clone of it never deletes them. A reused clone keeps what earlier runs left out.
    /// </summary>
    private static async Task Exclude(string dir, IReadOnlyList<string> files, CancellationToken ct)
    {
        if (!files.Any(f => File.Exists(Path.Combine(dir, f))))
            return;
        var path = Path.Combine(dir, ".git", "info", "sparse-checkout");
        List<string> lines = File.Exists(path) ? [.. await File.ReadAllLinesAsync(path, ct)] : ["/*"];
        lines.AddRange(files.Select(f => "!/" + SparseLiteral(f)).Where(l => !lines.Contains(l)).ToList());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllLinesAsync(path, lines, ct);
        if (await Git(dir, ct, "config", "core.sparseCheckout", "true") is null || await Git(dir, ct, "read-tree", "-mu", "HEAD") is null
            || files.Any(f => File.Exists(Path.Combine(dir, f))))
            throw new ChargehandException(ErrorCode.CheckoutHasSecrets, $"could not leave the files the preset denies reading out of {dir}",
                "Delete the checkout under worker_root/.checkouts and retry.");
    }

    /// <summary>A path as a sparse-checkout pattern that matches only itself.</summary>
    private static string SparseLiteral(string path) =>
        path.Contains('\n', StringComparison.Ordinal)
            ? throw new ChargehandException(ErrorCode.CheckoutHasSecrets, "a denied file's name holds a line break", "Rename it, or pin a commit that does not track it.")
            : string.Concat(path.Select(c => c is '\\' or '*' or '?' or '[' or ' ' ? $"\\{c}" : c.ToString()));

    /// <summary>Whether the run's output stays on the machine (ADR 0069): the request says so, or the profile forces the repository. A run with no repository is forced only by the root <c>/</c>.</summary>
    private async Task<bool> IsLocalAsync(RunRequest request, CancellationToken ct)
    {
        var path = request.Context.Repository?.Path;
        var topLevel = path is null ? "/" : await Git(Path.GetFullPath(path), ct, "rev-parse", "--show-toplevel") ?? Path.GetFullPath(path);
        return profile.DeliveryPolicy.IsLocal(request.Context.Delivery, topLevel);
    }

    /// <summary>Fetches the run's branch into the caller's repository as a new local branch; it is then there, and only there (ADR 0069). The failure text, or null.</summary>
    private static async Task<(BranchInfo Branch, string? Failure)> LandAsync(RunWorkspace workspace, BranchInfo branch, string path, CancellationToken ct)
    {
        var source = await Git(Path.GetFullPath(path), ct, "rev-parse", "--show-toplevel") ?? Path.GetFullPath(path);
        return await workspace.LandAsync(source, ct) is { } failure ? (branch, failure) : (branch with { Repository = source }, null);
    }

    /// <summary>The checkout a path that is not one on this server names: <c>owner/name</c>, a GitHub URL or a folder name (<see cref="RepositoryNames"/>).
    /// Fails with every name this server knows, so a caller does not guess spellings.</summary>
    private async Task<string> ByName(string given, CancellationToken ct)
    {
        var known = RepositoryNames.List(profile);
        return (RepositoryNames.Resolve(given, known) is { } path ? await Git(path, ct, "rev-parse", "--show-toplevel") : null)
            ?? throw new ChargehandException(ErrorCode.CheckoutInvalid, $"{given} is not a git checkout on this server, nor the name of one it reads; known: {RepositoryNames.Describe(known)}",
                "Set context.repository.path to one of those names (owner/name, a GitHub URL or a folder name) or to a checkout's path; the repositories tool lists them with the commit each has checked out.");
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

    /// <summary>Told in the task text, only when the checkout left files out: the shipped prompts stay as they are without them.</summary>
    internal static string ExcludedNote(IReadOnlyList<string> excluded) => excluded.Count == 0
        ? ""
        : $"\n\nThese tracked files are left out of your checkout, because the preset denies reading them: {string.Join(", ", excluded.Take(20))}" +
          $"{(excluded.Count > 20 ? $" and {excluded.Count - 20} more" : "")}. Do not look for them; if the answer depends on one, say so.";

    private static string TaskText(RunRequest request, TaskSpec spec, string commit, string? changeNote, string inputText, IReadOnlyList<PromptBlock> callerBlocks,
        PlanNode node, int nodes, IReadOnlyList<ResultContract> upstream)
    {
        var sb = new StringBuilder();
        sb.AppendLine(commit.Length > 0 ? $"Task (repository commit {commit}):" : "Task:").AppendLine(request.Text).AppendLine();
        if (changeNote is not null)
            sb.AppendLine(changeNote).AppendLine();
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

/// <summary>What <see cref="Orchestrator.ProbeAsync"/> found: the model answered or why not, and how long it took.</summary>
public sealed record ModelProbe(bool Ok, string Model, long Milliseconds, string? Error);
