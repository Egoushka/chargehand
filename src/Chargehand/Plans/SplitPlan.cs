using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Plans;

/// <summary>One node of a run's task graph. Goal is null for the single node of an answer run.</summary>
public sealed record PlanNode(string Id, string? Goal, IReadOnlyList<string> DependsOn);

/// <summary>Turns a split Task Spec into a validated task graph (ADR 0005).</summary>
public static class SplitPlan
{
    public const int MaxSubtasks = 4;

    private sealed record Subtask(string Id, string Goal, bool ReadOnly, IReadOnlyList<string>? DependsOn);

    private sealed record Detail(IReadOnlyList<Subtask> Subtasks);

    /// <summary>The nodes in dependency order, or why the split cannot run (the caller then runs one answer node).</summary>
    public static (IReadOnlyList<PlanNode>? Nodes, string? Reason) From(TaskSpec spec)
    {
        var subtasks = spec.ActionDetail?.Deserialize<Detail>(ContractJson.Options)?.Subtasks ?? [];
        if (subtasks.Count is < 2 or > MaxSubtasks)
            return (null, $"split needs 2 to {MaxSubtasks} subtasks, got {subtasks.Count}");
        // Writing subtasks need one worktree each (ADR 0015), which v1 does not build.
        if (subtasks.FirstOrDefault(s => !s.ReadOnly) is { } writer)
            return (null, $"subtask {writer.Id} is not read-only");
        var ids = subtasks.Select(s => s.Id).ToList();
        if (ids.Distinct().Count() != ids.Count)
            return (null, "duplicate subtask ids");
        if (subtasks.SelectMany(s => s.DependsOn ?? []).FirstOrDefault(d => !ids.Contains(d)) is { } unknown)
            return (null, $"unknown dependency {unknown}");

        // Kahn's algorithm, stable in the order intake listed the subtasks.
        var ordered = new List<PlanNode>();
        var pending = subtasks.ToList();
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(s => (s.DependsOn ?? []).All(d => ordered.Any(o => o.Id == d)));
            if (ready is null)
                return (null, "dependency cycle");
            ordered.Add(new PlanNode(ready.Id, ready.Goal, ready.DependsOn ?? []));
            pending.Remove(ready);
        }
        return (ordered, null);
    }
}
