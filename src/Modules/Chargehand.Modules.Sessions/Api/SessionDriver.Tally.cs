using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>What <see cref="SessionDriver"/> reads from the stream: the running tally, the stop cause, and the report and questions at the end.</summary>
public static partial class SessionDriver
{
    /// <summary>The report is the last <c>```json</c> block of the last assistant text that has one; the questions are the lines after the last
    /// <c>NEEDS_INPUT:</c> line.</summary>
    private static (string? Report, List<string> Questions) Extract(Tally tally)
    {
        string? report = null;
        foreach (var text in tally.Texts)
            foreach (Match m in JsonBlock().Matches(text))
                try
                {
                    _ = JsonDocument.Parse(m.Groups[1].Value);
                    report = m.Groups[1].Value.Trim();
                }
                catch (JsonException) { }
        var questions = new List<string>();
        foreach (var text in tally.Texts)
        {
            var at = text.LastIndexOf("NEEDS_INPUT:", StringComparison.Ordinal);
            if (at < 0)
                continue;
            questions.Clear();
            foreach (var line in text[(at + "NEEDS_INPUT:".Length)..].Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                questions.Add(Numbering().Replace(line, "").Trim());
        }
        return (report, questions.Where(q => q.Length > 0).ToList());
    }

    [GeneratedRegex(@"```json\s*(.*?)```", RegexOptions.Singleline)]
    private static partial Regex JsonBlock();

    [GeneratedRegex(@"^(\d+[.)]|[-*])\s*")]
    private static partial Regex Numbering();

    private sealed class StopCause
    {
        private readonly object _lock = new();

        public (SessionStatus Status, string Reason)? Cause { get; private set; }

        public DateTimeOffset At { get; private set; }

        /// <returns>True for the first cause only.</returns>
        public bool Set(SessionStatus status, string reason)
        {
            lock (_lock)
            {
                if (Cause is not null)
                    return false;
                Cause = (status, reason);
                At = DateTimeOffset.UtcNow;
                return true;
            }
        }
    }

    /// <summary>Running totals from the stream: per-message tokens while it runs (so a cap can bite), the <c>result</c> event's totals at the end. Each assistant message's usage
    /// carries its model and cache counts, so the cache totals and the dollar estimate are live too.</summary>
    private sealed class Tally(IReadOnlyDictionary<string, ModelPrice>? prices = null)
    {
        private readonly Dictionary<string, (string? Model, TokenCounts Tokens)> _perMessage = [];
        // The messages since the last result: a result's totals replace them, and each resumed process (the check gate) ends in a result of its own.
        private readonly HashSet<string> _open = [];
        private long _resultIn, _resultOut, _resultCache, _resultCacheWrite;

        public List<string> Texts { get; } = [];
        public bool HasResult { get; private set; }
        public bool ResultIsError { get; private set; }
        public string? ResultText { get; private set; }
        public decimal? CostUsd { get; private set; }
        public long Input => _resultIn + Open(t => t.Input);
        public long Output => _resultOut + Open(t => t.Output);
        public long CacheRead => _resultCache + Open(t => t.CacheRead);
        public long CacheWrite => _resultCacheWrite + Open(t => t.CacheWrite);
        public string? SessionId { get; private set; }
        public string? Model { get; private set; }
        public Dictionary<string, TokenCounts> ModelUsage { get; } = [];

        /// <summary>The per-message tokens priced with the task's table, model by model; null without a table or when no message could be priced (unknown, never 0).</summary>
        public decimal? UsdEstimate => Estimate().Usd;

        /// <summary>Some message's model had no price, so <see cref="UsdEstimate"/> sums only the others.</summary>
        public bool UsdEstimatePartial => Estimate().Partial;

        private long Open(Func<TokenCounts, long> count) => _open.Sum(id => count(_perMessage[id].Tokens));

        /// <summary>A resumed process starts: the last one's result no longer says whether the session finished.</summary>
        public void NewProcess()
        {
            HasResult = false;
            ResultIsError = false;
        }

        private (decimal? Usd, bool Partial) Estimate()
        {
            if (prices is null)
                return (null, false);
            var table = new PriceTable(prices);
            decimal total = 0;
            var any = false;
            var partial = false;
            foreach (var group in _perMessage.Values.GroupBy(v => v.Model ?? Model))
            {
                var sum = new TokenCounts(group.Sum(v => v.Tokens.Input), group.Sum(v => v.Tokens.Output), 0, group.Sum(v => v.Tokens.CacheRead), group.Sum(v => v.Tokens.CacheWrite));
                if (group.Key is { } model && DrivenTelemetry.Find(prices, model) is { } key && table.PriceUsd(key, sum) is { } usd)
                {
                    total += usd;
                    any = true;
                }
                else
                    partial = true;
            }
            return any ? (total, partial) : (null, false);
        }

        public void Add(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out var type))
                return;
            if (e.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                SessionId = sid.GetString();
            switch (type.GetString())
            {
                case "system" when e.TryGetProperty("subtype", out var sub) && sub.GetString() == "init" && e.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String:
                    Model = model.GetString();
                    break;
                case "assistant" when e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object:
                    OnAssistant(m);
                    break;
                case "result":
                    OnResult(e);
                    break;
            }
        }

        private void OnAssistant(JsonElement m)
        {
            if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && m.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                var counts = new TokenCounts(Long(u, "input_tokens"), Long(u, "output_tokens"), 0, Long(u, "cache_read_input_tokens"), Long(u, "cache_creation_input_tokens"));
                // A message with no usage (Claude Code's synthetic error messages) has no price and nothing to count.
                if (counts.Input + counts.Output + counts.CacheRead + counts.CacheWrite > 0)
                {
                    _perMessage[id.GetString()!] = (m.TryGetProperty("model", out var mm) && mm.ValueKind == JsonValueKind.String ? mm.GetString() : null, counts);
                    _open.Add(id.GetString()!);
                }
            }
            AddTexts(m);
        }

        private void AddTexts(JsonElement m)
        {
            if (m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var block in content.EnumerateArray())
                    if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var text) && text.GetString() is { } s)
                        Texts.Add(s);
        }

        private void OnResult(JsonElement e)
        {
            HasResult = true;
            ResultIsError = e.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
            if (e.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
                CostUsd = (CostUsd ?? 0) + cost.GetDecimal();
            if (e.TryGetProperty("usage", out var ru) && ru.ValueKind == JsonValueKind.Object)
            {
                _resultIn += Long(ru, "input_tokens");
                _resultOut += Long(ru, "output_tokens");
                _resultCache += Long(ru, "cache_read_input_tokens");
                _resultCacheWrite += Long(ru, "cache_creation_input_tokens");
                _open.Clear();
            }
            if (e.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
                foreach (var p in mu.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object))
                {
                    var counts = new TokenCounts(Long(p.Value, "inputTokens"), Long(p.Value, "outputTokens"), 0, Long(p.Value, "cacheReadInputTokens"), Long(p.Value, "cacheCreationInputTokens"));
                    ModelUsage[p.Name] = ModelUsage.TryGetValue(p.Name, out var before)
                        ? new TokenCounts(before.Input + counts.Input, before.Output + counts.Output, 0, before.CacheRead + counts.CacheRead, before.CacheWrite + counts.CacheWrite)
                        : counts;
                }
            if (e.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String && result.GetString() is { } r)
            {
                ResultText = r;
                Texts.Add(r);
            }
        }

        private static long Long(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    }
}
