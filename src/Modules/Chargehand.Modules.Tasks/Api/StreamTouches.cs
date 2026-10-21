using System.Text.Json;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>One assistant message of a session's <c>stream.jsonl</c> that edited a file: where it is in the stream, the tool, and what the model said alongside.</summary>
public sealed record FileTouch(int Line, string Tool, string Said);

/// <summary>Finds the assistant messages of a driven session's stream (<c>stream-json</c>, one event per line) whose edit tools named a file. Lines that are not JSON, or not assistant events, are skipped.</summary>
public static class StreamTouches
{
    private static readonly HashSet<string> EditTools = new(["Edit", "Write", "MultiEdit", "NotebookEdit"], StringComparer.Ordinal);

    /// <param name="relativePath">The path from the repository root, with <c>/</c>; a tool input names it by an absolute path or a relative one, so the match is on the path's end.</param>
    public static IReadOnlyList<FileTouch> Find(IEnumerable<string> lines, string relativePath)
    {
        List<FileTouch> found = [];
        var number = 0;
        foreach (var line in lines)
        {
            number++;
            if (line.Length == 0 || !line.Contains(relativePath, StringComparison.Ordinal))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;
                var said = string.Join(' ', content.EnumerateArray().Where(c => Is(c, "type", "text")).Select(c => c.GetProperty("text").GetString() ?? "")).Trim();
                foreach (var tool in content.EnumerateArray().Where(c => Is(c, "type", "tool_use") && EditTools.Contains(c.GetProperty("name").GetString() ?? "") && Names(c, relativePath)))
                    found.Add(new FileTouch(number, tool.GetProperty("name").GetString()!, said));
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Not an event of the shape; the next line may be.
            }
        }
        return found;
    }

    private static bool Is(JsonElement e, string property, string value) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() == value;

    private static bool Names(JsonElement tool, string relativePath) =>
        tool.TryGetProperty("input", out var input) && (input.TryGetProperty("file_path", out var file) || input.TryGetProperty("notebook_path", out file) || input.TryGetProperty("path", out file))
        && file.GetString() is { } named && (named == relativePath || named.EndsWith("/" + relativePath, StringComparison.Ordinal));
}
