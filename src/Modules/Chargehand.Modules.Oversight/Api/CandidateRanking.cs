namespace Chargehand.Modules.Oversight.Api;

/// <summary>A piece of work intake could start: a GitHub issue, a tracker item, anything with a repository and a goal (issue #439).</summary>
/// <param name="Id">The caller's name for it, returned as is: <c>owner/repo#12</c>, a tracker key.</param>
/// <param name="Priority">How much it is worth, 1 or more; 1 when the caller has no priority for it. A weight, not a rank: 2 is worth twice 1.</param>
public sealed record RankCandidate(string Id, string Repository, string? Goal, string? Size, double Priority = 1);

/// <summary>A candidate with its forecast and expected value, <c>completion_probability × priority ÷ predicted_usd</c>.</summary>
/// <param name="CompletionProbability">The forecast's completion share; <see cref="CandidateRanking.UnknownCompletion"/> when the forecast has none.</param>
/// <param name="CompletionKnown">The completion figure comes from finished tasks, not the even prior.</param>
/// <param name="PredictedUsd">The forecast's median spend; with no history, the flat budget (its p90).</param>
public sealed record RankedCandidate(string Id, string Repository, double ExpectedValue, double CompletionProbability, bool CompletionKnown, double Priority, decimal PredictedUsd,
    TaskForecast Forecast);

/// <summary>Pure ranking of intake candidates by expected value (issue #439) over the pre-flight forecast (<see cref="Forecaster"/>): the same candidates, records and time give the same order.
/// Highest value first; equal values keep the order they were given in.</summary>
public static class CandidateRanking
{
    /// <summary>The completion probability of a candidate whose forecast has no history: neither likely nor unlikely.</summary>
    public const double UnknownCompletion = 0.5;

    /// <summary>The least a predicted cost counts as, so a task forecast to cost nothing does not rank as infinitely valuable.</summary>
    public const decimal MinUsd = 0.01m;

    public static IReadOnlyList<RankedCandidate> Rank(DrivenGovernor settings, IReadOnlyList<RankCandidate> candidates, IReadOnlyList<LearningRecord> records, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return [.. candidates.Select(c => Value(c, Forecaster.Predict(settings, new ForecastQuery(c.Repository, c.Goal, c.Size), records, now))).OrderByDescending(r => r.ExpectedValue)];
    }

    private static RankedCandidate Value(RankCandidate candidate, TaskForecast forecast)
    {
        var completion = forecast.CompletionProbability ?? UnknownCompletion;
        var usd = forecast.UsdP50 ?? forecast.UsdP90;
        var value = completion * candidate.Priority / (double)Math.Max(usd, MinUsd);
        return new RankedCandidate(candidate.Id, candidate.Repository, value, completion, forecast.CompletionProbability is not null, candidate.Priority, usd, forecast);
    }
}
