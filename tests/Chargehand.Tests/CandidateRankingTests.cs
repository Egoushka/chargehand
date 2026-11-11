using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Tests;

/// <summary>Intake candidates ranked by expected value (issue #439): completion probability × priority ÷ predicted cost, from the pre-flight forecast.</summary>
public class CandidateRankingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static LearningRecord Past(decimal usd, string repository = "o/r", string size = "M", string outcome = LearningOutcome.Completed) =>
        LearningTests.Record($"run-{Guid.NewGuid():N}", repository, size, usd, outcome, at: Now.AddDays(-1));

    private static IReadOnlyList<RankedCandidate> Rank(IReadOnlyList<LearningRecord> records, params RankCandidate[] candidates) =>
        CandidateRanking.Rank(new DrivenGovernor(), candidates, records, Now);

    [Fact]
    public void A_cheaper_candidate_that_completes_as_often_ranks_first()
    {
        var records = new List<LearningRecord>();
        records.AddRange(Enumerable.Range(1, 5).Select(_ => Past(1m, "o/cheap")));
        records.AddRange(Enumerable.Range(1, 5).Select(_ => Past(4m, "o/dear")));

        var ranked = Rank(records, new RankCandidate("dear", "o/dear", null, "M"), new RankCandidate("cheap", "o/cheap", null, "M"));

        Assert.Equal(["cheap", "dear"], ranked.Select(r => r.Id));
        Assert.Equal(1.0, ranked[0].ExpectedValue);
        Assert.Equal(0.25, ranked[1].ExpectedValue);
        Assert.Equal(1m, ranked[0].PredictedUsd);
        Assert.Equal(ForecastBasis.Repository, ranked[0].Forecast.Basis);
    }

    [Fact]
    public void Completion_probability_and_priority_multiply_the_value()
    {
        var records = new List<LearningRecord>();
        records.AddRange(Enumerable.Range(1, 5).Select(i => Past(2m, "o/flaky", outcome: i == 1 ? LearningOutcome.Completed : LearningOutcome.Failed)));
        records.AddRange(Enumerable.Range(1, 5).Select(_ => Past(2m, "o/sure")));

        var ranked = Rank(records, new RankCandidate("flaky", "o/flaky", null, "M", Priority: 4), new RankCandidate("sure", "o/sure", null, "M"));

        // flaky: 0.2 × 4 ÷ 2 = 0.4; sure: 1 × 1 ÷ 2 = 0.5.
        Assert.Equal(["sure", "flaky"], ranked.Select(r => r.Id));
        Assert.Equal(0.4, ranked[1].ExpectedValue, 6);
        Assert.Equal(0.2, ranked[1].CompletionProbability, 6);
        Assert.True(ranked[1].CompletionKnown);
    }

    [Fact]
    public void Without_history_the_flat_budget_is_the_cost_and_completion_is_the_even_prior()
    {
        var ranked = Rank([], new RankCandidate("large", "o/r", null, "L"), new RankCandidate("small", "o/r", null, "S"));

        Assert.Equal(["small", "large"], ranked.Select(r => r.Id));
        Assert.All(ranked, r => Assert.False(r.CompletionKnown));
        Assert.All(ranked, r => Assert.Equal(CandidateRanking.UnknownCompletion, r.CompletionProbability));
        Assert.Equal(ranked[0].Forecast.UsdP90, ranked[0].PredictedUsd);
    }

    [Fact]
    public void Equal_values_keep_the_order_they_were_sent_in()
    {
        var ranked = Rank([], new RankCandidate("b", "o/r", null, "M"), new RankCandidate("a", "o/r", null, "M"), new RankCandidate("c", "o/r", null, "M"));

        Assert.Equal(["b", "a", "c"], ranked.Select(r => r.Id));
    }

    // POST /v1/intake/rank

    [Fact]
    public async Task The_server_ranks_the_candidates_it_is_sent_and_refuses_a_bad_list()
    {
        using var dir = new TempDir();
        var store = new JsonlLearningStore(Path.Combine(dir.Path, "learning.jsonl"));
        foreach (var record in Enumerable.Range(1, 5).Select(_ => LearningTests.Record($"c{Guid.NewGuid():N}", "o/cheap", usd: 1m, at: DateTimeOffset.UtcNow.AddHours(-1))))
            await store.AppendAsync(record, default);
        foreach (var record in Enumerable.Range(1, 5).Select(_ => LearningTests.Record($"d{Guid.NewGuid():N}", "o/dear", usd: 4m, at: DateTimeOffset.UtcNow.AddHours(-1))))
            await store.AppendAsync(record, default);
        DrivenRunTests.Rig? rig = null;
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), driven: (root, log) => (rig = new DrivenRunTests.Rig(dir: root, log: log, learning: store)).Run);
        using var _ = rig;

        var reply = await s.Http.PostAsJsonAsync("/v1/intake/rank", new
        {
            candidates = new object[]
            {
                new { id = "o/dear#1", repository = "o/dear", goal = "fix the parser", size = "M" },
                new { id = "o/cheap#2", repository = "o/cheap", goal = "fix the parser", size = "M", priority = 2 },
            },
        });

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var body = await reply.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("intake-rank/v1", body.GetProperty("contract_version").GetString());
        var ranked = body.GetProperty("candidates");
        Assert.Equal("o/cheap#2", ranked[0].GetProperty("id").GetString());
        Assert.Equal(2.0, ranked[0].GetProperty("expected_value").GetDouble());
        Assert.Equal("repository", ranked[0].GetProperty("forecast").GetProperty("basis").GetString());
        Assert.Equal("o/dear#1", ranked[1].GetProperty("id").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.PostAsJsonAsync("/v1/intake/rank", new { candidates = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.PostAsJsonAsync("/v1/intake/rank", new { candidates = new[] { new { id = "x", repository = "o/r", size = "XL" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.PostAsJsonAsync("/v1/intake/rank", new { candidates = new[] { new { id = "x", repository = "o/r", priority = 0 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.PostAsync("/v1/intake/rank", new StringContent("not json"))).StatusCode);
        using var anonymous = new HttpClient { BaseAddress = s.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/v1/intake/rank", new { candidates = Array.Empty<object>() })).StatusCode);
    }
}
