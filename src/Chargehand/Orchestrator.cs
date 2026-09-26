using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using Chargehand.Verification;

namespace Chargehand;

/// <summary>Request → intake → action: stop (ask, improve, deny, approval) or run one node (answer) or a task graph (split) → result/v1.</summary>
/// <param name="promptVersions">Langfuse prompt versions by block sha256, for linking spans; may be empty.</param>
/// <param name="memory">Long-term memory from the profile, or null (ADR 0008).</param>
public sealed class Orchestrator(
    Profile profile,
    IWorkerRuntime runtime,
    string opencodeVersion,
    string rootDirectory,
    IRunLog runLog,
    IReadOnlyDictionary<string, int> promptVersions,
    IMemoryProvider? memory = null)
{
    public const string NodeKindName = "worker";

    public async Task<ResultContract> RunAsync(RunRequest request, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var runId = $"run-{started:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        using var run = Telemetry.Source.StartActivity("chargehand.run");
        var traceId = run?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
        run?.SetTag("langfuse.trace.name", "chargehand.run");
        run?.SetTag("chargehand.run_id", runId);

        var registry = new PromptRegistry(Path.Combine(rootDirectory, "prompts"));
        var presetName = request.Context.Preset;
        var preset = Preset.Load(Path.Combine(rootDirectory, "presets"), presetName);
        var kind = preset.NodeKinds[NodeKindName];
        var workerModel = profile.ResolveModel(kind.Model);
        var callerBlocks = (request.CallerBlocks ?? []).Select(PromptChains.VerifyCallerBlock).ToList();
        var date = started.ToString("yyyy-MM-dd");

        // Intake.
        var intakeBlock = registry.Get("intake/task-spec");
        var intakeChain = PromptChains.Build([(intakeBlock, BlockSource.Registry)], new AsSent(opencodeVersion, "generate", profile.IntakeModel, date));
        IntakeOutcome intake;
        using (var span = Telemetry.Source.StartActivity("chargehand.intake"))
        {
            TagChain(span, intakeChain);
            span?.SetTag("gen_ai.request.model", profile.IntakeModel);
            intake = await new GenerateIntake(runtime, ParseModel(profile.IntakeModel), intakeBlock, runId).RunAsync(request, ct);
            span?.SetTag("chargehand.intake.action", intake.Spec?.Action.ToString().ToLowerInvariant() ?? "needs_input");
            foreach (var c in intake.Calls)
                await runLog.AppendAsync(new CallRecord(runId, "intake", "intake", null, null, profile.IntakeModel, c.Started, c.LatencyMs, null, null, intakeChain), ct);
        }

        var blocks = new List<(PromptBlock, BlockSource)> { (registry.Get($"core/{NodeKindName}"), BlockSource.Registry), (registry.Get($"preset/{presetName}"), BlockSource.Registry) };
        blocks.AddRange(callerBlocks.Select(b => (b, BlockSource.Caller)));
        var instructions = new List<(string Key, string Value)> { ("chargehand-core", blocks[0].Item1.Text), ("chargehand-preset", blocks[1].Item1.Text) };
        var chain = PromptChains.Build(blocks, new AsSent(opencodeVersion, kind.OpencodeAgent, workerModel, date,
            BasePromptVariant: kind.OpencodeAgent,
            InstructionFilesSha256: PromptChains.InstructionsSha256(instructions),
            ToolsSha256: PromptChains.ToolsSha256(opencodeVersion, kind.OpencodeAgent, kind.Rules)));

        ResultContract Stop(ResultStatus status, string summary, IReadOnlyList<string> questions, IReadOnlyList<Artifact>? artifacts = null) =>
            new("result/v1", runId, "intake", traceId, intakeChain, status, summary, [], [], artifacts ?? [], questions, 0, new Usage(0, 0, 0, 0, 0));

        ResultContract result;
        TaskAction? executed = null;
        if (intake.Spec is not { } spec)
            result = Stop(ResultStatus.NeedsInput, intake.NeedsInput!, [intake.NeedsInput!]);
        else
        {
            // An action the preset does not allow, or a split that fails validation, runs as one answer node.
            executed = preset.AllowedActions.Contains(Name(spec.Action)) ? spec.Action : TaskAction.Answer;
            IReadOnlyList<PlanNode> plan = [new PlanNode(NodeKindName, null, [])];
            if (executed == TaskAction.Split)
            {
                var (nodes, reason) = SplitPlan.From(spec);
                plan = nodes ?? plan;
                executed = nodes is null ? TaskAction.Answer : TaskAction.Split;
                run?.SetTag("chargehand.split.rejected", reason);
            }
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
                _ => await Execute(request, spec, plan, kind, workerModel, instructions, chain, callerBlocks, runId, traceId, run, ct),
            };
        }

        run?.SetTag("chargehand.contract.status", result.Status.ToString().ToLowerInvariant());
        run?.SetTag("chargehand.intake.action", intake.Spec is null ? null : Name(intake.Spec.Action));
        run?.SetTag("chargehand.action", executed is null ? null : Name(executed.Value));
        await runLog.AppendAsync(new RunRecord(runId, started, DateTimeOffset.UtcNow, presetName, intake.Spec is null ? null : Name(intake.Spec.Action), intake.Spec, result,
            executed is null ? null : Name(executed.Value)), ct);
        return result;
    }

    /// <summary>Runs the plan's nodes (one for answer) and returns the node's contract, or the merged one for a split.</summary>
    private async Task<ResultContract> Execute(RunRequest request, TaskSpec spec, IReadOnlyList<PlanNode> plan, NodeKind kind, string workerModel,
        IReadOnlyList<(string Key, string Value)> instructions, PromptChain chain, IReadOnlyList<PromptBlock> callerBlocks, string runId, string traceId,
        Activity? run, CancellationToken ct)
    {
        var repo = request.Context.Repository!;
        var commit = await CheckRepository(repo, ct);
        // ponytail: the run cap is split evenly across nodes up front; share the remainder dynamically if nodes vary a lot.
        var cap = new[] { profile.RunCapUsd / plan.Count, kind.Budget.MaxUsd, (request.Context.BudgetUsd ?? decimal.MaxValue) / plan.Count }.Min();
        var inputText = string.Join("\n", (request.Inputs ?? []).Select(i => $"[{i.Id}] ({i.Kind}) {i.Text}{(i.SourceUrl is null ? "" : $" <{i.SourceUrl}>")}"));
        var prices = new PriceTable(profile.Prices);
        // Recalled facts go in the prompt text, after the task (ADR 0007 order; ADR 0010 keeps instructions fixed, so
        // siblings still fork). The chain records them as a runtime block.
        var facts = await Recall(request.Text, run, ct);
        if (facts.Length > 0)
            chain = chain with { Blocks = [.. chain.Blocks, new ChainBlock("memory/recall", "1", PromptText.Sha256(facts), BlockSource.Runtime)] };

        async Task<NodeResult> RunNode(PlanNode node, IReadOnlyList<ResultContract> upstream, ForkPoint? fork, TaskCompletionSource<ForkPoint?>? primed, CancellationToken token)
        {
            var nodeRequest = new NodeRequest(runId, node.Id, traceId,
                new NodeSpec(Path.GetFullPath(repo.Path), kind.OpencodeAgent, ParseModel(workerModel), kind.Rules, new Dictionary<string, string> { ["chargehand.run"] = runId, ["chargehand.node"] = node.Id }),
                instructions, TaskText(request, spec, commit, inputText, callerBlocks, node, plan.Count, upstream) + facts, chain, repo.Path, commit,
                (request.Inputs ?? []).Select(i => i.Id).ToHashSet(), inputText, cap, TimeSpan.FromMinutes(15),
                kind.Budget.MaxInputTokens, kind.Compaction?.TriggerTokens, fork);

            var instructionRefs = nodeRequest.Instructions.Select(i => new InstructionRef(i.Key, PromptText.Sha256(i.Value))).ToList();
            using var span = Telemetry.Source.StartActivity("chargehand.node");
            TagChain(span, chain);
            span?.SetTag("chargehand.node", node.Id);
            var nodeResult = await new WorkerNode(runtime, prices, new GitEvidenceResolver()).RunAsync(nodeRequest, token, primed);
            span?.SetTag("langfuse.session.id", nodeResult.SessionId);
            run?.SetTag("langfuse.session.id", nodeResult.SessionId);
            span?.SetTag("chargehand.contract.status", nodeResult.Contract.Status.ToString().ToLowerInvariant());
            span?.SetTag("chargehand.forked_from", nodeResult.ForkedFrom);

            foreach (var m in nodeResult.Calls)
            {
                var usd = prices.PriceUsd(m.Model ?? workerModel, m.Tokens!);
                var latency = ((m.Completed ?? m.Created) - m.Created).TotalMilliseconds;
                await runLog.AppendAsync(new CallRecord(runId, node.Id, "worker", nodeResult.SessionId, m.Id, m.Model ?? workerModel, m.Created, latency, m.Tokens, usd, chain,
                    nodeResult.ForkedFrom, instructionRefs), token);
                using var call = Telemetry.Source.StartActivity("chargehand.call", ActivityKind.Client, span?.Context ?? default, startTime: m.Created);
                TagChain(call, chain);
                call?.SetTag("langfuse.session.id", nodeResult.SessionId);
                call?.SetTag("chargehand.message_id", m.Id);
                call?.SetTag("gen_ai.request.model", m.Model ?? workerModel);
                call?.SetEndTime((m.Completed ?? m.Created).UtcDateTime);
            }
            return nodeResult;
        }

        ResultContract Failed(PlanNode node, string reason) =>
            new("result/v1", runId, node.Id, traceId, chain, ResultStatus.Failed, reason, [], [], [], [reason], 0, new Usage(0, 0, 0, 0, 0));

        var outcomes = await new GraphRunner().RunAsync(plan, RunNode, Failed, ct);
        var result = plan.Count == 1 ? outcomes[0].Contract : ResultMerger.Merge(runId, traceId, chain, outcomes);
        if (memory is not null && profile.Memory!.Retain && result.Status == ResultStatus.Completed)
            await memory.RetainAsync(new MemoryItem($"{request.Text}\n{result.Summary}\n{string.Join("\n", result.Claims.Select(c => $"- {c.Text}"))}",
                "chargehand run result", DateTimeOffset.UtcNow, runId, ["chargehand"]), MemoryScopeOf(profile.Memory), ct);
        return result;
    }

    private static MemoryScope MemoryScopeOf(MemorySettings m) => new(m.Backend, m.Namespace);

    /// <summary>Memory is optional context: a failed recall leaves the run without it rather than failing it.</summary>
    private async Task<string> Recall(string query, Activity? run, CancellationToken ct)
    {
        if (memory is null)
            return "";
        try
        {
            var items = await memory.RecallAsync(query, MemoryScopeOf(profile.Memory!), ct);
            run?.SetTag("chargehand.memory.recalled", items.Count);
            return items.Count == 0 ? "" : "\nFacts from long-term memory (unverified; check them in the repository and cite files, never these):\n"
                + string.Join("\n", items.Select(i => $"- {i.Text}")) + "\n";
        }
        catch (HttpRequestException e)
        {
            run?.SetTag("chargehand.memory.error", e.Message);
            return "";
        }
    }

    private static string Name<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static Artifact Inline(string kind, string mediaType, string content) =>
        new(kind, mediaType, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), Content: content);

    /// <summary>The checkout must sit under worker_root (outside the OpenCode user's home, ADR 0003) at the requested commit.</summary>
    private async Task<string> CheckRepository(RepositoryRef repo, CancellationToken ct)
    {
        var full = Path.GetFullPath(repo.Path) + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(profile.WorkerRoot) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException($"repository {repo.Path} is not under worker_root {profile.WorkerRoot}");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + Path.DirectorySeparatorChar;
        if (full.StartsWith(home, StringComparison.Ordinal))
            throw new InvalidOperationException("worker checkouts must live outside the home directory (ADR 0003)");
        var psi = new ProcessStartInfo("git", ["-C", repo.Path, "rev-parse", "HEAD"]) { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var head = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
        await p.WaitForExitAsync(ct);
        return head.StartsWith(repo.Commit, StringComparison.OrdinalIgnoreCase)
            ? head
            : throw new InvalidOperationException($"checkout is at {head[..Math.Min(12, head.Length)]}, request pins {repo.Commit}");
    }

    /// <summary>Volatile content goes in the prompt text, after the fixed instruction entries (ADR 0010).</summary>
    private static string TaskText(RunRequest request, TaskSpec spec, string commit, string inputText, IReadOnlyList<PromptBlock> callerBlocks,
        PlanNode node, int nodes, IReadOnlyList<ResultContract> upstream)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Task (repository commit {commit}):").AppendLine(request.Text).AppendLine();
        if (node.Goal is not null)
            sb.AppendLine($"Your part ({node.Id}, one of {nodes} parts, each answered by a separate worker): {node.Goal}")
              .AppendLine("Answer only your part; do not research the other parts.").AppendLine();
        if (spec.Constraints.Count > 0)
            sb.AppendLine("Constraints:").AppendLine(string.Join("\n", spec.Constraints.Select(c => $"- {c}"))).AppendLine();
        // Acceptance criteria describe the whole answer, so a part does not get them.
        if (node.Goal is null && spec.AcceptanceCriteria.Count > 0)
            sb.AppendLine("A good answer:").AppendLine(string.Join("\n", spec.AcceptanceCriteria.Select(c => $"- {c}"))).AppendLine();
        foreach (var u in upstream)
            sb.AppendLine($"Result of part {u.NodeId} (you may rely on it; cite files yourself):")
              .AppendLine(JsonSerializer.Serialize(new { u.Summary, u.Claims, u.Evidence }, ContractJson.Options)).AppendLine();
        if (inputText.Length > 0)
            sb.AppendLine("Inputs (cite with kind \"input\" and the id):").AppendLine(inputText).AppendLine();
        foreach (var b in callerBlocks)
            sb.AppendLine($"Caller instructions ({b.Name} {b.Version}):").AppendLine(b.Text).AppendLine();
        return sb.ToString();
    }

    private void TagChain(Activity? span, PromptChain chain)
    {
        if (span is null)
            return;
        span.SetTag("chargehand.prompt_chain", JsonSerializer.Serialize(chain, ContractJson.Options));
        var first = chain.Blocks.FirstOrDefault();
        if (first is not null && promptVersions.TryGetValue(first.Sha256, out var version))
        {
            span.SetTag("langfuse.observation.prompt.name", first.Name);
            span.SetTag("langfuse.observation.prompt.version", version);
        }
    }

    public static ModelRef ParseModel(string model)
    {
        var slash = model.IndexOf('/');
        var hash = model.IndexOf('#');
        return new ModelRef(model[..slash], hash < 0 ? model[(slash + 1)..] : model[(slash + 1)..hash], hash < 0 ? null : model[(hash + 1)..]);
    }
}
