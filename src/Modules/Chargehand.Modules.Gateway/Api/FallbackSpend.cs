using System.Text.Json;

namespace Chargehand.Modules.Gateway.Api;

/// <summary>What one run spent on the fallback, from the lines it appears in.</summary>
public sealed record RunSpend(decimal Usd, int Calls);

/// <summary>What a batch's model endpoint spent on the fallback key (ADR 0088, decision 6), read from the egress container's log at teardown. <see cref="Usd"/>, <see cref="Calls"/> and
/// <see cref="Refused"/> come from the last fallback line, which carries running totals: the tail the server reads may have lost the early lines of a long batch, never the last one.
/// <see cref="ByRun"/> sums the lines that were read, so a run's share can fall short in that case. Never any text.</summary>
/// <param name="Refused">The fallback answered 402 and closed for the rest of the batch.</param>
/// <param name="LimitedUntil">The latest reset a limit named; null: none was seen.</param>
public sealed record FallbackSpend(decimal Usd, int Calls, bool Refused, DateTimeOffset? LimitedUntil, IReadOnlyDictionary<string, RunSpend> ByRun)
{
    public static readonly FallbackSpend None = new(0, 0, false, null, new Dictionary<string, RunSpend>());

    public FallbackSpend Plus(FallbackSpend other)
    {
        if (other == None)
            return this;
        if (this == None)
            return other;
        var runs = new Dictionary<string, RunSpend>(ByRun);
        foreach (var (run, spend) in other.ByRun)
            runs[run] = runs.TryGetValue(run, out var mine) ? new RunSpend(mine.Usd + spend.Usd, mine.Calls + spend.Calls) : spend;
        return new FallbackSpend(Usd + other.Usd, Calls + other.Calls, Refused || other.Refused, (LimitedUntil, other.LimitedUntil) switch
        {
            ({ } a, { } b) => a > b ? a : b,
            var (a, b) => a ?? b,
        }, runs);
    }

    /// <summary>The fallback calls in an egress container's log; <see cref="None"/> when there were none. Lines that are not the model endpoint's are skipped.</summary>
    public static FallbackSpend Parse(string log)
    {
        GatewayLogLine? last = null;
        DateTimeOffset? limitedUntil = null;
        Dictionary<string, RunSpend> runs = [];
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{') || !line.Contains("\"fallback\":true", StringComparison.Ordinal))
                continue;
            GatewayLogLine? entry;
            try
            {
                entry = JsonSerializer.Deserialize(line, GatewayJsonContext.Default.GatewayLogLine);
            }
            catch (JsonException)
            {
                continue;
            }
            if (entry is not { Fallback: true })
                continue;
            last = entry;
            if (entry.LimitedUntil is { } at && (limitedUntil is null || at > limitedUntil))
                limitedUntil = at;
            if (entry.ModelRun is { } run)
            {
                var mine = runs.GetValueOrDefault(run);
                runs[run] = new RunSpend((mine?.Usd ?? 0) + entry.Usd, (mine?.Calls ?? 0) + 1);
            }
        }
        return last is null ? None : new FallbackSpend(last.TotalUsd, last.Calls, last.Closed, limitedUntil, runs);
    }
}
