using System.Text.Json;
using Chargehand.Config;
using Chargehand.Memory;
using ModelContextProtocol.Protocol;

namespace Chargehand.Mcp;

/// <summary>
/// A memory behind an MCP server (ADR 0034): the entry's tool mapping says which tool each operation calls, how its arguments
/// are built and how a recall answer becomes facts. It throws when the server, the tool or the answer fails; the
/// <see cref="MemoryStack"/> around it owns the timeout, the skipping and the caps.
/// </summary>
/// <param name="settings">An entry <see cref="Profile.Load"/> has validated.</param>
/// <param name="pool">Opens the entry's server.</param>
public sealed class McpMemoryProvider(MemoryProviderSettings settings, McpConnectionPool pool) : IMemoryProvider
{
    /// <summary>Calls <c>tools.recall</c> with <c>{query}</c>, <c>{namespace}</c> (the scope's) and <c>{max_facts}</c> (the entry's fact cap).</summary>
    public async Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, MemoryScope scope, CancellationToken ct)
    {
        var call = settings.Tools.Recall;
        var result = await CallAsync(call, new() { ["query"] = query, ["namespace"] = scope.Namespace, ["max_facts"] = settings.Limits.MaxFacts }, ct);
        return RecallResults.Read(result, call.EffectiveResults);
    }

    /// <summary>Calls <c>tools.retain</c> with the item as it is given (the stack has set the source's tags).</summary>
    /// <exception cref="InvalidOperationException">The entry has no retain tool.</exception>
    public async Task RetainAsync(MemoryItem item, MemoryScope scope, CancellationToken ct)
    {
        var call = settings.Tools.Retain ?? throw new InvalidOperationException($"memory '{settings.Name}' has no retain tool");
        var timestamp = item.Timestamp is { } at ? JsonSerializer.SerializeToElement(at).GetString() : null;
        McpMemoryException.ThrowIfError(await CallAsync(call, new()
        {
            ["namespace"] = scope.Namespace,
            ["text"] = item.Text,
            ["context"] = item.Context,
            ["document_id"] = item.DocumentId,
            ["timestamp"] = timestamp,
            ["tags"] = item.Tags?.ToArray(),
        }, ct));
    }

    /// <summary>Calls <c>tools.invalidate</c>.</summary>
    /// <exception cref="InvalidOperationException">The entry has no invalidate tool.</exception>
    public async Task InvalidateAsync(string id, string reason, MemoryScope scope, CancellationToken ct)
    {
        var call = settings.Tools.Invalidate ?? throw new InvalidOperationException($"memory '{settings.Name}' has no invalidate tool");
        McpMemoryException.ThrowIfError(await CallAsync(call, new() { ["namespace"] = scope.Namespace, ["id"] = id, ["reason"] = reason }, ct));
    }

    private async Task<CallToolResult> CallAsync(ToolCall call, Dictionary<string, object?> values, CancellationToken ct)
    {
        var arguments = ToolArguments.Expand(call.Arguments, values);
        var client = await pool.GetAsync(settings.Server, ct);
        return await client.CallToolAsync(call.Tool, arguments, cancellationToken: ct);
    }
}
