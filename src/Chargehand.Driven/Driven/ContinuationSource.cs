using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.RunLog;

namespace Chargehand.Driven;

/// <summary>Resolves a task that continues an earlier batch's task (ADR 0045) from the run log: the earlier goal, the questions its session asked and the work it kept. The earlier task has to
/// have ended <c>needs_input</c>, or to carry a <c>kept-work</c> artifact (a partial: its branch bundle is in an output volume). Nothing here reads a volume; the task runner does when it starts.</summary>
public sealed class ContinuationSource(IRunLog log)
{
    /// <summary>How long an ended task's output volume (its bundle and handoff) is kept for a continuation: the sweep at a server start leaves it alone until then.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    public const string KeptWorkKind = "kept-work";

    /// <exception cref="ChargehandException"><see cref="ErrorCode.InvalidRequest"/>: the batch or task is not in the run log, or did not end in a state that can continue.</exception>
    public async Task<ResolvedTask> ResolveAsync(DrivenTask task, CancellationToken ct)
    {
        var from = task.ContinueFrom!;
        ChargehandException Refuse(string why) => new(ErrorCode.InvalidRequest, $"task '{task.Id}' continues {from.RunId}/{from.TaskId}, which {why}",
            "Name a task of a finished driven batch in this server's run log that ended needs_input or stopped with its work kept (a partial pull request).");

        var content = (await log.ReadAsync(from.RunId, ct)).Run?.Result.Artifacts.FirstOrDefault(a => a.Kind == "driven-batch")?.Content
            ?? throw Refuse("is not a finished driven batch in the run log");
        using var batch = JsonDocument.Parse(content);
        var row = batch.RootElement.GetProperty("tasks").EnumerateArray().FirstOrDefault(t => t.GetProperty("id").GetString() == from.TaskId);
        if (row.ValueKind != JsonValueKind.Object)
            throw Refuse("has no such task");
        var status = row.GetProperty("status").GetString() ?? "";
        var lastRun = row.GetProperty("run_id").GetString();
        var entry = lastRun is null ? null : await log.ReadAsync(lastRun, ct);
        var kept = KeptWorkOf(entry?.Run?.Result);
        var asked = status == "needs_input";
        if (!asked && kept is null)
            throw Refuse($"ended {status.Replace('_', ' ')} and kept no work; only a task that ended needs_input, or a partial one, can continue");
        if (entry is not { Start: { } start, Run: { } run })
            throw Refuse("has no record of its own in the run log");
        if (asked && task.Answers is not { Count: > 0 })
            throw new ChargehandException(ErrorCode.InvalidRequest, $"task '{task.Id}' continues {from.RunId}/{from.TaskId}, which ended needs_input: give answers",
                "Add answers to the task: the earlier session's questions are in that task's result.");

        var goal = new StringBuilder(start.Request.Text.TrimEnd());
        if (asked)
            goal.Append("\n\nAn earlier session of this task stopped to ask:\n").AppendJoin('\n', run.Result.OpenQuestions.Select(q => $"- {q}"));
        else
            goal.Append("\n\nAn earlier session of this task was stopped before it finished.");
        if (task.Answers is { Count: > 0 } answers)
            goal.Append("\n\nThe answers from the person who sent the task:\n").AppendJoin('\n', answers.Select(a => $"- {a}"));
        goal.Append(kept is null
            ? "\n\nIt made no commits: start from the base commit."
            : $"\n\nIts work so far is on this branch, {kept.Commits} commit(s) past {kept.Base} (the last may be 'WIP: ...'). Read `git log` and `git diff {kept.Base}..HEAD` first, keep what works, "
                + "do not redo finished work, finish the task, run the tests, and end with the same report block.");
        return new ResolvedTask(task.Id, null, goal.ToString(), task.MaxTurns, task.MaxMinutes, task.Size, task.BudgetUsd, new ContinuedFrom(lastRun!, kept));
    }

    /// <summary>The <c>kept-work</c> artifact of a task's result; null when it has none or cannot be read.</summary>
    public static KeptWork? KeptWorkOf(ResultContract? result)
    {
        if (result?.Artifacts.FirstOrDefault(a => a.Kind == KeptWorkKind)?.Content is not { } content)
            return null;
        try
        {
            return JsonSerializer.Deserialize<KeptWork>(content, ContractJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
