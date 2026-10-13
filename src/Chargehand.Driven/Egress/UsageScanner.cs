using System.Text;
using System.Text.Json;

namespace Chargehand.Egress;

/// <summary>Counts the tokens of one model response as it streams past (ADR 0039): <c>message_start</c> carries the input and the first output count, each
/// <c>message_delta</c> the running output count, all integers. It reads server-sent events line by line and keeps nothing but two numbers: no text of the response survives a
/// call. A response that is plain JSON is read from its <c>usage</c> object at the end. Input plus output is what the session's own tally counts, so the two agree.</summary>
public sealed class UsageScanner(bool serverSentEvents)
{
    private const int MaxLine = 1 << 20;
    private const int MaxJson = 1 << 20;
    private readonly List<byte> _line = [];
    private readonly List<byte> _json = [];
    private bool _skipping;
    private long _input;
    private long _output;

    /// <summary>Input plus output tokens seen so far in this response.</summary>
    public long Tokens => _input + _output;

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (!serverSentEvents)
        {
            if (_json.Count + chunk.Length <= MaxJson)
                _json.AddRange(chunk);
            return;
        }
        foreach (var b in chunk)
        {
            if (b == '\n')
            {
                if (!_skipping)
                    Line();
                _line.Clear();
                _skipping = false;
            }
            else if (!_skipping)
            {
                if (_line.Count >= MaxLine)
                {
                    _skipping = true;
                    _line.Clear();
                }
                else
                {
                    _line.Add(b);
                }
            }
        }
    }

    /// <summary>Ends the response: a last line without a newline, or the whole JSON body.</summary>
    public void Finish()
    {
        if (serverSentEvents)
        {
            if (!_skipping && _line.Count > 0)
                Line();
            return;
        }
        if (_json.Count == 0)
            return;
        try
        {
            using var doc = JsonDocument.Parse(_json.ToArray());
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("usage", out var usage))
                Absorb(usage, replaceInput: true);
        }
        catch (JsonException)
        {
        }
    }

    private void Line()
    {
        var text = Encoding.UTF8.GetString([.. _line]).TrimEnd('\r');
        if (!text.StartsWith("data:", StringComparison.Ordinal))
            return;
        try
        {
            using var doc = JsonDocument.Parse(text["data:".Length..]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type))
                return;
            switch (type.GetString())
            {
                case "message_start" when root.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var start):
                    Absorb(start, replaceInput: true);
                    break;
                case "message_delta" when root.TryGetProperty("usage", out var delta):
                    Absorb(delta, replaceInput: false);
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    private void Absorb(JsonElement usage, bool replaceInput)
    {
        if (usage.ValueKind != JsonValueKind.Object)
            return;
        if (Integer(usage, "input_tokens") is { } i && (replaceInput || i > _input))
            _input = i;
        if (Integer(usage, "output_tokens") is { } o)
            _output = o;
    }

    private static long? Integer(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n >= 0 ? n : null;
}
