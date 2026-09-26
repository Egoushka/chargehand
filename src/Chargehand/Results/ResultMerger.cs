using Chargehand.Contracts;
using Chargehand.Plans;

namespace Chargehand.Results;

/// <summary>
/// Deterministic merge of a split run's node contracts into the run's result/v1 (ADR 0017): no extra model call.
/// Evidence ids are prefixed with the node id; each node's evidence already resolved inside that node.
/// </summary>
public static class ResultMerger
{
    public const string NodeId = "merge";

    public static ResultContract Merge(string runId, string traceId, PromptChain chain, IReadOnlyList<NodeOutcome> parts)
    {
        var done = parts.Where(p => p.Contract.Status == ResultStatus.Completed).ToList();
        var summary = string.Join("\n", done.Select(p => $"[{p.Node.Id}] {p.Node.Goal}: {p.Contract.Summary}"));
        var questions = parts.Except(done).Select(p => $"[{p.Node.Id}] not answered: {p.Node.Goal} ({p.Contract.Summary})")
            .Concat(parts.SelectMany(p => p.Contract.OpenQuestions.Select(q => $"[{p.Node.Id}] {q}")))
            .ToList();
        var usage = parts.Select(p => p.Contract.Usage).Aggregate(new Usage(0, 0, 0, 0, 0), (a, u) =>
            new Usage(a.Input + u.Input, a.Output + u.Output, a.CacheRead + u.CacheRead, a.CacheWrite + u.CacheWrite, a.Usd + u.Usd));
        return new ResultContract(
            "result/v1", runId, NodeId, traceId, chain,
            done.Count > 0 ? ResultStatus.Completed : ResultStatus.Failed,
            summary.Length > 0 ? summary : "No subtask completed.",
            done.SelectMany(p => p.Contract.Claims.Select(c => c with { Evidence = c.Evidence.Select(e => $"{p.Node.Id}.{e}").ToList() })).ToList(),
            done.SelectMany(p => p.Contract.Evidence.Select(e => e with { Id = $"{p.Node.Id}.{e.Id}" })).ToList(),
            done.SelectMany(p => p.Contract.Artifacts).ToList(),
            questions,
            done.Count > 0 ? Math.Round(done.Average(p => p.Contract.Confidence), 3) : 0,
            usage);
    }
}
