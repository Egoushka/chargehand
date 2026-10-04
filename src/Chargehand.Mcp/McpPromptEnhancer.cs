using System.Text.Json;
using Chargehand.Config;
using Chargehand.Enhancement;
using ModelContextProtocol.Protocol;

namespace Chargehand.Mcp;

/// <summary>
/// A prompt enhancer behind an MCP server (ADR 0040): the tools <c>enhance</c> and <c>feedback</c> of whetstone's contract
/// (schemas enhance/v1 and feedback/v1 in its repository). It throws when the server, the tool or the answer fails;
/// <see cref="GuardedPromptEnhancer"/> around it owns the deadline and the fall back to the original prompt.
/// </summary>
/// <param name="settings">An entry <see cref="Profile.Load"/> has validated.</param>
/// <param name="pool">Opens the entry's server.</param>
public sealed class McpPromptEnhancer(PromptEnhancerSettings settings, McpConnectionPool pool) : IPromptEnhancer
{
    public const string EnhanceTool = "enhance";
    public const string FeedbackTool = "feedback";

    /// <summary>The names whetstone's contract needs the server to list.</summary>
    public static readonly IReadOnlyList<string> RequiredTools = [EnhanceTool, FeedbackTool];

    public async Task<Enhanced> EnhanceAsync(string prompt, EnhanceContext context, CancellationToken ct)
    {
        var result = await CallAsync(EnhanceTool, new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["context"] = new Dictionary<string, object?>
            {
                ["repository"] = context.Repository,
                ["commit"] = context.Commit,
                ["task_kind"] = context.TaskKind,
                ["client"] = context.Client,
            }.Where(p => p.Value is not null).ToDictionary(),
            ["deadline_ms"] = settings.EffectiveDeadlineMs,
        }, ct);
        var answer = result.StructuredContent ?? throw new McpMemoryException("enhance answered without structured content");
        var text = Text(answer, "prompt") ?? throw new McpMemoryException("enhance answered without a prompt");
        var changed = answer.TryGetProperty("changed", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new Enhanced(prompt, changed ? text : null, Text(answer, "reason") ?? "", Text(answer, "request_id"),
            Text(answer, "template_id"), Text(answer, "template_version"),
            answer.TryGetProperty("held_out", out var held) && held.ValueKind == JsonValueKind.True);
    }

    public async Task FeedbackAsync(string requestId, EnhanceOutcome outcome, CancellationToken ct) =>
        McpMemoryException.ThrowIfError(await CallAsync(FeedbackTool, new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["outcome"] = new Dictionary<string, object?>
            {
                ["rewrite_accepted"] = outcome.RewriteAccepted,
                ["model_overridden"] = outcome.ModelOverridden,
                ["score"] = outcome.Score,
                ["cost_usd"] = outcome.CostUsd,
                ["model"] = outcome.Model,
            }.Where(p => p.Value is not null).ToDictionary(),
        }, ct));

    private async Task<CallToolResult> CallAsync(string tool, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var client = await pool.GetAsync(settings.Server, ct);
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: ct);
        // The error text is the server's: it may quote the prompt, so it is not carried into the reason.
        if (result.IsError == true)
            throw new McpMemoryException($"{tool} reported an error");
        return result;
    }

    private static string? Text(JsonElement answer, string name) =>
        answer.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
