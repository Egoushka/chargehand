using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Driven;

/// <summary>Builds the <c>result/v1</c> of a batch (ADR 0039). A task's own result is Research's <c>TaskResultBuilder</c>.</summary>
public static class DrivenResult
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };

    /// <summary>The batch's own result: completed when every task ended in a draft pull request, else failed <c>tasks_incomplete</c> naming the tasks that did not; failed
    /// <c>cancelled</c> when the caller cancelled the batch before it finished (the same code as any cancelled run, <c>status</c> keeps its enum). Each task also has its
    /// own result under its own run id; this one lists them in the <c>driven-batch</c> artifact.</summary>
    /// <param name="cancelledByCaller">The caller (not a shutdown) cancelled the batch.</param>
    /// <param name="CredentialDelivery">How the model credential reached the containers (<c>gateway</c>: a per-task token, the real credential only in the egress container; <c>environment</c>: the real credential in the session container's environment).</param>
    /// <param name="plan">What the planning step did (ADR 0049), as the <c>plan</c> artifact; null: it did not run.</param>
    /// <param name="retro">What the retro after the batch did (ADR 0045), as the <c>retro</c> artifact; null: retros are off.</param>
    public static ResultContract BuildBatch(string runId, string traceId, string description, BatchOutcome outcome, string credentialDelivery, bool priced, string claudeVersion,
        bool cancelledByCaller = false, BatchPlan? plan = null, BatchRetro? retro = null)
    {
        var chain = new PromptChain([], new AsSent($"claude-code/{claudeVersion}", "driven", "batch", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        // A row says which runner ran the task only when the profile names its runners (ADR 0058), so a one-runner profile's artifact is what it always was.
        var rows = outcome.Tasks.Select(t =>
        {
            var row = new Dictionary<string, object?>
            {
                ["id"] = t.Id,
                ["run_id"] = t.RunId,
                ["status"] = Snake(t.State.ToString()),
                ["branch"] = t.Branch,
                ["pr_url"] = t.PrUrl,
                ["usd"] = priced ? (decimal?)t.Usd : null,
                ["tokens"] = t.Tokens,
                ["error_code"] = t.Error is { } e ? Snake(e.ToString()) : null,
                ["detail"] = t.Detail,
            };
            if (t.Runner is not null)
                row["runner"] = t.Runner;
            return row;
        }).ToList();
        var artifact = Inline("driven-batch", "application/json", new { credential_delivery = credentialDelivery, tasks = rows });
        Artifact[] artifacts = [artifact, .. plan is null ? [] : new[] { Inline("plan", "application/json", plan) }, .. retro is null ? [] : new[] { Inline("retro", "application/json", retro) }];
        var usage = new Usage(outcome.Tokens, 0, 0, 0, priced ? outcome.Usd : null);
        if (outcome.AllCompleted)
            return new ResultContract("result/v1", runId, "driven", traceId, chain, ResultStatus.Completed, $"All {outcome.Tasks.Count} tasks ended in a draft pull request. {description}".Trim(), [], [], artifacts, [], 0.9, usage);
        var incomplete = outcome.Incomplete;
        var message = $"{incomplete.Count} of {outcome.Tasks.Count} tasks did not produce a pull request";
        var action = $"Tasks that did not finish: {string.Join(", ", incomplete)}. Read each task's own result under its run id, fix what it names and send those tasks again."
            + (outcome.Action is null ? "" : $" {outcome.Action}");
        var code = ErrorCode.TasksIncomplete;
        if (cancelledByCaller)
        {
            message = $"cancelled by the caller: {outcome.Tasks.Count - incomplete.Count} of {outcome.Tasks.Count} tasks finished";
            action = $"Tasks that did not finish: {string.Join(", ", incomplete)}; nothing from them was pushed. Send those tasks again if they are still wanted.";
            code = ErrorCode.Cancelled;
        }
        else if (outcome.Error == ErrorCode.RateLimited)
        {
            message = $"the provider's usage or rate limit stopped the batch: {outcome.Tasks.Count - incomplete.Count} of {outcome.Tasks.Count} tasks finished";
            code = ErrorCode.RateLimited;
        }
        var questions = outcome.Tasks.Where(t => t.State != TaskState.Completed).Select(t => $"Task {t.Id}: {Snake(t.State.ToString()).Replace('_', ' ')}: {t.Detail ?? "no detail"}").ToList();
        return new ResultContract("result/v1", runId, "driven", traceId, chain, ResultStatus.Failed, message, [], [], artifacts, questions, 0.5, usage,
            new ResultError(code, message, ChargehandException.Retryable(code), action));
    }

    private static Artifact Inline(string kind, string mediaType, object value)
    {
        var content = JsonSerializer.Serialize(value, Json);
        return new Artifact(kind, mediaType, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), Content: content);
    }

    private static string Snake(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
}
