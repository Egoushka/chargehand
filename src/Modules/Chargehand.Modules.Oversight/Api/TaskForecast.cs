namespace Chargehand.Modules.Oversight.Api;

/// <summary>What to forecast: the task as it is about to start. <paramref name="Preset"/> is accepted so callers need not change when records carry one; today's records do not, so it is not yet
/// used to pick samples.</summary>
public sealed record ForecastQuery(string Repository, string? Goal, string? Size, string? Preset = null);

/// <summary>Where a forecast's figures come from: <c>repository</c> (finished tasks of the size class, this repository first, enough of them here), <c>size_class</c> (the same, across
/// repositories, because this one has too few), or <c>flat</c> (too few anywhere: the size class's flat budget is the only figure).</summary>
public static class ForecastBasis
{
    public const string Repository = "repository";
    public const string SizeClass = "size_class";
    public const string Flat = "flat";
}

/// <summary>A task's cost before it starts (CHARGEHAND-438), from the most similar finished tasks.</summary>
/// <param name="UsdP50">Median spend in the dollar equivalent; null for <see cref="ForecastBasis.Flat"/>.</param>
/// <param name="UsdP90">The 90th percentile of spend; for <see cref="ForecastBasis.Flat"/>, the flat budget, the most the task may cost under the governor.</param>
/// <param name="TurnsP50">Median turns; null for <see cref="ForecastBasis.Flat"/>.</param>
/// <param name="CompletionProbability">The share of the samples that completed, 0 to 1; null for <see cref="ForecastBasis.Flat"/>.</param>
/// <param name="Samples">How many finished tasks the figures stand on.</param>
/// <param name="SuggestSplit">The task is large, or fewer than half of its samples completed: it is worth splitting before it starts.</param>
public sealed record TaskForecast(string SizeClass, string Basis, int Samples, decimal? UsdP50, decimal UsdP90, int? TurnsP50, double? CompletionProbability, bool SuggestSplit)
{
    public string Describe() => Basis == ForecastBasis.Flat
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"no history: up to ${UsdP90:0.##} (flat budget, size {SizeClass})")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"${UsdP50:0.##} typical, ${UsdP90:0.##} at p90, {TurnsP50} turns, {CompletionProbability:P0} complete ({Samples} similar {(Basis == ForecastBasis.Repository ? "tasks here" : "tasks, all repositories")})");
}

/// <summary>Pure predictor over the learning records: the same records, query and time give the same forecast. The samples are finished tasks of the query's size class in the history window whose spend is
/// priced in full, the same pool <see cref="BudgetHistory"/> draws on, but any outcome counts, because a task that failed is what the completion figure is about. They are ordered this repository
/// first, then by similarity of the goal (<see cref="GoalTerms"/>), then newest, and the first <see cref="SampleSize"/> are used. With fewer than <c>min_tasks</c> from this repository the pool is
/// all repositories; with fewer than that, the flat budget.</summary>
public static class Forecaster
{
    public const int SampleSize = 10;

    public static TaskForecast Predict(DrivenGovernor settings, ForecastQuery query, IReadOnlyList<LearningRecord> records, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(records);
        var size = query.Size ?? "M";
        var history = settings.History ?? new DrivenGovernorHistory();
        var since = now.AddDays(-history.WindowDays);
        var pool = records.Where(r => r.SizeClass == size && r.At >= since && r.Spend is { UsdEstimate: not null, UsdEstimatePartial: false }).ToList();
        var here = pool.Count(r => r.Repository == query.Repository);
        var basis = here >= history.MinTasks ? ForecastBasis.Repository : pool.Count >= history.MinTasks ? ForecastBasis.SizeClass : ForecastBasis.Flat;
        var flat = settings.Budget(size, null).Usd;
        if (basis == ForecastBasis.Flat)
            return new TaskForecast(size, basis, pool.Count, null, flat, null, null, size == "L");
        var terms = GoalTerms.Of(query.Goal);
        var samples = pool.Where(r => basis == ForecastBasis.SizeClass || r.Repository == query.Repository)
            .OrderByDescending(r => r.Repository == query.Repository).ThenByDescending(r => GoalTerms.Similarity(terms, r.GoalTerms)).ThenByDescending(r => r.At)
            .Take(SampleSize).ToList();
        var usd = samples.Select(r => r.Spend.UsdEstimate!.Value).ToList();
        var completion = Math.Round((double)samples.Count(r => r.Completed) / samples.Count, 2);
        return new TaskForecast(size, basis, samples.Count, Math.Round(LearningStats.Percentile(usd, 50)!.Value, 4), Math.Round(LearningStats.Percentile(usd, 90)!.Value, 4),
            (int)LearningStats.Percentile(samples.Select(r => (decimal)r.Spend.Turns), 50)!.Value, completion, size == "L" || completion < 0.5);
    }
}
