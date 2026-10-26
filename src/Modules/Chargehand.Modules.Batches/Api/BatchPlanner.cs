using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>A shared number the plan gave one task so that no other task of the batch takes it (ADR 0049): the next migration number, the next ADR number.</summary>
public sealed record PlanAssignment(string What, string Value);

/// <param name="Size">S, M or L, and where it came from: <paramref name="SizeFrom"/> is <c>caller</c>, <c>plan</c> or null (the default, M, applies).</param>
/// <param name="DependsOn">The task ids the task waits for in the batch as it runs: the caller's, plus the previous link of a chain the plan made.</param>
/// <param name="SplitFrom">The id of the larger task the plan split this one out of; the chain's last link keeps that id.</param>
/// <param name="Kept">Why the plan left the task whole when it proposed a split.</param>
/// <param name="Forecast">What the task is expected to cost before it starts (CHARGEHAND-438); null when the host did not forecast it.</param>
public sealed record PlanEntry(string Id, string? Size, string? SizeFrom, IReadOnlyList<string> DependsOn, string? SplitFrom = null, IReadOnlyList<PlanAssignment>? Assigned = null, string? Kept = null,
    TaskForecast? Forecast = null);

/// <summary>What the planning step did to a batch (ADR 0049), recorded as the <c>plan</c> artifact of the batch result and as a <c>plan</c> run event. The plan is advice that the caller's own
/// <c>size</c> and <c>depends_on</c> override; it is read from the repository at the base commit and says what it read. Whether the tasks conflict in ways other than the numbers it assigned
/// (two migrations that touch one table, two edits to one function) is not checked, and <see cref="NotChecked"/> says so.</summary>
/// <param name="Source"><c>request</c> (the request's <c>driven.plan</c>) or <c>profile</c> (<c>driven.planning.enabled</c>).</param>
/// <param name="RunId">The planning run in the run log, a child of the batch; null when none ran.</param>
/// <param name="Status"><c>applied</c> or <c>skipped</c>; a skipped plan leaves every task as the caller sent it.</param>
/// <param name="Reason">Why it was skipped.</param>
/// <param name="Checked">What the planner says it read, one line each.</param>
/// <param name="Dropped">Proposals the plan did not use, each with the reason.</param>
public sealed record BatchPlan(string Source, string? RunId, string Status, string? Reason, IReadOnlyList<string> Checked, string? Note, IReadOnlyList<PlanEntry> Tasks, IReadOnlyList<string> Dropped,
    string NotChecked = BatchPlan.NotCheckedText)
{
    public const string NotCheckedText = "whether the tasks conflict in ways other than the numbers assigned";

    public bool Applied => Status == "applied";

    /// <summary>One line for the <c>plan</c> run event.</summary>
    public string Describe()
    {
        if (!Applied)
            return $"plan skipped: {Reason}";
        var parts = Tasks.Select(t => $"{t.Id} {t.Size ?? "M (default)"}{(t.SplitFrom is null ? "" : $" (split from {t.SplitFrom})")}{(t.Assigned is { Count: > 0 } a ? $" [{string.Join(", ", a.Select(x => $"{x.What} {x.Value}"))}]" : "")}");
        return $"plan: {string.Join("; ", parts)}{(Checked.Count > 0 ? $". Checked: {string.Join("; ", Checked)}" : ". Checked nothing")}.";
    }
}

/// <summary>A part of a task the planner would split, in the order the parts run.</summary>
public sealed record ProposedPart(string? Goal, string? Size);

public sealed record ProposedNumber(string? What, string? Value, int? Part);

public sealed record ProposedTask(string? Id, string? Size, IReadOnlyList<ProposedPart>? Split, IReadOnlyList<ProposedNumber>? Assign);

/// <summary>What the planner answers, before <see cref="PlanApplier"/> decides how much of it to use.</summary>
public sealed record PlanProposal(IReadOnlyList<ProposedTask> Tasks, IReadOnlyList<string> Checked, string? Note, string? RunId = null);

/// <param name="Request">The batch's request: the repository and commit the planner reads.</param>
/// <param name="Tasks">The batch's tasks as resolved: goals and the sizes the caller gave.</param>
/// <param name="RunId">The batch's run id; the planning run is recorded as its child.</param>
/// <param name="Replan">One task that stalled with no commits (issue #432), asked to be split regardless of its size: the pre-batch question is not asked.</param>
public sealed record PlanInput(RunRequest Request, IReadOnlyList<ResolvedTask> Tasks, string RunId, bool Replan = false);

