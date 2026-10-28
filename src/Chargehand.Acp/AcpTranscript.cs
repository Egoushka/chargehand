using System.Text;
using AgentClientProtocol;
using Chargehand.Kernel;
using Chargehand.Runtime;

namespace Chargehand.Acp;

/// <summary>One turn's assistant message, built from the agent's updates: its text, and what its tools were given and returned (the evidence scope).
/// ACP has no per-call ids, so a turn is one message.</summary>
internal sealed class AcpTranscript(string id, DateTimeOffset created)
{
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _tools = new();
    private readonly List<string> _results = [];

    public bool Empty => _text.Length == 0 && _tools.Length == 0;

    public void Apply(SessionUpdate update)
    {
        switch (update)
        {
            case AgentMessageChunk { Content: { Type: "text", Text: { } text } }:
                _text.Append(text);
                break;
            case ToolCall call:
                _tools.Append(call.Title).Append(' ').Append(Where(call.Locations)).Append(call.RawInput?.GetRawText()).AppendLine();
                Take(call.Content, call.RawOutput?.GetRawText());
                break;
            case ToolCallUpdate change:
                _tools.Append(Where(change.Locations)).Append(change.RawInput?.GetRawText());
                Take(change.Content, change.RawOutput?.GetRawText());
                break;
        }
    }

    public WorkerMessage Snapshot(TokenCounts? tokens, string? model, DateTimeOffset? completed) =>
        new(id, WorkerMessageKind.Assistant, created, _text.ToString(), tokens, completed, model, _tools.ToString(), ToolResults: _results.Count == 0 ? null : [.. _results]);

    private void Take(IReadOnlyList<ToolCallContent>? content, string? rawOutput)
    {
        foreach (var block in content ?? [])
            if (block is { Type: "content", Content.Text: { } text })
            {
                _tools.AppendLine(text);
                _results.Add(text);
            }
        if (rawOutput is { Length: > 0 })
        {
            _tools.AppendLine(rawOutput);
            _results.Add(rawOutput);
        }
    }

    private static string Where(IReadOnlyList<ToolCallLocation>? locations) =>
        locations is { Count: > 0 } ? string.Join(' ', locations.Select(l => l.Line is { } line ? $"{l.Path}:{line}" : l.Path)) + " " : "";
}
