using System.Text.Json;
using Chargehand.Config;
using Chargehand.Driven;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Chargehand.Mcp;

/// <summary>Reads a tracker item through the profile's <c>driven.task_source</c> mapping (ADR 0039): calls the mapped tool with the item id in place of
/// <c>{ref}</c> and takes the goal from the title path, then the body path if there is one. The id only ever fills a placeholder; it cannot add an
/// argument.</summary>
public sealed class McpTaskSource(DrivenTaskSource settings, McpConnectionPool pool) : ITaskSource
{
    private const int BodyLimit = 8000;

    public async Task<string> ResolveAsync(string @ref, CancellationToken ct)
    {
        CallToolResult result;
        try
        {
            var arguments = ToolArguments.Expand(settings.Arguments, new Dictionary<string, object?> { ["ref"] = @ref });
            var client = await pool.GetAsync(settings.Server, ct);
            result = await client.CallToolAsync(settings.Tool, arguments, cancellationToken: ct);
        }
        catch (Exception e) when (e is McpException or McpMemoryException or InvalidOperationException or TimeoutException or IOException)
        {
            throw new TaskSourceException($"the task source could not be reached: {e.Message}");
        }
        if (result.IsError == true)
            throw new TaskSourceException($"the task source answered with an error: {result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "no text"}");
        var root = Answer(result);
        var title = At(root, settings.Title);
        if (string.IsNullOrWhiteSpace(title))
            throw new TaskSourceException($"the answer has no title at '{settings.Title}'");
        var body = settings.Body is null ? null : At(root, settings.Body);
        if (string.IsNullOrWhiteSpace(body))
            return title.Trim();
        body = body.Trim();
        return $"{title.Trim()}\n\n{(body.Length > BodyLimit ? body[..BodyLimit] + "\n[cut]" : body)}";
    }

    private static JsonElement Answer(CallToolResult result)
    {
        if (result.StructuredContent is { ValueKind: JsonValueKind.Object } structured)
            return structured;
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? throw new TaskSourceException("the answer has no text");
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(text);
        }
        catch (JsonException)
        {
            throw new TaskSourceException("the answer is not JSON");
        }
    }

    private static string? At(JsonElement root, string path)
    {
        var node = root;
        foreach (var name in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            // Some servers wrap a string answer as {"result": "<json>"}: look inside it once.
            if (node.ValueKind == JsonValueKind.String && node.GetString() is { } inner && TryParse(inner, out var parsed))
                node = parsed;
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node))
                return null;
        }
        return node.ValueKind == JsonValueKind.String ? node.GetString() : node.ValueKind is JsonValueKind.Number ? node.ToString() : null;
    }

    private static bool TryParse(string text, out JsonElement element)
    {
        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(text);
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }
}
