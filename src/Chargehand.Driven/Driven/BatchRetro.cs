using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Driven;

/// <summary>What the retro after a batch did (ADR 0045), the batch result's <c>retro</c> artifact. A retro is advice written by a model from the batch's learning records: it can be wrong, and nothing reads it back.</summary>
/// <param name="Status"><c>written</c> or <c>skipped</c>.</param>
/// <param name="Reason">Why it was skipped.</param>
/// <param name="RunId">The retro run in the run log, a child of the batch; null when none ran.</param>
/// <param name="Tasks">How many task records it was given.</param>
public sealed record BatchRetro(string Status, string? Reason, string? RunId, int Tasks, string? Text = null)
{
    public static BatchRetro Skipped(string reason, int tasks = 0, string? runId = null) => new("skipped", reason, runId, tasks);

    public bool Written => Status == "written";
}

/// <param name="Request">The batch's request: the repository the retro run is pinned to, as the planning run is.</param>
/// <param name="RunId">The batch's run id; the retro run is recorded as its child.</param>
public sealed record RetroInput(RunRequest Request, string RunId, IReadOnlyList<LearningRecord> Records);

public sealed class RetroException(string message) : Exception(message);

/// <summary>Writes the retro of a batch from its records.</summary>
public interface IBatchRetro
{
    /// <exception cref="RetroException">The retro run ran and its answer cannot be used.</exception>
    Task<BatchRetro> WriteAsync(RetroInput input, CancellationToken ct);
}

/// <summary>Where a written retro goes besides the batch result: the extension point for filing it to a tracker's inbox. chargehand has no write path to a tracker yet (the task source only reads), so no
/// sink ships; a deployment that adds one is called after each written retro, and a sink that throws is reported and ignored.</summary>
public interface IRetroSink
{
    Task PublishAsync(string batchRunId, BatchRetro retro, CancellationToken ct);
}

/// <summary>The retro after a batch (ADR 0045): one run on a read-only preset (<c>cheap</c> by default) through the orchestrator's own pipeline, like <see cref="ModelPlanner"/>. It is given the batch's
/// learning records (sizes, budgets, spend, stop reasons, governor counts; never a goal or a session's text) and answers in plain text. The prompt is built here rather than in <c>prompts/</c>, so no
/// registry block changes and Prompt CI has nothing new to gate. Off unless <c>driven.learning.retro</c>.</summary>
public sealed class ModelRetro(PlanRunner run, string preset) : IBatchRetro
{
    public const int MaxTextLength = 4000;

    public async Task<BatchRetro> WriteAsync(RetroInput input, CancellationToken ct)
    {
        var runId = Orchestrator.NewRunId();
        var request = new RunRequest("request/v1", RetroPrompt(input.Records), new RequestContext(false, preset, Repository: input.Request.Context.Repository));
        var result = await run(request, runId, input.RunId, ct);
        if (result.Status != ResultStatus.Completed)
            throw new RetroException($"the retro run ended {JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Status.ToString())}: {result.Error?.Message ?? result.Summary}");
        var text = result.Summary.Trim();
        if (text.Length == 0)
            throw new RetroException("the retro run printed nothing");
        return new BatchRetro("written", null, runId, input.Records.Count, text.Length > MaxTextLength ? text[..MaxTextLength].TrimEnd() + " [cut]" : text);
    }

    public static string RetroPrompt(IReadOnlyList<LearningRecord> records)
    {
        var text = new StringBuilder();
        text.Append("Answer this question from the data below. It is one question with one answer: do not split it, do not ask for anything, do not read the repository and do not change any file.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"A batch of {records.Count} coding tasks has ended. Write a short retro for the maintainer, at most 12 lines of plain text: which tasks cost more than their budget or stopped early and the stop reason, ");
        text.Append("which governor signals fired and whether the task still completed, whether the size classes look right for the spend, and one or two concrete changes (a budget, a goal that should be split). ");
        text.Append("Cite task ids. Use only the data below; where it is too thin to say, say so. Put the retro in `summary` as plain text.\n\nOne task per line, as JSON (usd is the dollar equivalent of the session's tokens, not a bill):\n");
        foreach (var r in records)
            text.AppendLine(JsonSerializer.Serialize(new
            {
                id = r.TaskId,
                size = r.SizeClass,
                size_from = r.SizeFrom,
                budget_usd = r.Budget.Usd,
                budget_source = r.Budget.Source.Kind,
                usd = r.Spend.UsdEstimate,
                turns = r.Spend.Turns,
                continuations = r.Spend.Continuations,
                outcome = r.Outcome,
                stop_reason = r.StopReason,
                governor_mode = r.GovernorMode,
                governor = r.Governor.Select(g => new { g.Signal, g.Action, g.Count, g.Acted }),
                test_integrity_flagged = r.TestIntegrityFlagged,
            }, ContractJson.Options));
        return text.ToString();
    }
}
