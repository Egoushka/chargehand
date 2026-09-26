using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Chargehand.Budget;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Intake;
using Chargehand.Nodes;
using Chargehand.Prompts;
using Chargehand.RunLog;
using Chargehand.Runtime;
using Chargehand.Verification;

namespace Chargehand;

/// <summary>v0: request → intake → one worker node → result/v1 (roadmap phase 3).</summary>
/// <param name="promptVersions">Langfuse prompt versions by block sha256, for linking spans; may be empty.</param>
public sealed class Orchestrator(
    Profile profile,
    IWorkerRuntime runtime,
    string opencodeVersion,
    string rootDirectory,
    IRunLog runLog,
    IReadOnlyDictionary<string, int> promptVersions)
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

        ResultContract result;
        if (intake.Spec is null)
        {
            result = new ResultContract("result/v1", runId, "intake", traceId, intakeChain, ResultStatus.NeedsInput, intake.NeedsInput!, [], [], [], [intake.NeedsInput!], 0, new Usage(0, 0, 0, 0, 0));
        }
        else
        {
            var repo = request.Context.Repository!;
            var commit = await CheckRepository(repo, ct);
            var cap = new[] { profile.RunCapUsd, kind.Budget.MaxUsd, request.Context.BudgetUsd ?? decimal.MaxValue }.Min();
            var inputText = string.Join("\n", (request.Inputs ?? []).Select(i => $"[{i.Id}] ({i.Kind}) {i.Text}{(i.SourceUrl is null ? "" : $" <{i.SourceUrl}>")}"));
            var node = new NodeRequest(runId, NodeKindName, traceId,
                new NodeSpec(Path.GetFullPath(repo.Path), kind.OpencodeAgent, ParseModel(workerModel), kind.Rules, new Dictionary<string, string> { ["chargehand.run"] = runId, ["chargehand.node"] = NodeKindName }),
                instructions, TaskText(request, intake.Spec, commit, inputText, callerBlocks), chain, repo.Path, commit,
                (request.Inputs ?? []).Select(i => i.Id).ToHashSet(), inputText, cap, TimeSpan.FromMinutes(15));

            using var span = Telemetry.Source.StartActivity("chargehand.node");
            TagChain(span, chain);
            var prices = new PriceTable(profile.Prices);
            var nodeResult = await new WorkerNode(runtime, prices, new GitEvidenceResolver()).RunAsync(node, ct);
            result = nodeResult.Contract;
            span?.SetTag("langfuse.session.id", nodeResult.SessionId);
            run?.SetTag("langfuse.session.id", nodeResult.SessionId);
            span?.SetTag("chargehand.contract.status", result.Status.ToString().ToLowerInvariant());

            foreach (var m in nodeResult.Calls)
            {
                var usd = prices.PriceUsd(m.Model ?? workerModel, m.Tokens!);
                var latency = ((m.Completed ?? m.Created) - m.Created).TotalMilliseconds;
                await runLog.AppendAsync(new CallRecord(runId, NodeKindName, "worker", nodeResult.SessionId, m.Id, m.Model ?? workerModel, m.Created, latency, m.Tokens, usd, chain), ct);
                using var call = Telemetry.Source.StartActivity("chargehand.call", ActivityKind.Client, span?.Context ?? default, startTime: m.Created);
                TagChain(call, chain);
                call?.SetTag("langfuse.session.id", nodeResult.SessionId);
                call?.SetTag("chargehand.message_id", m.Id);
                call?.SetTag("gen_ai.request.model", m.Model ?? workerModel);
                call?.SetEndTime((m.Completed ?? m.Created).UtcDateTime);
            }
        }

        run?.SetTag("chargehand.contract.status", result.Status.ToString().ToLowerInvariant());
        run?.SetTag("chargehand.intake.action", intake.Spec?.Action.ToString().ToLowerInvariant());
        await runLog.AppendAsync(new RunRecord(runId, started, DateTimeOffset.UtcNow, presetName, intake.Spec?.Action.ToString().ToLowerInvariant(), intake.Spec, result), ct);
        return result;
    }

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
    private static string TaskText(RunRequest request, TaskSpec spec, string commit, string inputText, IReadOnlyList<PromptBlock> callerBlocks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Task (repository commit {commit}):").AppendLine(request.Text).AppendLine();
        if (spec.Constraints.Count > 0)
            sb.AppendLine("Constraints:").AppendLine(string.Join("\n", spec.Constraints.Select(c => $"- {c}"))).AppendLine();
        if (spec.AcceptanceCriteria.Count > 0)
            sb.AppendLine("A good answer:").AppendLine(string.Join("\n", spec.AcceptanceCriteria.Select(c => $"- {c}"))).AppendLine();
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
