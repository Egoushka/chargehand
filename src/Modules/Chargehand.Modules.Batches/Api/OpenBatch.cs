using System.Text.Json.Serialization;
using Chargehand.Contracts;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>An unfinished driven batch as the journal and the run log hold it (ADR 0061, amendments of 2026-10-14): what a restarted server needs to carry it on, and what the console shows of it.
/// The request, start, trace and account are the batch's start record's; the tasks are the <c>plan</c> step's, folded with their events; the tasks that did not resolve are the <c>resolve</c>
/// step's. The batch's token key is in a file of its own (<see cref="TokenKeyFile"/>), never here.</summary>
/// <param name="Tasks">The tasks as the journal folds them, in the batch's order.</param>
/// <param name="Unresolved">Tasks that did not resolve to a goal: final from the start.</param>
/// <param name="Plan">What the planning step did (ADR 0049), so a resumed batch reports it and does not plan again; null: no planning ran.</param>
/// <param name="Actor">The account that started the batch (ADR 0050); a resumed batch runs as it.</param>
public sealed record OpenBatch(string RunId, DateTimeOffset Started, string TraceId, RunRequest Request, IReadOnlyList<TaskEntry> Tasks, IReadOnlyList<TaskOutcome> Unresolved,
    BatchPlan? Plan = null, string? Actor = null)
{
    /// <summary>Every run id the batch's resources can carry: the batch's own and its tasks', every session of a task included (an earlier session's output volume holds the bundle a restart may need).</summary>
    public IEnumerable<string> RunIds => Tasks.Select(t => t.RunId).OfType<string>().Concat(Tasks.SelectMany(t => t.Runs ?? [])).Prepend(RunId);
}

/// <summary>The result of a batch's <c>resolve</c> step: the tasks as they resolved, before any planning, and those that did not.</summary>
public sealed record BatchResolved(IReadOnlyList<TaskEntry> Tasks, IReadOnlyList<TaskOutcome> Unresolved)
{
    /// <summary>A resolved task as the steps keep it: what planning may change, nothing of the request's per-task limits, which a resumed batch reads from the request again.</summary>
    public static TaskEntry Entry(ResolvedTask t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new(t.Id, t.Ref, t.Goal, From: t.From, DependsOn: t.DependsOn, Size: t.Size, SplitFrom: t.SplitFrom, SizeFrom: t.SizeFrom, Title: t.Title);
    }
}

/// <summary>The result of a batch's <c>plan</c> step: the tasks as planned, with nothing of a run (<see cref="TaskJournal.Planned"/>), and what the planning step did; null: it did not run.</summary>
public sealed record BatchPlanned(IReadOnlyList<TaskEntry> Tasks, BatchPlan? Plan);

/// <summary>The steps' results without reflection (ADR 0060): snake_case, nulls left out, enums as snake_case strings, as the snapshot they replace wrote them.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(SnakeCaseEnumConverter<TaskPhase>), typeof(SnakeCaseEnumConverter<TaskState>), typeof(SnakeCaseEnumConverter<SessionStatus>)])]
[JsonSerializable(typeof(BatchResolved))]
[JsonSerializable(typeof(BatchPlanned))]
[JsonSerializable(typeof(LegacySnapshot))]
internal sealed partial class BatchStepsJsonContext : JsonSerializerContext;
