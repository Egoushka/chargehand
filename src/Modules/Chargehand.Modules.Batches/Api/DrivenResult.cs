using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>Builds the <c>result/v1</c> of a batch (ADR 0039). A task's own result is Research's <c>TaskResultBuilder</c>.</summary>
public static class DrivenResult
{
    /// <summary>The batch's own result: completed when every task ended in a draft pull request, else failed <c>tasks_incomplete</c> naming the tasks that did not; failed
    /// <c>cancelled</c> when the caller cancelled the batch before it finished (the same code as any cancelled run, <c>status</c> keeps its enum). Each task also has its
    /// own result under its own run id; this one lists them in the <c>driven-batch</c> artifact.</summary>
    /// <param name="cancelledByCaller">The caller (not a shutdown) cancelled the batch.</param>
    /// <param name="CredentialDelivery">How the model credential reached the containers (<c>gateway</c>: a per-task token, the real credential only in the egress container; <c>environment</c>: the real credential in the session container's environment).</param>
    /// <param name="plan">What the planning step did (ADR 0049), as the <c>plan</c> artifact; null: it did not run.</param>
    /// <param name="retro">What the retro after the batch did (ADR 0045), as the <c>retro</c> artifact; null: retros are off.</param>
    /// <param name="fallback">What the batch spent on the API key inside its egress gateway (ADR 0088), in the <c>driven-batch</c> artifact and per task row; null: the batch had no fallback.</param>
    public static ResultContract BuildBatch(string runId, string traceId, string description, BatchOutcome outcome, string credentialDelivery, bool priced, string claudeVersion,
        bool cancelledByCaller = false, BatchPlan? plan = null, BatchRetro? retro = null, BatchFallback? fallback = null)
    {
        var chain = new PromptChain([], new AsSent($"claude-code/{claudeVersion}", "driven", "batch", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        // A row says which runner ran the task only when the profile names its runners (ADR 0058), so a one-runner profile's artifact is what it always was.
        var artifact = Inline("driven-batch", "application/json", new BatchArtifact(credentialDelivery, Rows(outcome, priced, fallback),
            fallback is null ? null : new BatchFallbackBody(fallback.Usd, fallback.Calls, fallback.Refused)), BatchResultJsonContext.Default.BatchArtifact);
        Artifact[] artifacts = [artifact, .. plan is null ? [] : new[] { Inline("plan", "application/json", plan, BatchResultJsonContext.Default.BatchPlan) },
            .. retro is null ? [] : new[] { Inline("retro", "application/json", retro, BatchResultJsonContext.Default.BatchRetro) }];
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

    private static List<BatchRow> Rows(BatchOutcome outcome, bool priced, BatchFallback? fallback) =>
        [.. outcome.Tasks.Select(t => new BatchRow(t.Id, t.RunId, Snake(t.State.ToString()), t.Branch, t.PrUrl, priced ? t.Usd : null, t.Tokens,
            t.Error is { } e ? Snake(e.ToString()) : null, t.Detail, t.Runner, t.RunId is { } run ? fallback?.Tasks?.GetValueOrDefault(run) : null))];

    private static Artifact Inline<T>(string kind, string mediaType, T value, JsonTypeInfo<T> info)
    {
        var content = JsonSerializer.Serialize(value, info);
        return new Artifact(kind, mediaType, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), Content: content);
    }

    private static string Snake(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
}

/// <summary>What a batch spent on the API key inside its egress gateway (ADR 0088), as the server settled it at teardown.</summary>
/// <param name="Tasks">By task run id, from the lines of the log that were read; a task's share can fall short of the batch's total when the log's tail lost its early lines.</param>
public sealed record BatchFallback(decimal Usd, int Calls, bool Refused, IReadOnlyDictionary<string, TaskFallback>? Tasks = null);

/// <summary>One task's calls on the fallback key and what they cost.</summary>
public sealed record TaskFallback(decimal Usd, int Calls);

/// <summary>The <c>driven-batch</c> artifact: how the credential reached the containers, and a row per task.</summary>
/// <param name="Fallback">Only when the batch had an API fallback (ADR 0088), so every other batch's artifact is what it always was.</param>
internal sealed record BatchArtifact(string CredentialDelivery, IReadOnlyList<BatchRow> Tasks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BatchFallbackBody? Fallback = null);

/// <summary>What the fallback cost a batch: dollars, calls, and whether the gateway's budget closed it.</summary>
internal sealed record BatchFallbackBody(decimal Usd, int Calls, bool Refused);

/// <param name="Runner">Only when the profile names its runners (ADR 0058), so a one-runner profile's row is what it always was.</param>
internal sealed record BatchRow(string Id, string? RunId, string Status, string? Branch, string? PrUrl, decimal? Usd, long Tokens, string? ErrorCode, string? Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Runner,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TaskFallback? Fallback = null);

/// <summary>The batch result's artifacts without reflection (ADR 0060): snake_case, and a null written as null, as the reflection options before it wrote them.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(BatchArtifact))]
[JsonSerializable(typeof(BatchFallbackBody))]
[JsonSerializable(typeof(BatchPlan))]
[JsonSerializable(typeof(BatchRetro))]
internal sealed partial class BatchResultJsonContext : JsonSerializerContext;
