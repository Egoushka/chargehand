using System.Text.Json;

namespace Chargehand.Driven;

/// <summary>Why a driven session was ended by the driver rather than by itself; the wire names go in the session outcome.</summary>
public enum StallReason { NoProgress, Repeating, TurnLimit, WallClock }

/// <summary>Decides from a headless Claude Code session's event stream whether it has stopped making progress (ADR 0039): nothing on the
/// stream for a while, the same tool call with the same input several times in a row, more assistant messages than allowed, or simply too
/// long. Pure: the caller passes the time.</summary>
public sealed class StallDetector
{
    private readonly TimeSpan _noProgress;
    private readonly int _repeatLimit;
    private readonly int _maxTurns;
    private readonly TimeSpan _wallClock;
    private readonly DateTimeOffset _start;

    public StallDetector(TimeSpan noProgress, int repeatLimit, int maxTurns, TimeSpan wallClock, DateTimeOffset start)
    {
        (_noProgress, _repeatLimit, _maxTurns, _wallClock, _start) = (noProgress, repeatLimit, maxTurns, wallClock, start);
        _lastEvent = start;
    }

    private readonly HashSet<string> _messages = [];
    private DateTimeOffset _lastEvent;
    private string? _lastCall;
    private int _repeats;

    /// <summary>Distinct assistant messages seen; one message streamed as several events counts once.</summary>
    public int Turns => _messages.Count;

    public static string Wire(StallReason reason) => reason switch
    {
        StallReason.NoProgress => "no_progress",
        StallReason.Repeating => "repeating",
        StallReason.TurnLimit => "turn_limit",
        _ => "wall_clock",
    };

    public void OnEvent(JsonElement e, DateTimeOffset now)
    {
        _lastEvent = now;
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out var type) || type.GetString() != "assistant"
            || !e.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return;
        var id = message.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString()! : null;
        if (id is null || !_messages.Add(id))
            return;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var t) || t.GetString() != "tool_use")
                continue;
            var call = $"{(block.TryGetProperty("name", out var n) ? n.GetString() : "")}|{(block.TryGetProperty("input", out var input) ? input.GetRawText() : "")}";
            _repeats = call == _lastCall ? _repeats + 1 : 1;
            _lastCall = call;
        }
    }

    public StallReason? Check(DateTimeOffset now)
    {
        if (now - _start > _wallClock)
            return StallReason.WallClock;
        if (_repeats >= _repeatLimit)
            return StallReason.Repeating;
        if (Turns > _maxTurns)
            return StallReason.TurnLimit;
        if (now - _lastEvent > _noProgress)
            return StallReason.NoProgress;
        return null;
    }
}
