using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Tests;

/// <summary>The pre-flight forecast over synthetic learning records: cold start, one repository, the fall back across repositories, and the similarity of goals.</summary>
public class TaskForecastTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly DrivenGovernor Settings = new();

    private static readonly decimal[] Spends = [1m, 2m, 3m, 4m, 10m];

    private static readonly decimal[] OtherSpends = [3m, 4m, 5m, 6m];

    private static LearningRecord Past(decimal usd, string repository = "o/r", string size = "M", string outcome = LearningOutcome.Completed, int turns = 10, int daysAgo = 1, string? goal = null,
        bool partial = false)
    {
        var record = LearningTests.Record($"run-{Guid.NewGuid():N}", repository, size, usd, outcome, at: Now.AddDays(-daysAgo));
        return record with { Spend = record.Spend with { Turns = turns, UsdEstimatePartial = partial }, GoalTerms = goal is null ? null : GoalTerms.Of(goal) };
    }

    private static TaskForecast Predict(IReadOnlyList<LearningRecord> records, string? goal = null, string size = "M", string repository = "o/r") =>
        Forecaster.Predict(Settings, new ForecastQuery(repository, goal, size), records, Now);

    [Fact]
    public void With_no_history_the_forecast_is_the_flat_budget_and_says_so()
    {
        var forecast = Predict([]);

        Assert.Equal(new TaskForecast("M", ForecastBasis.Flat, 0, null, 4m, null, null, false), forecast);
        Assert.Equal("no history: up to $4 (flat budget, size M)", forecast.Describe());
    }

    [Fact]
    public void Four_tasks_are_too_few_and_a_large_flat_task_suggests_a_split()
    {
        Assert.Equal(ForecastBasis.Flat, Predict([.. Enumerable.Range(1, 4).Select(i => Past(i))]).Basis);
        Assert.True(Predict([], size: "L").SuggestSplit);
    }

    [Fact]
    public void Five_tasks_of_the_repository_give_percentiles_turns_and_the_completion_share()
    {
        var records = Spends.Select((usd, i) => Past(usd, turns: 10 + i, outcome: i == 4 ? LearningOutcome.Failed : LearningOutcome.Completed)).ToList();

        var forecast = Predict(records);

        Assert.Equal(new TaskForecast("M", ForecastBasis.Repository, 5, 3m, 10m, 12, 0.8, false), forecast);
    }

    [Fact]
    public void Fewer_than_half_completing_suggests_a_split()
    {
        var records = Enumerable.Range(1, 6).Select(i => Past(i, outcome: i <= 2 ? LearningOutcome.Completed : LearningOutcome.Partial)).ToList();

        var forecast = Predict(records);

        Assert.Equal(0.33, forecast.CompletionProbability);
        Assert.True(forecast.SuggestSplit);
    }

    [Fact]
    public void A_repository_with_too_few_falls_back_to_the_size_class_across_repositories_and_other_sizes_are_left_out()
    {
        var records = new List<LearningRecord> { Past(1, "o/r"), Past(2, "o/r") };
        records.AddRange(OtherSpends.Select(usd => Past(usd, "o/other")));
        records.Add(Past(99, "o/r", size: "S"));

        var forecast = Predict(records);

        Assert.Equal(ForecastBasis.SizeClass, forecast.Basis);
        Assert.Equal(6, forecast.Samples);
        Assert.Equal(3m, forecast.UsdP50);
        Assert.Equal(6m, forecast.UsdP90);
    }

    [Fact]
    public void Old_partly_priced_and_unpriced_tasks_are_not_samples()
    {
        var records = new List<LearningRecord> { Past(1, daysAgo: 70), Past(1, partial: true), Past(1) with { Spend = Past(1).Spend with { UsdEstimate = null } } };
        records.AddRange(Enumerable.Range(1, 4).Select(i => Past(i)));

        Assert.Equal(ForecastBasis.Flat, Predict(records).Basis);
    }

    [Fact]
    public void The_most_similar_goals_of_the_repository_are_the_samples_when_there_are_more_than_ten()
    {
        var records = new List<LearningRecord>();
        records.AddRange(Enumerable.Range(1, 10).Select(_ => Past(1m, goal: "bump the nuget dependencies and fix the lockfile")));
        records.AddRange(Enumerable.Range(1, 10).Select(_ => Past(20m, goal: "rewrite the payment reconciliation job to stream rows")));

        var forecast = Predict(records, goal: "rewrite reconciliation job so the payment rows stream");

        Assert.Equal(10, forecast.Samples);
        Assert.Equal(20m, forecast.UsdP50);
    }

    [Fact]
    public void Records_without_goal_terms_rank_by_recency_and_the_forecast_is_deterministic()
    {
        var records = Enumerable.Range(1, 12).Select(i => Past(i, daysAgo: i)).ToList();

        var forecast = Predict(records, goal: "anything");

        Assert.Equal(forecast, Predict([.. records.AsEnumerable().Reverse()], goal: "anything"));
        Assert.Equal(10, forecast.Samples);
        Assert.Equal(5m, forecast.UsdP50);
    }

    [Fact]
    public void Goal_terms_keep_words_as_hashes_not_text()
    {
        var terms = GoalTerms.Of("Fix the Login bug: login fails");

        Assert.Equal(GoalTerms.Of("login FAILS fix bug"), terms);
        Assert.All(terms, t => Assert.Matches("^[0-9a-f]{8}$", t));
        Assert.Equal(4, terms.Count);
        Assert.Equal(0, GoalTerms.Similarity(null, terms));
        Assert.Equal(1, GoalTerms.Similarity(terms, terms));
    }
}
