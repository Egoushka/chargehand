using System.Diagnostics;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Intake;
using Chargehand.Plans;
using Chargehand.Prompts;
using Chargehand.RunLog;
using Chargehand.Runtime;

namespace Chargehand.Evals;

/// <summary>
/// Paired runs for Prompt CI (ADR 0019): each item runs under the base and under the change prompts and presets,
/// back to back, the order alternating between items so drift over the session cancels out. Evals never use memory
/// (ADR 0008), so earlier runs cannot leak answers into later ones.
/// </summary>
/// <param name="orchestratorFor">An orchestrator reading prompts/ and presets/ from the given root.</param>
/// <param name="flush">Exports pending spans, so a trace exists before a dataset run item links it.</param>
public sealed class EvalRunner(Func<string, Orchestrator> orchestratorFor, IWorkerRuntime runtime, string intakeModel, JsonlRunLog log,
    LangfuseEvals? langfuse, Action flush, TextWriter output)
{
    /// <summary>The cells a change touches, and the changed prompt or preset files no cell covers.</summary>
    public static (IReadOnlyList<EvalCell> Cells, IReadOnlyList<string> Uncovered) Affected(IReadOnlyList<EvalCell> cells, IEnumerable<string> changedFiles)
    {
        var gated = changedFiles.Where(f => f.StartsWith("prompts/", StringComparison.Ordinal) || f.StartsWith("presets/", StringComparison.Ordinal)).ToList();
        return (cells.Where(c => c.Files.Any(gated.Contains)).ToList(), gated.Where(f => !cells.Any(c => c.Files.Contains(f))).ToList());
    }

    /// <summary>Proposes items from runs in the log, for review before a push: one per run, and one per split subtask.</summary>
    public static IEnumerable<EvalItem> Seed(EvalCell cell, IEnumerable<RunEntry> runs)
    {
        foreach (var entry in runs)
        {
            if (entry.Run is not { } run)
                continue;
            // Runs before phase 5 kept no request: intake's goal stands in for its text, and the repository is filled in by hand.
            var request = entry.Start?.Request ?? new RunRequest("request/v1", run.Spec?.Goal ?? "", new RequestContext(false, run.Preset));
            var meta = new Dictionary<string, string> { ["source"] = "run-log", ["run_id"] = run.RunId, ["text_source"] = entry.Start is null ? "spec.goal" : "request" };
            var expected = cell.Kind switch
            {
                "intake" => new EvalExpected(Action: run.IntakeAction ?? "needs_input"),
                "draft" => new EvalExpected(Draft: new DraftExpectation(Math.Max(1200, run.Result.Artifacts.FirstOrDefault(a => a.Kind == "draft")?.Content?.Length ?? 0),
                    [.. run.Result.Evidence.Where(e => e.Kind == EvidenceKind.Input).Select(e => e.Locator).Distinct()])),
                _ => new EvalExpected(ReferenceFiles: Files(run.Result.Evidence), ReferenceClaims: run.Result.Claims.Count),
            };
            yield return new EvalItem(run.RunId, request, expected, meta);
            if (cell.Kind is "worker" or "intake" && run.Spec is { Action: TaskAction.Split } spec && SplitPlan.From(spec).Nodes is { } nodes)
                foreach (var node in nodes)
                {
                    // The merge prefixes a node's evidence ids with the node id (ResultMerger).
                    bool Mine(string id) => id.StartsWith(node.Id + ".", StringComparison.Ordinal);
                    yield return new EvalItem($"{run.RunId}.{node.Id}", request with { Text = node.Goal! },
                        cell.Kind == "intake" ? new EvalExpected(Action: "answer")
                            : new EvalExpected(ReferenceFiles: Files(run.Result.Evidence.Where(e => Mine(e.Id))), ReferenceClaims: run.Result.Claims.Count(c => c.Evidence.Any(Mine))),
                        new Dictionary<string, string>(meta) { ["subtask"] = node.Id });
                }
        }
    }

    public async Task<Verdict> RunAsync(EvalCell cell, IReadOnlyList<EvalItem> items, string baseRoot, string changeRoot, string name, Trade? trade, CancellationToken ct)
    {
        // A preset new in this change has nothing to compare against: the change's runs become its first baseline. That
        // holds for a whole cell and for an intake item, which keeps its own preset.
        bool InBase(string preset) => File.Exists(Path.Combine(baseRoot, "presets", preset + ".yaml"));
        var hasBase = cell.Preset is null || InBase(cell.Preset);
        var pairs = new List<Pair>();
        foreach (var (item, i) in items.Select((item, i) => (item, i)))
        {
            var paired = hasBase && InBase(cell.Preset ?? item.Request.Context.Preset);
            string[] arms = !paired ? ["change"] : i % 2 == 0 ? ["base", "change"] : ["change", "base"];
            var scored = new Dictionary<string, (double Quality, decimal Usd)>();
            foreach (var arm in arms)
                scored[arm] = await RunArm(cell, item, arm == "base" ? baseRoot : changeRoot, $"{name}-{arm}", ct);
            if (paired)
                pairs.Add(new Pair(item.Id, scored["base"].Quality, scored["change"].Quality, scored["base"].Usd, scored["change"].Usd));
        }
        return hasBase
            ? Gate.Decide(cell, pairs, trade)
            : new Verdict(cell.Name, items.Count, 0, null, 0, null, false, "new cell: the change's runs are its first baseline");
    }

    private async Task<(double Quality, decimal Usd)> RunArm(EvalCell cell, EvalItem item, string root, string runName, CancellationToken ct)
    {
        string runId, traceId;
        decimal usd = 0;
        IReadOnlyDictionary<string, double> scores;
        // A gateway rate limit says nothing about the prompts: run the arm again rather than score it 0, and stop the
        // gate if the limit outlasts the retries, since a verdict over missing runs would mislead.
        for (var attempt = 0; ; attempt++)
        {
            string? limited;
            if (cell.Kind == "intake")
                (runId, traceId, scores, limited) = await Intake(item, root, ct);
            else
            {
                var result = await orchestratorFor(root).RunAsync(item.Request with { Context = item.Request.Context with { Preset = cell.Preset! } }, ct);
                (runId, traceId, usd) = (result.TaskId, result.TraceId, result.Usage.Usd);
                var facts = cell.Kind == "worker" && result.Status == ResultStatus.Completed && item.Expected.Facts is { Count: > 0 } checklist
                    ? await FactJudge.JudgeAsync(runtime, Orchestrator.ParseModel(intakeModel), result, checklist, item.Expected.Wrong ?? [], ct)
                    : null;
                scores = cell.Kind == "draft" ? Scoring.Draft(result, item.Expected.Draft!) : Scoring.Worker(result, item.Expected.ReferenceFiles ?? [], item.Expected.ReferenceClaims, facts);
                limited = result.Error?.Code == ErrorCode.RateLimited ? result.Summary : null;
                // A run refused before any model call (a checkout the preset refuses, a bad pin) scores 0 on both arms
                // and passes as no change: stop the gate rather than score a cell without evidence.
                if (limited is null && result.Status == ResultStatus.Failed && result.Usage is { Input: 0, Output: 0, Usd: 0 })
                    throw new InvalidOperationException($"{cell.Name} {item.Id} ({runName}): failed before any model call: {result.Summary}");
            }
            if (limited is null)
                break;
            if (attempt == RateLimitRetries.Length)
                throw new InvalidOperationException($"{cell.Name} {item.Id} ({runName}): still rate limited after {attempt + 1} attempts: {limited}");
            output.WriteLine(FormattableString.Invariant($"{cell.Name}  {item.Id,-34} {runName,-32} rate limited, retrying in {RateLimitRetries[attempt].TotalSeconds:0} s  {runId}"));
            await Task.Delay(RateLimitRetries[attempt], ct);
        }
        foreach (var (key, value) in scores)
            await log.AppendAsync(new ScoreRecord(runId, key, value, DateTimeOffset.UtcNow, $"eval:{runName}"), ct);
        if (langfuse is not null)
        {
            flush();
            await langfuse.LinkAsync(runName, item.Id, traceId, ct);
            foreach (var (key, value) in scores)
                await langfuse.ScoreAsync(traceId, key, value, runName, ct);
        }
        output.WriteLine(FormattableString.Invariant($"{cell.Name}  {item.Id,-34} {runName,-32} quality {scores["quality"]:0.00}  usd {usd:0.000000}  {runId}"));
        return (scores["quality"], usd);
    }

    /// <summary>Intake alone: one generate call under the arm's intake prompt, scored on the action it chooses.</summary>
    private async Task<(string RunId, string TraceId, IReadOnlyDictionary<string, double> Scores, string? RateLimited)> Intake(EvalItem item, string root, CancellationToken ct)
    {
        var runId = Orchestrator.NewRunId();
        using var span = Telemetry.Source.StartActivity("chargehand.eval.intake");
        var traceId = span?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
        var (_, kind) = Orchestrator.AnswerKind(Preset.Load(Path.Combine(root, "presets"), item.Request.Context.Preset));
        var block = new PromptRegistry(Path.Combine(root, "prompts")).Get("intake/task-spec");
        try
        {
            var outcome = await new GenerateIntake(runtime, Orchestrator.ParseModel(intakeModel), block, runId, kind.Checkout).RunAsync(item.Request, ct);
            return (runId, traceId, Scoring.Intake(outcome.Spec, item.Expected.Action!), null);
        }
        catch (Exception e) when (ChargehandException.ErrorOf(e).Code == ErrorCode.RateLimited)
        {
            return (runId, traceId, new Dictionary<string, double> { ["quality"] = 0 }, e.Message);
        }
        catch (ChargehandException e) when (e.Code == ErrorCode.IntakeFailed)
        {
            return (runId, traceId, new Dictionary<string, double> { ["quality"] = 0 }, null); // no valid Task Spec after the retry
        }
    }

    /// <summary>Waits before each retry of a rate-limited arm; the gateway's parallel-request limit clears within seconds.</summary>
    internal static TimeSpan[] RateLimitRetries { get; set; } = [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private static IReadOnlyList<string> Files(IEnumerable<Evidence> evidence) =>
        [.. evidence.Where(e => e.Kind == EvidenceKind.File).Select(e => e.Locator.Split(':')[0]).Distinct().Order(StringComparer.Ordinal)];
}
