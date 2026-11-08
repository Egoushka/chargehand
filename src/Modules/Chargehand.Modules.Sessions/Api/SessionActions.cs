using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Modules.Journal.Api;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>Turns a session's stream into its action log (ADR 0090, first slice): every tool call becomes one <c>chargehand.tool.called</c> event, once, with its name, a one-line summary
/// and the SHA-256 of its input. Inputs, outputs and messages are not recorded.</summary>
public sealed class SessionActions(string run, IReadOnlyList<string> secrets)
{
    private const int MaxHint = 120;
    private static readonly string[] HintNames = ["file_path", "notebook_path", "path", "pattern", "command", "url", "skill"];
    private static readonly HashSet<string> Reads = ["Read", "Grep", "Glob", "LS", "WebSearch", "WebFetch", "TodoWrite", "ToolSearch"];
    private static readonly HashSet<string> Writes = ["Edit", "Write", "MultiEdit", "NotebookEdit", "Bash"];

    private readonly HashSet<string> _seen = [];

    /// <summary>The tool calls in <paramref name="e"/> not seen before, oldest first.</summary>
    public IReadOnlyList<ActionEvent> OnEvent(JsonElement e, DateTimeOffset at)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "assistant"
            || !e.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return [];
        List<ActionEvent> calls = [];
        foreach (var block in content.EnumerateArray())
            if (CallId(block) is { } id && _seen.Add(id))
                calls.Add(Call(id, block, at));
        return calls;
    }

    private static string? CallId(JsonElement block) =>
        block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "tool_use"
        && block.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    private ActionEvent Call(string id, JsonElement block, DateTimeOffset at)
    {
        var name = block.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        var input = block.TryGetProperty("input", out var i) ? i.GetRawText() : "";
        var hint = Hint(block);
        var summary = SessionDriver.Redact(hint is null ? name : $"{name} {hint}", secrets);
        return new ActionEvent(id, "chargehand/session", "chargehand.tool.called", name, at, run, summary, Effect(name),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input))));
    }

    private static string? Hint(JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in HintNames)
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
            {
                var line = s.ReplaceLineEndings(" ");
                return line.Length <= MaxHint ? line : line[..MaxHint] + "…";
            }
        return null;
    }

    /// <summary>What a tool does, by its name: reads, edits and commands, or calls out (an MCP tool).</summary>
    public static string Effect(string tool) =>
        Reads.Contains(tool) ? "read" : Writes.Contains(tool) ? "write" : tool.StartsWith("mcp__", StringComparison.Ordinal) ? "external" : "write";
}
