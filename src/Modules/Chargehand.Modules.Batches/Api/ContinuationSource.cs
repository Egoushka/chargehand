using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>Resolves a task that continues an earlier batch's task (ADR 0045) from the run log: the earlier goal, the questions its session asked and the work it kept. The earlier task has to
/// have ended <c>needs_input</c>, or to carry a <c>kept-work</c> artifact (a partial: its branch bundle is in an output volume), or to have <c>completed</c> with its branch pushed (a review round, ADR 0051:
/// the branch is fetched from the remote). Nothing here reads a volume or the remote; the task runner does when it starts.</summary>
public sealed class ContinuationSource(IRunLog log)
{
    /// <summary>How long an ended task's output volume (its bundle and handoff) is kept for a continuation: the sweep at a server start leaves it alone until then.</summary>
    public static readonly TimeSpan KeptFor = TaskRunner.KeptFor;

    public const string KeptWorkKind = "kept-work";

    /// <exception cref="ChargehandException"><see cref="ErrorCode.InvalidRequest"/>: the batch or task is not in the run log, or did not end in a state that can continue.</exception>
    public async Task<ResolvedTask> ResolveAsync(DrivenTask task, CancellationToken ct)
    {
        var from = task.ContinueFrom!;
        var content = (await log.ReadAsync(from.RunId, ct)).Run?.Result.Artifacts.FirstOrDefault(a => a.Kind == "driven-batch")?.Content
            ?? throw Refuse(task, "is not a finished driven batch in the run log");
        using var batch = JsonDocument.Parse(content);
        var row = batch.RootElement.GetProperty("tasks").EnumerateArray().FirstOrDefault(t => t.GetProperty("id").GetString() == from.TaskId);
        if (row.ValueKind != JsonValueKind.Object)
            throw Refuse(task, "has no such task");
        var status = row.GetProperty("status").GetString() ?? "";
        var lastRun = row.GetProperty("run_id").GetString();
        var entry = lastRun is null ? null : await log.ReadAsync(lastRun, ct);
        var earlier = Check(task, status, entry);
        var runner = row.TryGetProperty("runner", out var ranOn) && ranOn.ValueKind == JsonValueKind.String ? ranOn.GetString() : null;
        return new ResolvedTask(task.Id, null, Goal(task, earlier), task.MaxTurns, task.MaxMinutes, task.Size, task.BudgetUsd, new ContinuedFrom(lastRun!, earlier.Kept, earlier.Pushed, runner),
            Title: task.Title);
    }

    /// <summary>The earlier task's records and the work it left, as far as they let it continue.</summary>
    private sealed record Earlier(StartRecord Start, RunRecord Run, bool Asked, KeptWork? Kept, PushedWork? Pushed);

    private static ChargehandException Refuse(DrivenTask task, string why) =>
        new(ErrorCode.InvalidRequest, $"task '{task.Id}' continues {task.ContinueFrom!.RunId}/{task.ContinueFrom.TaskId}, which {why}",
            "Name a task of a finished driven batch in this server's run log that ended needs_input, stopped with its work kept (a partial pull request) or completed with its branch pushed.");

    private static Earlier Check(DrivenTask task, string status, RunEntry? entry)
    {
        var kept = KeptWorkOf(entry?.Run?.Result);
        var asked = status == "needs_input";
        var pushed = !asked && kept is null && status == "completed" ? PushedWorkOf(entry?.Run?.Result) : null;
        if (!asked && kept is null && pushed is null)
            throw Refuse(task, $"ended {status.Replace('_', ' ')} and kept no work; only a task that ended needs_input, a partial one or a completed one with a pushed branch can continue");
        if (entry is not { Start: { } start, Run: { } run })
            throw Refuse(task, "has no record of its own in the run log");
        RequireAnswers(task, asked, pushed is not null);
        return new Earlier(start, run, asked, kept, pushed);
    }

    private static void RequireAnswers(DrivenTask task, bool asked, bool pushed)
    {
        var from = task.ContinueFrom!;
        if (asked && task.Answers is not { Count: > 0 })
            throw new ChargehandException(ErrorCode.InvalidRequest, $"task '{task.Id}' continues {from.RunId}/{from.TaskId}, which ended needs_input: give answers",
                "Add answers to the task: the earlier session's questions are in that task's result.");
        if (pushed && task.Answers is not { Count: > 0 })
            throw new ChargehandException(ErrorCode.InvalidRequest, $"task '{task.Id}' continues {from.RunId}/{from.TaskId}, which completed: give answers, the review comments to address",
                "Add answers to the task: one entry per comment to address.");
    }

    private static string Goal(DrivenTask task, Earlier earlier)
    {
        var (asked, kept, pushed) = (earlier.Asked, earlier.Kept, earlier.Pushed);
        var goal = new StringBuilder(earlier.Start.Request.Text.TrimEnd());
        if (asked)
            goal.Append("\n\nAn earlier session of this task stopped to ask:\n").AppendJoin('\n', earlier.Run.Result.OpenQuestions.Select(q => $"- {q}"));
        else if (pushed is not null)
            goal.Append("\n\nThis task is finished: its branch is pushed and a pull request is open for it. A reviewer has asked for changes.");
        else
            goal.Append("\n\nAn earlier session of this task was stopped before it finished.");
        if (task.Answers is { Count: > 0 } answers)
            goal.Append(pushed is null ? "\n\nThe answers from the person who sent the task:\n" : "\n\nThe review comments to address:\n").AppendJoin('\n', answers.Select(a => $"- {a}"));
        if (pushed is not null)
            goal.Append($"\n\nThe finished work is on this branch, `{pushed.Branch}`, as the remote has it. Read `git log` and `git diff {pushed.Base}..HEAD` first, change only what the comments ask for, "
                + "do not redo finished work, run the tests, and end with the same report block.");
        else
            goal.Append(kept is null
                ? "\n\nIt made no commits: start from the base commit."
                : $"\n\nIts work so far is on this branch, {kept.Commits} commit(s) past {kept.Base} (the last may be 'WIP: ...'). Read `git log` and `git diff {kept.Base}..HEAD` first, keep what works, "
                    + "do not redo finished work, finish the task, run the tests, and end with the same report block.");
        return goal.ToString();
    }

    /// <summary>The branch of a task that pushed it, from the <c>branch</c> artifact of its result; null when it has none or cannot be read.</summary>
    public static PushedWork? PushedWorkOf(ResultContract? result)
    {
        if (result?.Artifacts.FirstOrDefault(a => a.Kind == "branch")?.Content is not { } content)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            return root.TryGetProperty("pushed", out var pushed) && pushed.ValueKind == JsonValueKind.True
                && root.TryGetProperty("branch", out var branch) && branch.GetString() is { Length: > 0 } name
                && root.TryGetProperty("base", out var start) && start.GetString() is { Length: > 0 } commit ? new PushedWork(name, commit) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>kept-work</c> artifact of a task's result; null when it has none or cannot be read.</summary>
    public static KeptWork? KeptWorkOf(ResultContract? result)
    {
        if (result?.Artifacts.FirstOrDefault(a => a.Kind == KeptWorkKind)?.Content is not { } content)
            return null;
        try
        {
            return JsonSerializer.Deserialize(content, BatchesJsonContext.Contract.KeptWork);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