public interface IBatchPlanner
{
    /// <exception cref="PlanException">The planner ran and its answer cannot be used.</exception>
    Task<PlanProposal> ProposeAsync(PlanInput input, CancellationToken ct);
}

public sealed class PlanException(string message) : Exception(message);

/// <summary>Turns a proposal into the batch that runs (ADR 0049). Pure: no model, no repository, so what a plan may change is settled here and tested here.
/// <list type="bullet">
/// <item>A task the caller gave a <c>size</c> keeps it and is never split; so does one with <c>budget_usd</c>, <c>max_turns</c> or <c>max_minutes</c> (the caller sized it by hand), and one that continues another.</item>
/// <item>A task with no size gets the plan's, S, M or L. One the plan sizes L and gives 2 to 4 parts becomes a chain: <c>t-1 → t-2 → … → t</c>, each link depending on the one before and the first on the
/// task's own <c>depends_on</c>. The last link keeps the id, the tracker reference and every later task's dependency on it, so the caller's graph still holds.</item>
/// <item>A shared number goes into the goal of the task it was given to. The same value given to two tasks stays with the first.</item>
/// </list></summary>
public static partial class PlanApplier
{
    public const int MaxParts = 4;

    /// <summary>The most tasks a batch has (request/v1).</summary>
    public const int MaxTasks = 20;

    private const int MaxIdLength = 64;

    private static readonly string[] Sizes = ["S", "M", "L"];

