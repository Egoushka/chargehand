using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>What a failure autopsy run answered (ADR 0045's sibling to the retro): a short root-cause class and, when the model gave one, one short line on why.</summary>
/// <param name="Class">An <see cref="AutopsyClass"/>; <see cref="AutopsyClass.Other"/> when the run's first line was not one of them.</param>
public sealed record AutopsyAnswer(string Class, string? Detail);

/// <summary>One task's line in the autopsy prompt: the same signals the retro prompt uses, without a goal or session text.</summary>
internal sealed record AutopsyLine(string Size, decimal? BudgetUsd, decimal? Usd, int Turns, int Continuations, string Outcome, string? StopReason, string? GovernorMode,
    IReadOnlyList<RetroGovernor> Governor, bool TestIntegrityFlagged);

/// <param name="Request">The batch's request: the repository the autopsy run is pinned to, as the retro run is.</param>
/// <param name="RunId">The task attempt's run id; the autopsy run is recorded as its child.</param>
public sealed record AutopsyInput(RunRequest Request, string RunId, LearningRecord Record);

public sealed class AutopsyException(string message) : Exception(message);

/// <summary>Classifies why a task attempt ended failed or partial.</summary>
public interface IFailureAutopsy
{
    /// <exception cref="AutopsyException">The autopsy run ran and its answer cannot be used.</exception>
    Task<AutopsyAnswer> WriteAsync(AutopsyInput input, CancellationToken ct);
}

/// <summary>The failure autopsy of a task attempt (ADR 0045): one run on a read-only preset (<c>cheap</c> by default) through the orchestrator's own pipeline, like <see cref="ModelRetro"/>. It is given
/// the one task's learning record (size, budget, spend, stop reason, governor counts; never a goal or a session's text) and answers with a short root-cause class. Off unless
/// <c>driven.learning.autopsy</c>.</summary>
public sealed class ModelAutopsy(PlanRunner run, string preset) : IFailureAutopsy
{
    public async Task<AutopsyAnswer> WriteAsync(AutopsyInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var runId = RunIds.New();
        var request = new RunRequest("request/v1", AutopsyPrompt(input.Record), new RequestContext(false, preset, Repository: input.Request.Context.Repository));
        var result = await run(request, runId, input.RunId, ct);
        if (result.Status != ResultStatus.Completed)
            throw new AutopsyException($"the autopsy run ended {JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Status.ToString())}: {result.Error?.Message ?? result.Summary}");
        var text = result.Summary.Trim();
        if (text.Length == 0)
            throw new AutopsyException("the autopsy run printed nothing");
        return Parse(text);
    }

    private static AutopsyAnswer Parse(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n', 2);
        var first = lines[0].Trim().TrimEnd('.').ToLowerInvariant();
        var detail = lines.Length > 1 ? lines[1].Trim() : null;
        return new AutopsyAnswer(AutopsyClass.All.Contains(first) ? first : AutopsyClass.Other, string.IsNullOrWhiteSpace(detail) ? null : detail);
    }

    public static string AutopsyPrompt(LearningRecord record)
    {
        var text = new StringBuilder();
        text.Append("Answer this question from the data below. It is one question with one answer: do not split it, do not ask for anything, do not read the repository and do not change any file.\n\n");
        text.Append("A driven coding task ended ").Append(record.Outcome).Append(". Classify the root cause as exactly one of these words: ").Append(string.Join(", ", AutopsyClass.All)).Append(".\n");
        text.Append("Put just that word on the first line of `summary`; if you can, add one short line after it on why. Use only the data below; where it is too thin to tell, answer `other`.\n\n");
        text.Append("The task, as JSON (usd is the dollar equivalent of the session's tokens, not a bill):\n");
        text.AppendLine(JsonSerializer.Serialize(new AutopsyLine(record.SizeClass, record.Budget.Usd, record.Spend.UsdEstimate, record.Spend.Turns, record.Spend.Continuations, record.Outcome,
            record.StopReason, record.GovernorMode, [.. record.Governor.Select(g => new RetroGovernor(g.Signal, g.Action, g.Count, g.Acted))], record.TestIntegrityFlagged),
            BatchesJsonContext.Contract.AutopsyLine));
        return text.ToString();
    }
}
