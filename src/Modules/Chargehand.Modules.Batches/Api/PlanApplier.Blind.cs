using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

public static partial class PlanApplier
{
    /// <summary>Adds the blind sibling (issue 789) of every task that opted in: <c>&lt;id&gt;.blind</c>, the same repository and base commit, the issue text as its goal and no <c>depends_on</c>,
    /// so it starts with the batch and sees neither the plan nor the implementer's work. Adding it twice changes nothing.</summary>
    public static IReadOnlyList<ResolvedTask> AddBlind(IReadOnlyList<ResolvedTask> tasks, IReadOnlyDictionary<string, string>? goals = null)
    {
        var present = tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        List<ResolvedTask> all = [];
        foreach (var task in tasks)
        {
            if (task.BlindTests && !present.Contains(BlindIds.Of(task.Id)))
                all.Add(Sibling(task, goals is not null && goals.TryGetValue(task.Id, out var goal) ? goal : task.Goal));
            all.Add(task);
        }
        return all;
    }

    private static ResolvedTask Sibling(ResolvedTask task, string goal) =>
        new(BlindIds.Of(task.Id), null, goal, BlindFor: task.Id);

    /// <summary>The blind siblings a batch will add, so the cap counts them before a plan splits anything.</summary>
    private static int BlindCount(IReadOnlyList<ResolvedTask> tasks) =>
        tasks.Count(t => t.BlindTests && !tasks.Any(o => o.Id == BlindIds.Of(t.Id)));
}
