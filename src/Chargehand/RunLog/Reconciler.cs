namespace Chargehand.RunLog;

/// <summary>A gateway spend-log row, as exported by the environment (the orchestrator never queries the gateway).</summary>
public sealed record SpendRow(string Model, long PromptTokens, long CompletionTokens, DateTimeOffset StartTime, decimal Spend);

public sealed record Reconciliation(IReadOnlyList<(CallRecord Call, SpendRow? Row)> Matches, decimal OwnUsd, decimal GatewayUsd, int Unmatched);

/// <summary>
/// Joins calls to spend rows without a shared id (ADR 0012): same model name, same prompt and completion token
/// counts, and the gateway's start time inside the call's lifetime (message created → completed) widened by a
/// slack. The gateway starts seconds after OpenCode creates the message (masking-proxy scan in between: 2–30 s
/// observed), so a window around the creation time alone misses. Each row is used once. Works because the
/// orchestrator's gateway key is its own.
/// </summary>
public static class Reconciler
{
    public static Reconciliation Match(IReadOnlyList<CallRecord> calls, IReadOnlyList<SpendRow> rows, TimeSpan? slack = null)
    {
        var w = slack ?? TimeSpan.FromSeconds(5);
        var free = rows.ToList();
        var matches = new List<(CallRecord, SpendRow?)>();
        foreach (var call in calls.Where(c => c.Tokens is not null).OrderBy(c => c.Started))
        {
            var completion = call.Tokens!.Output + call.Tokens.Reasoning;
            var from = call.Started - w;
            var to = call.Started + TimeSpan.FromMilliseconds(call.LatencyMs) + w;
            var row = free
                .Where(r => SameModel(r.Model, call.Model) && r.PromptTokens == call.PromptTokens && r.CompletionTokens == completion && r.StartTime >= from && r.StartTime <= to)
                .OrderBy(r => r.StartTime)
                .FirstOrDefault();
            if (row is not null)
                free.Remove(row);
            matches.Add((call, row));
        }
        return new Reconciliation(matches, matches.Sum(m => m.Item1.Usd ?? 0), matches.Sum(m => m.Item2?.Spend ?? 0), matches.Count(m => m.Item2 is null));
    }

    /// <summary>OpenCode names "provider/model"; gateways log "vendor/model" or the bare alias. Compare the last segment.</summary>
    private static bool SameModel(string gateway, string opencode) =>
        string.Equals(gateway[(gateway.LastIndexOf('/') + 1)..], opencode[(opencode.LastIndexOf('/') + 1)..], StringComparison.OrdinalIgnoreCase);
}