    public static (IReadOnlyList<ResolvedTask> Tasks, BatchPlan Plan) Apply(IReadOnlyList<ResolvedTask> tasks, PlanProposal proposal, string source)
    {
        var batch = new Applying(tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal), tasks.Count);
        foreach (var task in tasks)
            ApplyTo(task, proposal.Tasks.FirstOrDefault(p => p.Id == task.Id), batch);
        return (batch.Result, new BatchPlan(source, proposal.RunId, "applied", null, proposal.Checked, proposal.Note, batch.Entries, batch.Dropped));
    }

    /// <summary>The batch as the plan is applied to it, task by task: the ids taken, the task count so far and the numbers given.</summary>
    private sealed class Applying(HashSet<string> ids, int total)
    {
        public HashSet<string> Ids { get; } = ids;

        public int Total { get; set; } = total;

        public List<ResolvedTask> Result { get; } = [];

        public List<PlanEntry> Entries { get; } = [];

        public List<string> Dropped { get; } = [];

        public Dictionary<string, string> Given { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static void ApplyTo(ResolvedTask task, ProposedTask? proposed, Applying batch)
    {
        var planSize = SizeOf(proposed?.Size);
        var size = task.Size ?? planSize;
        var sizeFrom = task.Size is not null ? "caller" : planSize is not null ? "plan" : null;

        var parts = ValidParts(proposed?.Split);
        var kept = Kept(task, proposed, planSize, parts, batch);
        var chain = parts is not null && kept is null;
        if (kept is not null)
            batch.Dropped.Add($"{task.Id}: not split ({kept})");

        var numbers = Numbers(task.Id, proposed?.Assign, chain ? parts!.Count : 1, batch.Given, batch.Dropped);
        if (!chain)
        {
            var goal = AppendNumbers(task.Goal, numbers.GetValueOrDefault(1));
            batch.Result.Add(task with { Goal = goal, Size = size, SizeFrom = sizeFrom == "plan" ? "plan" : null });
            batch.Entries.Add(new PlanEntry(task.Id, size, sizeFrom, task.DependsOn ?? [], null, numbers.GetValueOrDefault(1), kept));
            return;
        }
        Chain(task, parts!, numbers, batch);
    }

    private static string? Kept(ResolvedTask task, ProposedTask? proposed, string? planSize, IReadOnlyList<ProposedPart>? parts, Applying batch)
    {
        if (proposed?.Split is { Count: > 0 } && parts is null)
            return $"the plan's split needs 2 to {MaxParts} parts with a goal each";
        return parts is not null ? WhyNotSplit(task, planSize, parts.Count, batch.Ids, batch.Total) : null;
    }

    private static void Chain(ResolvedTask task, IReadOnlyList<ProposedPart> parts, Dictionary<int, IReadOnlyList<PlanAssignment>> numbers, Applying batch)
    {
        var k = parts.Count;
        batch.Total += k - 1;
        for (var j = 0; j < k; j++)
        {
            var id = j == k - 1 ? task.Id : $"{task.Id}-{j + 1}";
            batch.Ids.Add(id);
            var part = parts[j];
            var goal = AppendNumbers(PartGoal(task.Goal, part.Goal!.Trim(), j + 1, k), numbers.GetValueOrDefault(j + 1));
            IReadOnlyList<string> dependsOn = j == 0 ? task.DependsOn ?? [] : [$"{task.Id}-{j}"];
            var partSize = SizeOf(part.Size) ?? "M";
            batch.Result.Add(new ResolvedTask(id, j == k - 1 ? task.Ref : null, goal, Size: partSize, DependsOn: dependsOn.Count > 0 ? dependsOn : null, SplitFrom: task.Id, SizeFrom: "plan",
                Acceptance: j == k - 1 ? task.Acceptance : null));
            batch.Entries.Add(new PlanEntry(id, partSize, "plan", dependsOn, task.Id, numbers.GetValueOrDefault(j + 1)));
        }
    }

    /// <summary>Splits a task that stalled with no commits into a chain of smaller tasks, each depending on the one before (issue #432): the same chain shape as <see cref="Apply"/>'s own split,
    /// but forced by the stall rather than chosen from the task's size, and kept to the task already in flight, so the first link keeps its own dependencies and upstream work (it already ran) and
    /// only the links after it depend on the one before. The last link keeps the task's id, its ref and its acceptance checks.</summary>
    /// <exception cref="PlanException">The proposal has no usable split for the task.</exception>
    public static IReadOnlyList<ResolvedTask> Replan(ResolvedTask task, IReadOnlyList<ProposedPart>? split)
    {
        if (ValidParts(split) is not { } parts)
            throw new PlanException($"the replan needs 2 to {MaxParts} parts with a goal each");
        var k = parts.Count;
        List<ResolvedTask> chain = [];
        for (var j = 0; j < k; j++)
        {
            var id = j == k - 1 ? task.Id : $"{task.Id}-replan-{j + 1}";
            var goal = PartGoal(task.Goal, parts[j].Goal!.Trim(), j + 1, k);
            var dependsOn = j == 0 ? task.DependsOn : [$"{task.Id}-replan-{j}"];
            chain.Add(new ResolvedTask(id, j == k - 1 ? task.Ref : null, goal, Size: SizeOf(parts[j].Size) ?? "M", DependsOn: dependsOn, Upstream: j == 0 ? task.Upstream : null,
                SplitFrom: task.Id, SizeFrom: "plan", Acceptance: j == k - 1 ? task.Acceptance : null));
        }
        return chain;
    }

    /// <summary>The plan of a batch whose planner failed or was missing: nothing changes.</summary>
    public static BatchPlan Skipped(string source, string reason, string? runId, IReadOnlyList<ResolvedTask> tasks) =>
        new(source, runId, "skipped", reason, [], null, [.. tasks.Select(t => new PlanEntry(t.Id, t.Size, t.Size is null ? null : "caller", t.DependsOn ?? []))], []);

    private static string? SizeOf(string? size) => size?.Trim().ToUpperInvariant() is { } s && Sizes.Contains(s) ? s : null;

    private static IReadOnlyList<ProposedPart>? ValidParts(IReadOnlyList<ProposedPart>? split) =>
        split is { Count: >= 2 and <= MaxParts } parts && parts.All(p => !string.IsNullOrWhiteSpace(p.Goal) && p.Goal.Length <= MaxGoalLength) ? parts : null;

    private const int MaxGoalLength = 8000;

    private static string? WhyNotSplit(ResolvedTask task, string? planSize, int parts, HashSet<string> ids, int total)
    {
        if (task.Size is not null)
            return "the caller set its size";
        if (task.BudgetUsd is not null || task.MaxTurns is not null || task.MaxMinutes is not null)
            return "the caller set its budget or limits";
        if (task.From is not null)
            return "it continues an earlier task";
        if (planSize != "L")
            return "the plan did not size it L";
        if (total + parts - 1 > MaxTasks)
            return $"a batch has at most {MaxTasks} tasks";
        if (task.Id.Length + 2 > MaxIdLength || Enumerable.Range(1, parts - 1).Any(j => ids.Contains($"{task.Id}-{j}")))
            return "the ids for its parts are not free";
        return null;
    }

    /// <summary>The numbers given to a task by part (1-based), each checked and none given twice in the batch.</summary>
    private static Dictionary<int, IReadOnlyList<PlanAssignment>> Numbers(string taskId, IReadOnlyList<ProposedNumber>? assign, int parts, Dictionary<string, string> given, List<string> dropped)
    {
        Dictionary<int, List<PlanAssignment>> byPart = [];
        foreach (var n in assign ?? [])
        {
            var (what, value) = (n.What?.Trim(), n.Value?.Trim());
            if (what is not { Length: > 0 } || value is not { Length: > 0 } || !Plain().IsMatch(what) || !Plain().IsMatch(value))
            {
                dropped.Add($"{taskId}: a number was left out (it must name what and a value of plain text, at most 80 characters)");
                continue;
            }
            var key = $"{what}|{value}";
            if (given.TryGetValue(key, out var owner))
            {
                dropped.Add($"{taskId}: {what} {value} left out; it was already given to {owner}");
                continue;
            }
            given[key] = taskId;
            var part = Math.Clamp(n.Part ?? 1, 1, parts);
            if (!byPart.TryGetValue(part, out var list))
                byPart[part] = list = [];
            list.Add(new PlanAssignment(what, value));
        }
        return byPart.ToDictionary(p => p.Key, p => (IReadOnlyList<PlanAssignment>)p.Value);
    }

    private static string AppendNumbers(string goal, IReadOnlyList<PlanAssignment>? numbers)
    {
        if (numbers is not { Count: > 0 })
            return goal;
        var text = new StringBuilder(goal.TrimEnd());
        foreach (var n in numbers)
            text.Append(CultureInfo.InvariantCulture, $"\n\nPlan: use {n.What} {n.Value} for this task. Other tasks of this batch were given different ones; do not take the next free one yourself.");
        return text.ToString();
    }

    private static string PartGoal(string whole, string part, int number, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"{part}\n\nThis is part {number} of {count} of a larger task, run in order on one branch; the work of the earlier parts is already on your branch, so read `git log` first and do only this part. "
            + $"The whole task, for context only:\n\n{(whole.Length > 1500 ? whole[..1500].TrimEnd() + " [cut]" : whole)}");

    [GeneratedRegex(@"^[\w .:/#-]{1,80}$")]
    private static partial Regex Plain();
}

/// <summary>Asks the orchestrator for a plan (ADR 0049): one run on a read-only preset (<c>cheap</c> by default) through the same pipeline as any other request, so the planner needs no model
/// client, credential or sandbox of its own. The answer is a JSON object in the result's summary; a result that is not <c>completed</c> or has no such object is a <see cref="PlanException"/>, and the batch
/// then runs as the caller sent it. The prompt is built here rather than in <c>prompts/</c>, so no registry block changes and Prompt CI has nothing new to gate.</summary>
public sealed class ModelPlanner(PlanRunner run, string preset) : IBatchPlanner
{
    public async Task<PlanProposal> ProposeAsync(PlanInput input, CancellationToken ct)
    {
        var runId = RunIds.New();
        var request = new RunRequest("request/v1", input.Replan ? ReplanPrompt(input.Tasks[0]) : PlanPrompt(input.Tasks), new RequestContext(false, preset, Repository: input.Request.Context.Repository));
        var result = await run(request, runId, input.RunId, ct);
        if (result.Status != ResultStatus.Completed)
            throw new PlanException($"the planning run ended {JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Status.ToString())}: {result.Error?.Message ?? result.Summary}");
        return Parse(result.Summary) with { RunId = runId };
    }

    /// <summary>The first <c>{</c> to the last <c>}</c> of the text: a model may fence the object or add a line around it.</summary>
    public static PlanProposal Parse(string summary)
    {
        var start = summary.IndexOf('{', StringComparison.Ordinal);
        var end = summary.LastIndexOf('}');
        if (start < 0 || end < start)
            throw new PlanException("the planning run printed no JSON plan");
        PlanWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize(summary[start..(end + 1)], BatchesJsonContext.Wire.PlanWire);
        }
        catch (JsonException e)
        {
            throw new PlanException($"the plan is not valid JSON: {e.Message}");
        }
        return new PlanProposal(wire?.Tasks ?? [], wire?.Checked ?? [], wire?.Note);
    }

    private const int GoalInPrompt = 3000;

    public static string PlanPrompt(IReadOnlyList<ResolvedTask> tasks)
    {
        var text = new StringBuilder();
        text.Append("Answer this question about the repository at the pinned commit. It is one question with one answer: do not split it, do not ask for anything, and do not change any file.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"{tasks.Count} tasks will be run by coding sessions, each in its own copy of the repository, in parallel unless one waits for another. Before they start, plan the batch. For each task below:\n\n");
        text.Append("1. Size. S: a few files and under about 100 changed lines. M: one module or feature. L: several modules, or more than one change a reviewer would want separately. A task marked \"size set by the caller\" keeps that size: do not resize it or split it.\n");
        text.Append("2. Split. Only for a task you size L: give 2 to 4 parts, in the order they must be done, each a complete goal that one session can finish and that names what the earlier parts did. Never split an S or M task.\n");
        text.Append("3. Numbers. If two or more tasks would each create something that takes the next number in a sequence (database migrations, ADR files, numbered changelog entries, ticket-numbered files), read the repository, find the scheme and the latest number, and give each task its own number. If you cannot tell the scheme, assign nothing and say so in note.\n\n");
        text.Append("Read the repository with your tools to decide; do not guess a number. Name what you looked at in `checked`, one short line each (for example \"listed db/migrations: latest is 0041\"). ");
        text.Append("Whether the tasks conflict in some other way is not part of this question.\n\n");
        text.Append("Put the plan in `summary` as exactly one JSON object and nothing else, shaped like this (omit `split` and `assign` when empty):\n\n");
        text.Append("""{"checked": ["what you read"], "tasks": [{"id": "t1", "size": "S", "split": [{"goal": "first part", "size": "M"}, {"goal": "second part", "size": "M"}], "assign": [{"what": "migration number", "value": "0042", "part": 1}]}], "note": "anything else the caller should know"}""");
        text.Append("\n\nThe tasks:\n");
        foreach (var task in tasks)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n- id: {task.Id}{(task.Size is { } size ? $" (size set by the caller: {size})" : "")}\n  goal: ");
            var goal = task.Goal.Length > GoalInPrompt ? task.Goal[..GoalInPrompt].TrimEnd() + " [cut]" : task.Goal;
            text.Append(goal.ReplaceLineEndings("\n").Replace("\n", "\n    ", StringComparison.Ordinal)).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>Asked after a task stalled with no commits (issue #432): unlike <see cref="PlanPrompt"/>, the split is wanted regardless of size, since continuing the same plan did not work.</summary>
    public static string ReplanPrompt(ResolvedTask task)
    {
        var text = new StringBuilder();
        text.Append("Answer this question about the repository at the pinned commit. It is one question with one answer: do not split it, do not ask for anything, and do not change any file.\n\n");
        text.Append("A coding session given the task below stopped without making a single commit. Split it into 2 to 4 smaller tasks, in the order they must be done: each a complete goal one "
            + "session can finish, naming what the earlier parts left in place (the next session starts from the branch the one before it left).\n\n");
        text.Append("Read the repository with your tools to judge the split; do not guess what exists. Name what you looked at in `checked`, one short line each.\n\n");
        text.Append("Put the plan in `summary` as exactly one JSON object and nothing else, shaped like this:\n\n");
        text.Append("""{"checked": ["what you read"], "tasks": [{"id": "the task's id", "split": [{"goal": "first part", "size": "M"}, {"goal": "second part", "size": "M"}]}]}""");
        text.Append(CultureInfo.InvariantCulture, $"\n\nUse exactly this id: {task.Id}\n\nThe task's goal:\n");
        var goal = task.Goal.Length > GoalInPrompt ? task.Goal[..GoalInPrompt].TrimEnd() + " [cut]" : task.Goal;
        text.Append(goal.ReplaceLineEndings("\n")).Append('\n');
        return text.ToString();
    }
}

/// <summary>The planner's answer as it reads from the summary.</summary>
internal sealed record PlanWire(IReadOnlyList<ProposedTask>? Tasks, IReadOnlyList<string>? Checked, string? Note);

/// <summary>Runs a request through the orchestrator as a child of the batch run; the CLI supplies it from its <c>Orchestrator</c>.</summary>
public delegate Task<ResultContract> PlanRunner(RunRequest request, string runId, string parentRunId, CancellationToken ct);
